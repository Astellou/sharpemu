// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;

namespace SharpEmu.Libs.Gpu.Prebuild;

// An extracted shader laid out in replay memory the way sceAgcCreateShader leaves it:
// header pointer fields relocated to absolute addresses and the code pointer patched.
internal sealed class AgcShaderHeader
{
    // Header and code live at these addresses in the replay memory the decoders read.
    public const ulong HeaderAddress = 0x20_0000_0000;
    public const ulong CodeAddress = 0x10_0000_0000;

    private const int UserDataField = 0x08;
    private const int CodeField = 0x10;
    private const int ShRegistersField = 0x20;
    private const int SpecialsField = 0x28;
    // A compute shader's specials hold the modifier the title passes to its dispatches,
    // the DISPATCH_INITIATOR bits it controls such as CS_W32_EN.
    private const int ComputeDispatchModifierOffset = 0x10;
    private const int InputSemanticsField = 0x30;
    private const int InputSemanticsCountOffset = 0x50;
    private const int ScratchDwordsOffset = 0x54;
    private const int ShRegisterCountOffset = 0x5C;
    private const int RegisterEntrySize = 8;
    private static readonly int[] HeaderPointerFields = [0x08, 0x18, 0x20, 0x28, 0x30, 0x38];
    private static readonly int[] UserDataPointerFields = [0x00, 0x08, 0x10, 0x18, 0x20];

    private readonly byte[] _header;

    public AgcShaderHeader(ReadOnlySpan<byte> blob, ExtractedShader shader)
    {
        Shader = shader;
        _header = blob[shader.HeaderOffset..shader.CodeOffset].ToArray();
        foreach (var field in HeaderPointerFields)
        {
            Relocate(field);
        }

        if (Pointer(UserDataField) is not 0 and var userData)
        {
            foreach (var field in UserDataPointerFields)
            {
                Relocate((int)(userData - HeaderAddress) + field);
            }
        }

        BinaryPrimitives.WriteUInt64LittleEndian(_header.AsSpan(CodeField), CodeAddress);
        Code = blob[shader.CodeOffset..].ToArray();
        Registers = new RegisterBanks(message => new InvalidDataException(message));
        var table = Pointer(ShRegistersField);
        for (var i = 0; i < _header[ShRegisterCountOffset]; i++)
        {
            var entry = _header.AsSpan((int)(table - HeaderAddress) + (i * RegisterEntrySize));
            var register = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            if (register < RegisterWriteTable.ShaderIndirect.Length && RegisterWriteTable.ShaderIndirect[register] is { } writer)
            {
                writer(Registers, register, BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]));
            }
        }
    }

    public ExtractedShader Shader { get; }

    // The code and everything after it, so a declared-hash block past the code resolves.
    public byte[] Code { get; }

    // The SH registers the header programs, decoded by the same writers draws use.
    public RegisterBanks Registers { get; }

    public uint ComputeDispatchModifier => Pointer(SpecialsField) is not 0 and var specials
        ? BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan((int)(specials - HeaderAddress) + ComputeDispatchModifierOffset))
        : 0;

    public RegisteredShader Registered => new(
        CodeAddress,
        HeaderAddress,
        (uint)Shader.CodeSize,
        BinaryPrimitives.ReadUInt16LittleEndian(_header.AsSpan(ScratchDwordsOffset)),
        Pointer(UserDataField),
        Pointer(InputSemanticsField),
        BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(InputSemanticsCountOffset)),
        0,
        0);

    public ShaderCodeCapture CodeCapture => new()
    {
        Hash = Shader.Hash,
        CodeSize = (uint)Shader.CodeSize,
        Address = CodeAddress,
        Generation = Generation.Gen5,
        Ranges = [new CodeRange(CodeAddress, Code)],
    };

    public CpuContext CreateContext() =>
        new(new ReplayCpuMemory([new CodeRange(CodeAddress, Code), new CodeRange(HeaderAddress, _header)]), Generation.Gen5);

    private ulong Pointer(int field) => BinaryPrimitives.ReadUInt64LittleEndian(_header.AsSpan(field));

    // A header pointer is stored relative to its own field; zero stays null.
    private void Relocate(int field)
    {
        var relative = BinaryPrimitives.ReadUInt64LittleEndian(_header.AsSpan(field));
        if (relative != 0)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_header.AsSpan(field), HeaderAddress + (ulong)field + relative);
        }
    }
}
