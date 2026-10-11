// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Prebuild;

// The LZ4 block format Unity packs bundles and shader blobs with (LZ4 and LZ4HC
// share it). The decoded size is always known up front.
internal static class Lz4Block
{
    private const int MinimumMatch = 4;

    public static byte[] Decode(ReadOnlySpan<byte> source, int decodedSize)
    {
        var destination = new byte[decodedSize];
        var read = 0;
        var written = 0;
        while (read < source.Length)
        {
            var token = source[read++];
            var literals = ReadLength(source, ref read, token >> 4);
            if (literals > source.Length - read || literals > decodedSize - written)
            {
                throw new InvalidDataException("An LZ4 literal run overruns its block.");
            }

            source.Slice(read, literals).CopyTo(destination.AsSpan(written));
            read += literals;
            written += literals;
            // The last sequence carries literals only.
            if (read == source.Length)
            {
                break;
            }

            if (source.Length - read < sizeof(ushort))
            {
                throw new InvalidDataException("An LZ4 match offset is truncated.");
            }

            var offset = source[read] | (source[read + 1] << 8);
            read += sizeof(ushort);
            var length = ReadLength(source, ref read, token & 0xF) + MinimumMatch;
            if (offset == 0 || offset > written || length > decodedSize - written)
            {
                throw new InvalidDataException("An LZ4 match reaches outside its block.");
            }

            // Matches may overlap what they write, so they copy forward byte by byte
            // unless the source lies wholly behind.
            if (offset >= length)
            {
                destination.AsSpan(written - offset, length).CopyTo(destination.AsSpan(written));
                written += length;
            }
            else
            {
                for (var end = written + length; written < end; written++)
                {
                    destination[written] = destination[written - offset];
                }
            }
        }

        if (written != decodedSize)
        {
            throw new InvalidDataException($"An LZ4 block decoded to {written} bytes instead of {decodedSize}.");
        }

        return destination;
    }

    private static int ReadLength(ReadOnlySpan<byte> source, ref int read, int length)
    {
        if (length != 0xF)
        {
            return length;
        }

        byte next;
        do
        {
            if (read >= source.Length)
            {
                throw new InvalidDataException("An LZ4 length is truncated.");
            }

            next = source[read++];
            length += next;
        }
        while (next == byte.MaxValue && length < int.MaxValue - byte.MaxValue);
        return length;
    }
}
