// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class DataSharePermuteDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Fact]
    public void PermuteSendsToTargetsAndHighestSenderWins()
    {
        foreach (var waveSize in new[] { 32u, 64u })
        foreach (var maskOddLanes in new[] { false, true })
            CheckPermute(waveSize, waveSize, maskOddLanes);
        CheckPermute(32, 17, false);
    }

    private void CheckPermute(uint waveSize, uint threadCount, bool maskOddLanes)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Gen5DataSharePermuteTests.CreateReadbackProgram(maskOddLanes);
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = threadCount, ThreadCountX = threadCount, WaveSize = waveSize,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(256);
        var registers = new uint[256];
        registers[6] = 256;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var actual = runner.ReadBack(result, 0, 256);
        for (var lane = 0u; lane < threadCount; lane++)
        {
            Assert.Equal(Gen5DataSharePermuteTests.Expected(lane, threadCount, maskOddLanes),
                BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan((int)lane * sizeof(uint))));
        }

        harness.AssertNoValidationMessages();
    }
}
