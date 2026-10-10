// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using Iced.Intel;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Prebuild;
using static Iced.Intel.AssemblerRegisters;

namespace SharpEmu.Core.Runtime;

// Runs the Oodle decoder statically linked into a title, so SharpEmu never ships one.
internal sealed class GuestOodle : IOodleDecompressor
{
    // Errors only OodleLZ_Decompress reports. Every Oodle SDK build carries them,
    // so they locate the function in titles with stripped symbols.
    private static readonly string[] DecompressErrors =
    [
        "OODLE ERROR : OodleLZ_Decompress: asked for len 0",
        "OODLE ERROR : OodleLZ_Decompress : decoder memory given, but too small!",
        "OODLE ERROR : OodleLZ_Decompress: asked to decode Legacy compressor; not supported!",
        "OODLE ERROR : LZ corruption : OodleLZ_Decompress failed",
    ];

    private const int MinimumAgreeingReferences = 2;
    private const int MaxFunctionLength = 0x10000;
    private const ulong StubSize = 0x1000;
    private const ulong DataOffset = 0x10000;
    private const ulong DataSize = 0x2000_0000;
    private const ulong DecoderMemorySize = 0x80_0000;
    private const ulong TableSize = 0x10_0000;
    private const int TableHeaderSize = 32;
    private const int JobSize = 40;
    private const int FuzzSafeYes = 1;
    private const int DecodeThreadPhaseAll = 3;
    private static readonly ulong ScratchBaseAddress = OperatingSystem.IsWindows() ? 0x7FFC_0000_0000UL : 0x6FFC_0000_0000UL;

    private readonly IVirtualMemory _memory;
    private readonly ICpuDispatcher _dispatcher;
    private readonly ulong _decompress;
    private readonly Generation _generation;
    private readonly IReadOnlyDictionary<ulong, string> _importStubs;
    private readonly IReadOnlyDictionary<string, ulong> _runtimeSymbols;
    private readonly CpuExecutionOptions _executionOptions;
    private ulong _stub;

    private GuestOodle(
        IVirtualMemory memory,
        ICpuDispatcher dispatcher,
        ulong decompress,
        Generation generation,
        IReadOnlyDictionary<ulong, string> importStubs,
        IReadOnlyDictionary<string, ulong> runtimeSymbols,
        CpuExecutionOptions executionOptions)
    {
        _memory = memory;
        _dispatcher = dispatcher;
        _decompress = decompress;
        _generation = generation;
        _importStubs = importStubs;
        _runtimeSymbols = runtimeSymbols;
        _executionOptions = executionOptions;
    }

    public ulong DecompressAddress => _decompress;

    public long MaxBatchBytes => (long)(DataSize - DecoderMemorySize - TableSize);

    public static GuestOodle? TryCreate(
        SelfImage image,
        IVirtualMemory memory,
        ICpuDispatcher dispatcher,
        Generation generation,
        IReadOnlyDictionary<ulong, string> importStubs,
        IReadOnlyDictionary<string, ulong> runtimeSymbols,
        CpuExecutionOptions executionOptions)
    {
        var decompress = FindDecompress(image, memory);
        return decompress == 0
            ? null
            : new GuestOodle(memory, dispatcher, decompress, generation, importStubs, runtimeSymbols, executionOptions);
    }

