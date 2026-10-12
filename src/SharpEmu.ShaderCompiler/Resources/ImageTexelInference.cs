// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace SharpEmu.ShaderCompiler.Resources;

// How a program treats the texels it loads from or stores to an image.
public enum ImageTexelUse : byte
{
    // Neither loaded nor stored: sampled only, or not accessed.
    None,
    Float,
    Integer,
    // Only moved, masked or shifted, or used both ways: either class fits.
    Ambiguous,
}

// The numeric class of an image comes from its descriptor's format, which a shader seen
// in no dispatch lacks. The instructions that consume a loaded texel, or produce a stored
// one, show which class the program expects.
public static partial class ImageTexelInference
{
    // A texel's registers are followed this many instructions past the access.
    private const int SearchWindow = 256;

    private enum Evidence : byte
    {
        None,
        Float,
        Integer,
        Bits,
    }

    public static ImageTexelUse[] Infer(Gen5ShaderProgram program, IReadOnlyList<MemoryAccessInfo> entries, int imageCount)
    {
        var instructionAt = new Dictionary<uint, int>(program.Instructions.Count);
        for (var index = 0; index < program.Instructions.Count; index++)
        {
            instructionAt.TryAdd(program.Instructions[index].Pc, index);
        }

        var uses = new ImageTexelUse[imageCount];
        var floats = new int[imageCount];
        var integers = new int[imageCount];
        var bits = new int[imageCount];
        var accessed = new bool[imageCount];
        foreach (var entry in entries)
        {
            if (entry.Kind != MemoryResourceKind.Image ||
                entry.Resource >= (uint)imageCount ||
                !(entry.Opcode.StartsWith("ImageLoad", StringComparison.Ordinal) || entry.Opcode.StartsWith("ImageStore", StringComparison.Ordinal)) ||
                !instructionAt.TryGetValue(entry.Pc, out var at) ||
                program.Instructions[at].Control is not Gen5ImageControl image)
            {
                continue;
            }

            var resource = (int)entry.Resource;
            accessed[resource] = true;
            var store = entry.Opcode.StartsWith("ImageStore", StringComparison.Ordinal);
            foreach (var evidence in store ? Producers(program, at, image) : Consumers(program, at))
            {
                switch (evidence)
                {
                    case Evidence.Float: floats[resource]++; break;
                    case Evidence.Integer: integers[resource]++; break;
                    case Evidence.Bits: bits[resource]++; break;
                }
            }
        }

        for (var index = 0; index < imageCount; index++)
        {
            uses[index] = !accessed[index] ? ImageTexelUse.None
                : floats[index] != 0 && integers[index] == 0 && bits[index] == 0 ? ImageTexelUse.Float
                : integers[index] != 0 && floats[index] == 0 ? ImageTexelUse.Integer
                : ImageTexelUse.Ambiguous;
        }

        return uses;
    }

    // The instructions that read the loaded registers, through plain copies.
    private static IEnumerable<Evidence> Consumers(Gen5ShaderProgram program, int at)
    {
        var instructions = program.Instructions;
        var registers = new HashSet<uint>();
        foreach (var destination in instructions[at].Destinations)
        {
            if (destination.Kind == Gen5OperandKind.VectorRegister)
            {
                registers.Add(destination.Value);
            }
        }

        for (var index = at + 1; index < instructions.Count && index <= at + SearchWindow && registers.Count != 0; index++)
        {
            var instruction = instructions[index];
            var reads = instruction.Sources.Any(operand => operand.Kind == Gen5OperandKind.VectorRegister && registers.Contains(operand.Value));
            var copy = IsCopy(instruction.Opcode);
            if (reads && !copy)
            {
                yield return Classify(instruction.Opcode, producer: false);
            }

            // A copy of the texel carries it on; any other write replaces what it held.
            foreach (var destination in instruction.Destinations)
            {
                if (destination.Kind != Gen5OperandKind.VectorRegister)
                {
                    continue;
                }

                if (reads && copy)
                {
                    registers.Add(destination.Value);
                }
                else
                {
                    registers.Remove(destination.Value);
                }
            }
        }
    }

    // The instructions that last wrote the stored registers, through plain copies.
    private static IEnumerable<Evidence> Producers(Gen5ShaderProgram program, int at, Gen5ImageControl image)
    {
        var instructions = program.Instructions;
        var registers = new HashSet<uint>();
        var count = (uint)BitOperations.PopCount(image.Dmask == 0 ? 1u : image.Dmask);
        for (var component = 0u; component < count; component++)
        {
            registers.Add(image.VectorData + component);
        }

        for (var index = at - 1; index >= 0 && index >= at - SearchWindow && registers.Count != 0; index--)
        {
            var instruction = instructions[index];
            foreach (var destination in instruction.Destinations)
            {
                if (destination.Kind != Gen5OperandKind.VectorRegister || !registers.Remove(destination.Value))
                {
                    continue;
                }

                if (IsCopy(instruction.Opcode))
                {
                    foreach (var operand in instruction.Sources)
                    {
                        if (operand.Kind == Gen5OperandKind.VectorRegister)
                        {
                            registers.Add(operand.Value);
                        }
                    }
                }
                else
                {
                    yield return Classify(instruction.Opcode, producer: true);
                }
            }
        }
    }

    private static bool IsCopy(string opcode) => opcode is "VMovB32" or "VCndmaskB32" or "VMovrelsB32" or "VMovreldB32";

    // The operand type an opcode reads or writes: a conversion reads its last type and
    // writes its first; any other vector opcode names one type for both.
    private static Evidence Classify(string opcode, bool producer)
    {
        if (!opcode.StartsWith('V'))
        {
            return Evidence.None;
        }

        // Two halves packed into one register are floats whatever the B32 says.
        if (opcode.StartsWith("VPack", StringComparison.Ordinal) && opcode.EndsWith("F16", StringComparison.Ordinal))
        {
            return Evidence.Float;
        }

        var types = TypeToken().Matches(opcode);
        if (types.Count == 0)
        {
            return Evidence.None;
        }

        var type = opcode.StartsWith("VCvt", StringComparison.Ordinal) && !producer ? types[^1].Value : types[0].Value;
        return type[0] == 'F' ? Evidence.Float
            : BitOperation().IsMatch(opcode) ? Evidence.Bits
            : Evidence.Integer;
    }

    [GeneratedRegex("(F|U|I|B)(16|24|32|64)")]
    private static partial Regex TypeToken();

    // Raw bit manipulation, which a program also applies to float texels it unpacks.
    [GeneratedRegex("B16|B32|B64|Bfe|Bfi|Lshl|Lshr|Ashr|Perm|Alignbit|Alignbyte|Readfirstlane|Readlane")]
    private static partial Regex BitOperation();
}
