// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Prebuild;

internal sealed class UnrealShaderPrebuildReport
{
    public int Libraries { get; set; }
    public int Groups { get; set; }
    public int FailedGroups { get; set; }
    public int MissingGroups { get; set; }
    public int Shaders { get; set; }
    public int Unextracted { get; set; }
    public long CompressedBytes { get; set; }
    public long DecompressedBytes { get; set; }
    public Dictionary<byte, int> AgcShaderTypes { get; } = [];
    public HashSet<ulong> UniqueHashes { get; } = [];
    // Per AGC shader type: prepared and failed counts, and failure reasons.
    public Dictionary<byte, (int Prepared, int Failed)> Preparation { get; } = [];
    public Dictionary<string, int> Failures { get; } = [];
    public TimeSpan Elapsed { get; set; }
}

// Extracts the PS5 shaders an Unreal Engine title cooked into its IoStore shader libraries.
internal static class UnrealShaderPrebuild
{
    private const byte ShaderCodeLibraryChunkType = 8;
    private const byte ShaderCodeChunkType = 9;
    private const string ManifestFileName = "manifest.tsv";

    public static UnrealShaderPrebuildReport Run(
        string app0Root,
        IOodleDecompressor oodle,
        string? dumpDirectory,
        int maxGroups,
        TextWriter log)
    {
        var started = Stopwatch.GetTimestamp();
        var report = new UnrealShaderPrebuildReport();
        var containers = new List<IoStoreContainer>();
        StreamWriter? manifest = null;
        if (dumpDirectory is not null)
        {
            Directory.CreateDirectory(dumpDirectory);
            manifest = new StreamWriter(Path.Combine(dumpDirectory, ManifestFileName));
            manifest.WriteLine("shader\thash\ttype\tcode_size");
        }

        try
        {
            var groupChunks = new Dictionary<IoChunkId, (IoStoreContainer Container, int Index)>();
            var libraries = new List<(IoStoreContainer Container, int Index)>();
            foreach (var tocPath in Directory.EnumerateFiles(app0Root, "*.utoc", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                IoStoreContainer container;
                try
                {
                    container = IoStoreContainer.Open(tocPath);
                }
                catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or IOException)
                {
                    log.WriteLine($"[PREBUILD][WARN] Skipping {Path.GetFileName(tocPath)}: {exception.Message}");
                    continue;
                }

                containers.Add(container);
                for (var index = 0; index < container.EntryCount; index++)
                {
                    var chunk = container.ChunkId(index);
                    if (chunk.Type == ShaderCodeChunkType)
                    {
                        groupChunks.TryAdd(chunk, (container, index));
                    }
                    else if (chunk.Type == ShaderCodeLibraryChunkType)
                    {
                        libraries.Add((container, index));
                    }
                }
            }

            log.WriteLine($"[PREBUILD][INFO] Unreal IoStore: containers={containers.Count} shader_libraries={libraries.Count} shader_groups={groupChunks.Count}");
            var visited = new HashSet<IoChunkId>();
            for (var libraryIndex = 0; libraryIndex < libraries.Count; libraryIndex++)
            {
                var (container, index) = libraries[libraryIndex];
                var archive = UnrealShaderArchive.Parse(container.ReadChunk(index));
                report.Libraries++;
                log.WriteLine(
                    $"[PREBUILD][INFO] Shader library {libraryIndex} in {Path.GetFileName(container.TocPath)}: " +
                    $"groups={archive.Groups.Length} shaders={archive.Shaders.Length}");
                var batch = new List<(int Group, OodleJob Job)>();
                long batchBytes = 0;
                for (var group = 0; group < archive.Groups.Length; group++)
                {
                    if (maxGroups > 0 && report.Groups + batch.Count >= maxGroups)
                    {
                        break;
                    }

                    var entry = archive.Groups[group];
                    if (!visited.Add(entry.Chunk))
                    {
                        continue;
                    }

                    if (!groupChunks.TryGetValue(entry.Chunk, out var location))
                    {
                        report.MissingGroups++;
                        continue;
                    }

                    var job = new OodleJob(location.Container.ReadChunk(location.Index), (int)entry.UncompressedSize);
                    var jobBytes = job.Source.Length + job.Destination.Length;
                    if (batch.Count != 0 && batchBytes + jobBytes > oodle.MaxBatchBytes)
                    {
                        Flush(archive, libraryIndex, batch, oodle, report, dumpDirectory, manifest);
                        batchBytes = 0;
                    }

                    batch.Add((group, job));
                    batchBytes += jobBytes;
                }

                Flush(archive, libraryIndex, batch, oodle, report, dumpDirectory, manifest);
            }
        }
        finally
        {
            manifest?.Dispose();
            foreach (var container in containers)
            {
                container.Dispose();
            }
        }

        report.Elapsed = Stopwatch.GetElapsedTime(started);
        return report;
    }

    private static void Flush(
        UnrealShaderArchive archive,
        int libraryIndex,
        List<(int Group, OodleJob Job)> batch,
        IOodleDecompressor oodle,
        UnrealShaderPrebuildReport report,
        string? dumpDirectory,
        TextWriter? manifest)
    {
        if (batch.Count == 0)
        {
            return;
        }

        oodle.Decompress(batch.Select(item => item.Job).ToList());
        var work = new List<AgcShaderHeader>();
        foreach (var (group, job) in batch)
        {
            report.Groups++;
            report.CompressedBytes += job.Source.Length;
            if (job.Result != job.Destination.Length)
            {
                report.FailedGroups++;
                continue;
            }

            report.DecompressedBytes += job.Destination.Length;
            foreach (var (shaderIndex, offset, length) in archive.ShadersOf(group))
            {
                report.Shaders++;
                var blob = job.Destination.AsSpan(offset, length);
                var name = $"lib{libraryIndex}_shader{shaderIndex:D6}";
                if (!UnrealShaderBlob.TryExtract(blob, out var shader))
                {
                    report.Unextracted++;
                    manifest?.WriteLine($"{name}\t-\t-\t-");
                }
                else
                {
                    report.AgcShaderTypes[shader.Type] = report.AgcShaderTypes.GetValueOrDefault(shader.Type) + 1;
                    if (report.UniqueHashes.Add(shader.Hash) && ShaderPrebuild.IsPrepared(shader.Type))
                    {
                        work.Add(new AgcShaderHeader(blob, shader));
                    }

                    manifest?.WriteLine($"{name}\t{shader.Hash:X16}\t{shader.Type}\t{shader.CodeSize}");
                }

                if (dumpDirectory is not null)
                {
                    File.WriteAllBytes(Path.Combine(dumpDirectory, name + ".bin"), blob.ToArray());
                }
            }
        }

        batch.Clear();
        var compiler = GuestGpu.Current;
        Parallel.ForEach(work, header =>
        {
            var prepared = ShaderPrebuild.TryPrepare(header, compiler, hostWave64Supported: false, out var error);
            lock (report.Failures)
            {
                var (ok, failed) = report.Preparation.GetValueOrDefault(header.Shader.Type);
                report.Preparation[header.Shader.Type] = prepared ? (ok + 1, failed) : (ok, failed + 1);
                if (!prepared)
                {
                    var reason = $"type{header.Shader.Type}: {(error.Length > 90 ? error[..90] : error)}";
                    report.Failures[reason] = report.Failures.GetValueOrDefault(reason) + 1;
                }
            }
        });
    }
}
