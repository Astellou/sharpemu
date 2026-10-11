// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Prebuild;

internal sealed class ExecutableShaderPrebuildReport : ShaderPrebuildReport
{
    public int ElfPrograms { get; set; }
    public int PairedPrograms { get; set; }
}

// One program found in an executable image: its code followed by its AGC header, the
// layout ExtractedShader offsets describe.
internal readonly record struct EmbeddedShader(byte[] Blob, ExtractedShader Shader, ulong HeaderAddress);

// Extracts the PS5 shaders a title compiled into its own executable, as Team Asobi's
// engine and other first-party engines do; nothing here depends on the title. Most
// remain the AGC compiler's shader ELFs, the code 0x100 bytes in and the header right
// after it. The others are headers gathered in a table with their code
// elsewhere; the title passes each to sceAgcCreateShader from a {header, code} pointer
// pair, which the loader has relocated by the time the image is read.
internal static class ExecutableShaderPrebuild
{
    private const string ManifestFileName = "manifest.tsv";
    private const uint EndProgram = 0xBF810000;
    private const int SniffChunkSize = 4 << 20;

    // Only an executable carrying an AGC shader header is worth loading for a prebuild.
    public static bool EmbedsShaders(string ebootPath)
    {
        var signature = UnityShaderBlob.AgcHeaderSignature;
        var buffer = new byte[SniffChunkSize + signature.Length];
        using var stream = File.OpenRead(ebootPath);
        var kept = 0;
        int read;
        while ((read = stream.ReadAtLeast(buffer.AsSpan(kept, SniffChunkSize), SniffChunkSize, throwOnEndOfStream: false)) > 0)
        {
            var filled = kept + read;
            if (buffer.AsSpan(0, filled).IndexOf(signature) >= 0)
            {
                return true;
            }

            // A signature split across two reads is found with the tail kept in front.
            kept = Math.Min(signature.Length - 1, filled);
            buffer.AsSpan(filled - kept, kept).CopyTo(buffer);
        }

        return false;
    }

    public static ExecutableShaderPrebuildReport Run(
        IReadOnlyList<(ulong Address, byte[] Bytes)> segments,
        string? dumpDirectory,
        TextWriter log)
    {
        var started = Stopwatch.GetTimestamp();
        var report = new ExecutableShaderPrebuildReport();
        var shaders = Extract(segments, report);
        log.WriteLine(
            $"[PREBUILD][INFO] Executable: segments={segments.Count} elf_programs={report.ElfPrograms} " +
            $"paired_programs={report.PairedPrograms} unpaired_headers={report.Unextracted}");

        StreamWriter? manifest = null;
        if (dumpDirectory is not null)
        {
            Directory.CreateDirectory(dumpDirectory);
            manifest = new StreamWriter(Path.Combine(dumpDirectory, ManifestFileName));
            manifest.WriteLine("shader\thash\ttype\tcode_size");
        }

        var work = new List<AgcShaderHeader>();
        try
        {
            foreach (var (blob, shader, headerAddress) in shaders)
            {
                report.Shaders++;
                report.AgcShaderTypes[shader.Type] = report.AgcShaderTypes.GetValueOrDefault(shader.Type) + 1;
                if (!report.UniqueHashes.Add(shader.Hash))
                {
                    continue;
                }

                if (ShaderPrebuild.IsPrepared(shader.Type))
                {
                    work.Add(new AgcShaderHeader(blob, shader));
                }

                var name = $"header_{headerAddress:X16}";
                manifest?.WriteLine($"{name}\t{shader.Hash:X16}\t{shader.Type}\t{shader.CodeSize}");
                if (dumpDirectory is not null)
                {
                    File.WriteAllBytes(Path.Combine(dumpDirectory, name + ".bin"), blob);
                }
            }
        }
        finally
        {
            manifest?.Dispose();
        }

        report.TotalShaders = work.Count;
        using (new ShaderPrebuildRunner.ProgressReporter(report, log))
        {
            ShaderPrebuildRunner.Prepare(work, report, log);
        }

        report.Elapsed = Stopwatch.GetElapsedTime(started);
        ShaderPrebuildRunner.WriteProgress(report, log);
        return report;
    }

