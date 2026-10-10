// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// The packed 16-bit integer VOP3P ops (Lords of the Fallen uses v_pk_add_u16 and
// v_pk_lshrrev_b16) on both lanes, against a per-lane reference.
public sealed class PackedInt16DeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    public static TheoryData<uint, string> Opcodes => new()
    {
        { 0x00, "VPkMadI16" },
        { 0x01, "VPkMulLoU16" },
        { 0x02, "VPkAddI16" },
        { 0x03, "VPkSubI16" },
        { 0x04, "VPkLshlrevB16" },
        { 0x05, "VPkLshrrevB16" },
        { 0x06, "VPkAshrrevI16" },
        { 0x07, "VPkMaxI16" },
        { 0x08, "VPkMinI16" },
        { 0x09, "VPkMadU16" },
        { 0x0A, "VPkAddU16" },
        { 0x0B, "VPkSubU16" },
        { 0x0C, "VPkMaxU16" },
        { 0x0D, "VPkMinU16" },
    };

    private static readonly (uint A, uint B, uint C)[] Inputs =
    [
        (0x8003_0005, 0xFFF0_7FFF, 0x0102_0304),
        (0x0011_FFFF, 0x8000_0001, 0xFFFF_8000),
        (0x7FFF_000F, 0x1234_8765, 0x0000_0001),
    ];

    [Theory]
    [MemberData(nameof(Opcodes))]
    public void PackedInt16OpMatchesTheReferenceOnBothLanes(uint opcode, string name)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;

        // v6 = op(v2, v3, v7) with op_sel_hi on all three sources, then stored to the buffer.
        var packed = Decode((0x33u << 26) | (1u << 14) | (opcode << 16) | 6u, 258u | (259u << 9) | (263u << 18) | (3u << 27));
        Assert.Equal(name, packed.Opcode);
        var program = Program(
            MoveVectorFromScalar(0, 2, 8), MoveVectorFromScalar(4, 3, 9), MoveVectorFromScalar(8, 7, 10),
            packed with { Pc = 12 },
            BufferAccess(20, "BufferStoreDword", 4, vectorData: 6), EndProgram(28));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan!);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(64);
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] };
        var registers = new uint[256];
        registers[6] = 64;
        foreach (var (a, b, c) in Inputs)
        {
            registers[8] = a;
            registers[9] = b;
            registers[10] = c;
            harness.Run(() => runner.Dispatch(registers, bindings, 1));
            var expected = Reference(name, a, b, c, 0) | (Reference(name, a, b, c, 16) << 16);
            Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(runner.ReadBack(result, 0, 4)));
        }

        harness.AssertNoValidationMessages();
    }

    // The VOP3 16-bit packing ops (Lords of the Fallen: v_pack_b32_f16). The f16 cases use
    // a = (1.0, 0.25), b = (-3.0, 2.0) as (high, low). A 16-bit integer result
    // only defines its low half, so mask selects the bits compared.
    [Theory]
    [InlineData(0x311u, "VPackB32F16", 0u, 0u, 0x4000_3400u, 0xFFFF_FFFFu, 0x3C00_3400u, 0xC200_4000u)]
    [InlineData(0x311u, "VPackB32F16", 1u, 0u, 0x4000_3C00u, 0xFFFF_FFFFu, 0x3C00_3400u, 0xC200_4000u)]
    [InlineData(0x311u, "VPackB32F16", 3u, 2u, 0x4200_3C00u, 0xFFFF_FFFFu, 0x3C00_3400u, 0xC200_4000u)]
    [InlineData(0x312u, "VCvtPknormI16F16", 0u, 0u, 0x7FFF_2000u, 0xFFFF_FFFFu, 0x3C00_3400u, 0xC200_4000u)]
    [InlineData(0x312u, "VCvtPknormI16F16", 2u, 0u, 0x8001_2000u, 0xFFFF_FFFFu, 0x3C00_3400u, 0xC200_4000u)]
    [InlineData(0x313u, "VCvtPknormU16F16", 0u, 0u, 0xFFFF_4000u, 0xFFFF_FFFFu, 0x3C00_3400u, 0xC200_4000u)]
    [InlineData(0x313u, "VCvtPknormU16F16", 2u, 0u, 0x0000_4000u, 0xFFFF_FFFFu, 0x3C00_3400u, 0xC200_4000u)]
    [InlineData(0x305u, "VMulLoU16", 0u, 0u, 0x0000_4E6Fu, 0x0000_FFFFu, 0x0000_0123u, 0x0000_0045u)]
    public void Vop3Int16PackingMatchesTheReference(uint opcode, string name, uint operandSelect, uint negate, uint expected, uint mask, uint a, uint b)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;

        // v6 = op(v2, v3); op_sel in bits 11-14 of the first word, neg in bits 29-31 of the second.
        var instruction = Decode((0x35u << 26) | (opcode << 16) | (operandSelect << 11) | 6u, 258u | (259u << 9) | (negate << 29));
        Assert.Equal(name, instruction.Opcode);
        var program = Program(
            MoveVectorFromScalar(0, 2, 8), MoveVectorFromScalar(4, 3, 9), MoveVector(8, 6, 0),
            instruction with { Pc = 12 },
            BufferAccess(20, "BufferStoreDword", 4, vectorData: 6), EndProgram(28));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan!);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(64);
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] };
        var registers = new uint[256];
        registers[6] = 64;
        registers[8] = a;
        registers[9] = b;
        harness.Run(() => runner.Dispatch(registers, bindings, 1));
        Assert.Equal(expected & mask, BinaryPrimitives.ReadUInt32LittleEndian(runner.ReadBack(result, 0, 4)) & mask);
        harness.AssertNoValidationMessages();
    }

    private static uint Reference(string name, uint a, uint b, uint c, int shift)
    {
        var x = (ushort)(a >> shift);
        var y = (ushort)(b >> shift);
        var z = (ushort)(c >> shift);
        var value = name switch
        {
            "VPkMadI16" or "VPkMadU16" => x * y + z,
            "VPkMulLoU16" => x * y,
            "VPkAddI16" or "VPkAddU16" => x + y,
            "VPkSubI16" or "VPkSubU16" => x - y,
            "VPkLshlrevB16" => y << (x & 15),
            "VPkLshrrevB16" => y >> (x & 15),
            "VPkAshrrevI16" => (short)y >> (x & 15),
            "VPkMaxI16" => Math.Max((short)x, (short)y),
            "VPkMinI16" => Math.Min((short)x, (short)y),
            "VPkMaxU16" => Math.Max(x, y),
            _ => Math.Min(x, y),
        };
        return (uint)value & 0xFFFF;
    }

    private static Gen5ShaderInstruction Decode(params uint[] instructionWords)
    {
        var bytes = new byte[(instructionWords.Length + 1) * sizeof(uint)];
        for (var index = 0; index < instructionWords.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), instructionWords[index]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(instructionWords.Length * sizeof(uint)), 0xBF810000);
        var context = new CpuContext(new InstructionMemory(bytes), Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var error), error);
        return program.Instructions[0];
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
