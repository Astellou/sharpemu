// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// Regression coverage for gfx10 DS / SOP1 / SOPC opcodes that Demon's Souls
// (PPSA01341) compute shaders use. They previously decoded as unknown-ds /
// unknown-sop1 / unknown-sopc, which made Gen5ShaderScalarEvaluator.TryEvaluate
// fail and dropped the whole dispatch. Word layouts follow the RDNA2 ISA:
// DS op = word0[25:18], SOP1 op = word0[15:8], SOPC op = word0[22:16].
public sealed class Gen5Gfx10OpcodeGapTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint EndPgm = 0xBF810000;
    private const uint ComputeUserDataRegister = 0x240;
    private const uint ComputePgmRsrc2Register = 0x213;

    [Theory]
    // ds_read_b64 v[2:3], v0  — the exact word seen in the Demon's Souls trace.
    [InlineData(0xD9D80000u, 0x02000000u, "DsReadB64")]
    // ds_write2_b64 v0, v[1:2], v[3:4] offset1:16 — Demon's Souls (word 0xD9381000).
    [InlineData(0xD9381000u, 0x00030100u, "DsWrite2B64")]
    // ds_write2st64_b64
    [InlineData(0xD93C0000u, 0x00030100u, "DsWrite2St64B64")]
    // ds_write_addtid_b32 v1
    [InlineData(0xDAC00000u, 0x00000100u, "DsWriteAddtidB32")]
    // ds_read_addtid_b32 v2 offset:4
    [InlineData(0xDAC40004u, 0x02000000u, "DsReadAddtidB32")]
    // ds_bpermute_b32 v3, v0, v1
    [InlineData(0xDACC0000u, 0x03000100u, "DsBpermuteB32")]
    public void Gfx10DataShareOpcodeDecodes(uint word0, uint word1, string expected)
    {
        var instruction = DecodeSingle(word0, word1);
        Assert.Equal(Gen5ShaderEncoding.Ds, instruction.Encoding);
        Assert.Equal(expected, instruction.Opcode);
    }

    [Theory]
    // v_mul_i32_i24 v3, v0, v1  — VOP2 opcode 0x09, seen in 3 Demon's Souls shaders.
    [InlineData(0x12060300u, "VMulI32I24")]
    // v_mul_hi_i32_i24 v3, v0, v1 — VOP2 opcode 0x0A.
    [InlineData(0x14060300u, "VMulHiI32I24")]
    public void Gfx10Vop2SignedMul24Decodes(uint word, string expected)
    {
        var instruction = DecodeSingle(word);
        Assert.Equal(Gen5ShaderEncoding.Vop2, instruction.Encoding);
        Assert.Equal(expected, instruction.Opcode);
    }

    [Theory]
    // v_cvt_pk_i16_i32 v3, v0, v1  — VOP3 opcode 0x36B. Demon's Souls hit this;
    // it was mis-decoded opaque (Vop3Raw36B) and rejected at SPIR-V emit.
    [InlineData(0x36Bu, "VCvtPkI16I32")]
    // v_xor3_b32 v3, v0, v1, v2 — real VOP3 opcode 0x178 (NOT 0x36B).
    [InlineData(0x178u, "VXor3B32")]
    public void Gfx10Vop3TriadicAndPackDecode(uint opcode, string expected)
    {
        var word = (0x35u << 26) | (opcode << 16) | 3u;
        var instruction = DecodeSingle(word, 0x000A0500u);
        Assert.Equal(Gen5ShaderEncoding.Vop3, instruction.Encoding);
        Assert.Equal(expected, instruction.Opcode);
    }

    [Fact]
    public void Gfx10SFf1I32B32DecodesAtOpcode0x10()
    {
        // s_ff1_i32_b32 s5, s6 — gfx10 encodes S_FF1_I32_B32 as SOP1 opcode 0x10.
        // SOP1 prefix is the 9 bits [31:23] == 0b1_0111_1101.
        var word = 0xBE800000u | (5u << 16) | (0x10u << 8) | 6u;
        var instruction = DecodeSingle(word);
        Assert.Equal(Gen5ShaderEncoding.Sop1, instruction.Encoding);
        Assert.Equal("SFF1I32B32", instruction.Opcode);
    }

    [Theory]
    [InlineData(0x12u, "SCmpEqU64")]
    [InlineData(0x13u, "SCmpLgU64")]
    public void Gfx10ScalarU64CompareDecodes(uint opcode, string expected)
    {
        // s_cmp_*_u64 s[0:1], s[2:3] — SOPC prefix is bits [31:23] == 0b1_0111_1110.
        var word = 0xBF000000u | (opcode << 16) | (2u << 8);
        var instruction = DecodeSingle(word);
        Assert.Equal(Gen5ShaderEncoding.Sopc, instruction.Encoding);
        Assert.Equal(expected, instruction.Opcode);
    }

    [Fact]
    public void DsReadB64AndBpermuteLowerToSpirv()
    {
        // ds_read_b64 v[2:3], v0 ; ds_bpermute_b32 v3, v0, v1
        var opcodes = CompileCompute(
            0xD9D80000u, 0x02000000u,
            0xDACC0000u, 0x03000100u);

        Assert.Contains((ushort)SpirvOp.Load, opcodes);
        Assert.Contains((ushort)SpirvOp.GroupNonUniformShuffle, opcodes);
    }

    [Fact]
    public void ScalarU64CompareLowersToSpirv()
    {
        // s_cmp_eq_u64 s[0:1], s[2:3]
        var word = 0xBF000000u | (0x12u << 16) | (2u << 8);
        var opcodes = CompileCompute(word);
        Assert.Contains((ushort)SpirvOp.IEqual, opcodes);
    }

    [Fact]
    public void Vop2SignedMul24AndVXor3LowerToSpirv()
    {
        // v_mul_i32_i24 v3, v0, v1 ; v_xor3_b32 v4, v0, v1, v2
        var vXor3 = (0x35u << 26) | (0x178u << 16) | 4u;
        var opcodes = CompileCompute(
            0x12060300u,
            vXor3, 0x000A0500u);

        Assert.Contains((ushort)SpirvOp.BitFieldSExtract, opcodes); // *_i24 conditioning
        Assert.Contains((ushort)SpirvOp.BitwiseXor, opcodes);       // v_xor3_b32
    }

    [Fact]
    public void Vop3CvtPkI16I32LowersToSpirv()
    {
        // v_cvt_pk_i16_i32 v3, v0, v1 — VOP3 0x36B
        var word = (0x35u << 26) | (0x36Bu << 16) | 3u;
        var opcodes = CompileCompute(word, 0x00000500u);
        Assert.Contains((ushort)SpirvOp.ExtInst, opcodes); // SMin/SMax saturate
    }

    [Fact]
    public void DsWrite2B64AndRead2B64LowerToLdsAccess()
    {
        // ds_write2_b64 v0, v[1:2], v[3:4] offset1:2 ; ds_read2_b64 v[5:8], v0
        var opcodes = CompileCompute(
            0xD9380200u, 0x00030100u,
            0xD9DC0000u, 0x05000000u);
        Assert.Contains((ushort)SpirvOp.Store, opcodes);
        Assert.Contains((ushort)SpirvOp.Load, opcodes);
    }

    private static Gen5ShaderInstruction DecodeSingle(params uint[] words)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteProgram(memory, ShaderAddress, words);
        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                0,
                new Dictionary<uint, uint> { [ComputePgmRsrc2Register] = 0 },
                ComputeUserDataRegister,
                out var state,
                out var error),
            error);
        return state.Program.Instructions[0];
    }

    private static HashSet<ushort> CompileCompute(params uint[] words)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteProgram(memory, ShaderAddress, words);

        var shaderRegisters = new Dictionary<uint, uint>
        {
            [ComputePgmRsrc2Register] = 16u << 1,
        };
        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                0,
                shaderRegisters,
                ComputeUserDataRegister,
                out var state,
                out var error),
            error);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(ctx, state, out var evaluation, out error),
            error);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var shader,
                out error),
            error);
        return CollectOpcodes(shader.Spirv);
    }

    private static void WriteProgram(FakeCpuMemory memory, ulong address, uint[] words)
    {
        Span<byte> buffer = stackalloc byte[4];
        foreach (var word in words.Append(EndPgm))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, word);
            Assert.True(memory.TryWrite(address, buffer));
            address += sizeof(uint);
        }
    }

    private static HashSet<ushort> CollectOpcodes(byte[] spirv)
    {
        var opcodes = new HashSet<ushort>();
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset, sizeof(uint)));
            opcodes.Add((ushort)word);
            offset += Math.Max((int)(word >> 16), 1) * sizeof(uint);
        }

        return opcodes;
    }
}