    // Segments are the loaded image's regions by address. Headers whose code nothing
    // points at are counted as unextracted.
    public static List<EmbeddedShader> Extract(IReadOnlyList<(ulong Address, byte[] Bytes)> segments, ExecutableShaderPrebuildReport report)
    {
        var shaders = new List<EmbeddedShader>();
        var tabled = new Dictionary<ulong, (byte[] Header, byte Type, uint CodeSize)>();
        foreach (var (address, bytes) in segments)
        {
            var framed = new HashSet<int>();
            foreach (var shader in UnityShaderBlob.Extract(bytes))
            {
                framed.Add(shader.HeaderOffset);
                shaders.Add(new EmbeddedShader(
                    bytes[shader.CodeOffset..(shader.HeaderOffset + shader.HeaderSize)],
                    shader with { CodeOffset = 0, HeaderOffset = shader.CodeSize },
                    address + (ulong)shader.HeaderOffset));
            }

            foreach (var header in Headers(bytes))
            {
                if (!framed.Contains(header) &&
                    TrySizes(bytes, header, out var headerSize, out var codeSize))
                {
                    tabled[address + (ulong)header] = (
                        bytes[header..(header + (int)headerSize)],
                        bytes[header + UnityShaderBlob.AgcTypeOffset],
                        codeSize);
                }
            }
        }

        var paired = Pair(segments, tabled);
        report.ElfPrograms = shaders.Count;
        report.PairedPrograms = paired.Count;
        report.Unextracted = tabled.Count - paired.Count;
        shaders.AddRange(paired);

        return shaders;
    }

    // Every 8-byte slot holding a tabled header's address is followed by its code's.
    private static List<EmbeddedShader> Pair(
        IReadOnlyList<(ulong Address, byte[] Bytes)> segments,
        Dictionary<ulong, (byte[] Header, byte Type, uint CodeSize)> tabled)
    {
        var shaders = new List<EmbeddedShader>();
        if (tabled.Count == 0)
        {
            return shaders;
        }

        var lowest = tabled.Keys.Min();
        var highest = tabled.Keys.Max();
        var found = new HashSet<ulong>();
        foreach (var (address, bytes) in segments)
        {
            var first = (int)((sizeof(ulong) - (address % sizeof(ulong))) % sizeof(ulong));
            for (var slot = first; slot + 2 * sizeof(ulong) <= bytes.Length; slot += sizeof(ulong))
            {
                var headerAddress = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(slot));
                if (headerAddress < lowest ||
                    headerAddress > highest ||
                    found.Contains(headerAddress) ||
                    !tabled.TryGetValue(headerAddress, out var entry))
                {
                    continue;
                }

                var codeAddress = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(slot + sizeof(ulong)));
                if (!TryCode(segments, codeAddress, entry.CodeSize, out var code))
                {
                    continue;
                }

                found.Add(headerAddress);
                var blob = new byte[code.Length + entry.Header.Length];
                code.CopyTo(blob);
                entry.Header.CopyTo(blob.AsSpan(code.Length));
                shaders.Add(new EmbeddedShader(
                    blob,
                    new ExtractedShader(
                        entry.Type,
                        ExtractedShader.Identify(blob, entry.CodeSize),
                        code.Length,
                        entry.Header.Length,
                        0,
                        code.Length),
                    headerAddress));
            }
        }

        return shaders;
    }

    // A code pointer is trusted once the program it names ends inside its code size.
    private static bool TryCode(
        IReadOnlyList<(ulong Address, byte[] Bytes)> segments,
        ulong codeAddress,
        uint codeSize,
        out ReadOnlySpan<byte> code)
    {
        foreach (var (address, bytes) in segments)
        {
            if (codeAddress < address ||
                codeAddress % sizeof(uint) != 0 ||
                codeSize > bytes.Length ||
                codeAddress - address > (ulong)(bytes.Length - codeSize))
            {
                continue;
            }

            code = bytes.AsSpan((int)(codeAddress - address), (int)codeSize);
            for (var word = 0; word < code.Length; word += sizeof(uint))
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(code[word..]) == EndProgram)
                {
                    return true;
                }
            }

            break;
        }

        code = default;
        return false;
    }

    private static IEnumerable<int> Headers(byte[] bytes)
    {
        for (var search = 0; search < bytes.Length;)
        {
            var found = bytes.AsSpan(search).IndexOf(UnityShaderBlob.AgcHeaderSignature);
            if (found < 0)
            {
                yield break;
            }

            yield return search + found;
            search += found + 1;
        }
    }

    private static bool TrySizes(byte[] bytes, int header, out uint headerSize, out uint codeSize)
    {
        headerSize = codeSize = 0;
        if (header + UnityShaderBlob.AgcHeaderMinimumSize > bytes.Length)
        {
            return false;
        }

        headerSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(header + UnityShaderBlob.AgcHeaderSizeOffset));
        codeSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(header + UnityShaderBlob.AgcCodeSizeOffset));
        return headerSize >= UnityShaderBlob.AgcHeaderMinimumSize &&
            headerSize <= bytes.Length - header &&
            codeSize != 0 &&
            codeSize % sizeof(uint) == 0;
    }
}
