// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class ResourceTrackerReachabilityTests
{
    // a' = a + b and b' = a ^ b, sixty-four times: each level doubles the paths to the
    // user data words. A search that finds nothing has to give up after visiting each
    // value once, not once per path.
    [Fact]
    public void ReachabilityVisitsSharedValuesOnce()
    {
        var graph = ScalarValueGraph.Build(Program(EndProgram(0)), 0, 4);
        var (left, right) = (graph.UserData(0), graph.UserData(1));
        for (var step = 0; step < 64; step++)
        {
            (left, right) = (
                graph.Operation(ScalarOperation.IAdd32, ScalarValueType.U32, left, right),
                graph.Operation(ScalarOperation.Xor32, ScalarValueType.U32, left, right));
        }

        var value = graph.Operation(ScalarOperation.IAdd32, ScalarValueType.U32, left, right);
        var search = Task.Run(() => (
            Missing: ResourceTracker.Reaches(value, static candidate => candidate.Kind == ScalarValueKind.ScalarBufferWord),
            Found: ResourceTracker.Reaches(value, static candidate => candidate.Kind == ScalarValueKind.UserData)));
        Assert.True(search.Wait(TimeSpan.FromSeconds(10)), "reachability walked every path through shared values");
        Assert.False(search.Result.Missing);
        Assert.True(search.Result.Found);
    }

    // s0 = s0 + s0, sixty-four times, then a buffer load through s[0:3]. The descriptor's
    // first word reaches the user data through 2^64 paths, as values shared across a large
    // shader do; asking whether it comes from a runtime read must visit each value once.
    [Fact]
    public void DescriptorBuiltFromHeavilySharedValuesIsTrackedPromptly()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        for (var step = 0; step < 64; step++)
        {
            instructions.Add(Sop2(pc, "SAddU32", 0, Gen5Operand.Scalar(0), Gen5Operand.Scalar(0)));
            pc += 4;
        }

        instructions.Add(BufferAccess(pc, "BufferLoadDword", 0));
        pc += 8;
        instructions.Add(EndProgram(pc));
        var program = Program([.. instructions]);

        var extraction = Task.Run(() => ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 4));
        Assert.True(extraction.Wait(TimeSpan.FromSeconds(10)), "resource tracking walked every path through shared values");
        Assert.Single(extraction.Result.Info.Buffers);
    }
}
