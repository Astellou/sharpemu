// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// A formatted access whose format the dispatch binds must convert exactly as the access
// compiled for that format: every format runs both on the device over the same data.
public sealed class RuntimeBufferFormatDeviceTests(HeadlessVulkanFixture fixture, ITestOutputHelper output)
    : IClassFixture<HeadlessVulkanFixture>
{
    private const ulong ShaderAddress = 0x1000_0000;
    private const ulong BufferAddress = 0x2_0000_0000;
    private const uint Lanes = 64;
    private const uint ElementBytes = 16;
    private const uint OutputOffset = Lanes * ElementBytes;
    private const uint BufferBytes = OutputOffset * 2;
    private const uint IdentitySwizzle = 4 | (5 << 3) | (6 << 6) | (7 << 9);
    private const uint DescriptorRegister = 16;

    [Fact]
    public void RuntimeFormatLoads_MatchTheCompiledFormatOnTheDevice()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        // buffer_load_format_xyzw v[4:7] from each lane's element, then its raw dwords out.
        var program = Program(loadOpcode: 3, storeOpcode: 30, loadOffset: 0, storeOffset: OutputOffset);
        Compare(vulkan!, program, LoadInput(), nanEqual: true, "load");
    }

    [Fact]
    public void RuntimeFormatStores_MatchTheCompiledFormatOnTheDevice()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        // buffer_load_dwordx4 v[4:7] raw, then buffer_store_format_xyzw into the output half.
        var program = Program(loadOpcode: 14, storeOpcode: 7, loadOffset: 0, storeOffset: OutputOffset);
        Compare(vulkan!, program, StoreInput(), nanEqual: false, "store");
    }

    private void Compare(HeadlessVulkan vulkan, Gen5ShaderProgram program, byte[] initial, bool nanEqual, string kind)
    {
        var runtime = default(Compiled);
        var formats = 0;
        var mismatches = new List<string>();
        for (uint format = 1; format < 128; format++)
        {
            if (!Gfx10UnifiedFormat.TryDecode(format, out var dataFormat, out _) || Gfx10UnifiedFormat.ComponentCount(dataFormat) == 0)
            {
                continue;
            }

            var userData = UserData(format);
            runtime ??= Compile(program, userData, runtimeFormat: true);
            Assert.True(runtime.Request.Resources.Info.Buffers[0].RuntimeFormat);
            var compiled = Compile(program, userData, runtimeFormat: false);
            Assert.False(compiled.Request.Resources.Info.Buffers[0].RuntimeFormat);

            var expected = Run(vulkan, compiled, userData, initial);
            var actual = Run(vulkan, runtime, userData, initial);
            for (var offset = 0; offset < expected.Length; offset += sizeof(uint))
            {
                var want = BitConverter.ToUInt32(expected, offset);
                var got = BitConverter.ToUInt32(actual, offset);
                if (want == got || (nanEqual && float.IsNaN(BitConverter.UInt32BitsToSingle(want)) && float.IsNaN(BitConverter.UInt32BitsToSingle(got))))
                {
                    continue;
                }

                mismatches.Add($"{kind} format={format} byte=0x{offset:X} compiled=0x{want:X8} runtime=0x{got:X8} " +
                    $"input=0x{BitConverter.ToUInt32(initial, offset % (int)OutputOffset):X8}");
            }

            formats++;
        }

        output.WriteLine($"Verified {kind} for {formats} formats on {vulkan.DeviceName}.");
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} mismatches:{Environment.NewLine}" +
            string.Join(Environment.NewLine, mismatches.Take(20)));
    }

    private sealed record Compiled(ShaderCompileRequest Request, byte[] Spirv, uint[][] Buffers);

    private static Compiled Compile(Gen5ShaderProgram program, uint[] userData, bool runtimeFormat)
    {
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 1, 0, 20);
        plan.Info.Buffers[0].RuntimeFormat = runtimeFormat;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram.Inputs(userData),
            ref snapshot, ref specialization, out var failure), failure.ToString());
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info,
            BindingLayout.CollectUserDataRegisters(program, 0, 20), false,
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = Lanes,
            ThreadCountX = Lanes,
            ThreadCountY = 1,
            ThreadCountZ = 1,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return new Compiled(request, shader.Spirv, snapshot.Buffers);
    }

    private static byte[] Run(HeadlessVulkan vulkan, Compiled compiled, uint[] userData, byte[] initial)
    {
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, compiled.Request, compiled.Spirv);
        var records = runner.CreateBuffer(initial);
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
        {
            [DescriptorBindingKind.Buffers] = compiled.Buffers.Select(_ => records).ToArray(),
        };
        // The descriptor fields come from this dispatch's descriptor, not the compiled one.
        var descriptors = compiled.Buffers.Select(_ => userData[(int)DescriptorRegister..(int)(DescriptorRegister + 4)]).ToArray();
        harness.Run(() => runner.Dispatch(userData, bindings, 1, bufferDescriptors: descriptors));
        var result = harness.ReadBack(records.Handle, OutputOffset, OutputOffset);
        harness.AssertNoValidationMessages();
        vulkan.AssertNoValidationMessages();
        return result;
    }

    private static uint[] UserData(uint format)
    {
        var userData = new uint[20];
        userData[DescriptorRegister] = (uint)(BufferAddress & uint.MaxValue);
        userData[DescriptorRegister + 1] = (uint)(BufferAddress >> 32);
        userData[DescriptorRegister + 2] = BufferBytes;
        userData[DescriptorRegister + 3] = (format << 12) | IdentitySwizzle;
        return userData;
    }

    // v_lshlrev_b32 v2, 4, v0; a load into v[4:7] and a store from v[4:7], both offen by v2.
    private static Gen5ShaderProgram Program(uint loadOpcode, uint storeOpcode, uint loadOffset, uint storeOffset)
    {
        const uint offen = 1u << 12;
        uint[] words =
        [
            (0x1Au << 25) | (2u << 17) | (0u << 9) | 132u,
            0xE000_0000u | (loadOpcode << 18) | offen | loadOffset,
            (0x80u << 24) | ((DescriptorRegister / 4) << 16) | (4u << 8) | 2u,
            0xE000_0000u | (storeOpcode << 18) | offen | storeOffset,
            (0x80u << 24) | ((DescriptorRegister / 4) << 16) | (4u << 8) | 2u,
            0xBF81_0000,
        ];
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var code = new byte[words.Length * 4];
        Buffer.BlockCopy(words, 0, code, 0, code.Length);
        Assert.True(memory.TryWrite(ShaderAddress, code));
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(memory, Generation.Gen5), ShaderAddress,
            out var program, out var error), error);
        Assert.Equal("VLshlrevB32", program.Instructions[0].Opcode);
        return program;
    }

    // Random bits, plus the edges: all ones, the snorm minimum, mini-float specials and denormals.
    private static byte[] LoadInput()
    {
        var bytes = new byte[BufferBytes];
        var random = new System.Random(1234);
        random.NextBytes(bytes.AsSpan(0, (int)OutputOffset));
        uint[] edges = [0xFFFF_FFFF, 0x8080_8080, 0x8000_8000, 0x0000_0001, 0x7C00_7C01, 0x0001_03FF, 0xFFC0_07C0, 0x3F80_0000, 0x0000_0000];
        for (var index = 0; index < edges.Length; index++)
        {
            for (var word = 0; word < 4; word++)
                BitConverter.TryWriteBytes(bytes.AsSpan(512 + index * (int)ElementBytes + word * 4), RotateLeft(edges[index], word * 8));
        }

        Array.Fill(bytes, (byte)0xCD, (int)OutputOffset, (int)OutputOffset);
        return bytes;
    }

    // Floats across and beyond every normalized range, specials, and integers of every width.
    private static byte[] StoreInput()
    {
        var bytes = new byte[BufferBytes];
        var random = new System.Random(4321);
        float[] specials = [0f, -0f, 1f, -1f, 0.5f, -0.5f, 2f, -2f, 127.5f, -128.5f, 65535.5f, 1e10f, -1e10f,
            float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1e-30f, 6.1e-5f, 3e-6f, 65024f, 65536f];
        for (var index = 0; index < Lanes * 4; index++)
        {
            uint value = (index % 4) switch
            {
                0 => BitConverter.SingleToUInt32Bits(specials[index / 4 % specials.Length]),
                1 => BitConverter.SingleToUInt32Bits((float)(random.NextDouble() * 4 - 2)),
                2 => (uint)random.Next(),
                _ => BitConverter.SingleToUInt32Bits((float)(random.NextDouble() * 140000 - 70000)),
            };
            BitConverter.TryWriteBytes(bytes.AsSpan(index * 4), value);
        }

        Array.Fill(bytes, (byte)0xCD, (int)OutputOffset, (int)OutputOffset);
        return bytes;
    }

    private static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> ((32 - bits) & 31));
}
