// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using Iced.Intel;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;
using static Iced.Intel.AssemblerRegisters;

namespace SharpEmu.Libs.Tests.Cpu;

// The native libSceAgc builders against their managed exports: the same random arguments and memory, then the same
// return value and the same bytes everywhere in the region. A builder that sends a call to the managed export must not
// have written anything.
public sealed unsafe class NativeAgcBuilderTests
{
    private const int RegionBytes = 0x40000;
    private const int CommandBuffer = 0x100;
    private const int Stack = 0x800;
    private const int CommandArea = 0x1000;
    private const int Data = 0x10000;
    private const ulong Fallback = 0xFA11BAC4FA11BAC4UL;

    private delegate void Setup(System.Random random, byte* region, ulong[] args);

    private static readonly Dictionary<string, (Func<CpuContext, int> Managed, Setup Setup)> Builders = new()
    {
        ["aJf+j5yntiU"] = (AgcExports.DcbEventWrite, EventWriteArgs),
        ["cFazmnXpJOE"] = (AgcExports.AcbEventWrite, EventWriteArgs),
        ["vcmNN+AAXnY"] = (AgcExports.SetCxRegIndirectPatchSetAddress, (r, m, a) => PatchArgs(r, m, a, 0x9F)),
        ["Qrj4c+61z4A"] = (AgcExports.SetShRegIndirectPatchSetAddress, (r, m, a) => PatchArgs(r, m, a, 0x63)),
        ["6lNcCp+fxi4"] = (AgcExports.SetUcRegIndirectPatchSetAddress, (r, m, a) => PatchArgs(r, m, a, 0x64)),
        ["57labkp+rSQ"] = (AgcExports.DcbAcquireMem, DcbAcquireMemArgs),
        ["KT-hTp-Ch14"] = (AgcExports.AcbAcquireMem, AcbAcquireMemArgs),
        ["VmW0Tdpy420"] = (AgcExports.DcbWaitRegMem, DcbWaitRegMemArgs),
        ["htn36gPnBk4"] = (AgcExports.AcbWaitRegMem, AcbWaitRegMemArgs),
        ["wr23dPKyWc0"] = (AgcExports.CbReleaseMem, ReleaseMemArgs),
        ["i1jyy49AjXU"] = (AgcExports.DcbWriteData, WriteDataArgs),
        ["eZ4+17OQz4Q"] = (AgcExports.AcbWriteData, WriteDataArgs),
        ["j3EtxFkSIhQ"] = (AgcExports.AcbDispatchIndirect, RandomArgs),
        ["CtB+A9-VxO0"] = (AgcExports.DcbDispatchIndirect, RandomArgs),
        ["Yw0jKSqop+E"] = (AgcExports.DcbDrawIndexAuto, RandomArgs),
        ["RmaJwLtc8rY"] = (AgcExports.DcbSetBaseIndirectArgs, RandomArgs),
        ["tSBxhAPyytQ"] = (AgcExports.DcbSetNumInstances, RandomArgs),
        ["UZbQjYAwwXM"] = (AgcExports.CbSetShRegistersDirect, ShRegistersArgs),
        ["dbOlWdppb4o"] = (AgcExports.CreateInterpolantMapping2, InterpolantArgs),
    };

    public static TheoryData<string> Nids => new(Builders.Keys);

    [Theory]
    [MemberData(nameof(Nids))]
    public void NativeBuilderMatchesTheManagedExport(string nid)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;

