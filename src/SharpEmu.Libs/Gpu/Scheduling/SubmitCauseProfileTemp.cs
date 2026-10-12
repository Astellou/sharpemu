// TEMP diagnostic, not to be committed: which call paths submit and which ones block on the GPU.
using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Scheduling;

internal static class SubmitCauseProfileTemp
{
    private static readonly Dictionary<string, int> Submits = new();
    private static readonly Dictionary<string, (int Count, double Ms)> Waits = new();
    private static long _submitCount;
    private static long _lastReport = Stopwatch.GetTimestamp();

    public static void NoteSubmit()
    {
        if (!VideoOut.RenderPhaseProfile.Enabled || Interlocked.Increment(ref _submitCount) % 8 != 0) return;
        var key = Describe(new StackTrace(2, false));
        lock (Submits)
        {
            Submits[key] = Submits.GetValueOrDefault(key) + 8;
        }

        MaybeReport();
    }

    public static void NoteWait(double milliseconds)
    {
        if (!VideoOut.RenderPhaseProfile.Enabled) return;
        var key = Describe(new StackTrace(2, false));
        lock (Submits)
        {
            var entry = Waits.GetValueOrDefault(key);
            Waits[key] = (entry.Count + 1, entry.Ms + milliseconds);
        }

        MaybeReport();
    }

    // A blocking vkWaitSemaphores, keyed by its thread and the code that asked for it.
    public static void NoteRenderWait(string kind, double milliseconds)
    {
        if (!VideoOut.RenderPhaseProfile.Enabled) return;
        var key = $"{kind} thread='{Thread.CurrentThread.Name}' " + Describe(new StackTrace(2, false), 10);
        lock (Submits)
        {
            var entry = Waits.GetValueOrDefault(key);
            Waits[key] = (entry.Count + 1, entry.Ms + milliseconds);
        }

        MaybeReport();
    }

    private static string Describe(StackTrace trace, int depth = 6)
    {
        var names = new List<string>();
        foreach (var frame in trace.GetFrames())
        {
            var method = frame.GetMethod();
            if (method?.DeclaringType is not { } type) continue;
            var name = $"{type.Name}.{method.Name}";
            if (type.Namespace?.EndsWith("Scheduling", StringComparison.Ordinal) == true && names.Count == 0) continue;
            if (names.Count == 0 || names[^1] != name) names.Add(name);
            if (names.Count == depth) break;
        }

        return string.Join(" <- ", names);
    }

    private static void MaybeReport()
    {
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastReport), now).TotalSeconds < 5) return;
        lock (Submits)
        {
            if (Stopwatch.GetElapsedTime(_lastReport, now).TotalSeconds < 5) return;
            _lastReport = now;
            foreach (var pair in Submits.OrderByDescending(static pair => pair.Value).Take(12))
                Console.Error.WriteLine($"[PERF][SUBMIT_CAUSE] count~{pair.Value} {pair.Key}");
            foreach (var pair in Waits.OrderByDescending(static pair => pair.Value.Ms).Take(20))
                Console.Error.WriteLine($"[PERF][WAIT_CAUSE] count={pair.Value.Count} ms={pair.Value.Ms:F1} {pair.Key}");
            Submits.Clear();
            Waits.Clear();
        }
    }
}
