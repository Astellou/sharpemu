// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

// The f32->f16 narrowing uses OpFConvert only when the request pins the f16 (and f64)
// float modes; otherwise it keeps the exact integer sequence. Widening never does.
public sealed class Gen5NativeFloat16ConversionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConversionsFollowTheRequest(bool native)
    {
        var mix = new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Vop3p, "VFmaMixloF16", [0u, 0u],
            [Gen5Operand.Vector(6), Gen5Operand.Source(242), Gen5Operand.Vector(5)], [Gen5Operand.Vector(7)],
            new Gen5Vop3pControl(0, 0b100, 0, 0, false));
        var (plan, resources, layout) = Prepare(Program(mix, EndProgram(8)));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1, NativeFloat16Conversions = native };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        var instructions = Instructions(shader.Spirv);
        Assert.Equal(native ? 1 : 0, instructions.Count(instruction => instruction.Op == SpirvOp.FConvert));
        Assert.Equal(native, instructions.Any(instruction =>
            instruction.Op == SpirvOp.Capability && instruction.Operands[0] == (uint)SpirvCapability.Float16));
        var modes = instructions
            .Where(instruction => instruction.Op == SpirvOp.ExecutionMode && instruction.Operands.Length == 3)
            .Select(instruction => (instruction.Operands[1], instruction.Operands[2]))
            .ToHashSet();
        foreach (var mode in new[] { SpirvExecutionMode.RoundingModeRte, SpirvExecutionMode.DenormPreserve, SpirvExecutionMode.SignedZeroInfNanPreserve })
        foreach (var width in new[] { 16u, 64u })
        {
            Assert.Equal(native, modes.Contains(((uint)mode, width)));
        }
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void SupportNeedsFloat16AndBothPinnedWidths(bool float16, bool denormPreserve64, bool expected) =>
        Assert.Equal(expected, ShaderCompileRequest.SupportsNativeFloat16Conversions(
            float16, true, true, true, true, denormPreserve64, true, true, true));

    private static List<(SpirvOp Op, uint[] Operands)> Instructions(byte[] code)
    {
        var result = new List<(SpirvOp, uint[])>();
        for (var offset = 20; offset + 4 <= code.Length;)
        {
            var word = BitConverter.ToUInt32(code, offset);
            var count = (int)(word >> 16);
            var operands = new uint[count - 1];
            for (var index = 1; index < count; index++)
            {
                operands[index - 1] = BitConverter.ToUInt32(code, offset + index * 4);
            }

            result.Add(((SpirvOp)(word & 0xFFFF), operands));
            offset += count * 4;
        }

        return result;
    }
}
