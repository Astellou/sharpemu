// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// f16 min/max run on the bits. Every pair of interesting halves (signed zeros, subnormals,
// the largest finite values, infinities and NaNs) must give what v_min_f16 / v_max_f16 give:
// the ordered result with -0 below +0, the other operand when one is a NaN, and the quiet
// NaN with the sign kept when both are.
public sealed class Float16MinMaxDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint Threads = 1024;

    private static readonly ushort[] Halves =
    [
        0x0000, 0x8000, 0x0001, 0x8001, 0x0002, 0x03FF, 0x83FF, 0x0400, 0x8400, 0x3BFF, 0x3C00, 0xBC00, 0x3C01,
        0xBC01, 0x4000, 0xC000, 0x7BFF, 0xFBFF, 0x7C00, 0xFC00, 0x7C01, 0xFC01, 0x7E00, 0xFE00, 0x7FFF, 0x1234,
        0x9234, 0x5555, 0xD555,
    ];

    public static TheoryData<string, uint, uint, bool> Cases => new()
    {
        { "VMinF16", 0, 0, false }, { "VMaxF16", 0, 0, false },
        { "VMinF16", 0b01, 0b10, false }, { "VMaxF16", 0b10, 0b11, false },
        { "VPkMinF16", 0, 0, true }, { "VPkMaxF16", 0, 0b11, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void MinMaxMatchesTheHardwareRules(string opcode, uint absoluteMask, uint negateMask, bool packed)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var inputs = Halves.SelectMany(left => Halves.Select(right => (uint)left | ((uint)right << 16))).ToArray();
        foreach (var native in vulkan.SupportsNativeFloat16Conversions ? new[] { false, true } : [false])
        {
            var output = Run(vulkan, opcode, absoluteMask, negateMask, packed, native, inputs);
            var isMax = opcode.Contains("Max", StringComparison.Ordinal);
            var mismatches = new List<string>();
            for (var index = 0; index < inputs.Length; index++)
            {
                var left = (ushort)inputs[index];
                var right = (ushort)(inputs[index] >> 16);
                var expected = packed
                    // Lane 0 takes (left, right), lane 1 (right, left); neg_hi negates lane 1.
                    ? Reference(left, right, 0, 0, isMax) | ((uint)Reference(right, left, 0, negateMask, isMax) << 16)
                    : Reference(left, right, absoluteMask, negateMask, isMax);
                var actual = packed ? output[index] : output[index] & 0xFFFF;
                if (actual != expected && mismatches.Count < 8)
                {
                    mismatches.Add($"native={native} left=0x{left:X4} right=0x{right:X4} expected=0x{expected:X8} actual=0x{actual:X8}");
                }
            }

            Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
        }
    }

    private static ushort Reference(ushort left, ushort right, uint absoluteMask, uint negateMask, bool isMax)
    {
        ushort Modify(ushort value, int index)
        {
            if ((absoluteMask & (1u << index)) != 0) value &= 0x7FFF;
            if ((negateMask & (1u << index)) != 0) value ^= 0x8000;
            return value;
        }

        static bool IsNan(ushort value) => (value & 0x7FFF) > 0x7C00;
        static int Key(ushort value) => (value & 0x8000) != 0 ? ~(value & 0x7FFF) : value & 0x7FFF;
        left = Modify(left, 0);
        right = Modify(right, 1);
        var result = IsNan(left) ? right : IsNan(right) ? left : (isMax ? Key(left) > Key(right) : Key(left) < Key(right)) ? left : right;
        return IsNan(result) ? (ushort)((result & 0x8000) | 0x7E00) : result;
    }

    private static uint[] Run(HeadlessVulkan vulkan, string opcode, uint absoluteMask, uint negateMask, bool packed, bool native, uint[] inputs)
    {
        // The operands are the two halves of v6: (lo, hi) for the scalar forms through op_sel, and
        // lane 0 = (lo, hi), lane 1 = (hi, lo) for the packed form.
        Gen5ShaderInstruction operation = packed
            ? new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Vop3p, opcode, [0u, 0u],
                [Gen5Operand.Vector(6), Gen5Operand.Vector(6)], [Gen5Operand.Vector(7)],
                new Gen5Vop3pControl(0b10, 0b01, 0, negateMask, false))
            : new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Vop3, opcode, [0u, 0u],
                [Gen5Operand.Vector(6), Gen5Operand.Vector(6)], [Gen5Operand.Vector(7)],
                new Gen5Vop3Control(absoluteMask, negateMask, 0, false, 0b10, null));
        var program = Program(
            Vop2(0, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)),
            BufferAccess(4, "BufferLoadDword", 4, vectorData: 6, offsetEnabled: true, vectorAddress: 5),
            MoveVector(6, 7, 0),
            operation,
            BufferAccess(20, "BufferStoreDword", 8, vectorData: 7, offsetEnabled: true, vectorAddress: 5),
            EndProgram(28));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = Threads, ThreadCountX = Threads, WaveSize = 64, NativeFloat16Conversions = native,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        var output = new uint[inputs.Length];
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        for (var start = 0; start < inputs.Length; start += (int)Threads)
        {
            var count = Math.Min((int)Threads, inputs.Length - start);
            var source = runner.CreateBuffer(MemoryMarshal.AsBytes(inputs.AsSpan(start, count)), Threads * sizeof(uint));
            var result = runner.CreateBuffer(Threads * sizeof(uint));
            var registers = new uint[16];
            registers[6] = Threads * sizeof(uint);
            registers[10] = Threads * sizeof(uint);
            harness.Run(() => runner.Dispatch(registers, new Dictionary<DescriptorBindingKind, GpuBuffer[]>
            {
                [DescriptorBindingKind.Buffers] = [source, result],
            }, 1));
            MemoryMarshal.Cast<byte, uint>(runner.ReadBack(result, 0, (ulong)count * sizeof(uint))).CopyTo(output.AsSpan(start, count));
        }

        harness.AssertNoValidationMessages();
        return output;
    }
}
