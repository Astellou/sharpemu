// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.Libs.Gpu.Prebuild;

// A UnityFS asset bundle: big-endian tables naming the files it packs, and the files
// themselves as one stream cut into separately compressed blocks.
internal static class UnityBundle
{
    private const int CompressionMask = 0x3F;
    private const int CompressionNone = 0;
    private const int CompressionLz4 = 2;
    private const int CompressionLz4Hc = 3;
    private const uint InfoAtEnd = 0x80;
    private const uint PaddingBeforeBlocks = 0x200;
    private const int InfoHashSize = 16;
    private const int BlockEntrySize = 10;
    private const int NodeFixedSize = 20;

    public static ReadOnlySpan<byte> Signature => "UnityFS\0"u8;

    public static List<(string Name, byte[] Data)> ReadFiles(byte[] bundle)
    {
        try
        {
            return ReadFilesCore(bundle);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new InvalidDataException("The bundle is truncated.", exception);
        }
    }

    private static List<(string Name, byte[] Data)> ReadFilesCore(byte[] bundle)
    {
        var position = 0;
        SkipString(bundle, ref position);
        var version = BinaryPrimitives.ReadUInt32BigEndian(bundle.AsSpan(position));
        position += sizeof(uint);
        SkipString(bundle, ref position);
        SkipString(bundle, ref position);
        position += sizeof(long);
        var infoPacked = BinaryPrimitives.ReadInt32BigEndian(bundle.AsSpan(position));
        var infoSize = BinaryPrimitives.ReadInt32BigEndian(bundle.AsSpan(position + 4));
        var flags = BinaryPrimitives.ReadUInt32BigEndian(bundle.AsSpan(position + 8));
        position += 3 * sizeof(int);
        if (version >= 7)
        {
            position = Align16(position);
        }

        var infoAtEnd = (flags & InfoAtEnd) != 0;
        var info = Decompress(
            bundle.AsSpan(infoAtEnd ? bundle.Length - infoPacked : position, infoPacked),
            infoSize,
            (int)(flags & CompressionMask));
        if (!infoAtEnd)
        {
            position += infoPacked;
        }

        if ((flags & PaddingBeforeBlocks) != 0)
        {
            position = Align16(position);
        }

        var cursor = InfoHashSize;
        var blockCount = BinaryPrimitives.ReadInt32BigEndian(info.AsSpan(cursor));
        cursor += sizeof(int);
        long streamSize = 0;
        for (var block = 0; block < blockCount; block++)
        {
            streamSize += BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(cursor + (block * BlockEntrySize)));
        }

        if (streamSize > Array.MaxLength)
        {
            throw new NotSupportedException($"The bundle unpacks to {streamSize} bytes.");
        }

        var stream = new byte[streamSize];
        var written = 0;
        for (var block = 0; block < blockCount; block++, cursor += BlockEntrySize)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(cursor));
            var packed = (int)BinaryPrimitives.ReadUInt32BigEndian(info.AsSpan(cursor + 4));
            var blockFlags = BinaryPrimitives.ReadUInt16BigEndian(info.AsSpan(cursor + 8));
            Decompress(bundle.AsSpan(position, packed), size, blockFlags & CompressionMask).CopyTo(stream, written);
            position += packed;
            written += size;
        }

        var nodeCount = BinaryPrimitives.ReadInt32BigEndian(info.AsSpan(cursor));
        cursor += sizeof(int);
        var files = new List<(string, byte[])>(nodeCount);
        for (var node = 0; node < nodeCount; node++)
        {
            var offset = BinaryPrimitives.ReadInt64BigEndian(info.AsSpan(cursor));
            var size = BinaryPrimitives.ReadInt64BigEndian(info.AsSpan(cursor + 8));
            cursor += NodeFixedSize;
            var nameStart = cursor;
            SkipString(info, ref cursor);
            if (offset < 0 || size < 0 || offset + size > stream.Length)
            {
                throw new InvalidDataException("A bundle file lies outside the bundle stream.");
            }

            files.Add((Encoding.UTF8.GetString(info, nameStart, cursor - nameStart - 1), stream.AsSpan((int)offset, (int)size).ToArray()));
        }

        return files;
    }

    private static byte[] Decompress(ReadOnlySpan<byte> packed, int size, int compression) => compression switch
    {
        CompressionNone => packed.ToArray(),
        CompressionLz4 or CompressionLz4Hc => Lz4Block.Decode(packed, size),
        _ => throw new NotSupportedException($"Bundle compression {compression} is not supported."),
    };

    private static void SkipString(byte[] data, ref int position)
    {
        var end = Array.IndexOf(data, (byte)0, position);
        if (end < 0)
        {
            throw new InvalidDataException("A bundle string is unterminated.");
        }

        position = end + 1;
    }

    private static int Align16(int position) => (position + 15) & ~15;
}
