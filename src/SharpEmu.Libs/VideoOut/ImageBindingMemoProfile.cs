// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

// How the render thread's remembered texture lookups serve shader images: a hit reuses the lookup,
// a stale one remembered the descriptor but an image was created or removed since, a new one
// remembered another descriptor (or none).
internal static class ImageBindingMemoProfile
{
    private static long _hits;
    private static long _stale;
    private static long _new;

    public static void Count(bool remembered, bool current)
    {
        if (!RenderPhaseProfile.Enabled)
        {
            return;
        }

        if (current)
        {
            Interlocked.Increment(ref _hits);
        }
        else if (remembered)
        {
            Interlocked.Increment(ref _stale);
        }
        else
        {
            Interlocked.Increment(ref _new);
        }
    }

    public static string TakeReport() => FormattableString.Invariant(
        $"[PERF][IMAGE_BINDING_MEMO] hits={Interlocked.Exchange(ref _hits, 0)} stale={Interlocked.Exchange(ref _stale, 0)} new={Interlocked.Exchange(ref _new, 0)}");
}
