// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Prebuild;

internal sealed class UnityShaderPrebuildReport : ShaderPrebuildReport
{
    public int SerializedFiles { get; set; }
    public int UnreadableFiles { get; set; }
    public int ShaderObjects { get; set; }
    // Shader objects in files built without type trees that no other file describes.
    public int UntypedShaderObjects { get; set; }
    public int Blobs { get; set; }
    public int FailedBlobs { get; set; }
}

// Extracts the PS5 shaders a Unity title serialized into its asset files and bundles.
internal static class UnityShaderPrebuild
{
    private const int ShaderClassId = 48;
    private const int ComputeShaderClassId = 72;
    private const string ManifestFileName = "manifest.tsv";
    private const int SniffSize = 48;
    private static readonly HashSet<string> ShaderFields =
        ["platforms", "offsets", "compressedLengths", "decompressedLengths", "compressedBlob"];

    // Unity players keep their data next to the executable in Media.
    public static bool IsUnityTitle(string app0Root) =>
        File.Exists(Path.Combine(app0Root, "Media", "globalgamemanagers")) ||
        File.Exists(Path.Combine(app0Root, "Media", "data.unity3d"));

    public static UnityShaderPrebuildReport Run(string app0Root, string? dumpDirectory, int maxFiles, TextWriter log)
    {
        var started = Stopwatch.GetTimestamp();
        var report = new UnityShaderPrebuildReport();
        StreamWriter? manifest = null;
        if (dumpDirectory is not null)
        {
            Directory.CreateDirectory(dumpDirectory);
            manifest = new StreamWriter(Path.Combine(dumpDirectory, ManifestFileName));
            manifest.WriteLine("shader\thash\ttype\tcode_size");
        }

        try
        {
            var mediaRoot = Path.Combine(app0Root, "Media");
            var files = Directory.EnumerateFiles(Directory.Exists(mediaRoot) ? mediaRoot : app0Root, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Where(IsCandidate)
                .Take(maxFiles > 0 ? maxFiles : int.MaxValue)
                .ToList();
            log.WriteLine($"[PREBUILD][INFO] Unity: candidate_files={files.Count}");
            var scan = new Scan(report, dumpDirectory, manifest);
            Parallel.ForEach(files, path => scan.File(path, untypedOnly: false, log));
            // Files built without type trees are read through a tree another file of the
            // same build carries for the same type hash.
            foreach (var path in scan.Untyped.Keys.Order(StringComparer.Ordinal))
            {
                scan.File(path, untypedOnly: true, log);
            }

            log.WriteLine(
                $"[PREBUILD][INFO] Unity: serialized_files={report.SerializedFiles} unreadable={report.UnreadableFiles} " +
                $"shader_objects={report.ShaderObjects} untyped={report.UntypedShaderObjects} " +
                $"blobs={report.Blobs} failed_blobs={report.FailedBlobs}");
            report.TotalShaders = scan.Work.Count;
            using (new ShaderPrebuildRunner.ProgressReporter(report, log))
            {
                ShaderPrebuildRunner.Prepare(scan.Work.Values.ToList(), report, log);
            }
        }
        finally
        {
            manifest?.Dispose();
        }

        report.Elapsed = Stopwatch.GetElapsedTime(started);
        ShaderPrebuildRunner.WriteProgress(report, log);
        return report;
    }

    // Bundles and serialized files carry their format in their first bytes; everything
    // else in Media (resource payloads, managed metadata, configuration) is skipped.
    private static bool IsCandidate(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > Array.MaxLength)
            {
                return false;
            }

            Span<byte> head = stackalloc byte[SniffSize];
            var read = stream.ReadAtLeast(head, SniffSize, throwOnEndOfStream: false);
            return head[..read].StartsWith(UnityBundle.Signature) ||
                (read == SniffSize && UnitySerializedFile.LooksLike(head, stream.Length));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class Scan(UnityShaderPrebuildReport report, string? dumpDirectory, TextWriter? manifest)
    {
        // Type trees by type hash, from every file that kept them.
        private readonly ConcurrentDictionary<UInt128, UnityTypeNode[]> _trees = new();

        public ConcurrentDictionary<string, byte> Untyped { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<ulong, AgcShaderHeader> Work { get; } = new();

        public void File(string path, bool untypedOnly, TextWriter log)
        {
            List<(string Name, byte[] Data)> serialized;
            try
            {
                var bytes = System.IO.File.ReadAllBytes(path);
                serialized = bytes.AsSpan().StartsWith(UnityBundle.Signature)
                    ? UnityBundle.ReadFiles(bytes)
                    : [(Path.GetFileName(path), bytes)];
            }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or IOException)
            {
                Count(() => report.UnreadableFiles++);
                log.WriteLine($"[PREBUILD][WARN] Skipping {Path.GetFileName(path)}: {exception.Message}");
                return;
            }

            foreach (var (name, data) in serialized)
            {
                if (!UnitySerializedFile.LooksLike(data, data.Length))
                {
                    continue;
                }

                UnitySerializedFile file;
                try
                {
                    file = new UnitySerializedFile(data);
                }
                catch (InvalidDataException exception)
                {
                    Count(() => report.UnreadableFiles++);
                    log.WriteLine($"[PREBUILD][WARN] Skipping {name}: {exception.Message}");
                    continue;
                }

                if (!untypedOnly)
                {
                    Count(() => report.SerializedFiles++);
                }

                foreach (var obj in file.Objects)
                {
                    var objectName = $"{Path.GetFileName(path)}_{name}_{obj.PathId}";
                    if (obj.Type.ClassId == ComputeShaderClassId && !untypedOnly)
                    {
                        Count(() => report.ShaderObjects++);
                        Programs(Raw(file, obj), objectName);
                        continue;
                    }

                    if (obj.Type.ClassId != ShaderClassId || (untypedOnly && obj.Type.Tree is not null))
                    {
                        continue;
                    }

                    if (obj.Type.Tree is { } own)
                    {
                        _trees.TryAdd(obj.Type.Hash, own);
                    }

                    var tree = obj.Type.Tree ?? (untypedOnly && _trees.TryGetValue(obj.Type.Hash, out var donor) ? donor : null);
                    if (tree is null)
                    {
                        if (untypedOnly)
                        {
                            Count(() => report.UntypedShaderObjects++);
                            Programs(Raw(file, obj), objectName);
                        }
                        else
                        {
                            Untyped.TryAdd(path, 0);
                        }

                        continue;
                    }

                    Count(() => report.ShaderObjects++);
                    Shader(file, obj, tree, objectName, log);
                }
            }
        }

        private void Shader(UnitySerializedFile file, UnityObject obj, UnityTypeNode[] tree, string name, TextWriter log)
        {
            List<(byte[] Blob, string Name)> blobs = [];
            try
            {
                var fields = file.Read(obj, tree, ShaderFields);
                if (fields.GetValueOrDefault("compressedBlob") is not byte[] packed ||
                    fields.GetValueOrDefault("platforms") is not List<object?> platforms)
                {
                    return;
                }

                for (var platform = 0; platform < platforms.Count; platform++)
                {
                    var offsets = Segments(fields, "offsets", platform);
                    var packedSizes = Segments(fields, "compressedLengths", platform);
                    var sizes = Segments(fields, "decompressedLengths", platform);
                    for (var segment = 0; segment < offsets.Count && segment < packedSizes.Count && segment < sizes.Count; segment++)
                    {
                        if (offsets[segment] + packedSizes[segment] > packed.Length)
                        {
                            throw new InvalidDataException("A shader blob segment lies outside the blob.");
                        }

                        var source = packed.AsSpan((int)offsets[segment], (int)packedSizes[segment]);
                        blobs.Add((Decode(source, (int)sizes[segment]), $"{name}_p{platform}_s{segment}"));
                    }
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidCastException or OverflowException or ArgumentOutOfRangeException)
            {
                Count(() => report.FailedBlobs++);
                log.WriteLine($"[PREBUILD][WARN] Shader {name}: {exception.Message}");
                return;
            }

            foreach (var (blob, blobName) in blobs)
            {
                Programs(blob, blobName);
            }
        }

        // A compute shader object, or a shader object no type tree describes, keeps its
        // programs uncompressed among its own fields, framed the same way.
        private static byte[] Raw(UnitySerializedFile file, UnityObject obj)
        {
            try
            {
                return file.ObjectBytes(obj).ToArray();
            }
            catch (InvalidDataException)
            {
                return [];
            }
        }

        private void Programs(byte[] blob, string blobName)
        {
            Count(() => report.Blobs++);
            var shaders = UnityShaderBlob.Extract(blob);
            foreach (var shader in shaders)
            {
                lock (report)
                {
                    report.Shaders++;
                    report.AgcShaderTypes[shader.Type] = report.AgcShaderTypes.GetValueOrDefault(shader.Type) + 1;
                    if (!report.UniqueHashes.Add(shader.Hash))
                    {
                        continue;
                    }

                    manifest?.WriteLine($"{blobName}\t{shader.Hash:X16}\t{shader.Type}\t{shader.CodeSize}");
                }

                if (ShaderPrebuild.IsPrepared(shader.Type))
                {
                    Work.TryAdd(shader.Hash, new AgcShaderHeader(blob.AsSpan(0, shader.HeaderOffset + shader.HeaderSize), shader));
                }
            }

            if (dumpDirectory is not null && shaders.Count != 0)
            {
                System.IO.File.WriteAllBytes(Path.Combine(dumpDirectory, Sanitize(blobName) + ".bin"), blob);
            }
        }

        // A segment is stored compressed unless compressing did not shrink it.
        private static byte[] Decode(ReadOnlySpan<byte> source, int size)
        {
            try
            {
                return Lz4Block.Decode(source, size);
            }
            catch (InvalidDataException) when (source.Length == size)
            {
                return source.ToArray();
            }
        }

        // Older players store one offset per platform, newer ones a list per platform.
        private static List<long> Segments(Dictionary<string, object?> fields, string field, int platform) =>
            (fields.GetValueOrDefault(field) as List<object?>)?[platform] switch
            {
                List<object?> list => list.Select(Convert.ToInt64).ToList(),
                long single => [single],
                _ => throw new InvalidDataException($"The shader field {field} is missing."),
            };

        private void Count(Action update)
        {
            lock (report)
            {
                update();
            }
        }

        private static string Sanitize(string name) =>
            string.Concat(name.Select(character => char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_'));
    }
}
