// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Prebuild;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Prebuild;

public sealed class AgcShaderHeaderTests
{
    private const int PrefixBytes = 24;
    private const int HeaderBytes = 0x100;
    private const int ShRegistersAt = 0x80;
    private const int SpecialsAt = 0xC0;
    private const uint EndProgram = 0xBF810000;

    // An Unreal shader blob: resource tables of any length, the header block and code
    // sizes, the AGC header with field-relative pointers, then the code.
    private static byte[] ComputeBlob(uint dispatchModifier)
    {
        var blob = new byte[PrefixBytes + 16 + HeaderBytes + 8];
        var header = blob.AsSpan(PrefixBytes + 16);
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(PrefixBytes), HeaderBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(PrefixBytes + 8), 8);
        "1234\x18\0\0\0"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x20..], ShRegistersAt - 0x20);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x28..], SpecialsAt - 0x28);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x44..], 8);
        header[0x5A] = 0;
        (uint Register, uint Value)[] registers = [(0x213, 5u << 1), (0x207, 8), (0x208, 4)];
        header[0x5C] = (byte)registers.Length;
        for (var index = 0; index < registers.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header[(ShRegistersAt + (index * 8))..], registers[index].Register);
            BinaryPrimitives.WriteUInt32LittleEndian(header[(ShRegistersAt + (index * 8) + 4)..], registers[index].Value);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(header[(SpecialsAt + 0x10)..], dispatchModifier);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(PrefixBytes + 16 + HeaderBytes), EndProgram);
        return blob;
    }

    [Theory]
    [InlineData(0x0001u, 64u)]
    [InlineData(0x8001u, 32u)]
    public void ComputeHeaderDrivesRegistersAndTheDispatchWaveSize(uint dispatchModifier, uint waveSize)
    {
        var blob = ComputeBlob(dispatchModifier);
        Assert.True(UnrealShaderBlob.TryExtract(blob, out var shader));
        Assert.Equal(PrefixBytes + 16, shader.HeaderOffset);
        Assert.Equal(PrefixBytes + 16 + HeaderBytes, shader.CodeOffset);
        Assert.Equal(8, shader.CodeSize);

        var header = new AgcShaderHeader(blob, shader);
        Assert.Equal(dispatchModifier, header.ComputeDispatchModifier);
        var compute = header.Registers.Shader.Compute;
        Assert.Equal(5, compute.UserScalarCount);
        Assert.Equal(8u, compute.ThreadsX);
        Assert.Equal(4u, compute.ThreadsY);
        Assert.Equal(waveSize, ShaderPrebuild.ResolveComputeInput(header, hostWave64Supported: false).WaveSize);
    }

    [Fact]
    public void HeaderWithoutAMatchingCodeSizeIsNotExtracted()
    {
        var blob = ComputeBlob(0x0001);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(PrefixBytes + 16 + 0x44), 12);
        Assert.False(UnrealShaderBlob.TryExtract(blob, out _));
    }
}
