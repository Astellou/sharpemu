// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.Libs.Gpu.Prebuild;

// One node of a Unity type tree, in the depth-first order the tree is stored in.
internal readonly record struct UnityTypeNode(string Type, string Name, int Level, int ByteSize, int MetaFlag)
{
    private const int AlignAfter = 0x4000;

    public bool Aligned => (MetaFlag & AlignAfter) != 0;
}

internal sealed record UnityType(int ClassId, UInt128 Hash, UnityTypeNode[]? Tree);

internal readonly record struct UnityObject(long PathId, long Offset, int Size, UnityType Type);

// A Unity serialized file (a .assets file or a file inside a bundle): the types it
// stores, with their type trees when the build kept them, and where each object lies.
internal sealed class UnitySerializedFile
{
    private const int MinimumVersion = 17;
    private const int MaximumVersion = 30;
    private const int WideOffsetsVersion = 22;
    private const int WideNodesVersion = 19;
    private const int TypeDependenciesVersion = 21;
    private const int MonoBehaviourClassId = 114;
    private const int HashSize = 16;
    private const int HeaderSize = 20;
    private const int WideHeaderSize = 48;
    private const uint CommonStringFlag = 0x8000_0000;

    private readonly byte[] _data;
    private readonly long _dataOffset;

    public UnitySerializedFile(byte[] data)
    {
        _data = data;
        try
        {
            var version = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(8));
            var reader = new Reader(data, version >= WideOffsetsVersion ? WideHeaderSize : HeaderSize);
            _dataOffset = version >= WideOffsetsVersion
                ? BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(32))
                : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12));
            reader.SkipString();
            reader.Skip(sizeof(int));
            var hasTypeTrees = reader.Byte() != 0;
            var types = new UnityType[reader.Int32()];
            for (var index = 0; index < types.Length; index++)
            {
                var classId = reader.Int32();
                reader.Skip(sizeof(byte) + sizeof(short));
                if (classId == MonoBehaviourClassId)
                {
                    reader.Skip(HashSize);
                }

                var hash = BinaryPrimitives.ReadUInt128LittleEndian(reader.Bytes(HashSize));
                types[index] = new UnityType(classId, hash, hasTypeTrees ? ReadTree(reader, version) : null);
            }

            var objects = new UnityObject[reader.Int32()];
            for (var index = 0; index < objects.Length; index++)
            {
                reader.Align();
                var pathId = reader.Int64();
                var offset = version >= WideOffsetsVersion ? reader.Int64() : reader.UInt32();
                var size = reader.Int32();
                objects[index] = new UnityObject(pathId, offset, size, types[reader.Int32()]);
            }

            Objects = objects;
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new InvalidDataException("The serialized file metadata is truncated.", exception);
        }
    }

    public IReadOnlyList<UnityObject> Objects { get; }

    public ReadOnlySpan<byte> ObjectBytes(UnityObject obj)
    {
        var start = _dataOffset + obj.Offset;
        if (obj.Offset < 0 || obj.Size < 0 || start + obj.Size > _data.Length)
        {
            throw new InvalidDataException("An object lies outside its serialized file.");
        }

        return _data.AsSpan((int)start, obj.Size);
    }

    // Serialized files carry no signature: the header's version and recorded file size
    // must match, and the metadata must be little-endian as every PS5 build writes it. The
    // head is the file's first bytes, enough for the widest header.
    public static bool LooksLike(ReadOnlySpan<byte> head, long fileLength)
    {
        if (head.Length < WideHeaderSize)
        {
            return false;
        }

        var version = BinaryPrimitives.ReadInt32BigEndian(head[8..]);
        if (version is < MinimumVersion or > MaximumVersion || head[16] != 0)
        {
            return false;
        }

        return version >= WideOffsetsVersion
            ? BinaryPrimitives.ReadInt64BigEndian(head[24..]) == fileLength
            : BinaryPrimitives.ReadUInt32BigEndian(head[4..]) == fileLength;
    }

    // Reads the object's top-level fields named in fields through a type tree,
    // skipping every other field.
    public Dictionary<string, object?> Read(UnityObject obj, UnityTypeNode[] tree, IReadOnlySet<string> fields)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            var reader = new Reader(_data, checked((int)(_dataOffset + obj.Offset)));
            var end = reader.Position + obj.Size;
            for (var index = 1; index < tree.Length && tree[index].Level == 1 && fields.Count != result.Count;)
            {
                var name = tree[index].Name;
                var keep = fields.Contains(name);
                var value = ReadValue(tree, ref index, reader, keep);
                if (keep)
                {
                    result[name] = value;
                }
            }

            if (reader.Position > end)
            {
                throw new InvalidDataException("An object reads past its recorded size.");
            }
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException("An object does not match its type tree.", exception);
        }

        return result;
    }

    private static UnityTypeNode[] ReadTree(Reader reader, int version)
    {
        var nodeCount = reader.Int32();
        var stringSize = reader.Int32();
        var nodeSize = version >= WideNodesVersion ? 32 : 24;
        var nodes = reader.Bytes(nodeCount * nodeSize);
        var strings = reader.Bytes(stringSize);
        var tree = new UnityTypeNode[nodeCount];
        for (var index = 0; index < nodeCount; index++)
        {
            var node = nodes.Slice(index * nodeSize, nodeSize);
            tree[index] = new UnityTypeNode(
                String(strings, BinaryPrimitives.ReadUInt32LittleEndian(node[4..])),
                String(strings, BinaryPrimitives.ReadUInt32LittleEndian(node[8..])),
                node[2],
                BinaryPrimitives.ReadInt32LittleEndian(node[12..]),
                BinaryPrimitives.ReadInt32LittleEndian(node[20..]));
        }

        if (version >= TypeDependenciesVersion)
        {
            reader.Skip(reader.Int32() * sizeof(int));
        }

        return tree;
    }

    private static object? ReadValue(UnityTypeNode[] tree, ref int index, Reader reader, bool keep)
    {
        var node = tree[index];
        var end = SubtreeEnd(tree, index);
        object? value;
        if (index + 1 < end && tree[index + 1].Type == "Array")
        {
            value = ReadArray(tree, index + 1, reader, keep);
            if (keep && node.Type == "string")
            {
                value = Encoding.UTF8.GetString((byte[])value!);
            }
        }
        else if (node.Type == "TypelessData")
        {
            var length = reader.Int32();
            value = keep ? reader.Bytes(length).ToArray() : null;
            if (!keep)
            {
                reader.Skip(length);
            }
        }
        else if (index + 1 < end)
        {
            Dictionary<string, object?>? fields = keep ? new(StringComparer.Ordinal) : null;
            for (var child = index + 1; child < end;)
            {
                var name = tree[child].Name;
                var childValue = ReadValue(tree, ref child, reader, keep);
                fields?.TryAdd(name, childValue);
            }

            value = fields;
        }
        else
        {
            value = ReadPrimitive(node, reader, keep);
        }

        if (node.Aligned)
        {
            reader.Align();
        }

        index = end;
        return value;
    }

    // An Array node holds its element count, then one element subtree.
    private static object? ReadArray(UnityTypeNode[] tree, int arrayIndex, Reader reader, bool keep)
    {
        var count = reader.Int32();
        if (count < 0)
        {
            throw new InvalidDataException("An array has a negative length.");
        }

        var element = arrayIndex + 2;
        var elementNode = tree[element];
        var single = SubtreeEnd(tree, element) == SubtreeEnd(tree, arrayIndex);
        object? value = null;
        if (single && elementNode.Type is "UInt8" or "SInt8" or "char")
        {
            value = keep ? reader.Bytes(count).ToArray() : null;
            if (!keep)
            {
                reader.Skip(count);
            }
        }
        else if (!keep && single && elementNode.ByteSize > 0 && !elementNode.Aligned && IsPrimitive(elementNode.Type))
        {
            reader.Skip(checked(count * elementNode.ByteSize));
        }
        else
        {
            var list = keep ? new List<object?>(Math.Min(count, 1 << 16)) : null;
            for (var item = 0; item < count; item++)
            {
                var cursor = element;
                var itemValue = ReadValue(tree, ref cursor, reader, keep);
                list?.Add(itemValue);
            }

            value = list;
        }

        if (tree[arrayIndex].Aligned)
        {
            reader.Align();
        }

        return value;
    }

    private static object? ReadPrimitive(UnityTypeNode node, Reader reader, bool keep)
    {
        switch (node.Type)
        {
            case "bool" or "UInt8" or "SInt8" or "char":
                return (long)reader.Byte();
            case "SInt16" or "short" or "UInt16" or "unsigned short":
                return (long)reader.Int16();
            case "SInt32" or "int":
                return (long)reader.Int32();
            case "UInt32" or "unsigned int" or "Type*":
                return (long)reader.UInt32();
            case "SInt64" or "long long" or "UInt64" or "unsigned long long" or "FileSize":
                return reader.Int64();
            case "float":
                reader.Skip(sizeof(float));
                return null;
            case "double":
                reader.Skip(sizeof(double));
                return null;
            default:
                if (node.ByteSize < 0)
                {
                    throw new InvalidDataException($"The type tree leaf {node.Type} has no size.");
                }

                reader.Skip(node.ByteSize);
                return null;
        }
    }

    private static bool IsPrimitive(string type) => type is
        "bool" or "UInt8" or "SInt8" or "char" or "SInt16" or "short" or "UInt16" or "unsigned short" or
        "SInt32" or "int" or "UInt32" or "unsigned int" or "SInt64" or "long long" or "UInt64" or
        "unsigned long long" or "float" or "double";

    private static int SubtreeEnd(UnityTypeNode[] tree, int index)
    {
        var end = index + 1;
        while (end < tree.Length && tree[end].Level > tree[index].Level)
        {
            end++;
        }

        return end;
    }

    private static string String(ReadOnlySpan<byte> strings, uint offset)
    {
        if ((offset & CommonStringFlag) != 0)
        {
            var common = (int)(offset & ~CommonStringFlag);
            var commonEnd = CommonStrings.IndexOf('\0', common);
            return common < CommonStrings.Length && commonEnd >= 0 ? CommonStrings[common..commonEnd] : string.Empty;
        }

        if (offset >= strings.Length)
        {
            throw new InvalidDataException("A type tree string lies outside its table.");
        }

        var tail = strings[(int)offset..];
        var length = tail.IndexOf((byte)0);
        return Encoding.ASCII.GetString(length < 0 ? tail : tail[..length]);
    }

    // Unity's shared type tree string table, which trees reference by offset.
    private const string CommonStrings =
        "AABB\0AnimationClip\0AnimationCurve\0AnimationState\0Array\0Base\0BitField\0bitset\0bool\0char\0ColorRGBA\0" +
        "Component\0data\0deque\0double\0dynamic_array\0FastPropertyName\0first\0float\0Font\0GameObject\0" +
        "Generic Mono\0GradientNEW\0GUID\0GUIStyle\0int\0list\0long long\0map\0Matrix4x4f\0MdFour\0MonoBehaviour\0" +
        "MonoScript\0m_ByteSize\0m_Curve\0m_EditorClassIdentifier\0m_EditorHideFlags\0m_Enabled\0m_ExtensionPtr\0" +
        "m_GameObject\0m_Index\0m_IsArray\0m_IsStatic\0m_MetaFlag\0m_Name\0m_ObjectHideFlags\0m_PrefabInternal\0" +
        "m_PrefabParentObject\0m_Script\0m_StaticEditorFlags\0m_Type\0m_Version\0Object\0pair\0PPtr<Component>\0" +
        "PPtr<GameObject>\0PPtr<Material>\0PPtr<MonoBehaviour>\0PPtr<MonoScript>\0PPtr<Object>\0PPtr<Prefab>\0" +
        "PPtr<Sprite>\0PPtr<TextAsset>\0PPtr<Texture>\0PPtr<Texture2D>\0PPtr<Transform>\0Prefab\0Quaternionf\0" +
        "Rectf\0RectInt\0RectOffset\0second\0set\0short\0size\0SInt16\0SInt32\0SInt64\0SInt8\0staticvector\0string\0" +
        "TextAsset\0TextMesh\0Texture\0Texture2D\0Transform\0TypelessData\0UInt16\0UInt32\0UInt64\0UInt8\0" +
        "unsigned int\0unsigned long long\0unsigned short\0vector\0Vector2f\0Vector3f\0Vector4f\0" +
        "m_ScriptingClassIdentifier\0Gradient\0Type*\0int2_storage\0int3_storage\0BoundsInt\0" +
        "m_CorrespondingSourceObject\0m_PrefabInstance\0m_PrefabAsset\0FileSize\0Hash128\0RenderingLayerMask\0";

    private sealed class Reader(byte[] data, int position)
    {
        public int Position { get; private set; } = position;

        public byte Byte() => data[Position++];

        public short Int16() => BinaryPrimitives.ReadInt16LittleEndian(Bytes(sizeof(short)));

        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Bytes(sizeof(int)));

        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(sizeof(uint)));

        public long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Bytes(sizeof(long)));

        public ReadOnlySpan<byte> Bytes(int count)
        {
            var span = data.AsSpan(Position, count);
            Position += count;
            return span;
        }

        public void Skip(int count)
        {
            if (count < 0 || count > data.Length - Position)
            {
                throw new InvalidDataException("A serialized value runs past the file.");
            }

            Position += count;
        }

        public void SkipString()
        {
            var end = Array.IndexOf(data, (byte)0, Position);
            if (end < 0)
            {
                throw new InvalidDataException("A serialized string is unterminated.");
            }

            Position = end + 1;
        }

        public void Align() => Position = (Position + 3) & ~3;
    }
}
