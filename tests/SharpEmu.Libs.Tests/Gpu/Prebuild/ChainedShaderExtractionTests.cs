// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Prebuild;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Prebuild;

public sealed class ChainedShaderExtractionTests
{
    private const int HeaderBytes = 0x100;
    private const int ShRegistersAt = 0x80;
    private const uint EndProgram = 0xBF810000;
    private const int TableBytes = 0x2C;

    // A table, then each program's code directly followed by its header.
    [Fact]
    public void ProgramsFollowTheirTableBackToBack()
    {
        var container = Container(out var first, out var second, codeBytes: [8, 16]);
        var shaders = ChainedShaderBlob.Extract(container);
        Assert.Equal(2, shaders.Count);
        Assert.Equal((first, 8), (shaders[0].CodeOffset, shaders[0].CodeSize));
        Assert.Equal((second, 16), (shaders[1].CodeOffset, shaders[1].CodeSize));
        Assert.Equal(first + 8, shaders[0].HeaderOffset);
        Assert.Equal(3, new AgcShaderHeader(container.AsSpan(0, shaders[1].HeaderOffset + HeaderBytes), shaders[1]).Registers.Shader.Compute.UserScalarCount);
    }

    // A header whose code would not start where the previous program ends is not a program.
    [Fact]
    public void AHeaderOffTheChainIsSkipped()
    {
        var container = Container(out _, out var second, codeBytes: [8, 16]);
        // The second code claims 8 more bytes, so it would start inside the first header.
        BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(second + 16 + 0x44), 24);
        Assert.Single(ChainedShaderBlob.Extract(container));
    }

    // A program with no predecessor must at least hold an s_endpgm in its code; the next
    // program then starts the chain.
    [Fact]
    public void ACodeWithoutEndProgramIsNotAProgram()
    {
        var container = Container(out var first, out var second, codeBytes: [8, 16]);
        BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(first + 4), 0);
        Assert.Equal(second, Assert.Single(ChainedShaderBlob.Extract(container)).CodeOffset);
    }

    private static byte[] Container(out int first, out int second, int[] codeBytes)
    {
        var bytes = new byte[TableBytes + codeBytes.Sum() + codeBytes.Length * HeaderBytes];
        first = TableBytes;
        second = first + codeBytes[0] + HeaderBytes;
        WriteProgram(bytes, first, codeBytes[0], userScalars: 5);
        WriteProgram(bytes, second, codeBytes[1], userScalars: 3);
        return bytes;
    }

    private static void WriteProgram(byte[] bytes, int code, int codeBytes, int userScalars)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(code + codeBytes - 4), EndProgram);
        var header = bytes.AsSpan(code + codeBytes, HeaderBytes);
        "1234\x18\0\0\0"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x20..], ShRegistersAt - 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x40..], HeaderBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x44..], (uint)codeBytes);
        header[0x5C] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(header[ShRegistersAt..], 0x213);
        BinaryPrimitives.WriteUInt32LittleEndian(header[(ShRegistersAt + 4)..], (uint)userScalars << 1);
    }
}
