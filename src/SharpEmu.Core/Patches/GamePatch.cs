// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using SharpEmu.Core.Loader;
using SharpEmu.HLE;

namespace SharpEmu.Core.Patches;

public sealed record GamePatchWrite(ulong Offset, byte[] Bytes);

public sealed record GamePatch(
    string Id, string FileName, string Name, string Author, string Note,
    string AppVersion, string Executable, IReadOnlyList<GamePatchWrite> Writes, string? Error)
{
    public string PatchVersion { get; init; } = string.Empty;

    public bool MatchesVersion(string? version) =>
        !string.IsNullOrWhiteSpace(version) &&
        (string.Equals(AppVersion, version, StringComparison.OrdinalIgnoreCase) ||
         Version.TryParse(AppVersion, out var expected) && Version.TryParse(version, out var actual) && expected == actual);

    internal static GamePatch Parse(string fileName, XElement metadata)
    {
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            fileName + "\n" + metadata.ToString(SaveOptions.DisableFormatting))));
        var name = (string?)metadata.Attribute("Name") ?? string.Empty;
        var author = (string?)metadata.Attribute("Author") ?? string.Empty;
        var note = (string?)metadata.Attribute("Note") ?? string.Empty;
        var version = (string?)metadata.Attribute("AppVer") ?? string.Empty;
        var executable = (string?)metadata.Attribute("AppElf") ?? string.Empty;
        var writes = new List<GamePatchWrite>();
        string? error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version) ||
                string.IsNullOrWhiteSpace(executable) || executable.IndexOfAny(['/', '\\', ':']) >= 0)
            {
                throw new FormatException("Name, AppVer and an AppElf file name are required.");
            }

            var imageBase = Hex((string?)metadata.Attribute("ImageBase") ?? "0");
            var list = metadata.Element("PatchList") ?? throw new FormatException("PatchList is missing.");
            var totalBytes = 0;
            foreach (var line in list.Elements())
            {
                if (line.Name != "Line" || line.HasElements || (string?)line.Attribute("Type") != "bytes" ||
                    line.Attributes().Any(attribute => attribute.Name != "Type" && attribute.Name != "Address" && attribute.Name != "Value"))
                {
                    throw new FormatException("Only bytes lines with Address and Value are supported.");
                }

                var address = Hex((string?)line.Attribute("Address") ?? string.Empty);
                var value = (string?)line.Attribute("Value") ?? string.Empty;
                var bytes = Convert.FromHexString(string.Concat(value.Where(character => !char.IsWhiteSpace(character))));
                totalBytes = checked(totalBytes + bytes.Length);
                if (address < imageBase || bytes.Length == 0 || totalBytes > 1 << 20 || writes.Count >= 4096)
                {
                    throw new FormatException("A patch line has an invalid address or size.");
                }

                writes.Add(new GamePatchWrite(address - imageBase, bytes));
            }

            if (writes.Count == 0)
            {
                throw new FormatException("PatchList is empty.");
            }
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            error = exception.Message;
            writes.Clear();
        }

        return new GamePatch(id, fileName, name, author, note, version, executable, writes, error)
        {
            PatchVersion = (string?)metadata.Attribute("PatchVer") ?? string.Empty,
        };
    }

    private static ulong Hex(string value) => ulong.Parse(
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value,
        NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    internal bool TryApply(ICpuMemory memory, IReadOnlyList<ProgramHeader> headers, ulong loadBias, out string? error)
    {
        error = Error;
        if (error is not null || Writes.Count == 0)
        {
            return false;
        }

        var segments = headers.Where(header => header.HeaderType == ProgramHeaderType.Load && header.MemorySize > 0).ToArray();
        var prepared = new List<(ulong Address, byte[] Bytes, byte[] Original)>();
        try
        {
            if (segments.Length == 0)
            {
                throw new InvalidDataException("The executable has no loadable segments.");
            }

            var imageBase = checked(loadBias + segments.Min(header => header.VirtualAddress));
            foreach (var write in Writes)
            {
                var address = checked(imageBase + write.Offset);
                var end = checked(address + (ulong)write.Bytes.Length);
                if (!segments.Any(segment => address >= checked(loadBias + segment.VirtualAddress) &&
                    end <= checked(loadBias + segment.VirtualAddress + segment.MemorySize)))
                {
                    throw new InvalidDataException($"Patch range 0x{address:X}-0x{end:X} is outside the executable's segments.");
                }

                var original = new byte[write.Bytes.Length];
                if (!memory.TryRead(address, original))
                {
                    throw new InvalidDataException($"Patch address 0x{address:X} is not readable.");
                }

                prepared.Add((address, write.Bytes, original));
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException)
        {
            error = exception.Message;
            return false;
        }

        for (var index = 0; index < prepared.Count; index++)
        {
            var write = prepared[index];
            if (memory.TryWrite(write.Address, write.Bytes))
            {
                continue;
            }

            // Include the failing write: a memory implementation may have written part of it.
            for (var rollback = index; rollback >= 0; rollback--)
            {
                var previous = prepared[rollback];
                if (!memory.TryWrite(previous.Address, previous.Original))
                {
                    throw new InvalidOperationException($"Could not restore patch memory at 0x{previous.Address:X}.");
                }
            }

            error = $"Could not write patch memory at 0x{write.Address:X}; original bytes restored.";
            return false;
        }

        return true;
    }
}
