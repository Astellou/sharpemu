// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Gpu.Prebuild;

// Programs stored back to back after a container's own table, each its code directly
// followed by its AGC header: Bluepoint's .csdr shader bundles (Demon's Souls). The tables
// differ between versions, so the programs are found by their chain instead: the first
// code ends at its header, and every next one starts where the previous header ends.
internal static class ChainedShaderBlob
{
    public const string ContainerPattern = "*.csdr";
    private const uint EndProgram = 0xBF810000;

    public static bool HasContainers(string app0Root) =>
        Directory.EnumerateFiles(app0Root, ContainerPattern, SearchOption.AllDirectories).Any();

    public static IEnumerable<string> FindContainers(string app0Root) =>
        Directory.EnumerateFiles(app0Root, ContainerPattern, SearchOption.AllDirectories).Order(StringComparer.Ordinal);

    public static List<ExtractedShader> Extract(ReadOnlySpan<byte> blob)
    {
        var shaders = new List<ExtractedShader>();
        var previousEnd = -1;
        for (var search = 0; search < blob.Length;)
        {
            var found = blob[search..].IndexOf(UnityShaderBlob.AgcHeaderSignature);
            if (found < 0)
            {
                break;
            }

            var header = search + found;
            search = header + 1;
            if (header + UnityShaderBlob.AgcHeaderMinimumSize > blob.Length)
            {
                continue;
            }

            var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(blob[(header + UnityShaderBlob.AgcHeaderSizeOffset)..]);
            var codeSize = BinaryPrimitives.ReadUInt32LittleEndian(blob[(header + UnityShaderBlob.AgcCodeSizeOffset)..]);
            if (codeSize == 0 ||
                codeSize % sizeof(uint) != 0 ||
                codeSize > header ||
                headerSize < UnityShaderBlob.AgcHeaderMinimumSize ||
                headerSize > blob.Length - header)
            {
                continue;
            }

            var code = header - (int)codeSize;
            if ((previousEnd >= 0 && code != previousEnd) || !EndsProgram(blob[code..header]))
            {
                continue;
            }

            shaders.Add(new ExtractedShader(
                blob[header + UnityShaderBlob.AgcTypeOffset],
                ExtractedShader.Identify(blob[code..(header + (int)headerSize)], codeSize),
                header,
                (int)headerSize,
                code,
                (int)codeSize));
            previousEnd = header + (int)headerSize;
            search = previousEnd;
        }

        return shaders;
    }

    // A program's code holds its s_endpgm, followed only by its metadata.
    private static bool EndsProgram(ReadOnlySpan<byte> code)
    {
        for (var word = 0; word + sizeof(uint) <= code.Length; word += sizeof(uint))
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(code[word..]) == EndProgram)
            {
                return true;
            }
        }

        return false;
    }
}
