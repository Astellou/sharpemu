// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.Libs.Gpu.Prebuild;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Prebuild;

public sealed class UnityShaderExtractionTests
{
    private const int HeaderBytes = 0x100;
    private const int ShRegistersAt = 0x80;
    private const int CodeBytes = 8;
    private const uint EndProgram = 0xBF810000;
    private const int AlignAfter = 0x4000;

    [Fact]
    public void Lz4MatchesMayOverlapWhatTheyWrite()
    {
        // "abc", then a 9-byte match three bytes back, then a final literal.
        byte[] packed = [0x35, (byte)'a', (byte)'b', (byte)'c', 3, 0, 0x10, (byte)'d'];
        Assert.Equal("abcabcabcabcd", Encoding.ASCII.GetString(Lz4Block.Decode(packed, 13)));
    }

    [Fact]
    public void Lz4RejectsAMatchBeforeTheBlock()
    {
        byte[] packed = [0x15, (byte)'a', 4, 0, 0x10, (byte)'d'];
        Assert.Throws<InvalidDataException>(() => Lz4Block.Decode(packed, 11));
    }

    // Newer players prefix each program with its total size; older ones wrap it in an
    // ELF whose code section starts 0x100 bytes in. Either way the header follows the code.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProgramsAreTheCodeBeforeEachHeader(bool elf)
    {
        var blob = ProgramBlob(elf, out var codeOffset);
        var shader = Assert.Single(UnityShaderBlob.Extract(blob));
        Assert.Equal(codeOffset, shader.CodeOffset);
        Assert.Equal(CodeBytes, shader.CodeSize);
        Assert.Equal(codeOffset + CodeBytes, shader.HeaderOffset);
        Assert.Equal(HeaderBytes, shader.HeaderSize);
        Assert.Equal(0, shader.Type);

        var header = new AgcShaderHeader(blob.AsSpan(0, shader.HeaderOffset + shader.HeaderSize), shader);
        Assert.Equal(5, header.Registers.Shader.Compute.UserScalarCount);
        Assert.Equal(EndProgram, BinaryPrimitives.ReadUInt32LittleEndian(header.Code));
    }

    [Fact]
    public void AnUnframedSignatureIsNotAProgram()
    {
        var blob = ProgramBlob(elf: false, out var codeOffset);
        blob[codeOffset - 4] = 1;
        Assert.Empty(UnityShaderBlob.Extract(blob));
    }

    [Fact]
    public void ShaderFieldsAreReadThroughTheTypeTree()
    {
        var program = ProgramBlob(elf: false, out _);
        var packed = Lz4Literals(program);
        var file = new UnitySerializedFile(SerializedShader(packed, program.Length));
        var shader = Assert.Single(file.Objects);
        Assert.Equal(48, shader.Type.ClassId);
        Assert.NotNull(shader.Type.Tree);

        var fields = file.Read(shader, shader.Type.Tree!, new HashSet<string> { "platforms", "offsets", "compressedLengths", "decompressedLengths", "compressedBlob" });
        Assert.Equal(new object?[] { 24L }, Assert.IsType<List<object?>>(fields["platforms"]));
        var offsets = Assert.IsType<List<object?>>(Assert.Single(Assert.IsType<List<object?>>(fields["offsets"])));
        Assert.Equal(new object?[] { 0L }, offsets);
        var decompressed = Assert.IsType<List<object?>>(Assert.Single(Assert.IsType<List<object?>>(fields["decompressedLengths"])));
        Assert.Equal(new object?[] { (long)program.Length }, decompressed);
        var blob = Assert.IsType<byte[]>(fields["compressedBlob"]);
        Assert.Equal(packed, blob);
        Assert.Single(UnityShaderBlob.Extract(Lz4Block.Decode(blob, program.Length)));
    }

