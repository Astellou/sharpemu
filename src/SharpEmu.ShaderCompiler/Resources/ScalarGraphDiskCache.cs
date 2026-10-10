// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SharpEmu.ShaderCompiler.Resources;

// Selects the analysis cache of the title being run. Each title keeps its own bounded
// cache, so one large title cannot evict another, and a prebuild can fill it ahead.
public static class ShaderAnalysisCache
{
    public static void UseTitle(string? titleId) => ScalarGraphDiskCache.UseTitle(titleId);
}

internal static class ScalarGraphDiskCache
{
    private const int Version = 3;
    private const int MaxEntryBytes = 32 * 1024 * 1024;
    // Brotli quality 5 shrinks snapshots about sevenfold for about 1.5 ms per write,
    // which happens once per program against an analysis that costs far more.
    private const int CompressionQuality = 5;
    private const long DefaultMaxCacheMegabytes = 2048;
    private static readonly string RootPath = Environment.GetEnvironmentVariable("SHARPEMU_SHADER_ANALYSIS_CACHE_PATH") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SharpEmu", "shader-analysis");
    private static readonly long MaxCacheBytes =
        (long.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_SHADER_ANALYSIS_CACHE_MAX_MB"), out var megabytes) && megabytes > 0
            ? megabytes
            : DefaultMaxCacheMegabytes) * 1024 * 1024;
    private static readonly bool Trace = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_SHADER_PLANNING") == "1";
    private static readonly object WriteLock = new();
    // Bytes on disk per cache directory, counted once and then kept current by writes,
    // so a write does not enumerate a directory that may hold tens of thousands of entries.
    private static readonly Dictionary<string, long> DirectoryBytes = new(StringComparer.OrdinalIgnoreCase);
    private static string _cachePath = RootPath;

