// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Prebuild;

// What a prebuild extracted and prepared, whichever engine packed the shaders.
internal class ShaderPrebuildReport
{
    public int Shaders { get; set; }
    public int Unextracted { get; set; }
    public Dictionary<byte, int> AgcShaderTypes { get; } = [];
    public HashSet<ulong> UniqueHashes { get; } = [];
    // Per AGC shader type: prepared and failed counts, and failure reasons.
    public Dictionary<byte, (int Prepared, int Failed)> Preparation { get; } = [];
    public Dictionary<string, int> Failures { get; } = [];
    public long TotalShaders;
    public long ProcessedShaders;
    public TimeSpan Elapsed { get; set; }

    public void WriteSummary(TextWriter log)
    {
        log.WriteLine(
            $"[PREBUILD][INFO] Shaders={Shaders} unextracted={Unextracted} " +
            $"unique_hashes={UniqueHashes.Count} types=" +
            string.Join(',', AgcShaderTypes.OrderBy(type => type.Key).Select(type => $"{type.Key}:{type.Value}")));
        log.WriteLine(
            "[PREBUILD][INFO] Prepared " +
            string.Join(' ', Preparation.OrderBy(type => type.Key).Select(type => $"type{type.Key}={type.Value.Prepared}/{type.Value.Prepared + type.Value.Failed}")));
        foreach (var (reason, count) in Failures.OrderByDescending(failure => failure.Value).Take(12))
        {
            log.WriteLine($"[PREBUILD][INFO]   {count,6} x {reason}");
        }
    }
}

// The preparation every engine's prebuild ends with, and the progress a launcher follows.
internal static class ShaderPrebuildRunner
{
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SlowCheckInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SlowPreparation = TimeSpan.FromSeconds(10);

    // A launcher follows a prebuild through these lines: shader entries handled so far
    // and the total, each entry counted once whether prepared or skipped.
    public static void WriteProgress(ShaderPrebuildReport report, TextWriter log) =>
        log.WriteLine($"[PREBUILD][PROGRESS] {Interlocked.Read(ref report.ProcessedShaders)} {Interlocked.Read(ref report.TotalShaders)}");

    public static void Prepare(IReadOnlyCollection<AgcShaderHeader> work, ShaderPrebuildReport report, TextWriter log)
    {
        var compiler = GuestGpu.Current;
        // One pathological program can hold a whole batch, so every program still running
        // long is reported while the batch waits for it.
        var running = new ConcurrentDictionary<AgcShaderHeader, long>();
        using var slowCheck = new Timer(_ =>
        {
            foreach (var (header, started) in running)
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                if (elapsed >= SlowPreparation)
                {
                    log.WriteLine(
                        $"[PREBUILD][WARN] Still preparing hash=0x{header.Shader.Hash:X16} type={header.Shader.Type} " +
                        $"code_bytes={header.Shader.CodeSize} for {elapsed.TotalSeconds:F0}s");
                }
            }
        }, null, SlowCheckInterval, SlowCheckInterval);
        Parallel.ForEach(work, header =>
        {
            running[header] = Stopwatch.GetTimestamp();
            var prepared = ShaderPrebuild.TryPrepare(header, compiler, hostWave64Supported: false, out var error);
            running.TryRemove(header, out _);
            Interlocked.Increment(ref report.ProcessedShaders);
            lock (report.Failures)
            {
                var (ok, failed) = report.Preparation.GetValueOrDefault(header.Shader.Type);
                report.Preparation[header.Shader.Type] = prepared ? (ok + 1, failed) : (ok, failed + 1);
                if (!prepared)
                {
                    var reason = $"type{header.Shader.Type}: {(error.Length > 90 ? error[..90] : error)}";
                    report.Failures[reason] = report.Failures.GetValueOrDefault(reason) + 1;
                }
            }
        });
    }

    // Reports from its own thread: the preparation keeps every pool thread busy, which
    // holds a pool timer's callbacks back for many seconds.
    public sealed class ProgressReporter : IDisposable
    {
        private readonly ManualResetEventSlim _stop = new();
        private readonly Thread _thread;

        public ProgressReporter(ShaderPrebuildReport report, TextWriter log)
        {
            _thread = new Thread(() =>
            {
                do
                {
                    WriteProgress(report, log);
                }
                while (!_stop.Wait(ProgressReportInterval));
            })
            {
                IsBackground = true,
                Name = "SharpEmu prebuild progress",
            };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop.Set();
            _thread.Join();
            _stop.Dispose();
        }
    }
}
