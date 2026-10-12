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

// The f16 conversions must give System.Half's bits with and without native narrowing
// (OpFConvert under RTE / DenormPreserve 16): every f16 widened, and f32 values chosen
// around each rounding boundary, subnormal, overflow and Inf/NaN case narrowed.
public sealed class Float16ConversionDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint Threads = 1024;

    [Fact]
    public void WideningMatchesTheExactSequenceForEveryHalf()
    {
        var inputs = Enumerable.Range(0, 1 << 16).Select(static value => (uint)value).ToArray();
        Compare(inputs, narrow: false, static (exact, native) => SameFloat(exact, native, 0x7F80_0000u, 0x007F_FFFFu, 0x8000_0000u),
            static input => BitConverter.SingleToUInt32Bits((float)BitConverter.UInt16BitsToHalf((ushort)input)));
    }

    [Fact]
    public void NarrowingMatchesTheExactSequenceAroundEveryRoundingBoundary()
    {
        uint[] lowBits = [0, 1, 0x7FF, 0xFFF, 0x1000, 0x1001, 0x1FFF, 0x17FF, 0x1800, 0x0800];
        uint[] highBits = [0, 1, 0x155, 0x2AA, 0x3FE, 0x3FF];
        var inputs = new List<uint>();
        for (var exponent = 0u; exponent < 256; exponent++)
        foreach (var sign in new[] { 0u, 0x8000_0000u })
        foreach (var high in highBits)
        foreach (var low in lowBits)
        {
            inputs.Add(sign | (exponent << 23) | (high << 13) | low);
        }

        // Subnormal f16 results take a variable shift: walk the low mantissa bits there.
        var random = new System.Random(1234);
        for (var index = 0; index < 1 << 16; index++)
        {
            var exponent = (uint)random.Next(96, 144);
            inputs.Add(((uint)random.Next(2) << 31) | (exponent << 23) | ((uint)random.Next() & 0x7F_FFFF));
        }

        Compare(inputs.ToArray(), narrow: true, static (exact, native) => SameFloat(exact & 0xFFFF, native & 0xFFFF, 0x7C00u, 0x03FFu, 0x8000u),
            static input => BitConverter.HalfToUInt16Bits((Half)BitConverter.UInt32BitsToSingle(input)));
    }

    // NaN payloads are not part of the guarantee: a NaN only has to stay a NaN of the same sign.
    private static bool SameFloat(uint exact, uint native, uint exponentMask, uint mantissaMask, uint signMask)
    {
        bool IsNan(uint bits) => (bits & exponentMask) == exponentMask && (bits & mantissaMask) != 0;
        if (IsNan(exact) || IsNan(native))
        {
            return IsNan(exact) && IsNan(native) && ((exact ^ native) & signMask) == 0;
        }

        return exact == native;
    }

    private void Compare(uint[] inputs, bool narrow, Func<uint, uint, bool> equivalent, Func<uint, uint> reference)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true) || !vulkan.SupportsNativeFloat16Conversions) return;
        var exact = Run(vulkan, inputs, narrow, native: false);
        var native = Run(vulkan, inputs, narrow, native: true);
        // The exact sequence itself must agree with System.Half, so a miswired dispatch cannot pass.
        var wrong = Enumerable.Range(0, inputs.Length)
            .Where(index => !equivalent(reference(inputs[index]), exact[index]))
            .Take(8)
            .Select(index => $"in=0x{inputs[index]:X8} exact=0x{exact[index]:X8} half=0x{reference(inputs[index]):X8}")
            .ToArray();
        Assert.True(wrong.Length == 0, "exact results disagree with System.Half:" + Environment.NewLine + string.Join(Environment.NewLine, wrong));
        var mismatches = Enumerable.Range(0, inputs.Length)
            .Where(index => !equivalent(exact[index], native[index]))
            .Take(8)
            .Select(index => $"in=0x{inputs[index]:X8} exact=0x{exact[index]:X8} native=0x{native[index]:X8}")
            .ToArray();
        Assert.True(mismatches.Length == 0, string.Join(Environment.NewLine, mismatches));
    }

    private static uint[] Run(HeadlessVulkan vulkan, uint[] inputs, bool narrow, bool native)
    {
        // v_fma_mix(src, 1.0, -0.0) returns src exactly, so the result is just the conversion:
        // an f16 source is widened on the way in, an f32 result is narrowed by _MIXLO.
        var mix = new Gen5ShaderInstruction(12, Gen5ShaderEncoding.Vop3p, narrow ? "VFmaMixloF16" : "VFmaMixF32", [0u, 0u],
            [Gen5Operand.Vector(6), Gen5Operand.Source(242), Gen5Operand.Source(128)], [Gen5Operand.Vector(7)],
            new Gen5Vop3pControl(0, narrow ? 0u : 1u, 4, 0, false));
        var program = Program(
            Vop2(0, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)),
            BufferAccess(4, "BufferLoadDword", 4, vectorData: 6, offsetEnabled: true, vectorAddress: 5),
            MoveVector(8, 7, 0),
            mix,
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
        // The runner's descriptor pool holds 64 sets, so a fresh runner takes each batch of dispatches.
        const int DispatchesPerRunner = 32;
        for (var batch = 0; batch < inputs.Length; batch += DispatchesPerRunner * (int)Threads)
        {
            using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
            var end = Math.Min(inputs.Length, batch + DispatchesPerRunner * (int)Threads);
            for (var start = batch; start < end; start += (int)Threads)
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
        }

        harness.AssertNoValidationMessages();
        return output;
    }
}
