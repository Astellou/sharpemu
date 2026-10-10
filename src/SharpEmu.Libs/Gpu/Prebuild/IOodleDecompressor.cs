// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Prebuild;

internal sealed class OodleJob(byte[] source, int rawLength)
{
    public byte[] Source { get; } = source;

    public byte[] Destination { get; } = new byte[rawLength];

    // The decoded length Oodle reported; anything other than Destination.Length is a failure.
    public long Result { get; set; }
}

// Decodes Oodle streams. The decoder ships inside the title, never with SharpEmu.
internal interface IOodleDecompressor
{
    // The largest total of source plus destination bytes one batch may carry.
    long MaxBatchBytes { get; }

    void Decompress(IReadOnlyList<OodleJob> jobs);
}
