// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// Gen5ShaderScalarEvaluator.TryEvaluate rents pooled global-memory buffers as it
// discovers bindings and only hands them to the caller (which returns them) when
// evaluation succeeds. Its many early `return false` paths must not strand those
// buffers — on a shader-heavy title that was a per-dispatch 16 MiB leak that
// OOM'd the AGC wait monitor within minutes.
//
// The test swaps the process-wide Gen5ShaderScalarEvaluator.GlobalMemoryPool, so
// it must not run alongside any other test that calls TryEvaluate.
[CollectionDefinition(nameof(GlobalMemoryPoolSwapCollection), DisableParallelization = true)]
public sealed class GlobalMemoryPoolSwapCollection;

[Collection(nameof(GlobalMemoryPoolSwapCollection))]
public sealed class Gen5ScalarEvaluatorPooledBufferLeakTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint SEndpgm = 0xBF810000;

    [Fact]
    public void FailedEvaluationReturnsEveryPooledBufferItRented()
    {
        // flat_load_ubyte v0, v[1:2]   with v[1:2] = s[12:13] + v6  → base = ShaderAddress (readable → rents a pooled buffer, binding added)
        // flat_load_ubyte v3, v[4:5]   with v[4:5] = s[20:21] + v7  → base = 0 (s20/s21 unseeded) → TryEvaluate bails with "global-address-null"
        // Without the fix the first binding's 16 MiB pooled array is never returned.
        uint[] words =
        [
            // v_add_co_u32 v1, vcc_lo, s12, v6
            0xD70F6A01, 0x00020C0C,
            // v_add_co_ci_u32 v2, vcc_lo, 0, s13, vcc_lo
            0x50041AF9, 0x86860680,
            // flat_load_ubyte v0, v[1:2]
            0xDC200000, 0x007D0001,
            // v_add_co_u32 v4, vcc_lo, s20, v7
            0xD70F6A04, 0x00020E14,
            // v_add_co_ci_u32 v5, vcc_lo, 0, s21, vcc_lo
            0x500A2AF9, 0x86860680,
            // flat_load_ubyte v3, v[4:5]
            0xDC200000, 0x007D0304,
            SEndpgm,
        ];

        var memory = new TestCpuMemory(ShaderAddress, 0x4000);
        var shader = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                shader.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, shader));
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out var program,
                out var decodeError),
            decodeError);

        uint[] userData =
        [
            unchecked((uint)ShaderAddress),
            unchecked((uint)(ShaderAddress >> 32)),
        ];
        var state = new Gen5ShaderState(
            program,
            userData,
            null,
            UserDataScalarRegisterBase: 12);

        var countingPool = new CountingArrayPool(ArrayPool<byte>.Shared);
        var previousPool = Gen5ShaderScalarEvaluator.GlobalMemoryPool;
        Gen5ShaderScalarEvaluator.GlobalMemoryPool = countingPool;
        try
        {
            var succeeded = Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out _,
                out var error);

            Assert.False(succeeded, "expected the second flat load to fail evaluation");
            Assert.Contains("global-address-null", error);
        }
        finally
        {
            Gen5ShaderScalarEvaluator.GlobalMemoryPool = previousPool;
        }

        Assert.True(
            countingPool.Rented > 0,
            "the first flat load should have rented at least one pooled buffer");
        Assert.Equal(countingPool.Rented, countingPool.Returned);
        Assert.Equal(0, countingPool.Outstanding);
    }

    private sealed class CountingArrayPool(ArrayPool<byte> inner) : ArrayPool<byte>
    {
        private readonly HashSet<byte[]> _live =
            new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

        public int Rented { get; private set; }

        public int Returned { get; private set; }

        public int Outstanding => _live.Count;

        public override byte[] Rent(int minimumLength)
        {
            var array = inner.Rent(minimumLength);
            lock (_live)
            {
                Rented++;
                _live.Add(array);
            }

            return array;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            lock (_live)
            {
                if (_live.Remove(array))
                {
                    Returned++;
                }
            }

            inner.Return(array, clearArray);
        }
    }

    private sealed class TestCpuMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _storage = new byte[size];

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < baseAddress)
            {
                return false;
            }

            var relative = virtualAddress - baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}
