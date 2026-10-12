// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Threading;

namespace SharpEmu.GUI;

/// <summary>
/// Shows the shader prewarm a running game holds its splash screen for, in the prebuild's
/// panel: the pipelines it compiled before a SharpEmu or driver update are compiled again.
/// </summary>
public partial class MainWindow
{
    private const string PrewarmProgressPrefix = "[PREWARM][PROGRESS] ";
    private const string PrewarmDoneLine = "[PREWARM][DONE]";

    private bool _prewarmShown;

    // True when the line was the prewarm's and drove the panel instead of the console.
    private bool TryHandleShaderPrewarmLine(string line)
    {
        if (line.StartsWith(PrewarmDoneLine, StringComparison.Ordinal))
        {
            Dispatcher.UIThread.Post(HideShaderPrewarm);
            return true;
        }

        if (!TryParseProgressLine(line, PrewarmProgressPrefix, out var done, out var total))
        {
            return false;
        }

        if (total > 0)
        {
            Dispatcher.UIThread.Post(() => UpdateShaderPrewarm(done, total));
        }

        return true;
    }

    private void UpdateShaderPrewarm(long done, long total)
    {
        if (!_isRunning || _prebuild is not null)
        {
            return;
        }

        if (!_prewarmShown)
        {
            _prewarmShown = true;
            ShowProgressPanel(_runningGameName ?? string.Empty, prewarm: true);
        }

        UpdateProgressPanel(done, total);
    }

    private void HideShaderPrewarm()
    {
        if (!_prewarmShown)
        {
            return;
        }

        _prewarmShown = false;
        PrebuildOverlay.IsVisible = false;
    }
}
