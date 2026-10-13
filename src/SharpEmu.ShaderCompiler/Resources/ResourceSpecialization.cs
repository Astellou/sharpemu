// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;

namespace SharpEmu.ShaderCompiler.Resources;

public readonly record struct BufferSpecialization(uint PackedStride, uint DescriptorFormat, uint DescriptorSwizzle);

// The module-affecting layout of one bounded runtime V# table. Candidate descriptors
// themselves are appended to Buffers; this names their contiguous run and the flattened
// key mapping, so the switch body does not have to embed per-draw addresses.
public readonly record struct BufferCandidateTableSpecialization(
    uint FirstCandidate,
    uint CandidateCount,
    uint MappingOffset,
    uint SearchIterations);

public readonly record struct ImageSpecialization(
    ImageNumericClass NumericClass,
    ImageDimension Dimension,
    uint MipCount,
    uint ConversionFormat,
    uint ShaderSwizzle,
    uint IndirectRoot,
    uint IndirectMappingOffset,
    uint IndirectSearchIterations,
    bool Cube,
    int EmulatedCompareFunction = -1,
    bool MipStatistics = false);

// The module-affecting resource state of one draw. Addresses and descriptor payloads
// stay in the snapshot, so they never create a permutation.
public sealed class ResourceSpecialization : IEquatable<ResourceSpecialization>
{
    // The plan's base buffer count; Buffers beyond it are materialized candidates.
    public int BaseBufferCount { get; init; }
    public List<BufferSpecialization> Buffers { get; init; } = [];
    public List<ImageSpecialization> Images { get; init; } = [];
    public List<BufferCandidateTableSpecialization> BufferCandidateTables { get; init; } = [];

    public bool Equals(ResourceSpecialization? other) =>
        other is not null && BaseBufferCount == other.BaseBufferCount && Buffers.SequenceEqual(other.Buffers) &&
        Images.SequenceEqual(other.Images) && BufferCandidateTables.SequenceEqual(other.BufferCandidateTables);

    public override bool Equals(object? obj) => Equals(obj as ResourceSpecialization);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(BaseBufferCount);
        foreach (var buffer in Buffers)
        {
            hash.Add(buffer);
        }

        foreach (var image in Images)
        {
            hash.Add(image);
        }

        foreach (var table in BufferCandidateTables)
        {
            hash.Add(table);
        }

        return hash.ToHashCode();
    }

    public ResourceSpecialization Clone() => new()
    {
        BaseBufferCount = BaseBufferCount,
        Buffers = [.. Buffers],
        Images = [.. Images],
        BufferCandidateTables = [.. BufferCandidateTables],
    };

    // The specialization of a plan before any draw: raw buffers and the tracked image classes.
    public static ResourceSpecialization Default(ShaderResourceInfo info) => new()
    {
        BaseBufferCount = info.Buffers.Count,
        Buffers = info.Buffers.Select(_ => new BufferSpecialization(0, DescriptorConstants.InvalidFormat, DescriptorConstants.IdentityDestinationSelect)).ToList(),
        Images = info.Images.Select(image => new ImageSpecialization(
            image.NumericClass == ImageNumericClass.Unsupported ? (image.Atomic ? ImageNumericClass.Uint : ImageNumericClass.Float) : image.NumericClass,
            image.Dimension == ImageDimension.Unknown ? ImageDimension.Dim2D : image.Dimension,
            image.MipCount, image.ConversionFormat, image.ShaderSwizzle,
            image.IndirectRoot, image.IndirectMappingOffset, image.IndirectSearchIterations, image.Cube)).ToList(),
    };

    // Beyond this many open choices, only the variant taking every first option and the one
    // taking every second option are prepared.
    private const int MaxChoiceCombinations = 2;

    // The specializations a shader seen in no dispatch most likely compiles with: the
    // defaults, except an image the program uses as integers is Uint. Two things only a
    // dispatch decides are prepared both ways: the class of an image the program uses both
    // ways, or only moves and masks, and whether an arrayed access binds an array (a
    // dispatch narrows it when the descriptor holds one layer).
    public static List<ResourceSpecialization> Predict(ShaderResourceInfo info, IReadOnlyList<ImageTexelUse> uses)
    {
        var predicted = Default(info);
        var choices = new List<Func<ImageSpecialization, ImageSpecialization>>();
        var choiceImages = new List<int>();
        for (var index = 0; index < predicted.Images.Count; index++)
        {
            var image = info.Images[index];
            if (image.NumericClass == ImageNumericClass.Unsupported && !image.Atomic && index < uses.Count)
            {
                if (uses[index] == ImageTexelUse.Integer)
                {
                    predicted.Images[index] = predicted.Images[index] with { NumericClass = ImageNumericClass.Uint };
                }
                else if (uses[index] == ImageTexelUse.Ambiguous)
                {
                    choiceImages.Add(index);
                    choices.Add(specialized => specialized with { NumericClass = ImageNumericClass.Uint });
                }
            }

            if (!image.Cube && Narrowed(predicted.Images[index].Dimension) is { } narrowed)
            {
                choiceImages.Add(index);
                choices.Add(specialized => specialized with { Dimension = narrowed });
            }
        }

        if (choices.Count == 0)
        {
            return [predicted];
        }

        IEnumerable<Func<int, bool>> takeSecond = choices.Count <= MaxChoiceCombinations
            ? Enumerable.Range(0, 1 << choices.Count).Select(mask => (Func<int, bool>)(choice => (mask & (1 << choice)) != 0))
            : [_ => false, _ => true];
        return takeSecond.Select(second =>
        {
            var variant = predicted.Clone();
            for (var choice = 0; choice < choices.Count; choice++)
            {
                if (second(choice))
                {
                    variant.Images[choiceImages[choice]] = choices[choice](variant.Images[choiceImages[choice]]);
                }
            }

            return variant;
        }).ToList();
    }

    // The dimension a single-layer descriptor narrows an arrayed access to.
    private static ImageDimension? Narrowed(ImageDimension dimension) => dimension switch
    {
        ImageDimension.Dim1DArray => ImageDimension.Dim1D,
        ImageDimension.Dim2DArray => ImageDimension.Dim2D,
        ImageDimension.Dim2DMsaaArray => ImageDimension.Dim2DMsaa,
        _ => null,
    };
}

// A plan's resource tables with one draw's specialization applied, which the emitter
// compiles against. Sampler overrides name the point-filtering duplicate an access uses.
public sealed class SpecializedResourceInfo
{
    public ShaderResourceInfo Info { get; init; } = new();
    public IReadOnlyDictionary<int, uint> SamplerByMemoryIndex { get; init; } = new Dictionary<int, uint>();
}