        var (managed, setup) = Builders[nid];
        var fallback = Place(a => { a.mov(rax, Fallback); a.ret(); });
        var assemble = typeof(DirectExecutionBackend).GetMethod("AssembleNativeAgc", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(assemble);
        var native = (nint)assemble.Invoke(null, [nid, fallback])!;
        Assert.NotEqual(0, native);
        var call = Place(Adapter);

        var region = (byte*)NativeMemory.AlignedAlloc(RegionBytes, 4096);
        var initial = new byte[RegionBytes];
        var random = new System.Random(nid.GetHashCode(StringComparison.Ordinal) ^ 0x5EED);
        var nativeRuns = 0;
        try
        {
            for (var iteration = 0; iteration < 20000; iteration++)
            {
                var span = new Span<byte>(region, RegionBytes);
                random.NextBytes(span[Data..]);
                span[..Data].Clear();
                var up = (ulong)(region + CommandArea + random.Next(0, 64) * 4);
                *(ulong*)(region + CommandBuffer + 0x10) = up;
                *(ulong*)(region + CommandBuffer + 0x18) = random.Next(8) switch
                {
                    0 => up - 64,
                    1 => up + (ulong)random.Next(0, 80) * 4,
                    _ => up + 0x8000,
                };
                *(uint*)(region + CommandBuffer + 0x30) = random.Next(4) == 0 ? (uint)random.Next(0, 16) : 0;
                var args = new ulong[12];
                args[0] = (ulong)(region + CommandBuffer);
                setup(random, region, args);
                for (var index = 0; index < 6; index++) *(ulong*)(region + Stack + 8 + index * 8) = args[6 + index];
                span.CopyTo(initial);

                var memory = new FakeCpuMemory((ulong)region, RegionBytes);
                Assert.True(memory.TryWrite((ulong)region, initial));
                var ctx = new CpuContext(memory, Generation.Gen5);
                CpuRegister[] registers = [CpuRegister.Rdi, CpuRegister.Rsi, CpuRegister.Rdx, CpuRegister.Rcx, CpuRegister.R8, CpuRegister.R9];
                for (var index = 0; index < 6; index++) ctx[registers[index]] = args[index];
                ctx[CpuRegister.Rsp] = (ulong)(region + Stack);
                ctx[CpuRegister.Rax] = 0;
                managed(ctx);
                var expected = new byte[RegionBytes];
                Assert.True(memory.TryRead((ulong)region, expected));

                ulong result;
                fixed (ulong* packed = args)
                {
                    result = ((delegate* unmanaged<nint, ulong*, ulong>)call)(native, packed);
                }

                var actual = new ReadOnlySpan<byte>(region, RegionBytes);
                if (result == Fallback)
                {
                    Assert.True(actual.SequenceEqual(initial), $"{nid} #{iteration}: wrote memory before going to the managed export");
                    continue;
                }

                nativeRuns++;
                var managedResult = ctx[CpuRegister.Rax];
                Assert.True(result == managedResult, $"{nid} #{iteration}: native 0x{result:X} managed 0x{managedResult:X} args {Describe(args)}");
                if (!actual.SequenceEqual(expected))
                {
                    var at = 0;
                    while (actual[at] == expected[at]) at++;
                    Assert.Fail($"{nid} #{iteration}: byte +0x{at:X} native 0x{actual[at]:X2} managed 0x{expected[at]:X2} args {Describe(args)}");
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(region);
        }

        Assert.True(nativeRuns > 1000, $"{nid}: only {nativeRuns} calls stayed native");
    }

    private static string Describe(ulong[] args) => string.Join(' ', args.Select(static value => $"0x{value:X}"));

    // Win64 (code=rcx, args=rdx) to SysV: six register arguments, then six stack arguments.
    private static void Adapter(Assembler a)
    {
		a.push(rdi);
		a.push(rsi);
		a.push(rbx);
		a.mov(r11, rcx);
		a.mov(rbx, rdx);
		a.sub(rsp, 48);
		for (var index = 0; index < 6; index++)
		{
			a.mov(rax, __qword_ptr[rbx + 48 + index * 8]);
			a.mov(__qword_ptr[rsp + index * 8], rax);
		}

		a.mov(rdi, __qword_ptr[rbx]);
		a.mov(rsi, __qword_ptr[rbx + 8]);
		a.mov(rdx, __qword_ptr[rbx + 16]);
		a.mov(rcx, __qword_ptr[rbx + 24]);
		a.mov(r8, __qword_ptr[rbx + 32]);
		a.mov(r9, __qword_ptr[rbx + 40]);
		a.call(r11);
		a.add(rsp, 48);
		a.pop(rbx);
		a.pop(rsi);
		a.pop(rdi);
		a.ret();
    }

    private static nint Place(Action<Assembler> emit)
    {
        var a = new Assembler(64);
        emit(a);
        var page = (byte*)HostMemory.Alloc(null, 4096, HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE, HostMemory.PAGE_EXECUTE_READWRITE);
        Assert.True(page != null);
        a.Assemble(new PointerCodeWriter(page), (ulong)page);
        return (nint)page;
    }

    private sealed class PointerCodeWriter(byte* start) : CodeWriter
    {
        private int _offset;
        public override void WriteByte(byte value) => start[_offset++] = value;
    }

    private static ulong Any(System.Random random) => (ulong)random.NextInt64() ^ ((ulong)random.Next(2) << 63);

    private static ulong Pick(System.Random random, params ulong[] values) =>
        random.Next(4) == 0 ? Any(random) : values[random.Next(values.Length)];

    private static ulong Junk(System.Random random, ulong low) => random.Next(2) == 0 ? low : low | (Any(random) & ~0xFFFFUL);

    private static void RandomArgs(System.Random random, byte* region, ulong[] args)
    {
        for (var index = 1; index < 12; index++) args[index] = Any(random);
        if (random.Next(16) == 0) args[0] = 0;
    }

    private static void EventWriteArgs(System.Random random, byte* region, ulong[] args)
    {
        RandomArgs(random, region, args);
        args[1] = Junk(random, Pick(random, 0, 1, 7, 0x0F, 0x10, 0x38, 0x39, 0x3A, 0x3F, 0x40, 0xFF, (ulong)random.Next(0x40)));
    }

    private static void PatchArgs(System.Random random, byte* region, ulong[] args, uint op)
    {
        var packet = region + Data;
        var header = random.Next(4) switch
        {
            0 => 0xC0001000u | (2u << 16) | (0x2Eu << 2),
            1 => (uint)Any(random),
            2 => 0xC0000000u | (3u << 16) | ((op ^ 1) << 8),
            _ => 0xC0000000u | (3u << 16) | (op << 8),
        };
        *(uint*)packet = header;
        args[0] = random.Next(16) == 0 ? 0 : (ulong)packet;
        args[1] = random.Next(16) == 0 ? 0 : Any(random);
    }

    private static ulong AcquireBase(System.Random random) => Pick(random, 0, 0x1000, 0x10F0, (1UL << 40) - 0x100, 1UL << 40,
        (ulong)random.NextInt64(1L << 40));

    private static ulong AcquireSize(System.Random random) => Pick(random, 0, ulong.MaxValue, 0x20, 0x300, 1UL << 40, (1UL << 40) - 1,
        (ulong)random.NextInt64(1L << 40), (ulong)random.Next(0x10000));

    private static void DcbAcquireMemArgs(System.Random random, byte* region, ulong[] args)
    {
        RandomArgs(random, region, args);
        args[1] = Junk(random, Pick(random, 0, 1, 2));
        args[4] = AcquireBase(random);
        args[5] = AcquireSize(random);
    }

    private static void AcbAcquireMemArgs(System.Random random, byte* region, ulong[] args)
    {
        RandomArgs(random, region, args);
        args[2] = AcquireBase(random);
        args[3] = AcquireSize(random);
    }

    private static void DcbWaitRegMemArgs(System.Random random, byte* region, ulong[] args)
    {
        RandomArgs(random, region, args);
        args[1] = Junk(random, Pick(random, 0, 1, 2));
        args[2] = Junk(random, (ulong)random.Next(9));
        args[3] = Junk(random, Pick(random, 0, 1, 2, 4, 5));
        args[4] = Junk(random, (ulong)random.Next(5));
        args[8] = Pick(random, 0, 15, 16, 0xFFFF0, 0x100000, uint.MaxValue);
    }

    private static void AcbWaitRegMemArgs(System.Random random, byte* region, ulong[] args)
    {
        RandomArgs(random, region, args);
        args[1] = Junk(random, Pick(random, 0, 1, 2));
        args[2] = Junk(random, (ulong)random.Next(9));
        args[3] = Junk(random, (ulong)random.Next(5));
        args[7] = Pick(random, 0, 15, 16, 0xFFFF0, 0x100000, uint.MaxValue);
    }

    private static void ReleaseMemArgs(System.Random random, byte* region, ulong[] args)
    {
        RandomArgs(random, region, args);
        args[2] = Junk(random, Pick(random, 0, 0x1234));
        args[3] = Junk(random, Pick(random, 0, 1, 1, 2));
        args[5] = Pick(random, 0, 0x1000, 0x1004, 0x1008, 0x1003);
        if (random.Next(2) == 0) args[2] &= ~0xFFFFUL;
        args[6] = Junk(random, Pick(random, 0, 1, 2, 3, 4));
        args[8] = Junk(random, random.Next(8) == 0 ? 1UL : 0UL);
        args[9] = Junk(random, Pick(random, 0, 1, 2, 3));
        args[10] = Junk(random, Pick(random, 0, 1, 3, 4, 5, 6, 7));
    }

    private static void WriteDataArgs(System.Random random, byte* region, ulong[] args)
    {
        RandomArgs(random, region, args);
        args[4] = random.Next(6) switch
        {
            0 => 0,
            1 => *(ulong*)(region + CommandBuffer + 0x10) + (ulong)random.Next(-8, 64) * 4,
            _ => (ulong)(region + Data + random.Next(0, 64) * 4),
        };
        args[5] = Junk(random, Pick(random, 0, 1, 2, 7, 40, 0x3FFD, 0x3FFE, (ulong)random.Next(64)));
    }

    private static void ShRegistersArgs(System.Random random, byte* region, ulong[] args)
    {
        RandomArgs(random, region, args);
        var count = random.Next(8) == 0 ? random.Next(4090, 4100) : random.Next(0, 20);
        var table = random.Next(8) == 0
            ? (byte*)*(ulong*)(region + CommandBuffer + 0x10) + random.Next(-16, 64) * 4
            : region + Data;
        var offset = (uint)random.Next(0, 0x400);
        uint[] title = [0x20C, 0x20D, 0x22A, 0x22A, 0x212, 0x213, 0x228, 0x207, 0x208, 0x209];
        var pattern = random.Next(4);
        if (pattern == 0) count = 10;
        for (var index = 0; index < Math.Min(count, 24); index++)
        {
            offset += random.Next(4) switch { 0 => 2u, 1 => (uint)random.Next(1, 300), _ => 1u };
            if (random.Next(40) == 0) offset -= (uint)random.Next(0, 3);
            if (random.Next(200) == 0) offset |= 0xFFFF0000u;
            *(uint*)(table + index * 8) = pattern switch
            {
                0 => title[index],
                1 => (uint)random.Next(0x200, 0x210),
                2 => offset,
                _ => (uint)random.Next(0, 3) == 0 ? (uint)Any(random) : offset,
            };
            *(uint*)(table + index * 8 + 4) = (uint)Any(random);
        }

        // Shuffle some tables (equal offsets keep distinct values, so the order between them shows).
        if (random.Next(2) == 0)
        {
            var n = Math.Min(count, 24);
            for (var index = n - 1; index > 0; index--)
            {
                var other = random.Next(index + 1);
                (*(ulong*)(table + index * 8), *(ulong*)(table + other * 8)) = (*(ulong*)(table + other * 8), *(ulong*)(table + index * 8));
            }
        }

        args[1] = random.Next(16) == 0 ? 0 : (ulong)table;
        args[2] = Junk(random, (ulong)count);
    }

    private static void InterpolantArgs(System.Random random, byte* region, ulong[] args)
    {
        var registers = region + Data + 0x4000;
        var gs = region + Data + 0x5000;
        var ps = region + Data + 0x6000;
        var inputs = region + Data + 0x7000;
        var outputs = region + Data + 0x8000;
        args[0] = random.Next(32) == 0 ? 0 : random.Next(32) == 0 ? (ulong)inputs + 64 : (ulong)registers;
        args[1] = random.Next(16) == 0 ? 0 : (ulong)gs;
        args[2] = random.Next(8) == 0 ? 0 : (ulong)ps;
        *(ulong*)(ps + 0x30) = random.Next(16) == 0 ? 0 : (ulong)inputs;
        *(uint*)(ps + 0x50) = (uint)Pick(random, 0, 1, 5, 16, 32, 33, (ulong)random.Next(33));
        *(ulong*)(gs + 0x38) = random.Next(16) == 0 ? 0 : (ulong)outputs;
        // Tables stay inside the region: the native builder, like the title's own code, does not check guest bounds.
        *(ushort*)(gs + 0x56) = (ushort)(Pick(random, 0, 1, 8, 40, (ulong)random.Next(40)) % 0x2000);
        for (var index = 0; index < 48; index++)
        {
            *(uint*)(inputs + index * 4) = ((uint)Any(random) & 0xFFFFFF00u) | (uint)random.Next(0, 12);
            *(uint*)(outputs + index * 4) = ((uint)Any(random) & 0xFFFFFF00u) | (uint)random.Next(0, 12);
        }
    }
}
