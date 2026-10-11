// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.Libs.Gpu.Prebuild;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Core.Runtime;

// Whether a title's shaders were prepared ahead of its launch with the analysis build
// that will read them. The launcher and --prebuild-shaders share it so both resolve the
// same title folder and the same stamp.
public static class ShaderPrebuildState
{
    // Unset or anything but "0" prepares a title's shaders before it starts.
    public const string VariableName = "SHARPEMU_SHADER_PREBUILD";
    private const string StampFileName = "prebuild.stamp";

    // Unreal Engine titles cook their shaders into IoStore containers and Unity titles
    // serialize them into the asset files under Media; those are prepared ahead.
    public static bool HasPreparableShaders(string ebootPath)
    {
        try
        {
            var root = Path.GetDirectoryName(Path.GetFullPath(ebootPath));
            return root is not null && (UnityShaderPrebuild.IsUnityTitle(root) || Directory.EnumerateFiles(root, "*.utoc", SearchOption.AllDirectories).Any());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsCurrent(string ebootPath)
    {
        try
        {
            return StampPath(ebootPath) is { } path && File.Exists(path) && File.ReadAllText(path) == Stamp(ebootPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static void MarkCurrent(string ebootPath)
    {
        try
        {
            if (StampPath(ebootPath) is { } path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, Stamp(ebootPath));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[PREBUILD][WARN] The prebuild stamp cannot be written: {exception.Message}");
        }
    }

    // The analysis key version and the executable it was prepared from: a new SharpEmu
    // build or a title update makes the prepared analyses stale.
    private static string Stamp(string ebootPath)
    {
        var eboot = new FileInfo(Path.GetFullPath(ebootPath));
        return $"{ShaderAnalysisCache.KeyVersion}|{eboot.Length}|{eboot.LastWriteTimeUtc.Ticks}";
    }

    // The title id the runtime selects its folder by, from the same param.json the loader reads.
    internal static string? TitleId(string ebootPath)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(ebootPath));
        var paramJson = root is null ? null : Path.Combine(root, "sce_sys", "param.json");
        return paramJson is not null && File.Exists(paramJson)
            ? Ps5ParamJsonReader.TryReadPs5Param(File.ReadAllBytes(paramJson)).TitleId
            : null;
    }

    private static string? StampPath(string ebootPath) =>
        ShaderAnalysisCache.DirectoryFor(TitleId(ebootPath)) is { } directory ? Path.Combine(directory, StampFileName) : null;
}
