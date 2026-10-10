// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5DataSharePermuteTests
{
    [Fact]
    public void DecodesAndCompilesOnBothBackends()
    {
        // DS_PERMUTE_B32 v4, v5, v4 offset:8, as Lords of the Fallen's shaders encode it.
        uint[] words = [0xDAC80000u | 8, (4u << 24) | (4u << 8) | 5, 0xBF810000];
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(new InstructionMemory(bytes), Generation.Gen5),
            0x1000, out var program, out var error), error);
        var instruction = program.Instructions[0];
        Assert.Equal("DsPermuteB32", instruction.Opcode);
        Assert.Equal(new[] { Gen5Operand.Vector(5), Gen5Operand.Vector(4) }, instruction.Sources);
        Assert.Equal(new[] { Gen5Operand.Vector(4) }, instruction.Destinations);
        Assert.Equal(8u, Assert.IsType<Gen5DataShareControl>(instruction.Control).SingleOffsetBytes);

        var request = Request(CreateReadbackProgram(maskOddLanes: true));
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var metal, out var metalError), metalError);
        Assert.Contains("sharpemu_ballot(exec)", metal.Source);
        Assert.Contains("simd_shuffle", metal.Source);
    }

    [Fact]
    public void GlobalDataSharePermuteRemainsRejected()
    {
        var instruction = DataShare(0, "DsPermuteB32", gds: true, [Gen5Operand.Vector(5), Gen5Operand.Vector(4)], [4]);
        var request = Request(Program(instruction, EndProgram(8)));
        Assert.False(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError));
        Assert.Contains("not valid on the global data share", spirvError);
        Assert.False(Gen5MslTranslator.TryCompileProgram(request, out _, out var metalError));
        Assert.Contains("not valid on the global data share", metalError);
    }

    // Each lane sends 1000 + lane to lane (lane >> 1) of its half-wave, so lanes 2k and
    // 2k + 1 collide on lane k and the upper half of each half-wave receives nothing.
    // The result is stored at lane * 4.
    public static Gen5ShaderProgram CreateReadbackProgram(bool maskOddLanes)
    {
        return Program(
            Vop2(0, "VAddI32", 4, Operand(1000), Gen5Operand.Vector(0)),
            Vop2(4, "VLshrrevB32", 5, Operand(1), Gen5Operand.Vector(0)),
            Vop2(8, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(5)),
            Vop2(12, "VAndB32", 6, Operand(maskOddLanes ? 1u : 0u), Gen5Operand.Vector(0)),
            Vopc(16, "VCmpxEqU32", Operand(0), 6),
            DataShare(20, "DsPermuteB32", false, [Gen5Operand.Vector(5), Gen5Operand.Vector(4)], [4]),
            MoveScalar(28, 126, uint.MaxValue),
            MoveScalar(32, 127, uint.MaxValue),
            Vop2(36, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)),
            BufferAccess(40, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5),
            EndProgram(48));
    }

    // The value a lane holds after the permute: from the highest active sender aimed at it,
    // 0 when none is, and its own value when the lane itself was masked off.
    public static uint Expected(uint lane, uint threadCount, bool maskOddLanes)
    {
        bool Active(uint candidate) => candidate < threadCount && !(maskOddLanes && (candidate & 1) != 0);
        if (!Active(lane))
        {
            return 1000 + lane;
        }

        var result = 0u;
        for (var sender = lane & ~31u; sender < (lane & ~31u) + 32; sender++)
        {
            if (Active(sender) && ((sender >> 1) & 31) == (lane & 31))
            {
                result = 1000 + sender;
            }
        }

        return result;
    }

    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || destination.Length > bytes.Length ||
                address - 0x1000 > (ulong)(bytes.Length - destination.Length)) return false;
            bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
