// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using SharpEmu.Core.Runtime;

namespace SharpEmu.GUI;

/// <summary>
/// Prepares an Unreal Engine game's shaders in its own emulator process before the game
/// starts, behind a progress panel; the pending launch starts when it ends or is skipped.
/// </summary>
public partial class MainWindow
{
    private const string PrebuildProgressPrefix = "[PREBUILD][PROGRESS] ";
    // The rate settles once a few batches are done; a remaining time shown earlier swings.
    private static readonly TimeSpan PrebuildEstimateDelay = TimeSpan.FromSeconds(5);

    private EmulatorProcess? _prebuild;
    private bool _prebuildSkipped;
    private long _prebuildStarted;
    private long _prebuildFirstProgress;
    private long _prebuildFirstDone;

    // Starts the prebuild a launch needs and returns true, or returns false when the
    // game should start right away: the switch is off, the game has nothing to prepare,
    // or its shaders were prepared by this SharpEmu build already.
    private bool TryStartShaderPrebuild(PendingLaunch launch)
    {
        if (!DefaultOnEnvironmentSwitch.ShaderPrebuild.IsEnabled(launch.Settings.EnvironmentToggles) ||
            string.IsNullOrWhiteSpace(_emulatorExePath) ||
            ShaderPrebuildState.IsCurrent(launch.EbootPath) ||
            !ShaderPrebuildState.HasPreparableShaders(launch.EbootPath))
        {
            return false;
        }

        var process = new EmulatorProcess();
        process.OutputReceived += OnShaderPrebuildOutput;
        process.Exited += code => Dispatcher.UIThread.Post(() => OnShaderPrebuildExited(code));
        _prebuild = process;
        _prebuildSkipped = false;
        _prebuildStarted = Stopwatch.GetTimestamp();
        _prebuildFirstProgress = 0;
        ShowShaderPrebuild(launch.DisplayName);
        try
        {
            process.Start(
                _emulatorExePath,
                ["--cpu-engine=native", $"--log-level={launch.Settings.LogLevel}", "--prebuild-shaders", launch.EbootPath],
                Path.GetDirectoryName(_emulatorExePath));
        }
        catch (Exception exception)
        {
            _prebuild = null;
            process.Dispose();
            PrebuildOverlay.IsVisible = false;
            AppendConsoleLine(Localization.Instance.Format("Launch.StartFailed", exception.Message), ErrorLineBrush);
            return false;
        }

        AppendConsoleLine(Localization.Instance.Format("Prebuild.Console.Started", launch.EbootPath), DimLineBrush);
        return true;
    }

    private void ShowShaderPrebuild(string displayName)
    {
        ShowProgressPanel(displayName, prewarm: false);
        PrebuildDetail.Text = Localization.Instance.Get("Prebuild.Starting");
        PrebuildSkipButton.IsEnabled = true;
    }

    // The prebuild and the prewarm share one panel; only the prebuild can be skipped.
    private void ShowProgressPanel(string displayName, bool prewarm)
    {
        PrebuildGameName.Text = displayName;
        PrebuildTitle.IsVisible = PrebuildNote.IsVisible = PrebuildSkipButton.IsVisible = !prewarm;
        PrewarmTitle.IsVisible = PrewarmNote.IsVisible = prewarm;
        PrebuildProgress.IsIndeterminate = true;
        PrebuildProgress.Value = 0;
        PrebuildDetail.Text = string.Empty;
        PrebuildRemaining.Text = string.Empty;
        _prebuildFirstProgress = 0;
        PrebuildOverlay.IsVisible = true;
    }

    // "<prefix><done> <total>", the progress line a prebuild and a prewarm print.
    private static bool TryParseProgressLine(string line, string prefix, out long done, out long total)
    {
        done = total = 0;
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var fields = line[prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2 ||
            !long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out done) ||
            !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out total))
        {
            return false;
        }

        done = Math.Min(done, total);
        return true;
    }

    // Progress lines drive the panel; every other line goes to the console as the game's do.
    private void OnShaderPrebuildOutput(string line, bool isError)
    {
        if (!line.StartsWith(PrebuildProgressPrefix, StringComparison.Ordinal))
        {
            _pendingLines.Enqueue((line, isError));
            return;
        }

        if (TryParseProgressLine(line, PrebuildProgressPrefix, out var done, out var total) && total > 0)
        {
            Dispatcher.UIThread.Post(() => UpdateShaderPrebuild(done, total));
        }
    }

    private void UpdateShaderPrebuild(long done, long total)
    {
        if (_prebuild is not null)
        {
            UpdateProgressPanel(done, total);
        }
    }

    private void UpdateProgressPanel(long done, long total)
    {
        var now = Stopwatch.GetTimestamp();
        if (_prebuildFirstProgress == 0)
        {
            _prebuildFirstProgress = now;
            _prebuildFirstDone = done;
        }

        PrebuildProgress.IsIndeterminate = false;
        PrebuildProgress.Value = done * PrebuildProgress.Maximum / total;
        PrebuildDetail.Text = Localization.Instance.Format("Prebuild.Progress", done, total, done * 100 / total);

        // The remaining time follows the rate since the first progress line, which leaves
        // a prebuild's boot and library reads out of the estimate.
        var measured = Stopwatch.GetElapsedTime(_prebuildFirstProgress, now);
        var measuredDone = done - _prebuildFirstDone;
        PrebuildRemaining.Text = measured >= PrebuildEstimateDelay && measuredDone > 0 && done < total
            ? Localization.Instance.Format(
                "Prebuild.Remaining",
                FormatDuration(TimeSpan.FromSeconds(measured.TotalSeconds * (total - done) / measuredDone)))
            : string.Empty;
    }

    private void SkipShaderPrebuild()
    {
        if (_prebuild is null || _prebuildSkipped)
        {
            return;
        }

        _prebuildSkipped = true;
        PrebuildSkipButton.IsEnabled = false;
        _prebuild.Stop();
    }

    // Stopping the game while its shaders are prepared cancels the launch as well.
    private bool TryCancelShaderPrebuild()
    {
        if (_prebuild is null)
        {
            return false;
        }

        _pendingLaunch = null;
        _prebuild.Stop();
        return true;
    }

    private void OnShaderPrebuildExited(int exitCode)
    {
        FlushAllPendingConsoleLines();
        _prebuild?.Dispose();
        _prebuild = null;
        PrebuildOverlay.IsVisible = false;
        if (_pendingLaunch is null)
        {
            OnEmulatorExited(EmulatorProcess.HostStopExitCode);
            return;
        }

        if (_prebuildSkipped)
        {
            AppendConsoleLine(Localization.Instance.Get("Prebuild.Console.Skipped"), DimLineBrush);
        }
        else if (exitCode == 0)
        {
            AppendConsoleLine(
                Localization.Instance.Format("Prebuild.Console.Done", FormatDuration(Stopwatch.GetElapsedTime(_prebuildStarted))),
                SuccessLineBrush);
        }
        else
        {
            AppendConsoleLine(Localization.Instance.Format("Prebuild.Console.Failed", exitCode), ErrorLineBrush);
        }

        StartPendingSession();
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalMinutes >= 1
            ? Localization.Instance.Format("Prebuild.Duration.Minutes", (int)duration.TotalMinutes, duration.Seconds)
            : Localization.Instance.Format("Prebuild.Duration.Seconds", Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds)));
}
