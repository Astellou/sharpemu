// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Pipelines;

namespace SharpEmu.Libs.Gpu.Prebuild;

// One PS5 shader cut out of an Unreal shader blob: the AGC header the title passes to
// sceAgcCreateShader and the code it points at, identified the way draws identify it.
internal readonly record struct ExtractedShader(byte Type, ulong Hash, int HeaderOffset, int HeaderSize, int CodeOffset, int CodeSize);

internal static class UnrealShaderBlob
{
    private const int AgcHeaderMinimumSize = 0x60;
    private const int AgcCodeSizeOffset = 0x44;
    private const int AgcTypeOffset = 0x5A;
    // Code lives at this address in the replay memory the identity hash reads.
    private const ulong ReplayCodeAddress = 0x1_0000;
    private static ReadOnlySpan<byte> AgcHeaderSignature => "1234\x18\0\0\0"u8;

    // Unreal prefixes the AGC header with resource tables of varying length. The
    // two 64-bit words just before the header give its block size and the code size,
    // and the code follows the header block.
    public static bool TryExtract(ReadOnlySpan<byte> blob, out ExtractedShader shader)
    {
        shader = default;
        for (var search = 0; search < blob.Length;)
        {
            var found = blob[search..].IndexOf(AgcHeaderSignature);
            if (found < 0)
            {
                return false;
            }

            var header = search + found;
            search = header + 1;
            if (header < 2 * sizeof(ulong) || header + AgcHeaderMinimumSize > blob.Length)
            {
                continue;
            }

            var headerSize = BinaryPrimitives.ReadUInt64LittleEndian(blob[(header - 16)..]);
            var codeSize = BinaryPrimitives.ReadUInt64LittleEndian(blob[(header - 8)..]);
            if (codeSize == 0 ||
                codeSize % sizeof(uint) != 0 ||
                codeSize != BinaryPrimitives.ReadUInt32LittleEndian(blob[(header + AgcCodeSizeOffset)..]) ||
                headerSize < AgcHeaderMinimumSize ||
                (ulong)header + headerSize + codeSize > (ulong)blob.Length)
            {
                continue;
            }

            var code = header + (int)headerSize;
            shader = new ExtractedShader(
                blob[header + AgcTypeOffset],
                Hash(blob[code..], (uint)codeSize),
                header,
                (int)headerSize,
                code,
                (int)codeSize);
            return true;
        }

        return false;
    }

    // The bytes after the code stay readable so a declared-hash block past it resolves.
    private static ulong Hash(ReadOnlySpan<byte> codeAndRest, uint codeSize)
    {
        var memory = new ReplayCpuMemory([new CodeRange(ReplayCodeAddress, codeAndRest.ToArray())]);
        return ShaderIdentity.Compute(memory, ReplayCodeAddress, [(ReplayCodeAddress, codeSize)], "prebuild");
    }
}
