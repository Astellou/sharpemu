// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Runtime;

namespace SharpEmu.GUI;

// An environment switch that is on unless set to "0". A disabled switch is stored as
// an explicit "=0" so it differs from the enabled default.
internal sealed class DefaultOnEnvironmentSwitch(string variableName)
{
    public static readonly DefaultOnEnvironmentSwitch StrictCompute = new("SHARPEMU_STRICT_COMPUTE");
    public static readonly DefaultOnEnvironmentSwitch ShaderPrebuild = new(ShaderPrebuildState.VariableName);

    private static readonly DefaultOnEnvironmentSwitch[] All = [StrictCompute, ShaderPrebuild];

    public string VariableName { get; } = variableName;

    public static DefaultOnEnvironmentSwitch? Find(string name) =>
        All.FirstOrDefault(candidate => string.Equals(candidate.VariableName, name, StringComparison.OrdinalIgnoreCase));

    public bool IsEnabled(IEnumerable<string> entries)
    {
        var enabled = true;
        foreach (var entry in entries)
        {
            var parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (string.Equals(parts[0], VariableName, StringComparison.OrdinalIgnoreCase))
                enabled = parts.Length == 1 || parts[1] != "0";
        }
        return enabled;
    }

    public void SetEnabled(List<string> entries, bool enabled)
    {
        entries.RemoveAll(entry => string.Equals(entry.Split('=', 2, StringSplitOptions.TrimEntries)[0],
            VariableName, StringComparison.OrdinalIgnoreCase));
        entries.Add(enabled ? VariableName : VariableName + "=0");
    }

    public string GetLaunchValue(IEnumerable<string> entries) => IsEnabled(entries) ? "1" : "0";
}
