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
    public void UnrealGamesWithIoStoreContainersArePreparable()
    {
        Assert.False(ShaderPrebuildState.HasPreparableShaders(Eboot));
        Directory.CreateDirectory(Path.Combine(_game, "Game", "Content", "Paks"));
        File.WriteAllBytes(Path.Combine(_game, "Game", "Content", "Paks", "pakchunk0-ps5.utoc"), []);
        Assert.True(ShaderPrebuildState.HasPreparableShaders(Eboot));
    }

    [Fact]
    public void UnityPlayersArePreparable()
    {
        Assert.False(ShaderPrebuildState.HasPreparableShaders(Eboot));
        Directory.CreateDirectory(Path.Combine(_game, "Media"));
        File.WriteAllBytes(Path.Combine(_game, "Media", "globalgamemanagers"), []);
        Assert.True(ShaderPrebuildState.HasPreparableShaders(Eboot));
    }

    // The header signature straddles two sniffed chunks.
    [Fact]
    public void ExecutablesWithEmbeddedShadersArePreparable()
    {
        Assert.False(ShaderPrebuildState.HasPreparableShaders(Eboot));
        var executable = new byte[(4 << 20) + 0x100];
        "1234\x18\0\0\0"u8.CopyTo(executable.AsSpan((4 << 20) - 3));
        File.WriteAllBytes(Eboot, executable);
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

    [Fact]
    public void ClearingDeletesTheTitleCachesAndItsPrebuildStamp()
    {
        if (ShaderAnalysisCache.DirectoryFor(_titleId) is null)
        {
            return; // The analysis cache is disabled in this environment.
        }

        var emulator = Path.Combine(_game, "emulator");
        var pipelines = Path.Combine(emulator, "user", "pipeline_cache", _titleId);
        Directory.CreateDirectory(pipelines);
        File.WriteAllBytes(Path.Combine(pipelines, "vulkan-pipeline-cache.bin"), new byte[100]);
        ShaderPrebuildState.MarkCurrent(Eboot);
        var other = Path.Combine(emulator, "user", "pipeline_cache", "OTHER00000");
        Directory.CreateDirectory(other);

        Assert.Equal(2, GameCacheStorage.Directories(_titleId, emulator).Count(Directory.Exists));
        Assert.True(GameCacheStorage.Size(_titleId, emulator) > 100);
        Assert.Equal(GameCacheStorage.Size(_titleId, emulator), GameCacheStorage.Clear(_titleId, emulator));
        Assert.DoesNotContain(GameCacheStorage.Directories(_titleId, emulator), Directory.Exists);
        Assert.False(ShaderPrebuildState.IsCurrent(Eboot));
        Assert.True(Directory.Exists(other));
        Assert.Equal(0, GameCacheStorage.Clear(_titleId, emulator));
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
