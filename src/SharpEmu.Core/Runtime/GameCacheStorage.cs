// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Core.Runtime;

// The caches SharpEmu keeps per title: shader analyses with the prebuild stamp, and the
// Vulkan pipeline cache with the shader prewarm list. Clearing them makes the next launch
// prepare the title's shaders again.
public static class GameCacheStorage
{
    public static IReadOnlyList<string> Directories(string titleId, string emulatorDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(titleId);
        var directories = new List<string>(2);
        if (ShaderAnalysisCache.DirectoryFor(titleId) is { } analysis)
        {
            directories.Add(analysis);
        }

        directories.Add(VulkanPipelineCacheStorage.TitleDirectory(titleId, emulatorDirectory));
        return directories;
    }

    public static long Size(string titleId, string emulatorDirectory) =>
        Directories(titleId, emulatorDirectory).Sum(directory =>
        {
            try
            {
                return Directory.Exists(directory)
                    ? new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length)
                    : 0L;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return 0L;
            }
        });

    // Deletes every cache folder of the title, which must not be running, and returns
    // the bytes freed. A folder that cannot be deleted is reported through the exception.
    public static long Clear(string titleId, string emulatorDirectory)
    {
        var freed = Size(titleId, emulatorDirectory);
        foreach (var directory in Directories(titleId, emulatorDirectory))
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        return freed;
    }
}
