// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// Regression tests for scalar opcodes that were absent from the decode tables,
// so every shader using them was dropped whole:
//
//   SOP1 0x09  S_WQM_B32          wave32 sibling of S_WQM_B64 (0x0A)
//   SOP1 0x14  S_FF1_I32_B64      64-bit source, single-dword destination
//   SOPC 0x12  S_CMP_EQ_U64
//   SOPC 0x13  S_CMP_LG_U64
//   SOPP 0x17  S_CBRANCH_CDBGSYS  never taken outside a hardware debugger
//
// Plus the linear decode sweep running past s_setpc_b64 into trailing data.
// Every word below is the real encoding taken from an Astro Bot trace.
public sealed class Gen5ScalarOpcodeSpirvTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;

    // SOPC: [31:23]=0b101111110, [22:16]=op, [15:8]=ssrc1, [7:0]=ssrc0.
    private const uint Sopc = 0xBF000000;

    // GLSL.std.450 FindILsb.
    private const uint GlslFindILsb = 73;

    [Fact]
    public void WqmB32_ExpandsEachQuadOfTheWave32Mask()
    {
        // s_wqm_b32 exec_lo, exec_lo — the exact word seen in the Astro Bot trace.
        var spirv = Compile([0xBEFE097Eu]);

        // The quad expansion is (v | v>>1 | v>>2 | v>>3) & 0x11111111, then *0xF.
        // The mask constant is the part that distinguishes a real WQM from any
        // other bitwise lowering, so pin it rather than just "it compiled".
        Assert.True(
            ContainsUIntConstant(spirv, 0x1111_1111u),
            "S_WQM_B32 must expand quads with the 0x11111111 lane mask");
    }

    [Fact]
    public void CmpLgU64_ComparesAFullRegisterPair()
    {
        // s_cmp_lg_u64 s[0:1], 0
        var spirv = Compile([Sopc | (0x13u << 16) | (128u << 8) | 0u]);

        Assert.True(
            ComparesSixtyFourBitOperands(spirv),
            "S_CMP_LG_U64 must compare 64-bit operands, not just the low dword");
    }

    [Fact]
    public void CmpEqU64_ComparesAFullRegisterPair()
    {
        // s_cmp_eq_u64 s[0:1], s[2:3]
        var spirv = Compile([Sopc | (0x12u << 16) | (2u << 8) | 0u]);

        Assert.True(
            ComparesSixtyFourBitOperands(spirv),
            "S_CMP_EQ_U64 must compare 64-bit operands, not just the low dword");
    }

    [Fact]
    public void Ff1I32B64_SearchesBothDwordsAndWritesASingleDword()
    {
        // s_ff1_i32_b64 vcc_hi, exec — the exact word from the Astro Bot trace.
        // The opcode name ends in B64 but the destination is one dword, so it
        // must not take the 64-bit scalar path; the search is split across the
        // two halves because GLSL.std.450 FindILsb is only dependable for 32-bit
        // operands. Two FindILsb uses is the signature of that split.
        var spirv = Compile([0xBEEB147Eu]);

        Assert.Equal(
            2,
            CountExtInst(spirv, GlslFindILsb));

        // -1 for an all-zero source has to be a materialised constant.
        Assert.True(
            ContainsUIntConstant(spirv, uint.MaxValue),
            "an all-zero source must yield -1");
    }

    [Fact]
    public void CbranchCdbgsys_DecodesAndIsNeverTaken()
    {
        // s_cbranch_cdbgsys +36 — the exact word from the Astro Bot trace. The
        // CDBG flags come from hardware-debugger trap state, so the branch is
        // never taken in retail; it must lower to a constant-false condition
        // rather than dropping the shader ("unknown-sopp op=0x17").
        var program = Decode([0xBF970024u]);

        Assert.Equal("SCbranchCdbgsys", program.Instructions[0].Opcode);

        // Translating proves the condition reached TryGetBranchCondition; an
        // unmapped conditional branch fails with "invalid conditional scalar
        // branch" instead.
        Compile([0xBF970024u]);
    }

    [Fact]
    public void SetpcB64_EndsTheProgramInsteadOfDecodingTrailingData()
    {
        // s_setpc_b64 s[6:7] is an unconditional indirect jump, so nothing falls
        // through it. Astro Bot pixel shaders end on one with a string blob
        // straight after; sweeping past it decoded that blob as instructions and
        // failed the whole shader ("unknown-vop2 op=0x00 word=0x00000048").
        // These are the real trailing words from the trace.
        var program = Decode(
        [
            0x340A0A82u, // v_lshlrev_b32 v5, 2, v5
            0xBF8CC07Fu, // s_waitcnt
            0xBE802006u, // s_setpc_b64 s[6:7]
            0x30306C73u, // "sl00" — data, not code
            0x00000048u,
            0x00000061u,
        ]);

        Assert.Equal(
            ["VLshlrevB32", "SWaitcnt", "SSetpcB64"],
            program.Instructions.Select(instruction => instruction.Opcode));
    }

    [Fact]
    public void SetpcB64_ShaderStillTranslates()
    {
        // The decode fix alone is not enough: s_setpc_b64 ends with "B64" and so
        // used to reach the 64-bit scalar ALU and fail there. It must be treated
        // as a program terminator on the emission side too.
        Compile([0xBF8CC07Fu, 0xBE802006u, 0x30306C73u, 0x00000048u]);
    }

    // True when some OpIEqual / OpINotEqual takes an operand whose defining
    // instruction produced a 64-bit integer. This is what separates a genuine
    // u64 compare from one that silently truncated to the low dword.
    private static bool ComparesSixtyFourBitOperands(byte[] spirv)
    {
        // Both a signed and an unsigned 64-bit type may be declared, so gather
        // every one of them rather than assuming which comes first.
        var longTypes = FindSixtyFourBitIntTypes(spirv);
        Assert.NotEmpty(longTypes);

        // Value-producing instructions are (opcode, resultType, resultId, ...),
        // so anything whose first word is a 64-bit type defines a 64-bit value.
        var wideValues = new HashSet<uint>();
        foreach (var (_, wordCount, offset) in EnumerateInstructions(spirv))
        {
            if (wordCount >= 3 && longTypes.Contains(ReadWord(spirv, offset + 4)))
            {
                wideValues.Add(ReadWord(spirv, offset + 8));
            }
        }

        foreach (var (op, wordCount, offset) in EnumerateInstructions(spirv))
        {
            // OpIEqual = 170, OpINotEqual = 171: (op, resultType, resultId, a, b).
            if (op is not (170 or 171) || wordCount < 5)
            {
                continue;
            }

            if (wideValues.Contains(ReadWord(spirv, offset + 12)) ||
                wideValues.Contains(ReadWord(spirv, offset + 16)))
            {
                return true;
            }
        }

        return false;
    }

    // Result ids of every OpTypeInt with width 64.
    private static HashSet<uint> FindSixtyFourBitIntTypes(byte[] spirv)
    {
        var types = new HashSet<uint>();
        foreach (var (op, wordCount, offset) in EnumerateInstructions(spirv))
        {
            // OpTypeInt = 21: (opcode, resultId, width, signedness).
            if (op == 21 && wordCount >= 4 && ReadWord(spirv, offset + 8) == 64)
            {
                types.Add(ReadWord(spirv, offset + 4));
            }
        }

        return types;
    }

    // Number of OpExtInst selecting the given GLSL.std.450 instruction.
    private static int CountExtInst(byte[] spirv, uint instruction)
    {
        var count = 0;
        foreach (var (op, wordCount, offset) in EnumerateInstructions(spirv))
        {
            // OpExtInst = 12: (op, resultType, resultId, set, instruction, ...).
            if (op == 12 && wordCount >= 5 && ReadWord(spirv, offset + 16) == instruction)
            {
                count++;
            }
        }

        return count;
    }

    private static bool ContainsUIntConstant(byte[] spirv, uint value)
    {
        foreach (var (op, wordCount, offset) in EnumerateInstructions(spirv))
        {
            // OpConstant = 43: (opcode, resultType, resultId, value...).
            // A 32-bit constant is exactly 4 words; skip the 64-bit ones.
            if (op == 43 && wordCount == 4 && ReadWord(spirv, offset + 12) == value)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(ushort Op, int WordCount, int Offset)> EnumerateInstructions(
        byte[] spirv)
    {
        // 5-word SPIR-V header, then (wordCount << 16 | opcode) packed instructions.
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var word = ReadWord(spirv, offset);
            var wordCount = (int)(word >> 16);
            if (wordCount <= 0)
            {
                yield break;
            }

            yield return ((ushort)word, wordCount, offset);
            offset += wordCount * sizeof(uint);
        }
    }

    private static uint ReadWord(byte[] spirv, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset, sizeof(uint)));

    // Decodes without requiring the words to end in s_endpgm — WriteProgram
    // appends one, which is fine because the sweep must stop before reaching it.
    private static Gen5ShaderProgram Decode(uint[] programWords)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, ShaderAddress, programWords);
        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                0,
                new Dictionary<uint, uint>
                {
                    [Gen5ShaderAtomicDecodeTests.ComputePgmRsrc2Register] = 16u << 1,
                },
                Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister,
                out var state,
                out var error),
            error);
        return state.Program;
    }

    private static byte[] Compile(uint[] programWords)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, ShaderAddress, programWords);
        var shaderRegisters = new Dictionary<uint, uint>
        {
            [Gen5ShaderAtomicDecodeTests.ComputePgmRsrc2Register] = 16u << 1,
        };

        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                0,
                shaderRegisters,
                Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister,
                out var state,
                out var error),
            error);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(ctx, state, out var evaluation, out error),
            error);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state, evaluation, 1, 1, 1, out var shader, out error),
            error);
        return shader.Spirv;
    }
}
