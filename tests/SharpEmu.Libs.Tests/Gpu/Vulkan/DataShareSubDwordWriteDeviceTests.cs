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

// DS_WRITE_B8 and DS_WRITE_B16 (Lords of the Fallen) replace only their byte or half of
// the LDS word they address and keep the other bits.
public sealed class DataShareSubDwordWriteDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Fact]
    public void SubDwordWritesKeepTheRestOfTheWord()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;

        // ds_write_b16 v3, v10 offset:6 and ds_write_b8 v3, v11 offset:5, as the game encodes them.
        var writeHalf = Decode(0xD87C0006, 3u | (10u << 8));
        var writeByte = Decode(0xD8780005, 3u | (11u << 8));
        Assert.Equal("DsWriteB16", writeHalf.Opcode);
        Assert.Equal("DsWriteB8", writeByte.Opcode);
        var program = Program(
            MoveVector(0, 3, 0), MoveVector(4, 30, 0x1122_3344),
            DataShare(8, "DsWriteB32", false, [Gen5Operand.Vector(3), Gen5Operand.Vector(30)], [], 4),
            MoveVector(16, 10, 0xAAAA_BEEF), MoveVector(20, 11, 0x0000_0155),
            writeHalf with { Pc = 24 }, writeByte with { Pc = 32 },
            DataShare(40, "DsReadB32", false, [Gen5Operand.Vector(3)], [4], 4),
            BufferAccess(48, "BufferStoreDword", 4, vectorData: 4), EndProgram(56));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1, LocalDataShareDwords = 64 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan!);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(64);
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] };
        var registers = new uint[256];
        registers[6] = 64;
        harness.Run(() => runner.Dispatch(registers, bindings, 1));

        // 0x11223344, then 0xBEEF into bytes 2-3, then 0x55 into byte 1.
        Assert.Equal(0xBEEF_5544u, BinaryPrimitives.ReadUInt32LittleEndian(runner.ReadBack(result, 0, 4)));
        harness.AssertNoValidationMessages();
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
