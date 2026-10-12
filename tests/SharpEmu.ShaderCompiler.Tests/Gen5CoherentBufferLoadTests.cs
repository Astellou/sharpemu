// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

// A GLC buffer load must observe atomics from other workgroups: guest work queues
// spin on such loads and never leave the loop when the host load is cached.
public sealed class Gen5CoherentBufferLoadTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    public void GlcDwordLoadsBecomeDeviceAtomicLoads(bool glc, int expectedAtomicLoads)
    {
        var load = BufferLoad(0, 4, dwords: 2);
        load = load with { Control = (Gen5BufferMemoryControl)load.Control! with { Glc = glc } };
        var (plan, resources, layout) = Prepare(Program(load, EndProgram(8)));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        Assert.Equal(expectedAtomicLoads, Count(shader.Spirv, SpirvOp.AtomicLoad));
    }

    private static int Count(byte[] code, SpirvOp op)
    {
        var count = 0;
        for (var offset = 20; offset + 4 <= code.Length;)
        {
            var word = BitConverter.ToUInt32(code, offset);
            if ((SpirvOp)(word & 0xFFFF) == op)
            {
                count++;
            }

            offset += (int)(word >> 16) * 4;
        }

        return count;
    }
}