    [Fact]
    public void BundlesUnpackTheFilesTheyPack()
    {
        var serialized = SerializedShader(Lz4Literals([1, 2, 3, 4]), 4);
        Assert.True(UnitySerializedFile.LooksLike(serialized, serialized.Length));
        var (name, data) = Assert.Single(UnityBundle.ReadFiles(Bundle("CAB-test", serialized)));
        Assert.Equal("CAB-test", name);
        Assert.Equal(serialized, data);
    }

    private static byte[] ProgramBlob(bool elf, out int codeOffset)
    {
        var prefix = elf ? 0x100 : 8;
        var blob = new byte[16 + prefix + CodeBytes + HeaderBytes + 16];
        codeOffset = 16 + prefix;
        if (elf)
        {
            "\u007FELF"u8.CopyTo(blob.AsSpan(16));
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(codeOffset - 8), CodeBytes + HeaderBytes);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(codeOffset), EndProgram);
        var header = blob.AsSpan(codeOffset + CodeBytes, HeaderBytes);
        "1234\x18\0\0\0"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x20..], ShRegistersAt - 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x40..], HeaderBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x44..], CodeBytes);
        header[0x5C] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(header[ShRegistersAt..], 0x213);
        BinaryPrimitives.WriteUInt32LittleEndian(header[(ShRegistersAt + 4)..], 5u << 1);
        return blob;
    }

    // An LZ4 block holding its input as one literal run.
    private static byte[] Lz4Literals(byte[] data)
    {
        var packed = new List<byte> { (byte)(Math.Min(data.Length, 15) << 4) };
        for (var rest = data.Length - 15; rest >= 0; rest -= 255)
        {
            packed.Add((byte)Math.Min(rest, 255));
            if (rest < 255)
            {
                break;
            }
        }

        packed.AddRange(data);
        return [.. packed];
    }

    // A version 22 serialized file holding one Shader object with its type tree; fields
    // the reader is not asked for, a float and a nested structure, are skipped.
    private static byte[] SerializedShader(byte[] blob, int decodedSize)
    {
        var tree = new TreeBuilder();
        tree.Node("Shader", "Base", 0);
        tree.Node("float", "m_Skipped", 1, 4);
        tree.String("m_Name", 1);
        tree.Node("Pair", "m_Nested", 1, align: true);
        tree.Node("int", "first", 2, 4);
        tree.Node("bool", "second", 2, 1);
        tree.Vector("platforms", 1, "unsigned int", 4);
        foreach (var name in new[] { "offsets", "compressedLengths", "decompressedLengths" })
        {
            tree.Node("vector", name, 1);
            tree.Node("Array", "Array", 2);
            tree.Node("int", "size", 3, 4);
            tree.Vector("data", 3, "unsigned int", 4);
        }

        tree.Vector("compressedBlob", 1, "UInt8", 1, align: true);

        var data = new Writer();
        data.U32(0x3F800000);
        data.I32(4);
        data.Bytes("Test"u8);
        data.Align();
        data.I32(7);
        data.Bytes([1]);
        data.Align();
        data.I32(1);
        data.U32(24);
        foreach (var value in new[] { 0, blob.Length, decodedSize })
        {
            data.I32(1);
            data.I32(1);
            data.U32((uint)value);
        }

        data.I32(blob.Length);
        data.Bytes(blob);
        data.Align();

        var metadata = new Writer();
        metadata.Bytes("2022.3.0f1\0"u8);
        metadata.I32(0);
        metadata.Bytes([1]);
        metadata.I32(1);
        metadata.I32(48);
        metadata.Bytes([0]);
        metadata.Bytes([0xFF, 0xFF]);
        metadata.Bytes(new byte[16]);
        tree.WriteTo(metadata);
        metadata.I32(0);
        metadata.I32(1);
        metadata.Align(48);
        metadata.I64(1);
        metadata.I64(0);
        metadata.I32(data.Length);
        metadata.I32(0);

        var dataOffset = (48 + metadata.Length + 15) & ~15;
        var file = new byte[dataOffset + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(8), 22);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(20), (uint)metadata.Length);
        BinaryPrimitives.WriteInt64BigEndian(file.AsSpan(24), file.Length);
        BinaryPrimitives.WriteInt64BigEndian(file.AsSpan(32), dataOffset);
        metadata.ToArray().CopyTo(file, 48);
        data.ToArray().CopyTo(file, dataOffset);
        return file;
    }

    // A version 8 UnityFS bundle with an uncompressed table and one LZ4 block.
    private static byte[] Bundle(string name, byte[] file)
    {
        var info = new List<byte>(new byte[16]);
        void BigEndian(long value, int size)
        {
            for (var shift = (size - 1) * 8; shift >= 0; shift -= 8)
            {
                info.Add((byte)(value >> shift));
            }
        }

        var block = Lz4Literals(file);
        BigEndian(1, 4);
        BigEndian(file.Length, 4);
        BigEndian(block.Length, 4);
        BigEndian(2, 2);
        BigEndian(1, 4);
        BigEndian(0, 8);
        BigEndian(file.Length, 8);
        BigEndian(4, 4);
        info.AddRange(Encoding.UTF8.GetBytes(name + "\0"));

        var bundle = new List<byte>();
        bundle.AddRange("UnityFS\0"u8.ToArray());
        bundle.AddRange([0, 0, 0, 8]);
        bundle.AddRange("5.x.x\0"u8.ToArray());
        bundle.AddRange("2022.3.0f1\0"u8.ToArray());
        bundle.AddRange(new byte[8]);
        foreach (var value in new[] { info.Count, info.Count, 0x40 })
        {
            bundle.AddRange([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
        }

        while (bundle.Count % 16 != 0)
        {
            bundle.Add(0);
        }

        bundle.AddRange(info);
        bundle.AddRange(block);
        return [.. bundle];
    }

    private sealed class TreeBuilder
    {
        private readonly Writer _nodes = new();
        private readonly Writer _strings = new();
        private int _count;

        public void Node(string type, string name, int level, int size = -1, bool align = false)
        {
            _nodes.Bytes([1, 0]);
            _nodes.Bytes([(byte)level, 0]);
            _nodes.U32(Intern(type));
            _nodes.U32(Intern(name));
            _nodes.I32(size);
            _nodes.I32(_count++);
            _nodes.I32(align ? AlignAfter : 0);
            _nodes.I64(0);
        }

        public void String(string name, int level)
        {
            Node("string", name, level);
            Node("Array", "Array", level + 1, align: true);
            Node("int", "size", level + 2, 4);
            Node("char", "data", level + 2, 1);
        }

        public void Vector(string name, int level, string element, int size, bool align = false)
        {
            Node("vector", name, level);
            Node("Array", "Array", level + 1, align: align);
            Node("int", "size", level + 2, 4);
            Node(element, "data", level + 2, size);
        }

        public void WriteTo(Writer writer)
        {
            writer.I32(_count);
            writer.I32(_strings.Length);
            writer.Bytes(_nodes.ToArray());
            writer.Bytes(_strings.ToArray());
        }

        private uint Intern(string value)
        {
            var offset = (uint)_strings.Length;
            _strings.Bytes(Encoding.ASCII.GetBytes(value + "\0"));
            return offset;
        }
    }

    private sealed class Writer
    {
        private readonly MemoryStream _stream = new();

        public int Length => (int)_stream.Length;

        public void Bytes(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

        public void I32(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            Bytes(bytes);
        }

        public void U32(uint value) => I32((int)value);

        public void I64(long value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            Bytes(bytes);
        }

        // Aligns the absolute position a reader sees once the bytes land at base.
        public void Align(int @base = 0)
        {
            while ((@base + Length) % 4 != 0)
            {
                _stream.WriteByte(0);
            }
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