    public void Decompress(IReadOnlyList<OodleJob> jobs)
    {
        var data = MapScratch() + DataOffset;
        var decoderMemory = data + TableSize;
        if ((ulong)(TableHeaderSize + (jobs.Count * JobSize)) > TableSize)
        {
            throw new ArgumentException("Too many Oodle jobs for one batch.", nameof(jobs));
        }

        var table = new byte[TableHeaderSize + (jobs.Count * JobSize)];
        BinaryPrimitives.WriteUInt64LittleEndian(table, (ulong)jobs.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(8), _decompress);
        BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(16), decoderMemory);
        BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(24), DecoderMemorySize);
        var cursor = decoderMemory + DecoderMemorySize;
        var destinations = new ulong[jobs.Count];
        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            var source = cursor;
            destinations[i] = source + (ulong)job.Source.Length;
            cursor = destinations[i] + (ulong)job.Destination.Length;
            if (cursor > data + DataSize || !_memory.TryWrite(source, job.Source))
            {
                throw new InvalidOperationException("Oodle batch does not fit the guest scratch region.");
            }

            var entry = table.AsSpan(TableHeaderSize + (i * JobSize));
            BinaryPrimitives.WriteUInt64LittleEndian(entry, source);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], (ulong)job.Source.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], destinations[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[24..], (ulong)job.Destination.Length);
        }

        if (!_memory.TryWrite(data, table))
        {
            throw new InvalidOperationException("Oodle job table cannot be written to guest memory.");
        }

        var result = _dispatcher.DispatchHostCall(_stub, data, _generation, _importStubs, _runtimeSymbols, _executionOptions);
        if (result != OrbisGen2Result.ORBIS_GEN2_OK)
        {
            throw new InvalidOperationException($"Guest Oodle batch failed: {result}.");
        }

        if (!_memory.TryRead(data, table))
        {
            throw new InvalidOperationException("Oodle job table cannot be read back from guest memory.");
        }

        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            job.Result = BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(TableHeaderSize + (i * JobSize) + 32));
            if (job.Result == job.Destination.Length && !_memory.TryRead(destinations[i], job.Destination))
            {
                job.Result = 0;
            }
        }
    }

    private ulong MapScratch()
    {
        if (_stub != 0)
        {
            return _stub;
        }

        var stub = Assemble(ScratchBaseAddress);
        var code = new byte[StubSize];
        stub.CopyTo(code, 0);
        _memory.Map(ScratchBaseAddress, StubSize, fileOffset: 0, code, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
        _memory.Map(ScratchBaseAddress + DataOffset, DataSize, fileOffset: 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        return _stub = ScratchBaseAddress;
    }

    // void run(table*): for each job, job.result = OodleLZ_Decompress(job.src, job.srcLen,
    // job.dst, job.dstLen, FuzzSafe_Yes, CheckCRC_No, Verbosity_None, null, 0, null, null,
    // table.decoderMemory, table.decoderMemorySize, ThreadPhaseAll).
    private static byte[] Assemble(ulong address)
    {
        var a = new Assembler(64);
        var loop = a.CreateLabel();
        var done = a.CreateLabel();
        a.push(rbp);
        a.mov(rbp, rsp);
        a.push(rbx);
        a.push(r12);
        a.push(r13);
        a.push(r14);
        a.push(r15);
        a.sub(rsp, 8);
        a.mov(rbx, rdi);
        a.mov(r12, __qword_ptr[rbx]);
        a.mov(r13, __qword_ptr[rbx + 8]);
        a.mov(r14, __qword_ptr[rbx + 16]);
        a.mov(r15, __qword_ptr[rbx + 24]);
        a.add(rbx, TableHeaderSize);
        a.Label(ref loop);
        a.test(r12, r12);
        a.jz(done);
        a.sub(rsp, 64);
        a.mov(__qword_ptr[rsp], 0);
        a.mov(__qword_ptr[rsp + 8], 0);
        a.mov(__qword_ptr[rsp + 16], 0);
        a.mov(__qword_ptr[rsp + 24], 0);
        a.mov(__qword_ptr[rsp + 32], 0);
        a.mov(__qword_ptr[rsp + 40], r14);
        a.mov(__qword_ptr[rsp + 48], r15);
        a.mov(__qword_ptr[rsp + 56], DecodeThreadPhaseAll);
        a.mov(rdi, __qword_ptr[rbx]);
        a.mov(rsi, __qword_ptr[rbx + 8]);
        a.mov(rdx, __qword_ptr[rbx + 16]);
        a.mov(rcx, __qword_ptr[rbx + 24]);
        a.mov(r8d, FuzzSafeYes);
        a.xor(r9d, r9d);
        a.call(r13);
        a.add(rsp, 64);
        a.mov(__qword_ptr[rbx + 32], rax);
        a.add(rbx, JobSize);
        a.dec(r12);
        a.jmp(loop);
        a.Label(ref done);
        // The backend reports a non-zero return from a host call as a failure.
        a.xor(eax, eax);
        a.add(rsp, 8);
        a.pop(r15);
        a.pop(r14);
        a.pop(r13);
        a.pop(r12);
        a.pop(rbx);
        a.pop(rbp);
        a.ret();
        using var stream = new MemoryStream();
        a.Assemble(new StreamCodeWriter(stream), address);
        return stream.ToArray();
    }

    private static ulong FindDecompress(SelfImage image, IVirtualMemory memory)
    {
        var code = new List<(ulong Address, byte[] Bytes)>();
        var strings = new List<ulong>();
        foreach (var region in image.MappedRegions)
        {
            // The loader maps read-only data together with code, so strings are
            // searched in every region regardless of its protection.
            var bytes = new byte[region.MemorySize];
            if (!memory.TryRead(region.VirtualAddress, bytes))
            {
                continue;
            }

            if ((region.Protection & ProgramHeaderFlags.Execute) != 0)
            {
                code.Add((region.VirtualAddress, bytes));
            }

            foreach (var error in DecompressErrors)
            {
                var offset = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(error));
                if (offset >= 0)
                {
                    strings.Add(region.VirtualAddress + (ulong)offset);
                }
            }
        }

        var functionStarts = FunctionStarts(image, memory);
        if (functionStarts is null)
        {
            return 0;
        }

        var votes = new Dictionary<ulong, int>();
        foreach (var (address, bytes) in code)
        {
            for (var i = 0; i + 7 <= bytes.Length; i++)
            {
                // lea r64, [rip + disp32]
                if ((bytes[i] & 0xFB) != 0x48 || bytes[i + 1] != 0x8D || (bytes[i + 2] & 0xC7) != 0x05)
                {
                    continue;
                }

                var reference = address + (ulong)i;
                var target = reference + 7 + (ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i + 3));
                if (!strings.Contains(target))
                {
                    continue;
                }

                // The function is the last one starting at or before the reference.
                var index = Array.BinarySearch(functionStarts, reference);
                index = index >= 0 ? index : ~index - 1;
                if (index >= 0 && reference - functionStarts[index] < MaxFunctionLength)
                {
                    var function = functionStarts[index];
                    votes[function] = votes.GetValueOrDefault(function) + 1;
                }
            }
        }

        var best = votes.OrderByDescending(vote => vote.Value).FirstOrDefault();
        return best.Value >= MinimumAgreeingReferences ? best.Key : 0;
    }

    // Every function's start address, from the sorted search table of .eh_frame_hdr.
    // Prologue shapes vary across compilers and inlining, unwind tables do not.
    private static ulong[]? FunctionStarts(SelfImage image, IVirtualMemory memory)
    {
        const byte EhFrameHdrVersion = 1;
        const byte DataRelativeSigned4 = 0x3B;
        const byte Format4Bytes = 0x03;
        const byte FormatMask = 0x07;
        var imageBase = image.EntryPoint >= image.ElfHeader.EntryPoint ? image.EntryPoint - image.ElfHeader.EntryPoint : 0;
        foreach (var header in image.ProgramHeaders)
        {
            if (header.HeaderType != ProgramHeaderType.GnuEhFrame)
            {
                continue;
            }

            var address = imageBase + header.VirtualAddress;
            Span<byte> head = stackalloc byte[12];
            if (!memory.TryRead(address, head) ||
                head[0] != EhFrameHdrVersion ||
                (head[1] & FormatMask) != Format4Bytes ||
                (head[2] & FormatMask) != Format4Bytes ||
                head[3] != DataRelativeSigned4)
            {
                return null;
            }

            var count = BinaryPrimitives.ReadInt32LittleEndian(head[8..]);
            var table = new byte[count * 8];
            if (count <= 0 || !memory.TryRead(address + 12, table))
            {
                return null;
            }

            var starts = new ulong[count];
            for (var i = 0; i < count; i++)
            {
                starts[i] = address + (ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(i * 8));
            }

            Array.Sort(starts);
            return starts;
        }

        return null;
    }
}
