// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Prebuild;

// Prepares an extracted shader the way its first draw or dispatch would. The resource
// analysis it caches depends on the program and its user data layout only, never on
// the descriptors a draw binds, so it is exactly what the title's first use reads.
internal static class ShaderPrebuild
{
    private const byte AgcComputeType = 0;
    private const byte AgcPixelType = 1;
    private const byte AgcGeometryType = 2;
    // Graphics stages compile as wave32 (see ShaderProgramCache.CreateEntry).
    private const uint GraphicsWaveSize = 32;

    public static bool IsPrepared(byte type) => type is AgcComputeType or AgcPixelType or AgcGeometryType;

    public static bool TryPrepare(AgcShaderHeader header, IGuestGpuBackend compiler, bool hostWave64Supported, out string error)
    {
        try
        {
            return header.Shader.Type switch
            {
                AgcComputeType => TryCompileCompute(header, compiler, hostWave64Supported, out error),
                AgcPixelType => TryAnalyzeGraphics(header, ShaderStage.Pixel, out error),
                AgcGeometryType => TryAnalyzeGraphics(header, ShaderStage.Vertex, out error),
                _ => Fail($"AGC shader type {header.Shader.Type} is not prepared", out error),
            };
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or IndexOutOfRangeException or ResourcePlanException)
        {
            error = exception.Message;
            return false;
        }
    }

    // The dispatch initiator sceAgcCbDispatch builds from the modifier the header carries,
    // which decides the wave size the analysis is keyed by.
    public static ComputeInputInfo ResolveComputeInput(AgcShaderHeader header, bool hostWave64Supported) =>
        ComputeStageInputResolver.Resolve(
            header.Registers.Shader.Compute, header.Registered, Agc.AgcExports.DirectDispatchInitiator(header.ComputeDispatchModifier),
            !hostWave64Supported, 0, 0, 0);

    private static bool TryCompileCompute(AgcShaderHeader header, IGuestGpuBackend compiler, bool hostWave64Supported, out string error)
    {
        var compute = header.Registers.Shader.Compute;
        var record = new ComputePrewarmRecord
        {
            Hash = header.Shader.Hash,
            CodeSize = (uint)header.Shader.CodeSize,
            Address = AgcShaderHeader.CodeAddress,
            UserDataBase = 0,
            UserDataCount = compute.UserScalarCount,
            PushDataCursor = 0,
            Info = ResolveComputeInput(header, hostWave64Supported),
            SystemRegisters = ShaderPipelineCache.DecodeComputeSystemRegisters(compute),
            Specialization = new ResourceSpecialization(),
        };
        return ShaderProgramCache.TryCompilePrewarm(
            record, header.CodeCapture, compiler, sharedInt64Atomics: false, execGuardElision: false, signedZeroInfNanPreserve: false,
            // A prebuild only translates, so a Nanite shader is checked whatever the GPU;
            // the first dispatch refuses it on a device without 64-bit image atomics.
            out _, out _, out error, hostWave64Supported, predictSpecializations: true, imageInt64Atomics: true);
    }

    // Draws compile a graphics stage only with the descriptors and pipeline state they
    // bind, so a prebuild stops at the analysis: the program as CreateEntry rewrites it,
    // with the user data the stage's resource register declares.
    private static bool TryAnalyzeGraphics(AgcShaderHeader header, ShaderStage stage, out string error)
    {
        var shaders = header.Registers.Shader;
        uint declaredCount = stage == ShaderStage.Pixel ? shaders.Pixel.Resource2.UserScalarCount : shaders.Vertex.GeometryResource2.UserScalarCount;
        if (!Gen5ShaderTranslator.TryDecodeProgram(header.CreateContext(), AgcShaderHeader.CodeAddress, out var decoded, out error))
        {
            return false;
        }

        var userDataBase = stage == ShaderStage.Vertex ? ShaderPipelineCache.VertexUserDataBase : 0u;
        var metadata = new VertexTableMetadata();
        if (stage == ShaderStage.Vertex &&
            !VertexInputResolver.TryReadTables(header.CreateContext(), header.Registered, UserScalarRegisters.Capacity, out metadata, out error))
        {
            return false;
        }

        // A stage declaring no user data makes a draw count the user registers the title
        // wrote, one past the highest written, which differs between draws. Every count a
        // draw can reach is prepared.
        var counts = declaredCount != 0 ? [declaredCount] : Enumerable.Range(0, UserScalarRegisters.Capacity + 1).Select(count => (uint)count);
        foreach (var userDataCount in counts)
        {
            var program = decoded;
            HashSet<uint>? fixedFunctionLoads = null;
            if (stage == ShaderStage.Vertex && metadata.VertexAttributeRegister >= 0)
            {
                var fetch = EmbeddedVertexFetchDetector.Detect(
                    program,
                    (int)userDataBase + Math.Max(metadata.VertexAttributeRegister, 0),
                    (int)userDataBase + Math.Max(metadata.VertexBufferRegister, 0),
                    userDataBase,
                    userDataCount,
                    GraphicsWaveSize);
                program = fetch.RemoveReplacedTableLoads(program);
                fixedFunctionLoads = fetch.Loads.Select(load => load.Pc).ToHashSet();
            }

            _ = ShaderResourcePlan.Extract(program, stage, header.Shader.Hash, userDataBase, userDataCount, fixedFunctionLoads,
                waveSize: GraphicsWaveSize);
        }

        error = string.Empty;
        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
