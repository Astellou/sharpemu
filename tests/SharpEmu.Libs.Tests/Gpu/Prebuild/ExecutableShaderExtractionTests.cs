// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Prebuild;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Prebuild;

public sealed class ExecutableShaderExtractionTests
{
    private const int HeaderBytes = 0x100;
    private const int ShRegistersAt = 0x80;
    private const int CodeBytes = 8;
    private const uint EndProgram = 0xBF810000;
    private const ulong RodataAddress = 0x1000;
    private const ulong DataAddress = 0x8000;

    // A shader ELF keeps the code 0x100 bytes in, the header right after it.
    [Fact]
    public void ShaderElfsAreReadInPlace()
    {
        var rodata = new byte[0x400];
        "\u007FELF"u8.CopyTo(rodata.AsSpan(0x40));
        WriteProgram(rodata, code: 0x140, header: 0x140 + CodeBytes, userScalars: 3);

        var report = new ExecutableShaderPrebuildReport();
        var shader = Assert.Single(ExecutableShaderPrebuild.Extract([(RodataAddress, rodata)], report));
        Assert.Equal(1, report.ElfPrograms);
        Assert.Equal(RodataAddress + 0x148, shader.HeaderAddress);
        Assert.Equal(3, new AgcShaderHeader(shader.Blob, shader.Shader).Registers.Shader.Compute.UserScalarCount);
    }

    // A tabled header's code is wherever the pointer after a slot naming the header leads.
    [Fact]
    public void TabledHeadersFindTheirCodeThroughAPointerPair()
    {
        var data = new byte[0x800];
        WriteProgram(data, code: 0x400, header: 0x100, userScalars: 7);
        WriteProgram(data, code: 0x500, header: 0x200, userScalars: 2);
        var pairs = new byte[0x40];
        BinaryPrimitives.WriteUInt64LittleEndian(pairs.AsSpan(0x10), DataAddress + 0x100);
        BinaryPrimitives.WriteUInt64LittleEndian(pairs.AsSpan(0x18), DataAddress + 0x400);

        var report = new ExecutableShaderPrebuildReport();
        var shader = Assert.Single(ExecutableShaderPrebuild.Extract([(RodataAddress, pairs), (DataAddress, data)], report));
        Assert.Equal(1, report.PairedPrograms);
        Assert.Equal(1, report.Unextracted);
        Assert.Equal(DataAddress + 0x100, shader.HeaderAddress);
        var header = new AgcShaderHeader(shader.Blob, shader.Shader);
        Assert.Equal(7, header.Registers.Shader.Compute.UserScalarCount);
        Assert.Equal(EndProgram, BinaryPrimitives.ReadUInt32LittleEndian(header.Code));
        Assert.Equal(ExtractedShader.Identify(data.AsSpan(0x400, CodeBytes), CodeBytes), shader.Shader.Hash);
    }

    // A pointer after a header's address that leads to no program is not its code.
    [Fact]
    public void APointerToSomethingElseIsNotCode()
    {
        var data = new byte[0x800];
        WriteProgram(data, code: 0x400, header: 0x100, userScalars: 7);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x400), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x700), DataAddress + 0x100);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x708), DataAddress + 0x400);

        var report = new ExecutableShaderPrebuildReport();
        Assert.Empty(ExecutableShaderPrebuild.Extract([(DataAddress, data)], report));
        Assert.Equal(1, report.Unextracted);
    }

    private static void WriteProgram(byte[] bytes, int code, int header, int userScalars)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(code), EndProgram);
        var target = bytes.AsSpan(header, HeaderBytes);
        "1234\x18\0\0\0"u8.CopyTo(target);
        BinaryPrimitives.WriteUInt64LittleEndian(target[0x20..], ShRegistersAt - 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x40..], HeaderBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(target[0x44..], CodeBytes);
        target[0x5C] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(target[ShRegistersAt..], 0x213);
        BinaryPrimitives.WriteUInt32LittleEndian(target[(ShRegistersAt + 4)..], (uint)userScalars << 1);
    }
}
