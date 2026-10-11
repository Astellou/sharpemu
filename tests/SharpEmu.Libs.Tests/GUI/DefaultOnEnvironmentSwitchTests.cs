// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class DefaultOnEnvironmentSwitchTests
{
    public static TheoryData<string> Switches => ["SHARPEMU_STRICT_COMPUTE", "SHARPEMU_SHADER_PREBUILD"];

    public static TheoryData<string, bool> SwitchesAndChoices => new()
    {
        { "SHARPEMU_STRICT_COMPUTE", false },
        { "SHARPEMU_STRICT_COMPUTE", true },
        { "SHARPEMU_SHADER_PREBUILD", false },
        { "SHARPEMU_SHADER_PREBUILD", true },
    };

    public static TheoryData<string, bool, bool> SwitchesAndOverrides => new()
    {
        { "SHARPEMU_STRICT_COMPUTE", false, false },
        { "SHARPEMU_STRICT_COMPUTE", false, true },
        { "SHARPEMU_STRICT_COMPUTE", true, false },
        { "SHARPEMU_STRICT_COMPUTE", true, true },
        { "SHARPEMU_SHADER_PREBUILD", false, false },
        { "SHARPEMU_SHADER_PREBUILD", false, true },
        { "SHARPEMU_SHADER_PREBUILD", true, false },
        { "SHARPEMU_SHADER_PREBUILD", true, true },
    };

    private static DefaultOnEnvironmentSwitch Switch(string name) => Assert.IsType<DefaultOnEnvironmentSwitch>(DefaultOnEnvironmentSwitch.Find(name));

    [Theory]
    [MemberData(nameof(Switches))]
    public void NewAndExistingSettingsEnableTheSwitchByDefault(string name)
    {
        foreach (var json in new[] { "{}", "{\"EnvironmentToggles\":[]}", "{\"EnvironmentToggles\":[\"SHARPEMU_PROFILE_PERFORMANCE\"]}" })
        {
            var settings = GuiSettings.NormalizeFromJson(json);
            var effective = EffectiveLaunchSettings.Resolve(settings, new PerGameSettings());
            Assert.True(Switch(name).IsEnabled(effective.EnvironmentToggles));
            Assert.Equal("1", Switch(name).GetLaunchValue(effective.EnvironmentToggles));
        }
    }

    [Theory]
    [MemberData(nameof(SwitchesAndChoices))]
    public void GlobalChoiceSurvivesReloadAndOverridesPreviousEntries(string name, bool enabled)
    {
        var settings = new GuiSettings();
        settings.EnvironmentToggles.AddRange([name, name + "=0"]);
        Switch(name).SetEnabled(settings.EnvironmentToggles, enabled);
        var restored = GuiSettings.NormalizeFromJson(JsonSerializer.Serialize(settings));
        var effective = EffectiveLaunchSettings.Resolve(restored, new PerGameSettings());
        Assert.Equal(enabled, Switch(name).IsEnabled(effective.EnvironmentToggles));
        Assert.Equal(enabled ? "1" : "0", Switch(name).GetLaunchValue(effective.EnvironmentToggles));
        Assert.Single(restored.EnvironmentToggles, entry => entry.StartsWith(name));
        Assert.Contains("SHARPEMU_WRITABLE_APP0", restored.EnvironmentToggles);
    }

    [Theory]
    [MemberData(nameof(SwitchesAndOverrides))]
    public void GameOverrideSurvivesReloadWithoutChangingGlobalChoice(string name, bool globalEnabled, bool gameEnabled)
    {
        var global = new GuiSettings();
        Switch(name).SetEnabled(global.EnvironmentToggles, globalEnabled);
        var game = new PerGameSettings { EnvironmentToggles = [.. global.EnvironmentToggles] };
        Switch(name).SetEnabled(game.EnvironmentToggles, gameEnabled);
        game.RemoveInheritedValues(global);
        Assert.Equal(globalEnabled == gameEnabled, game.IsEmpty);
        var restored = PerGameSettings.NormalizeFromJson(JsonSerializer.Serialize(game));
        var effective = EffectiveLaunchSettings.Resolve(global, restored);
        Assert.Equal(gameEnabled ? "1" : "0", Switch(name).GetLaunchValue(effective.EnvironmentToggles));
        Assert.Equal(globalEnabled, Switch(name).IsEnabled(global.EnvironmentToggles));
    }

    [Theory]
    [MemberData(nameof(SwitchesAndChoices))]
    public void ExplicitGameChoiceIsComparedWithTheEnabledDefault(string name, bool gameEnabled)
    {
        var global = new GuiSettings();
        var game = new PerGameSettings { EnvironmentToggles = [.. global.EnvironmentToggles] };
        Switch(name).SetEnabled(game.EnvironmentToggles, gameEnabled);
        game.RemoveInheritedValues(global);
        Assert.Equal(gameEnabled, game.IsEmpty);
        Assert.Equal(gameEnabled, Switch(name).IsEnabled(EffectiveLaunchSettings.Resolve(global, game).EnvironmentToggles));
    }
}
