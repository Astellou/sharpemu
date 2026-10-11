// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Runtime;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Prebuild;

public sealed class ShaderPrebuildStateTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "sharpemu-prebuild-" + Guid.NewGuid().ToString("N"));
    private readonly string _titleId = "TEST" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    public ShaderPrebuildStateTests()
    {
        Directory.CreateDirectory(Path.Combine(_game, "sce_sys"));
        File.WriteAllText(Path.Combine(_game, "sce_sys", "param.json"), $"{{\"titleId\":\"{_titleId}\"}}");
        File.WriteAllBytes(Eboot, [1, 2, 3, 4]);
    }

    private string Eboot => Path.Combine(_game, "eboot.bin");

    [Fact]
    public void OnlyGamesWithIoStoreContainersArePreparable()
    {
        Assert.False(ShaderPrebuildState.HasPreparableShaders(Eboot));
        Directory.CreateDirectory(Path.Combine(_game, "Game", "Content", "Paks"));
        File.WriteAllBytes(Path.Combine(_game, "Game", "Content", "Paks", "pakchunk0-ps5.utoc"), []);
        Assert.True(ShaderPrebuildState.HasPreparableShaders(Eboot));
    }

    [Fact]
    public void StampFollowsTheTitleFolderAndTheExecutable()
    {
        if (ShaderAnalysisCache.DirectoryFor(_titleId) is null)
        {
            return; // The analysis cache is disabled in this environment.
        }

        Assert.False(ShaderPrebuildState.IsCurrent(Eboot));
        ShaderPrebuildState.MarkCurrent(Eboot);
        Assert.True(ShaderPrebuildState.IsCurrent(Eboot));
        Assert.True(File.Exists(Path.Combine(ShaderAnalysisCache.DirectoryFor(_titleId)!, "prebuild.stamp")));

        // A title update replaces the executable, which leaves the prepared analyses stale.
        File.WriteAllBytes(Eboot, [1, 2, 3, 4, 5]);
        Assert.False(ShaderPrebuildState.IsCurrent(Eboot));
    }

    public void Dispose()
    {
        Directory.Delete(_game, recursive: true);
        if (ShaderAnalysisCache.DirectoryFor(_titleId) is { } cache && Directory.Exists(cache))
        {
            Directory.Delete(cache, recursive: true);
        }
    }
}
