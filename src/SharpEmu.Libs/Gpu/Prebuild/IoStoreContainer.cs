// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.Libs.Gpu.Prebuild;

// A 12-byte FIoChunkId: an 8-byte id, a 2-byte index, padding, and the chunk type last.
internal readonly record struct IoChunkId(ulong Id, uint IndexAndType)
{
    public const int Size = 12;

    public byte Type => (byte)(IndexAndType >> 24);

    public static IoChunkId Read(ReadOnlySpan<byte> bytes) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(bytes),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]));
}

// An Unreal Engine IoStore container (.utoc table of contents with its .ucas data).
// PS5 containers leave compression to the platform I/O path, so only stored blocks are read.
internal sealed class IoStoreContainer : IDisposable
{
    private const string TocMagic = "-==--==--==--==-";
    private const int MinimumHeaderSize = 144;
    private const byte VersionPerfectHash = 4;
    private const byte VersionPerfectHashWithOverflow = 5;
    private const int OffsetAndLengthSize = 10;
    private const int CompressionBlockSize = 12;

    private readonly byte[] _toc;
    private readonly int _chunkIdsOffset;
    private readonly int _offsetsOffset;
    private readonly int _blocksOffset;
    private readonly uint _blockSize;
    private readonly ulong _partitionSize;
    private readonly FileStream?[] _partitions;
    private readonly string _casBasePath;

    private IoStoreContainer(string tocPath, byte[] toc)
    {
        _toc = toc;
        TocPath = tocPath;
        var version = toc[16];
        var headerSize = ReadInt32(20);
        EntryCount = ReadInt32(24);
        var blockCount = ReadInt32(28);
        var methodCount = ReadInt32(36);
        _blockSize = (uint)ReadInt32(44);
        var partitionCount = Math.Max(1, ReadInt32(52));
        var seedCount = ReadInt32(84);
        var partitionSize = BinaryPrimitives.ReadUInt64LittleEndian(toc.AsSpan(88));
        _partitionSize = partitionSize == 0 ? ulong.MaxValue : partitionSize;
        var withoutPerfectHashCount = version >= VersionPerfectHashWithOverflow ? ReadInt32(96) : 0;
        if (methodCount != 0)
        {
            throw new NotSupportedException($"IoStore container {Path.GetFileName(tocPath)} uses engine-side compression.");
        }

        var position = headerSize;
        _chunkIdsOffset = position;
        position += EntryCount * IoChunkId.Size;
        _offsetsOffset = position;
        position += EntryCount * OffsetAndLengthSize;
        if (version >= VersionPerfectHash)
        {
            position += seedCount * sizeof(int);
        }

        position += withoutPerfectHashCount * sizeof(int);
        _blocksOffset = position;
        if (_blocksOffset + ((long)blockCount * CompressionBlockSize) > toc.Length)
        {
            throw new InvalidDataException($"IoStore container {Path.GetFileName(tocPath)} is truncated.");
        }

        _casBasePath = Path.ChangeExtension(tocPath, null);
        _partitions = new FileStream?[partitionCount];
    }

    public string TocPath { get; }

    public int EntryCount { get; }

    public static IoStoreContainer Open(string tocPath)
    {
        var toc = File.ReadAllBytes(tocPath);
        if (toc.Length < MinimumHeaderSize || Encoding.ASCII.GetString(toc, 0, TocMagic.Length) != TocMagic)
        {
            throw new InvalidDataException($"{Path.GetFileName(tocPath)} is not an IoStore table of contents.");
        }

        return new IoStoreContainer(tocPath, toc);
    }

    public IoChunkId ChunkId(int index) => IoChunkId.Read(_toc.AsSpan(_chunkIdsOffset + (index * IoChunkId.Size)));

    public long ChunkLength(int index) => (long)ReadUInt40BigEndian(_offsetsOffset + (index * OffsetAndLengthSize) + 5);

    public byte[] ReadChunk(int index)
    {
        var offset = ReadUInt40BigEndian(_offsetsOffset + (index * OffsetAndLengthSize));
        var length = ChunkLength(index);
        var data = new byte[length];
        long done = 0;
        for (var block = (int)(offset / _blockSize); done < length; block++)
        {
            var entry = _toc.AsSpan(_blocksOffset + (block * CompressionBlockSize));
            var blockOffset = BinaryPrimitives.ReadUInt64LittleEndian(entry) & 0xFF_FFFF_FFFFUL;
            var compressedSize = (int)(BinaryPrimitives.ReadUInt32LittleEndian(entry[5..]) & 0xFF_FFFF);
            var uncompressedSize = (int)(BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]) & 0xFF_FFFF);
            if (entry[11] != 0 || compressedSize != uncompressedSize)
            {
                throw new NotSupportedException($"IoStore block {block} in {Path.GetFileName(TocPath)} is compressed.");
            }

            var start = done == 0 ? (int)(offset % _blockSize) : 0;
            var count = (int)Math.Min(uncompressedSize - start, length - done);
            var stream = Partition((int)(blockOffset / _partitionSize));
            stream.Position = (long)(blockOffset % _partitionSize) + start;
            stream.ReadExactly(data.AsSpan((int)done, count));
            done += count;
        }

        return data;
    }

    public void Dispose()
    {
        foreach (var stream in _partitions)
        {
            stream?.Dispose();
        }
    }

    private FileStream Partition(int index) =>
        _partitions[index] ??= File.OpenRead(index == 0 ? _casBasePath + ".ucas" : $"{_casBasePath}_s{index}.ucas");

    private int ReadInt32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(_toc.AsSpan(offset));

    private ulong ReadUInt40BigEndian(int offset)
    {
        var bytes = _toc.AsSpan(offset, 5);
        return ((ulong)bytes[0] << 32) | ((ulong)bytes[1] << 24) | ((ulong)bytes[2] << 16) | ((ulong)bytes[3] << 8) | bytes[4];
    }
}
