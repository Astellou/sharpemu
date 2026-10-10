// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Gpu.Prebuild;

// One compressed IoStore chunk holding the code of several shaders back to back.
internal readonly record struct UnrealShaderGroup(IoChunkId Chunk, uint ShaderIndicesOffset, uint ShaderCount, uint UncompressedSize, uint CompressedSize);

// A shader's place inside its group. Frequency is the engine's EShaderFrequency.
internal readonly record struct UnrealShaderEntry(int GroupIndex, uint OffsetInGroup, byte Frequency);

// The table of an IoStore shader library (FIoStoreShaderCodeArchiveHeader), the chunk
// a .ushaderbytecode file becomes when the cook writes it into an IoStore container.
internal sealed class UnrealShaderArchive
{
    private const uint SupportedVersion = 1;
    private const int ShaHashSize = 20;
    private const int ShaderMapEntrySize = 8;
    private const int FrequencyBits = 4;
    private const int GroupIndexBits = 30;

    private UnrealShaderArchive(UnrealShaderGroup[] groups, UnrealShaderEntry[] shaders, uint[] shaderIndices)
    {
        Groups = groups;
        Shaders = shaders;
        ShaderIndices = shaderIndices;
    }

    public UnrealShaderGroup[] Groups { get; }

    public UnrealShaderEntry[] Shaders { get; }

    public uint[] ShaderIndices { get; }

    public static UnrealShaderArchive Parse(ReadOnlySpan<byte> data)
    {
        var reader = new Reader(data);
        var version = reader.UInt32();
        if (version != SupportedVersion)
        {
            throw new NotSupportedException($"Unreal shader library version {version} is not supported.");
        }

        reader.SkipArray(ShaHashSize); // shader map hashes
        reader.SkipArray(ShaHashSize); // shader hashes
        var groupChunks = new IoChunkId[reader.Count()];
        for (var i = 0; i < groupChunks.Length; i++)
        {
            groupChunks[i] = IoChunkId.Read(reader.Take(IoChunkId.Size));
        }

        reader.SkipArray(ShaderMapEntrySize);
        var shaders = new UnrealShaderEntry[reader.Count()];
        for (var i = 0; i < shaders.Length; i++)
        {
            var packed = reader.UInt64();
            shaders[i] = new UnrealShaderEntry(
                (int)((packed >> FrequencyBits) & ((1UL << GroupIndexBits) - 1)),
                (uint)(packed >> (FrequencyBits + GroupIndexBits)),
                (byte)(packed & ((1UL << FrequencyBits) - 1)));
        }

        var groups = new UnrealShaderGroup[reader.Count()];
        if (groups.Length != groupChunks.Length)
        {
            throw new InvalidDataException("Unreal shader library group tables disagree.");
        }

        for (var i = 0; i < groups.Length; i++)
        {
            groups[i] = new UnrealShaderGroup(groupChunks[i], reader.UInt32(), reader.UInt32(), reader.UInt32(), reader.UInt32());
        }

        var indices = new uint[reader.Count()];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = reader.UInt32();
        }

        if (!reader.AtEnd)
        {
            throw new InvalidDataException("Unreal shader library has trailing data.");
        }

        return new UnrealShaderArchive(groups, shaders, indices);
    }

    // The byte ranges of a decompressed group's shaders, in group order.
    public IEnumerable<(int ShaderIndex, int Offset, int Length)> ShadersOf(int groupIndex)
    {
        var group = Groups[groupIndex];
        var members = new List<(int ShaderIndex, int Offset)>((int)group.ShaderCount);
        for (var i = 0; i < group.ShaderCount; i++)
        {
            var shaderIndex = (int)ShaderIndices[group.ShaderIndicesOffset + i];
            members.Add((shaderIndex, (int)Shaders[shaderIndex].OffsetInGroup));
        }

        members.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        for (var i = 0; i < members.Count; i++)
        {
            var end = i + 1 < members.Count ? members[i + 1].Offset : (int)group.UncompressedSize;
            yield return (members[i].ShaderIndex, members[i].Offset, end - members[i].Offset);
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public readonly bool AtEnd => _position == _data.Length;

        public uint UInt32()
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_data[_position..]);
            _position += sizeof(uint);
            return value;
        }

        public ulong UInt64()
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(_data[_position..]);
            _position += sizeof(ulong);
            return value;
        }

        public int Count()
        {
            var count = (int)UInt32();
            return count >= 0 && count <= _data.Length ? count : throw new InvalidDataException("Unreal shader library array count is out of range.");
        }

        public ReadOnlySpan<byte> Take(int length)
        {
            var slice = _data.Slice(_position, length);
            _position += length;
            return slice;
        }

        public void SkipArray(int elementSize)
        {
            var count = Count();
            _position = checked(_position + (count * elementSize));
        }
    }
}