    internal static void UseTitle(string? titleId)
    {
        if (RootPath == "0")
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(titleId)
            ? "unknown"
            : string.Concat(titleId.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        Volatile.Write(ref _cachePath, Path.Combine(RootPath, name));
    }

    internal static ScalarValueGraph Build(Gen5ShaderProgram program, uint userDataBase, uint userDataCount,
        IReadOnlySet<uint>? fixedFunctionVertexLoads, uint waveSize, string? cachePath = null)
    {
        var directory = cachePath ?? Volatile.Read(ref _cachePath);
        if (directory == "0") return ScalarValueGraph.Build(program, userDataBase, userDataCount, fixedFunctionVertexLoads, waveSize);
        var key = Key(program, userDataBase, userDataCount, fixedFunctionVertexLoads, waveSize);
        var path = Path.Combine(directory, key + ".graph");
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length is > 32 and <= MaxEntryBytes)
            {
                var digest = new byte[32];
                file.ReadExactly(digest);
                var bytes = new byte[checked((int)file.Length - 32)];
                file.ReadExactly(bytes);
                if (CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(bytes)))
                {
                    // The snapshot reader bounds its counts by the stream length, so it reads a seekable copy.
                    var snapshot = new MemoryStream();
                    using (var decompressor = new BrotliStream(new MemoryStream(bytes, false), CompressionMode.Decompress))
                    {
                        decompressor.CopyTo(snapshot);
                    }

                    snapshot.Position = 0;
                    using var reader = new BinaryReader(snapshot);
                    if (reader.ReadString() != key) throw new InvalidDataException("Graph cache key mismatch.");
                    var cached = ScalarValueGraph.ReadSnapshot(reader, program, userDataBase, userDataCount, fixedFunctionVertexLoads, waveSize);
                    if (Trace) Console.Error.WriteLine($"[SHADER_ANALYSIS_CACHE] hit key={key} bytes={bytes.Length}");
                    return cached;
                }
            }
        }
        catch (Exception error) when (IsCacheFailure(error)) { }

        var graph = ScalarValueGraph.Build(program, userDataBase, userDataCount, fixedFunctionVertexLoads, waveSize);
        try
        {
            using var stream = new MemoryStream();
            using (var compressor = new BrotliStream(stream, new BrotliCompressionOptions { Quality = CompressionQuality }, true))
            using (var writer = new BinaryWriter(compressor, Encoding.UTF8, true))
            {
                writer.Write(key);
                graph.WriteSnapshot(writer);
            }
            if (stream.Length <= MaxEntryBytes - 32)
            {
                var bytes = stream.GetBuffer().AsSpan(0, (int)stream.Length);
                lock (WriteLock)
                {
                    Directory.CreateDirectory(directory);
                    var replaced = File.Exists(path) ? new FileInfo(path).Length : 0;
                    ReserveSpace(directory, stream.Length + 32 - replaced);
                    var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            file.Write(SHA256.HashData(bytes));
                            file.Write(bytes);
                        }
                        File.Move(temporary, path, true);
                    }
                    finally { File.Delete(temporary); }
                }
                if (Trace) Console.Error.WriteLine($"[SHADER_ANALYSIS_CACHE] miss key={key} bytes={bytes.Length}");
            }
        }
        catch (Exception error) when (IsCacheFailure(error)) { }
        return graph;
    }

    internal static string Key(Gen5ShaderProgram program, uint userDataBase, uint userDataCount,
        IReadOnlySet<uint>? fixedFunctionVertexLoads, uint waveSize)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(Version);
            writer.Write(typeof(ScalarValueGraph).Module.ModuleVersionId.ToByteArray());
            writer.Write(userDataBase); writer.Write(userDataCount); writer.Write(waveSize);
            var loads = fixedFunctionVertexLoads?.Order().ToArray() ?? [];
            writer.Write(loads.Length);
            foreach (var load in loads) writer.Write(load);
            writer.Write(program.Instructions.Count);
            foreach (var instruction in program.Instructions)
            {
                writer.Write(instruction.Pc); writer.Write(instruction.ProgramOffset); writer.Write((int)instruction.Encoding); writer.Write(instruction.Opcode);
                writer.Write(instruction.Words.Count);
                foreach (var word in instruction.Words) writer.Write(word);
                writer.Write(instruction.Sources.Count);
                foreach (var operand in instruction.Sources) { writer.Write((int)operand.Kind); writer.Write(operand.Value); }
                writer.Write(instruction.Destinations.Count);
                foreach (var operand in instruction.Destinations) { writer.Write((int)operand.Kind); writer.Write(operand.Value); }
                // Controls are a closed set of decoded record types. Include the
                // runtime type and every property, including variable NSA arrays.
                writer.Write(instruction.Control?.GetType().FullName ?? "");
                writer.Write(instruction.Control is { } control ? JsonSerializer.Serialize(control, control.GetType()) : "");
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
    }

    // Keeps the directory bounded across compiler versions too: when a write would pass
    // the limit, the oldest entries go until a tenth of the limit is free again. No live
    // analysis objects or guest memory are retained by the cache.
    private static void ReserveSpace(string directory, long entryBytes)
    {
        if (!DirectoryBytes.TryGetValue(directory, out var total))
        {
            total = new DirectoryInfo(directory).GetFiles("*.graph").Sum(file => file.Length);
        }

        total += entryBytes;
        if (total > MaxCacheBytes)
        {
            var entries = new DirectoryInfo(directory).GetFiles("*.graph").OrderBy(file => file.LastWriteTimeUtc).ToArray();
            total = entries.Sum(file => file.Length) + entryBytes;
            foreach (var entry in entries)
            {
                if (total <= MaxCacheBytes - (MaxCacheBytes / 10)) break;
                total -= entry.Length;
                entry.Delete();
            }
        }

        DirectoryBytes[directory] = total;
    }

    private static bool IsCacheFailure(Exception error) => error is IOException or UnauthorizedAccessException or
        InvalidDataException or ArgumentException or OverflowException;
}
