// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class ImageTexelInferenceTests
{
    // A loaded texel is whatever the instruction reading it expects.
    [Theory]
    [InlineData("VMulF32", ImageTexelUse.Float)]
    [InlineData("VCmpGtU32", ImageTexelUse.Integer)]
    [InlineData("VCvtF32U32", ImageTexelUse.Integer)]
    [InlineData("VBfeU32", ImageTexelUse.Ambiguous)]
    [InlineData("VPackB32F16", ImageTexelUse.Float)]
    public void LoadsFollowTheirFirstConsumer(string consumer, ImageTexelUse expected)
    {
        var program = Program(Load(0), Use(8, consumer, source: 0, destination: 10));
        Assert.Equal(expected, Infer(program, Access(0, "ImageLoad")));
    }

    // A copy carries the texel to the register its consumer reads.
    [Fact]
    public void CopiesAreFollowed()
    {
        var program = Program(Load(0), Use(8, "VMovB32", source: 0, destination: 5), Use(12, "VAddU32", source: 5, destination: 6));
        Assert.Equal(ImageTexelUse.Integer, Infer(program, Access(0, "ImageLoad")));
    }

    // A register written again before any read no longer holds the texel.
    [Fact]
    public void AnOverwrittenRegisterEndsTheSearch()
    {
        var program = Program(Load(0), Use(8, "VMovB32", source: 7, destination: 0), Use(12, "VAddU32", source: 0, destination: 6));
        Assert.Equal(ImageTexelUse.Ambiguous, Infer(program, Access(0, "ImageLoad")));
    }

    // A stored texel is whatever the instruction writing it produced.
    [Theory]
    [InlineData("VMulLoU32", ImageTexelUse.Integer)]
    [InlineData("VCvtF32U32", ImageTexelUse.Float)]
    [InlineData("VPackB32F16", ImageTexelUse.Float)]
    [InlineData("VLshlOrB32", ImageTexelUse.Ambiguous)]
    public void StoresFollowTheirProducer(string producer, ImageTexelUse expected)
    {
        var program = Program(Use(0, producer, source: 9, destination: 0), Store(4));
        Assert.Equal(expected, Infer(program, Access(4, "ImageStore")));
    }

    [Fact]
    public void ASampledOnlyImageHasNoTexelUse()
    {
        var program = Program(Load(0, "ImageSample"), Use(8, "VCmpGtU32", source: 0, destination: 10));
        Assert.Equal(ImageTexelUse.None, Infer(program, Access(0, "ImageSample")));
    }

    [Fact]
    public void AnIntegerImageIsPredictedUint()
    {
        var predicted = ResourceSpecialization.Predict(Info(1), [ImageTexelUse.Integer]);
        Assert.Equal(ImageNumericClass.Uint, Assert.Single(predicted).Images[0].NumericClass);
    }

    [Fact]
    public void AmbiguousImagesArePredictedEveryWay()
    {
        var predicted = ResourceSpecialization.Predict(Info(3), [ImageTexelUse.Ambiguous, ImageTexelUse.Float, ImageTexelUse.Ambiguous]);
        Assert.Equal(4, predicted.Count);
        Assert.Equal(4, predicted.Select(variant => (variant.Images[0].NumericClass, variant.Images[2].NumericClass)).Distinct().Count());
        Assert.All(predicted, variant => Assert.Equal(ImageNumericClass.Float, variant.Images[1].NumericClass));
    }

    // Past two ambiguous images the combinations would multiply, so only the two
    // uniform variants are prepared.
    [Fact]
    public void ManyAmbiguousImagesArePredictedUniformly()
    {
        var predicted = ResourceSpecialization.Predict(Info(3), [ImageTexelUse.Ambiguous, ImageTexelUse.Ambiguous, ImageTexelUse.Ambiguous]);
        Assert.Equal(2, predicted.Count);
        Assert.All(predicted[0].Images, image => Assert.Equal(ImageNumericClass.Float, image.NumericClass));
        Assert.All(predicted[1].Images, image => Assert.Equal(ImageNumericClass.Uint, image.NumericClass));
    }

    // An arrayed access may meet a single-layer descriptor, which a dispatch narrows, so it
    // is prepared both ways; a cube never narrows.
    [Fact]
    public void ArrayedImagesArePredictedWithAndWithoutLayers()
    {
        var info = new ShaderResourceInfo
        {
            Images =
            [
                new ImageResource { Dimension = ImageDimension.Dim2DArray },
                new ImageResource { Dimension = ImageDimension.Dim2DArray, Cube = true },
            ],
        };
        var predicted = ResourceSpecialization.Predict(info, [ImageTexelUse.None, ImageTexelUse.None]);
        Assert.Equal([ImageDimension.Dim2DArray, ImageDimension.Dim2D], predicted.Select(variant => variant.Images[0].Dimension));
        Assert.All(predicted, variant => Assert.Equal(ImageDimension.Dim2DArray, variant.Images[1].Dimension));
    }

    // RDNA's DIM 3 names a cube, which the plan keeps for a prediction.
    [Fact]
    public void ACubeInstructionMakesACubeImage()
    {
        var program = new Gen5ShaderProgram(0x1000,
        [
            ResourceTestProgram.Image(0, "ImageSampleLz", resourceRegister: 8, samplerRegister: 16, dimension: 3),
            ResourceTestProgram.EndProgram(8),
        ]);
        var image = Assert.Single(ResourceTestProgram.Extract(program, userDataCount: 20).Info.Images);
        Assert.True(image.Cube);
        Assert.Equal(ImageDimension.Dim2DArray, image.Dimension);
    }

    private static ImageTexelUse Infer(Gen5ShaderProgram program, MemoryAccessInfo access) =>
        Assert.Single(ImageTexelInference.Infer(program, [access], 1));

    private static Gen5ShaderProgram Program(params Gen5ShaderInstruction[] instructions) =>
        new(0x1000, [.. instructions, new(0x100, Gen5ShaderEncoding.Sopp, "SEndpgm", [0u], [], [], null)]);

    private static MemoryAccessInfo Access(uint pc, string opcode) =>
        new() { Pc = pc, Opcode = opcode, Kind = MemoryResourceKind.Image, Resource = 0 };

    // Texels land in, or come from, v0..v3.
    private static Gen5ShaderInstruction Load(uint pc, string opcode = "ImageLoad") =>
        new(pc, Gen5ShaderEncoding.Mimg, opcode, [0u, 0u], [Gen5Operand.Vector(20)],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2), Gen5Operand.Vector(3)],
            new Gen5ImageControl(0xF, 20, [20], 0, 0, 0, 1, false, false, false, false, false));

    private static Gen5ShaderInstruction Store(uint pc) =>
        new(pc, Gen5ShaderEncoding.Mimg, "ImageStore", [0u, 0u], [Gen5Operand.Vector(20), Gen5Operand.Vector(0)], [],
            new Gen5ImageControl(0x1, 20, [20], 0, 0, 0, 1, false, false, false, false, false));

    private static Gen5ShaderInstruction Use(uint pc, string opcode, uint source, uint destination) =>
        new(pc, Gen5ShaderEncoding.Vop2, opcode, [0u], [Gen5Operand.Vector(source)], [Gen5Operand.Vector(destination)], null);

    private static ShaderResourceInfo Info(int images) =>
        new() { Images = Enumerable.Range(0, images).Select(_ => new ImageResource { Dimension = ImageDimension.Dim2D }).ToList() };
}
