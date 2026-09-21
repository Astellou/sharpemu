// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Usbd;

public static class UsbdExports
{
    private const int SceUsbdErrorInvalidParam = unchecked((int)0x80240002);

    [SysAbiExport(
        Nid = "+wU6CGuZcWk",
        ExportName = "sceUsbdHandleEventsTimeout",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUsbd")]
    public static int UsbdHandleEventsTimeout(CpuContext ctx)
    {
        var timeoutAddress = ctx[CpuRegister.Rdi];
        if (timeoutAddress == 0 ||
            !ctx.TryReadUInt64(timeoutAddress, out var secondsValue) ||
            !ctx.TryReadUInt64(timeoutAddress + sizeof(ulong), out var microsecondsValue))
        {
            return ctx.SetReturn(SceUsbdErrorInvalidParam);
        }

        var seconds = unchecked((long)secondsValue);
        var microseconds = unchecked((long)microsecondsValue);
        if (seconds < 0 || microseconds is < 0 or >= 1_000_000)
        {
            return ctx.SetReturn(SceUsbdErrorInvalidParam);
        }

        // There is no USB backend yet. Match libusb's no-event timeout behavior
        // closely enough to keep guest event threads paced instead of turning
        // this import into a hot loop. With no devices/transfers to dispatch,
        // a timeout completes successfully after the requested interval.
        long totalMicroseconds;
        try
        {
            totalMicroseconds = checked(seconds * 1_000_000 + microseconds);
        }
        catch (OverflowException)
        {
            return ctx.SetReturn(SceUsbdErrorInvalidParam);
        }

        HostTiming.SleepMicroseconds(totalMicroseconds);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }
}
