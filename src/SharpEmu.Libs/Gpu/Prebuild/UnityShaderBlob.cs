// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Gpu.Prebuild;

// Unity stores each PS5 shader program as its code followed directly by its AGC header,
// many programs to one decompressed subprogram blob. The header gives both sizes.
// Newer players prefix the pair with its total size; older ones wrap it in an ELF
// whose first section, at a fixed offset, is the code.
internal static class UnityShaderBlob
{
    private const int AgcHeaderMinimumSize = 0x60;
    private const int AgcHeaderSizeOffset = 0x40;
    private const int AgcCodeSizeOffset = 0x44;
    private const int AgcTypeOffset = 0x5A;
    private const int ElfCodeOffset = 0x100;
    private static ReadOnlySpan<byte> AgcHeaderSignature => "1234\x18\0\0\0"u8;
    private static ReadOnlySpan<byte> ElfSignature => "\u007FELF"u8;

    public static List<ExtractedShader> Extract(ReadOnlySpan<byte> blob)
    {
        var shaders = new List<ExtractedShader>();
        for (var search = 0; search < blob.Length;)
        {
            var found = blob[search..].IndexOf(AgcHeaderSignature);
            if (found < 0)
            {
                break;
            }

            var header = search + found;
            search = header + 1;
            if (header + AgcHeaderMinimumSize > blob.Length)
            {
                continue;
            }

            var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(blob[(header + AgcHeaderSizeOffset)..]);
            var codeSize = BinaryPrimitives.ReadUInt32LittleEndian(blob[(header + AgcCodeSizeOffset)..]);
            if (codeSize == 0 ||
                codeSize % sizeof(uint) != 0 ||
                codeSize > header ||
                headerSize < AgcHeaderMinimumSize ||
                headerSize > blob.Length - header)
            {
                continue;
            }

            var code = header - (int)codeSize;
            if (!IsFramed(blob, code, codeSize + headerSize))
            {
                continue;
            }

            shaders.Add(new ExtractedShader(
                blob[header + AgcTypeOffset],
                ExtractedShader.Identify(blob[code..(header + (int)headerSize)], codeSize),
                header,
                (int)headerSize,
                code,
                (int)codeSize));
            search = header + (int)headerSize;
        }

        return shaders;
    }

    private static bool IsFramed(ReadOnlySpan<byte> blob, int code, uint programSize) =>
        (code >= 2 * sizeof(uint) &&
         BinaryPrimitives.ReadUInt32LittleEndian(blob[(code - 8)..]) == programSize &&
         BinaryPrimitives.ReadUInt32LittleEndian(blob[(code - 4)..]) == 0) ||
        (code >= ElfCodeOffset && blob[(code - ElfCodeOffset)..].StartsWith(ElfSignature));
}
