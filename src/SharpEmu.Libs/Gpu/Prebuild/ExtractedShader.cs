// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;

namespace SharpEmu.Libs.Gpu.Prebuild;

// One PS5 shader cut out of an engine's shader blob: the AGC header the title passes to
// sceAgcCreateShader and the code it points at, identified the way draws identify it.
internal readonly record struct ExtractedShader(byte Type, ulong Hash, int HeaderOffset, int HeaderSize, int CodeOffset, int CodeSize)
{
    // Code lives at this address in the replay memory the identity hash reads.
    private const ulong ReplayCodeAddress = 0x1_0000;

    // The bytes after the code stay readable so a declared-hash block past it resolves.
    public static ulong Identify(ReadOnlySpan<byte> codeAndRest, uint codeSize)
    {
        var memory = new ReplayCpuMemory([new CodeRange(ReplayCodeAddress, codeAndRest.ToArray())]);
        return ShaderIdentity.Compute(memory, ReplayCodeAddress, [(ReplayCodeAddress, codeSize)], "prebuild");
    }
}
