// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
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
    public long TotalShaders;
    public long ProcessedShaders;
    public TimeSpan Elapsed { get; set; }
}

// Extracts the PS5 shaders an Unreal Engine title cooked into its IoStore shader libraries.
internal static class UnrealShaderPrebuild
{
    private const byte ShaderCodeLibraryChunkType = 8;
    private const byte ShaderCodeChunkType = 9;
    private const string ManifestFileName = "manifest.tsv";
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SlowCheckInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SlowPreparation = TimeSpan.FromSeconds(10);

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
            // The libraries are small tables, so all are read first and the progress total
            // is known before the first group is decompressed.
            var archives = libraries
                .Select(library => (library.Container, Archive: UnrealShaderArchive.Parse(library.Container.ReadChunk(library.Index))))
                .ToList();
            report.TotalShaders = archives.Sum(library => library.Archive.Groups.Sum(group => (long)group.ShaderCount));
            using var progress = new ProgressReporter(report, log);
            var visited = new HashSet<IoChunkId>();
            for (var libraryIndex = 0; libraryIndex < archives.Count; libraryIndex++)
            {
                var (container, archive) = archives[libraryIndex];
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
                        Interlocked.Add(ref report.ProcessedShaders, (long)entry.ShaderCount);
                        continue;
                    }

                    if (!groupChunks.TryGetValue(entry.Chunk, out var location))
                    {
                        report.MissingGroups++;
                        Interlocked.Add(ref report.ProcessedShaders, (long)entry.ShaderCount);
                        continue;
                    }

                    var job = new OodleJob(location.Container.ReadChunk(location.Index), (int)entry.UncompressedSize);
                    var jobBytes = job.Source.Length + job.Destination.Length;
                    if (batch.Count != 0 && batchBytes + jobBytes > oodle.MaxBatchBytes)
                    {
                        Flush(archive, libraryIndex, batch, oodle, report, dumpDirectory, manifest, log);
                        batchBytes = 0;
                    }

                    batch.Add((group, job));
                    batchBytes += jobBytes;
                }

                Flush(archive, libraryIndex, batch, oodle, report, dumpDirectory, manifest, log);
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
        WriteProgress(report, log);
        return report;
    }

    // Reports from its own thread: the preparation keeps every pool thread busy, which
    // holds a pool timer's callbacks back for many seconds.
    private sealed class ProgressReporter : IDisposable
    {
        private readonly ManualResetEventSlim _stop = new();
        private readonly Thread _thread;

        public ProgressReporter(UnrealShaderPrebuildReport report, TextWriter log)
        {
            _thread = new Thread(() =>
            {
                do
                {
                    WriteProgress(report, log);
                }
                while (!_stop.Wait(ProgressReportInterval));
            })
            {
                IsBackground = true,
                Name = "SharpEmu prebuild progress",
            };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop.Set();
            _thread.Join();
            _stop.Dispose();
        }
    }

    // A launcher follows a prebuild through these lines: shader entries handled so far
    // and the total, each library entry counted once whether prepared or skipped.
    private static void WriteProgress(UnrealShaderPrebuildReport report, TextWriter log) =>
        log.WriteLine($"[PREBUILD][PROGRESS] {Interlocked.Read(ref report.ProcessedShaders)} {report.TotalShaders}");

    private static void Flush(
        UnrealShaderArchive archive,
        int libraryIndex,
        List<(int Group, OodleJob Job)> batch,
        IOodleDecompressor oodle,
        UnrealShaderPrebuildReport report,
        string? dumpDirectory,
        TextWriter? manifest,
        TextWriter log)
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
                Interlocked.Add(ref report.ProcessedShaders, (long)archive.Groups[group].ShaderCount);
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
                    Interlocked.Increment(ref report.ProcessedShaders);
                    manifest?.WriteLine($"{name}\t-\t-\t-");
                }
                else
                {
                    report.AgcShaderTypes[shader.Type] = report.AgcShaderTypes.GetValueOrDefault(shader.Type) + 1;
                    if (report.UniqueHashes.Add(shader.Hash) && ShaderPrebuild.IsPrepared(shader.Type))
                    {
                        // Counted once its preparation finishes.
                        work.Add(new AgcShaderHeader(blob, shader));
                    }
                    else
                    {
                        Interlocked.Increment(ref report.ProcessedShaders);
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
        // One pathological program can hold a whole batch, so every program still running
        // long is reported while the batch waits for it.
        var running = new ConcurrentDictionary<AgcShaderHeader, long>();
        using var slowCheck = new Timer(_ =>
        {
            foreach (var (header, started) in running)
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                if (elapsed >= SlowPreparation)
                {
                    log.WriteLine(
                        $"[PREBUILD][WARN] Still preparing hash=0x{header.Shader.Hash:X16} type={header.Shader.Type} " +
                        $"code_bytes={header.Shader.CodeSize} for {elapsed.TotalSeconds:F0}s");
                }
            }
        }, null, SlowCheckInterval, SlowCheckInterval);
        Parallel.ForEach(work, header =>
        {
            running[header] = Stopwatch.GetTimestamp();
            var prepared = ShaderPrebuild.TryPrepare(header, compiler, hostWave64Supported: false, out var error);
            running.TryRemove(header, out _);
            Interlocked.Increment(ref report.ProcessedShaders);
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
