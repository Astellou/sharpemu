// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu;

public interface ICpuDispatcher
{
    CpuTrapInfo? LastTrapInfo { get; }

    CpuMemoryFaultInfo? LastMemoryFaultInfo { get; }

    CpuControlTransferInfo? LastControlTransferInfo { get; }

    CpuNotImplementedInfo? LastNotImplementedInfo { get; }

    string? LastImportResolutionTrace { get; }

    string? LastBasicBlockTrace { get; }

    string? LastMilestoneLog { get; }

    string? LastRecentInstructionWindow { get; }

    string? LastRecentControlTransferTrace { get; }

    CpuSessionSummary LastSessionSummary { get; }

    OrbisGen2Result DispatchEntry(
        ulong entryPoint,
        Generation generation,
        IReadOnlyDictionary<ulong, string>? importStubs = null,
        IReadOnlyDictionary<string, ulong>? runtimeSymbols = null,
        string processImageName = "eboot.bin",
        CpuExecutionOptions executionOptions = default);

    OrbisGen2Result DispatchModuleInitializer(
        ulong entryPoint,
        Generation generation,
        IReadOnlyDictionary<ulong, string>? importStubs = null,
        IReadOnlyDictionary<string, ulong>? runtimeSymbols = null,
        string moduleName = "module",
        CpuExecutionOptions executionOptions = default);

    // Runs a guest routine on behalf of the host, with its first argument in rdi.
    // Every host call shares one stack, TLS block, and return stub.
    OrbisGen2Result DispatchHostCall(
        ulong entryPoint,
        ulong argument,
        Generation generation,
        IReadOnlyDictionary<ulong, string>? importStubs = null,
        IReadOnlyDictionary<string, ulong>? runtimeSymbols = null,
        CpuExecutionOptions executionOptions = default);
}
