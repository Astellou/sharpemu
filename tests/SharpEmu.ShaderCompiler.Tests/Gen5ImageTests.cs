// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Vulkan;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ImageTests
{

    private const ulong ShaderAddress = 0x1_0000_C000;

    [Theory]
    [InlineData("ImageStore", SpirvOp.ImageWrite, 1)]
    [InlineData("ImageLoad", SpirvOp.ImageFetch, 3)]
    public void ArrayImageAccess_PreservesTheLayerCoordinate(string opcode, SpirvOp operation, int coordinateOperand)
    {
        var instructions = ReadSpirvInstructions(CompileImageOperation(opcode, 5,
            descriptorType: 13, arrayed: true));
        var imageType = Assert.Single(instructions, item => item.Opcode == SpirvOp.TypeImage);
        Assert.Equal(1u, imageType.Operands[4]);
        AssertCoordinateVectorWidth(instructions, operation, coordinateOperand, 3);
    }

    [Theory]
    [InlineData("ImageSampleLz", 2u, 9u, SpirvImageDim.Dim2D)]
    [InlineData("ImageStore", 2u, 9u, SpirvImageDim.Dim2D)]
    [InlineData("ImageSampleLz", 1u, 10u, SpirvImageDim.Dim3D)]
    [InlineData("ImageStore", 1u, 10u, SpirvImageDim.Dim3D)]
    public void BoundDescriptorControlsVolumeImageType(
        string opcode, uint instructionDimension, uint descriptorType, SpirvImageDim expected)
    {
        var instructions = ReadSpirvInstructions(CompileImageOperation(opcode, instructionDimension,
            descriptorType: descriptorType));
        var imageType = Assert.Single(instructions, item => item.Opcode == SpirvOp.TypeImage);
        Assert.Equal((uint)expected, imageType.Operands[2]);
        if (opcode == "ImageStore")
            AssertCoordinateVectorWidth(instructions, SpirvOp.ImageWrite, 1, expected == SpirvImageDim.Dim3D ? 3u : 2u);
    }
    private const uint SEndpgm = 0xBF810000;

    [Theory]
    [InlineData(1u, SpirvImageDim.Dim2D, 2u)]
    [InlineData(2u, SpirvImageDim.Dim3D, 3u)]
    public void ImageStoreDimensionControlsImageAndCoordinateTypes(
        uint dimension,
        SpirvImageDim expectedImageDimension,
        uint expectedCoordinateComponents)
    {
        var instructions = ReadSpirvInstructions(
            CompileImageOperation("ImageStore", dimension));
        var imageType = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.TypeImage);

        Assert.Equal((uint)expectedImageDimension, imageType.Operands[2]);
        Assert.Equal(2u, imageType.Operands[6]);
        AssertCoordinateVectorWidth(
            instructions,
            SpirvOp.ImageWrite,
            coordinateOperand: 1,
            expectedComponents: expectedCoordinateComponents);

        // Image stores outside the image bounds are discarded by the host API.
        Assert.DoesNotContain(instructions, item => item.Opcode == SpirvOp.ImageQuerySize);
    }

    [Fact]
    public void ImageSampleDim3DUsesThreeComponentSampleCoordinates()
    {
        var instructions = ReadSpirvInstructions(
            CompileImageOperation("ImageSampleLz", dimension: 2));
        var imageType = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.TypeImage);

        Assert.Equal((uint)SpirvImageDim.Dim3D, imageType.Operands[2]);
        Assert.Equal(1u, imageType.Operands[6]);
        AssertCoordinateVectorWidth(
            instructions,
            SpirvOp.ImageSampleExplicitLod,
            coordinateOperand: 3,
            expectedComponents: 3);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(5u)]
    [InlineData(6u)]
    public void ImageSampleCompareUsesNativeDepthComparison(
        uint compareFunction)
    {
        var instructions = ReadSpirvInstructions(
            CompileImageOperation(
                "ImageSampleCLz",
                dimension: 1,
                samplerWord0: compareFunction << 12,
                unifiedFormat: 22u));

        Assert.Contains(instructions, item => item.Opcode == SpirvOp.ImageSampleDrefExplicitLod);
    }

    [Theory]
    [InlineData(1u, SpirvOp.FOrdLessThan)]
    [InlineData(4u, SpirvOp.FOrdGreaterThan)]
    [InlineData(6u, SpirvOp.FOrdGreaterThanEqual)]
    public void ImageSampleCompareOnAColorFormatComparesInTheShader(
        uint compareFunction,
        SpirvOp expectedComparison)
    {
        // RGBA8 has no Vulkan depth-compare view, so the shader samples and compares.
        var instructions = ReadSpirvInstructions(
            CompileImageOperation(
                "ImageSampleCLz",
                dimension: 1,
                samplerWord0: compareFunction << 12));

        Assert.DoesNotContain(instructions, item => item.Opcode == SpirvOp.ImageSampleDrefExplicitLod);
        Assert.Contains(instructions, item => item.Opcode == SpirvOp.ImageSampleExplicitLod);
        Assert.Contains(instructions, item => item.Opcode == expectedComparison);
    }

    [Fact]
    public void ImageSampleCompareLzUsesNativeFilteringWithTheSampler()
    {
        var instructions = ReadSpirvInstructions(
            CompileImageOperation(
                "ImageSampleCLz",
                dimension: 1,
                samplerWord0: 0x00006012,
                samplerWord2: 0x00500000,
                unifiedFormat: 22u));

        Assert.Single(instructions, item => item.Opcode == SpirvOp.ImageSampleDrefExplicitLod);
        Assert.DoesNotContain(instructions, item => item.Opcode == SpirvOp.ImageFetch);
        Assert.DoesNotContain(instructions, item => item.Opcode == SpirvOp.ImageSampleExplicitLod);
    }

    [Fact]
    public void ImageGatherExplicitLodSamplesTheFootprintAtTheSelectedLevel()
    {
        // A Vulkan gather cannot take a level, so IMAGE_GATHER4_L samples its four
        // footprint texels at their centers on the level its LOD selects.
        var instructions = ReadSpirvInstructions(
            CompileImageOperation("ImageGather4L", dimension: 1, dmask: 1, unifiedFormat: 22u));

        Assert.DoesNotContain(instructions, item => item.Opcode == SpirvOp.ImageGather);
        Assert.Equal(4, instructions.Count(item => item.Opcode == SpirvOp.ImageSampleExplicitLod));
        Assert.Contains(instructions, item => item.Opcode == SpirvOp.ImageQueryLevels);
        Assert.Contains(instructions, item => item.Opcode == SpirvOp.ImageQuerySizeLod);

        var request = ImageOperationRequest("ImageGather4L", dimension: 1, dmask: 1, unifiedFormat: 22u);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var metal, out var metalError), metalError);
        Assert.DoesNotContain(".gather(", metal.Source, StringComparison.Ordinal);
        Assert.Contains("get_num_mip_levels()", metal.Source, StringComparison.Ordinal);
        Assert.Equal(4, metal.Source.Split(".sample(").Length - 1);
    }

    [Fact]
    public void ImageGatherCompareLzUsesNativeDepthGather()
    {
        var instructions = ReadSpirvInstructions(
            CompileImageOperation("ImageGather4CLz", dimension: 1, unifiedFormat: 22u));

        Assert.Contains(instructions, item => item.Opcode == SpirvOp.ImageDrefGather);
    }
    // A storage image store applies its descriptor's destination selects from the dispatch's
    // shader data, so its module is the same for every swizzle (the device tests check the
    // components it writes).
    [Theory]
    [InlineData("ImageStore")]
    [InlineData("ImageStoreMip")]
    public void ImageStoreModuleDoesNotDependOnTheDescriptorSwizzle(string opcode)
    {
        var request = ImageOperationRequest(opcode, dimension: 1, dmask: 0xF, dstSelect: 0x9F5u);
        Assert.True(Assert.Single(request.Resources.Info.Images).RuntimeSwizzle);
        Assert.NotEqual(0u, request.Bindings.ImageSwizzleCount);

        Assert.Equal(
            CompileImageOperation(opcode, dimension: 1, dmask: 0xF),
            CompileImageOperation(opcode, dimension: 1, dmask: 0xF, dstSelect: 0x9F5u));
    }

    [Fact]
    public void UintImageStoreUsesAUintTexel()
    {
        var spirv = CompileImageOperation(
            "ImageStore",
            dimension: 1,
            dmask: 0xF,
            dstSelect: 0xF2Eu,
            unifiedFormat: 69u); // FORMAT_16_16_16_16_UINT
        var instructions = ReadSpirvInstructions(spirv);
        var imageType = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.TypeImage);
        var scalarType = Assert.Single(
            instructions,
            item =>
                item.Opcode == SpirvOp.TypeInt &&
                item.Operands[0] == imageType.Operands[1]);

        Assert.Equal(0u, scalarType.Operands[2]);
    }

    private static byte[] CompileImageOperation(
        string opcode,
        uint dimension,
        uint dmask = 0xF,
        uint dstSelect = Gen5ShaderTranslator.IdentityImageDstSelect,
        uint unifiedFormat = 71u,
        uint samplerWord0 = 0u,
        uint samplerWord2 = 0u,
        uint? descriptorType = null,
        bool arrayed = false)
    {
        var request = ImageOperationRequest(opcode, dimension, dmask, dstSelect, unifiedFormat, samplerWord0, samplerWord2,
            descriptorType, arrayed);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }

    private static ShaderCompileRequest ImageOperationRequest(
        string opcode,
        uint dimension,
        uint dmask = 0xF,
        uint dstSelect = Gen5ShaderTranslator.IdentityImageDstSelect,
        uint unifiedFormat = 71u,
        uint samplerWord0 = 0u,
        uint samplerWord2 = 0u,
        uint? descriptorType = null,
        bool arrayed = false)
    {
        var coordinateCount = dimension == 2 || arrayed ? 3 : 2;
        var addressCount = coordinateCount +
            (opcode.Contains("SampleC", StringComparison.Ordinal) ? 1 : 0);
        var addressRegisters = Enumerable.Range(0, addressCount)
            .Select(static value => (uint)value)
            .ToArray();
        var control = new Gen5ImageControl(
            Dmask: dmask,
            VectorAddress: 0,
            AddressRegisters: addressRegisters,
            VectorData: 4,
            ScalarResource: 8,
            ScalarSampler: 16,
            Dimension: dimension,
            IsArray: arrayed,
            Glc: false,
            Slc: false,
            A16: false,
            D16: false);
        var imageInstruction = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Mimg,
            opcode,
            [],
            [],
            [],
            control);
        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [SEndpgm],
            [],
            [],
            null);
        var program = new Gen5ShaderProgram(ShaderAddress, [imageInstruction, end]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, ResourceTestProgram.Hash, 0, 20);
        var userData = new uint[20];
        userData[8] = 0x20;
        userData[9] = unifiedFormat << 20;
        userData[11] = ((descriptorType ?? (dimension == 2 ? 10u : 9u)) << 28) | dstSelect;
        userData[16] = samplerWord0;
        userData[18] = samplerWord2;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, ResourceTestProgram.Inputs(userData),
            ref snapshot, ref specialization));
        if (opcode.Contains("Sample", StringComparison.Ordinal))
        {
            Assert.Equal(samplerWord0, Assert.Single(snapshot.Samplers)[0]);
            Assert.Equal(samplerWord2, snapshot.Samplers[0][2]);
        }

        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info,
            BindingLayout.CollectUserDataRegisters(program, 0, 20), false,
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        return new ShaderCompileRequest(plan, resources, layout);
    }

    private static IReadOnlyList<ParsedSpirvInstruction> ReadSpirvInstructions(
        byte[] spirv)
    {
        var instructions = new List<ParsedSpirvInstruction>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset));
            var wordCount = checked((int)(instruction >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            var operands = new uint[wordCount - 1];
            for (var operand = 0; operand < operands.Length; operand++)
            {
                operands[operand] = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + (operand + 1) * sizeof(uint)));
            }

            instructions.Add(
                new ParsedSpirvInstruction((SpirvOp)(ushort)instruction, operands));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }

    private static void AssertCoordinateVectorWidth(
        IReadOnlyList<ParsedSpirvInstruction> instructions,
        SpirvOp operation,
        int coordinateOperand,
        uint expectedComponents)
    {
        var imageOperation = Assert.Single(
            instructions,
            item => item.Opcode == operation);
        var coordinateId = imageOperation.Operands[coordinateOperand];
        var coordinate = Assert.Single(
            instructions,
            item =>
                item.Opcode == SpirvOp.CompositeConstruct &&
                item.Operands.Length >= 2 &&
                item.Operands[1] == coordinateId);
        AssertVectorTypeWidth(
            instructions,
            coordinate.Operands[0],
            expectedComponents);
    }

    private static void AssertVectorTypeWidth(
        IReadOnlyList<ParsedSpirvInstruction> instructions,
        uint vectorTypeId,
        uint expectedComponents)
    {
        var vectorType = Assert.Single(
            instructions,
            item =>
                item.Opcode == SpirvOp.TypeVector &&
                item.Operands[0] == vectorTypeId);
        Assert.Equal(expectedComponents, vectorType.Operands[2]);
    }

    private readonly record struct ParsedSpirvInstruction(
        SpirvOp Opcode,
        uint[] Operands);
}
