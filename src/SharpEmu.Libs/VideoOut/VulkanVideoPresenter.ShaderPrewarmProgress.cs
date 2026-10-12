// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

// A launcher follows the shader prewarm through these lines, as it follows a prebuild.
internal static partial class VulkanVideoPresenter
{
    private const string ShaderPrewarmProgressPrefix = "[PREWARM][PROGRESS]";
    private const string ShaderPrewarmDoneLine = "[PREWARM][DONE]";
    private static readonly TimeSpan ShaderPrewarmProgressInterval = TimeSpan.FromSeconds(1);

    // Reports from its own thread until the prewarm releases the game, finished or stopped:
    // the prewarm workers keep the pool busy.
    private static void StartShaderPrewarmProgress()
    {
        new Thread(() =>
        {
            while (!Volatile.Read(ref _shaderPrewarmReleased))
            {
                WriteShaderPrewarmProgress();
                Thread.Sleep(ShaderPrewarmProgressInterval);
            }

            WriteShaderPrewarmProgress();
            Console.Error.WriteLine(ShaderPrewarmDoneLine);
        })
        {
            IsBackground = true,
            Name = "SharpEmu shader prewarm progress",
        }.Start();
    }

    private static void WriteShaderPrewarmProgress() =>
        Console.Error.WriteLine(
            $"{ShaderPrewarmProgressPrefix} {Volatile.Read(ref _shaderPrewarmProgress)} {Volatile.Read(ref _shaderPrewarmTotal)}");
}
