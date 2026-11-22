// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using Iced.Intel;
using SharpEmu.HLE;
using static Iced.Intel.AssemblerRegisters;

namespace SharpEmu.Core.Cpu.Native;

// The hottest libSceAgc command builders as native code on the guest thread (SysV ABI, caller-saved registers only, no stack). Like the title's own stores, their
// writes go straight to guest memory. Anything off the common path (no command space: the title's callback, invalid
// or unusual arguments) jumps to the managed export's trampoline with the argument registers untouched.
// The command buffer layout and packet encodings mirror AgcExports (CommandBuffer*Offset, Pm4, It*, R*).
public sealed unsafe partial class DirectExecutionBackend
{
	private const ulong AgcCursorUp = 0x10, AgcCursorDown = 0x18, AgcReservedDwords = 0x30;

	private static readonly string[] NativeAgcNids =
	{
		"t1vNu082-jM", "n2fD4A+pb+g", "d-6uF9sZDIU", "z2duB-hHQSM", "vRoArM9zaIk", "V++UgBtQhn0", "ZvwO9euwYzc", "-HOOCn0JY48", "hvUfkUIQcOE", "k3GhuSNmBLU", "8N2tmT3jmC8", "l4fM9K-Lyks", "B+aG9DUnTKA", "D9sr1xGUriE",
		"dbOlWdppb4o", "UZbQjYAwwXM", "aJf+j5yntiU", "cFazmnXpJOE", "vcmNN+AAXnY", "Qrj4c+61z4A", "6lNcCp+fxi4", "57labkp+rSQ", "KT-hTp-Ch14", "VmW0Tdpy420", "htn36gPnBk4", "wr23dPKyWc0", "i1jyy49AjXU", "eZ4+17OQz4Q", "j3EtxFkSIhQ", "CtB+A9-VxO0", "Yw0jKSqop+E", "RmaJwLtc8rY", "tSBxhAPyytQ",
	};

	private static bool IsNativeAgcNid(string nid) => Array.IndexOf(NativeAgcNids, nid) >= 0;

	private static uint NativePm4(uint lengthDwords, uint op, uint register) =>
		0xC0000000u | (((lengthDwords - 2u) & 0x3FFFu) << 16) | ((op & 0xFFu) << 8) | ((register & 0x3Fu) << 2);

	// The import's native code, falling back to its managed trampoline.
	private static bool TryCreateNativeAgc(string nid, nint fallback, out nint code)
	{
		code = IsNativeAgcNid(nid) ? AssembleNativeAgc(nid, fallback) : 0;
		return code != 0;
	}

	private static nint AssembleNativeAgc(string nid, nint fallback)
	{
		var a = new Assembler(64);
		var fb = a.CreateLabel();
		switch (nid)
		{
			case "t1vNu082-jM": DrawIndexIndirect(a, fb); break;
			case "n2fD4A+pb+g": SetShRegisterRangeDirect(a, fb); break;
			case "d-6uF9sZDIU": PatchAddRegisters(a, fb, 0x9F); break;
			case "z2duB-hHQSM": PatchAddRegisters(a, fb, 0x63); break;
			case "vRoArM9zaIk": PatchAddRegisters(a, fb, 0x64); break;
			case "V++UgBtQhn0": DataPacketPayloadAddress(a, fb); break;
			case "ZvwO9euwYzc": RegistersIndirect(a, fb, 0x9F); break;
			case "-HOOCn0JY48": RegistersIndirect(a, fb, 0x63); break;
			case "hvUfkUIQcOE": RegistersIndirect(a, fb, 0x64); break;
			case "k3GhuSNmBLU": Dispatch(a, fb); break;
			case "8N2tmT3jmC8": FixedPacket(a, fb, 2, () => { a.mov(__dword_ptr[rax], unchecked((int)NativePm4(2, 0x10, 0x1C))); a.mov(__dword_ptr[rax + 4], esi); }); break;
			case "l4fM9K-Lyks": IndexBuffer(a, fb); break;
			case "B+aG9DUnTKA": DrawIndexOffset(a, fb); break;
			case "D9sr1xGUriE": PrimState(a, fb); break;
			case "dbOlWdppb4o": InterpolantMapping(a, fb); break;
			case "UZbQjYAwwXM": ShRegistersDirect(a, fb); break;
			case "aJf+j5yntiU": EventWrite(a, fb, dcb: true); break;
			case "cFazmnXpJOE": EventWrite(a, fb, dcb: false); break;
			case "vcmNN+AAXnY": PatchSetAddress(a, fb, 0x9F); break;
			case "Qrj4c+61z4A": PatchSetAddress(a, fb, 0x63); break;
			case "6lNcCp+fxi4": PatchSetAddress(a, fb, 0x64); break;
			case "57labkp+rSQ": AcquireMem(a, fb, dcb: true); break;
			case "KT-hTp-Ch14": AcquireMem(a, fb, dcb: false); break;
			case "VmW0Tdpy420": DcbWaitRegMem(a, fb); break;
			case "htn36gPnBk4": AcbWaitRegMem(a, fb); break;
			case "wr23dPKyWc0": ReleaseMem(a, fb); break;
			case "i1jyy49AjXU": case "eZ4+17OQz4Q": WriteData(a, fb); break;
			case "j3EtxFkSIhQ": FixedPacket(a, fb, 4, () =>
			{
				a.mov(__dword_ptr[rax], unchecked((int)NativePm4(4, 0x16, 0)));
				a.mov(__dword_ptr[rax + 4], esi);
				a.mov(r9, rsi);
				a.shr(r9, 32);
				a.mov(__dword_ptr[rax + 8], r9d);
				a.mov(r9d, edx);
				a.and(r9d, 0xA038);
				a.or(r9d, 0x41);
				a.mov(__dword_ptr[rax + 12], r9d);
			}); break;
			case "CtB+A9-VxO0": FixedPacket(a, fb, 3, () =>
			{
				a.mov(__dword_ptr[rax], unchecked((int)NativePm4(3, 0x16, 0)));
				a.mov(__dword_ptr[rax + 4], esi);
				a.mov(r9d, edx);
				a.and(r9d, 0xA038);
				a.or(r9d, 0x41);
				a.mov(__dword_ptr[rax + 8], r9d);
			}); break;
			case "Yw0jKSqop+E": FixedPacket(a, fb, 7, () =>
			{
				a.mov(__dword_ptr[rax], unchecked((int)NativePm4(7, 0x10, 0x04)));
				a.mov(__dword_ptr[rax + 4], esi);
				a.xor(r9d, r9d);
				a.mov(__qword_ptr[rax + 8], r9);
				a.mov(__qword_ptr[rax + 16], r9);
				a.mov(__dword_ptr[rax + 24], r9d);
			}); break;
			case "RmaJwLtc8rY": FixedPacket(a, fb, 4, () =>
			{
				a.lea(r9d, __[rsi + rsi]);
				a.or(r9d, unchecked((int)NativePm4(4, 0x11, 0)));
				a.mov(__dword_ptr[rax], r9d);
				a.mov(__dword_ptr[rax + 4], 1);
				a.mov(r9d, edx);
				a.and(r9d, -8);
				a.mov(__dword_ptr[rax + 8], r9d);
				a.mov(r9, rdx);
				a.shr(r9, 32);
				a.mov(__dword_ptr[rax + 12], r9d);
			}); break;
			case "tSBxhAPyytQ": FixedPacket(a, fb, 2, () =>
			{
				a.mov(__dword_ptr[rax], unchecked((int)NativePm4(2, 0x2F, 0)));
				a.mov(__dword_ptr[rax + 4], esi);
			}); break;
			default: return 0;
		}

		a.Label(ref fb);
		a.mov(rax, (ulong)fallback);
		a.jmp(rax);
		return Place(a);
	}

	private static nint Place(Assembler a)
	{
		var page = (byte*)HostMemory.Alloc(null, 4096, HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE, HostMemory.PAGE_EXECUTE_READWRITE);
		if (page == null) return 0;
		var writer = new SpanCodeWriter(new Span<byte>(page, 4096));
		a.Assemble(writer, (ulong)page);
		HostMemory.Protect(page, 4096, HostMemory.PAGE_EXECUTE_READ, out _);
		return (nint)page;
	}

	// Remaining command space check: rax = cursorUp; jumps to fb unless the free dwords (the cursor distance truncated to
	// 32 bits, as AgcExports.GetRemainingCommandDwords) reach reserved + need. A down cursor below the up cursor goes to
	// the managed export. need: a register holding the dword count (64-bit, zero-extended) or 0 with a constant.
	private static void CheckSpace(Assembler a, Label fb, AssemblerRegister64? needRegister, int needConstant)
	{
		a.mov(rax, __qword_ptr[rdi + (long)AgcCursorUp]);
		a.mov(r10, __qword_ptr[rdi + (long)AgcCursorDown]);
		a.cmp(r10, rax);
		a.jb(fb);
		a.sub(r10, rax);
		a.shr(r10, 2);
		a.mov(r10d, r10d);
		a.mov(r11d, __dword_ptr[rdi + (long)AgcReservedDwords]);
		if (needRegister is { } need) a.add(r11, need);
		a.add(r11, needConstant);
		a.cmp(r10, r11);
		a.jb(fb);
	}

	// sceAgcDcbDrawIndexIndirect(cb=rdi, dataOffset=esi, modifier=rdx) -> packet address (5 dwords). Register locations as
	// AgcExports.DrawModifierRegister: start vertex (bit 0, slot at bit 9) low, start instance (bit 2, slot at bit 19)
	// high; 0x280 when not enabled or when bit 4 passes the values in VGPRs. User data starts at 0x10C for stages 3 and 5.
	private static void DrawIndexIndirect(Assembler a, Label fb)
	{
		var hi = a.CreateLabel(); var lo = a.CreateLabel(); var noA = a.CreateLabel(); var noB = a.CreateLabel();
		var two = a.CreateLabel(); var store = a.CreateLabel();
		a.test(rdi, rdi);
		a.jz(fb);
		CheckSpace(a, fb, null, 5);
		a.lea(r10, __[rax + 20]);
		a.mov(__qword_ptr[rdi + (long)AgcCursorUp], r10);
		a.mov(__dword_ptr[rax], unchecked((int)0xC0032500u)); // Pm4(5, IT_DRAW_INDEX_INDIRECT, 0)
		a.mov(__dword_ptr[rax + 4], esi);
		a.mov(ecx, edx);
		a.shr(ecx, 29);
		a.mov(r8d, 0x8C);
		a.cmp(ecx, 3);
		a.je(hi);
		a.cmp(ecx, 5);
		a.jne(lo);
		a.Label(ref hi);
		a.mov(r8d, 0x10C);
		a.Label(ref lo);
		a.mov(r9d, 0x280);
		a.mov(ecx, edx);
		a.and(ecx, 0x11);
		a.cmp(ecx, 1);
		a.jne(noA);
		a.mov(r9d, edx);
		a.shr(r9d, 9);
		a.and(r9d, 0x1F);
		a.add(r9d, r8d);
		a.Label(ref noA);
		a.mov(__dword_ptr[rax + 8], r9d);
		a.mov(r10d, 0x280);
		a.mov(ecx, edx);
		a.and(ecx, 0x14);
		a.cmp(ecx, 4);
		a.jne(noB);
		a.mov(r10d, edx);
		a.shr(r10d, 19);
		a.and(r10d, 0x1F);
		a.add(r10d, r8d);
		a.Label(ref noB);
		a.mov(__dword_ptr[rax + 12], r10d);
		a.bt(rdx, 32);
		a.jc(two);
		a.mov(ecx, edx);
		a.shr(ecx, 3);
		a.and(ecx, 0x20);
		a.or(ecx, 2);
		a.jmp(store);
		a.Label(ref two);
		a.mov(ecx, 2);
		a.Label(ref store);
		a.mov(__dword_ptr[rax + 16], ecx);
		a.ret();
	}

	// sceAgcCbSetShRegisterRangeDirect(cb=rdi, offset=esi, values=rdx, count=ecx) -> SET_SH_REG packet address,
	// after the 2-dword marker NOP; the payload is copied unless values is null (reserved for the caller).
	private static void SetShRegisterRangeDirect(Assembler a, Label fb)
	{
		var check = a.CreateLabel(); var ok = a.CreateLabel(); var done = a.CreateLabel();
		a.test(rdi, rdi);
		a.jz(fb);
		a.test(esi, esi);
		a.jz(fb);
		a.cmp(esi, 0x3FF);
		a.ja(fb);
		a.lea(r10d, __[rcx - 1]);
		a.cmp(r10d, 0x3FFE);
		a.ja(fb);
		a.mov(r8d, ecx); // count, zero-extended
		CheckSpace(a, fb, r8, 4);
		a.test(rdx, rdx);
		a.jz(ok);
		// The payload [cursorUp + 16, +count*4) must not overlap the source (the managed export copies word by word then).
		a.Label(ref check);
		a.lea(r9, __[rax + 16]);
		a.lea(r10, __[r8 * 4]);
		a.lea(r11, __[rdx + r10]);
		a.cmp(r11, r9);
		a.jbe(ok);
		a.lea(r11, __[r9 + r10]);
		a.cmp(r11, rdx);
		a.ja(fb);
		a.Label(ref ok);
		a.mov(__dword_ptr[rax], unchecked((int)0xC0001000u)); // Pm4(2, IT_NOP, 0)
		a.mov(__dword_ptr[rax + 4], 0x6875000D); // CbSetShRegisterRangeMarker
		a.mov(r9d, ecx);
		a.shl(r9d, 16);
		a.or(r9d, unchecked((int)0xC0007600u)); // Pm4(count + 2, IT_SET_SH_REG, 0)
		a.mov(__dword_ptr[rax + 8], r9d);
		a.mov(__dword_ptr[rax + 12], esi);
		a.lea(r10, __[rax + r8 * 4 + 16]);
		a.mov(__qword_ptr[rdi + (long)AgcCursorUp], r10);
		a.test(rdx, rdx);
		a.jz(done);
		a.lea(rdi, __[rax + 16]);
		a.mov(rsi, rdx);
		a.mov(rcx, r8);
		a.rep.movsd();
		a.Label(ref done);
		a.add(rax, 8);
		a.ret();
	}

	// sceAgcSet{Cx,Sh,Uc}RegIndirectPatchAddRegisters(packet=rdi, count=esi) -> 0; the 5-dword SET_*_REG_INDIRECT form
	// only (the legacy NOP-encoded form goes to the managed export).
	private static void PatchAddRegisters(Assembler a, Label fb, int op)
	{
		a.test(rdi, rdi);
		a.jz(fb);
		a.mov(eax, __dword_ptr[rdi]);
		a.mov(r10d, eax);
		a.shr(r10d, 8);
		a.and(r10d, 0xFF);
		a.cmp(r10d, op);
		a.jne(fb);
		a.mov(r10d, eax);
		a.shr(r10d, 16);
		a.and(r10d, 0x3FFF);
		a.cmp(r10d, 3);
		a.jne(fb);
		a.mov(r10d, __dword_ptr[rdi + 16]);
		a.mov(r11d, r10d);
		a.and(r11d, 0x3FFF);
		a.add(r11d, esi);
		a.and(r11d, 0x3FFF);
		a.and(r10d, unchecked((int)0xFFFFC000u));
		a.or(r10d, r11d);
		a.mov(__dword_ptr[rdi + 16], r10d);
		a.xor(eax, eax);
		a.ret();
	}

	// Fixed-size packet: cb=rdi; space check and cursor advance, then body stores the dwords at rax (packet address,
	// also the return value). body may use rsi, rdx, rcx, r8, r9, r10, r11.
	private static void FixedPacket(Assembler a, Label fb, int dwords, Action body)
	{
		a.test(rdi, rdi);
		a.jz(fb);
		CheckSpace(a, fb, null, dwords);
		a.lea(r10, __[rax + dwords * 4]);
		a.mov(__qword_ptr[rdi + (long)AgcCursorUp], r10);
		body();
		a.ret();
	}

	// sceAgcDcbSet{Cx,Sh,Uc}RegistersIndirect(cb=rdi, table=rsi, count=edx) -> 5-dword SET_*_REG_INDIRECT.
	private static void RegistersIndirect(Assembler a, Label fb, uint op) => FixedPacket(a, fb, 5, () =>
	{
		a.mov(__dword_ptr[rax], unchecked((int)NativePm4(5, op, 0)));
		a.mov(r9d, esi);
		a.and(r9d, -4);
		a.mov(__dword_ptr[rax + 4], r9d);
		a.mov(r9, rsi);
		a.shr(r9, 32);
		a.mov(__dword_ptr[rax + 8], r9d);
		a.mov(__dword_ptr[rax + 12], unchecked((int)0x80000000u));
		a.mov(r9d, edx);
		a.and(r9d, 0x3FFF);
		a.mov(__dword_ptr[rax + 16], r9d);
	});

	// sceAgcCbDispatch(cb=rdi, x=esi, y=edx, z=ecx, modifier=r8d) -> DISPATCH_DIRECT.
	private static void Dispatch(Assembler a, Label fb) => FixedPacket(a, fb, 5, () =>
	{
		a.mov(__dword_ptr[rax], unchecked((int)NativePm4(5, 0x15, 0)));
		a.mov(__dword_ptr[rax + 4], esi);
		a.mov(__dword_ptr[rax + 8], edx);
		a.mov(__dword_ptr[rax + 12], ecx);
		a.mov(r9d, r8d);
		a.and(r9d, 0xA038);
		a.or(r9d, 0x41);
		a.mov(__dword_ptr[rax + 16], r9d);
	});

	// sceAgcDcbSetIndexBuffer(cb=rdi, address=rsi, count=edx) -> INDEX_BASE + INDEX_BUFFER_SIZE.
	private static void IndexBuffer(Assembler a, Label fb) => FixedPacket(a, fb, 5, () =>
	{
		a.mov(__dword_ptr[rax], unchecked((int)NativePm4(3, 0x26, 0)));
		a.mov(__dword_ptr[rax + 4], esi);
		a.mov(r9, rsi);
		a.shr(r9, 32);
		a.mov(__dword_ptr[rax + 8], r9d);
		a.mov(__dword_ptr[rax + 12], unchecked((int)NativePm4(2, 0x13, 0)));
		a.mov(__dword_ptr[rax + 16], edx);
	});

	// sceAgcDcbDrawIndexOffset(cb=rdi, offset=esi, count=edx, modifier=rcx) -> DRAW_INDEX_OFFSET_2.
	private static void DrawIndexOffset(Assembler a, Label fb) => FixedPacket(a, fb, 5, () =>
	{
		var init = a.CreateLabel();
		a.mov(__dword_ptr[rax], unchecked((int)NativePm4(5, 0x35, 0)));
		a.mov(r9d, 1);
		a.cmp(edx, r9d);
		a.cmovae(r9d, edx);
		a.mov(__dword_ptr[rax + 4], r9d);
		a.mov(__dword_ptr[rax + 8], esi);
		a.mov(__dword_ptr[rax + 12], edx);
		a.xor(r9d, r9d);
		a.bt(rcx, 32);
		a.jc(init);
		a.mov(r9d, ecx);
		a.shr(r9d, 3);
		a.and(r9d, 0x20);
		a.Label(ref init);
		a.mov(__dword_ptr[rax + 16], r9d);
	});

	// sceAgcCreatePrimState(cx=rdi, uc=rsi, hull=rdx, gs=rcx, primType=r8d) -> 0. Mirrors AgcExports.CreatePrimState /
	// TryCreatePrimCxState (shader: type byte +0x5A, specials pointer +0x28; specials: GE_CNTL +0x00,
	// VGT_SHADER_STAGES_EN +0x08, VGT_GS_OUT_PRIM_TYPE +0x20, GE_USER_VGPR_EN +0x28); errors go to the managed export.
	private static void PrimState(Assembler a, Label fb)
	{
		var gsOk = a.CreateLabel(); var noHull = a.CreateLabel(); var noCx = a.CreateLabel();
		var noGs = a.CreateLabel(); var outDone = a.CreateLabel(); var hullDone = a.CreateLabel(); var noUc = a.CreateLabel();
		var ucSrc = a.CreateLabel();
		a.mov(rax, rdi);
		a.or(rax, rsi);
		a.jz(noUc);
		a.test(rcx, rcx);
		a.jz(fb);
		a.movzx(eax, __byte_ptr[rcx + 0x5A]);
		a.cmp(eax, 2);
		a.je(gsOk);
		a.cmp(eax, 6);
		a.jne(fb);
		a.Label(ref gsOk);
		a.mov(r9, __qword_ptr[rcx + 0x28]);
		a.test(r9, r9);
		a.jz(fb);
		a.xor(r10d, r10d);
		a.test(rdx, rdx);
		a.jz(noHull);
		a.cmp(__byte_ptr[rdx + 0x5A], 3);
		a.jne(fb);
		a.mov(r10, __qword_ptr[rdx + 0x28]);
		a.test(r10, r10);
		a.jz(fb);
		a.Label(ref noHull);
		a.test(rdi, rdi);
		a.jz(noCx);
		a.mov(r11d, __dword_ptr[r9 + 8]);
		a.mov(__dword_ptr[rdi], r11d);
		a.mov(eax, __dword_ptr[r9 + 12]);
		a.test(eax, 0x20);
		a.jz(noGs);
		a.mov(r11d, __dword_ptr[r9 + 0x20]);
		a.mov(ecx, __dword_ptr[r9 + 0x24]);
		a.jmp(outDone);
		a.Label(ref noGs);
		// PrimitiveTypeToGsOut: 1 -> points 0; 2,3,10,11,18 -> lines 1; 7 -> rectangle 3; 0x11 -> rect list 4; else triangles 2.
		var notPoints = a.CreateLabel(); var notRect = a.CreateLabel(); var notRectList = a.CreateLabel();
		a.mov(r11d, 0x29B);
		a.mov(ecx, 2);
		a.cmp(r8d, 1);
		a.jne(notPoints);
		a.xor(ecx, ecx);
		a.jmp(outDone);
		a.Label(ref notPoints);
		a.cmp(r8d, 7);
		a.jne(notRect);
		a.mov(ecx, 3);
		a.jmp(outDone);
		a.Label(ref notRect);
		a.cmp(r8d, 0x11);
		a.jne(notRectList);
		a.mov(ecx, 4);
		a.jmp(outDone);
		a.Label(ref notRectList);
		a.cmp(r8d, 31);
		a.ja(outDone);
		a.mov(edx, 0x40C0C);
		a.bt(edx, r8d);
		a.jnc(outDone);
		a.mov(ecx, 1);
		a.Label(ref outDone);
		a.test(r10, r10);
		a.jz(hullDone);
		a.or(eax, __dword_ptr[r10 + 12]);
		a.test(eax, 0x20);
		a.jnz(hullDone);
		a.mov(r11d, __dword_ptr[r10 + 0x20]);
		a.mov(ecx, __dword_ptr[r10 + 0x24]);
		a.Label(ref hullDone);
		a.mov(__dword_ptr[rdi + 4], eax);
		a.mov(__dword_ptr[rdi + 8], r11d);
		a.mov(__dword_ptr[rdi + 12], ecx);
		a.Label(ref noCx);
		a.test(rsi, rsi);
		a.jz(noUc);
		a.mov(rax, __qword_ptr[r9]);
		a.mov(__qword_ptr[rsi], rax);
		a.test(r10, r10);
		a.jz(ucSrc);
		a.mov(r9, r10);
		a.Label(ref ucSrc);
		a.mov(rax, __qword_ptr[r9 + 0x28]);
		a.mov(__qword_ptr[rsi + 8], rax);
		a.mov(__dword_ptr[rsi + 16], 0x242);
		a.mov(__dword_ptr[rsi + 20], r8d);
		a.Label(ref noUc);
		a.xor(eax, eax);
		a.ret();
	}

	// sceAgcGetDataPacketPayloadAddress(out=rdi, packet=rsi, type=edx) -> 0; *out = payload address.
	private static void DataPacketPayloadAddress(Assembler a, Label fb)
	{
		var store = a.CreateLabel(); var zero = a.CreateLabel();
		a.test(rdi, rdi);
		a.jz(fb);
		a.test(rsi, rsi);
		a.jz(fb);
		a.lea(rax, __[rsi + 8]);
		a.test(edx, edx);
		a.jnz(store);
		a.mov(r10d, __dword_ptr[rsi]);
		a.and(r10d, 0x3FFF0000);
		a.cmp(r10d, 0x3FFF0000);
		a.je(zero);
		a.lea(rax, __[rsi + 4]);
		a.jmp(store);
		a.Label(ref zero);
		a.xor(eax, eax);
		a.Label(ref store);
		a.mov(__qword_ptr[rdi], rax);
		a.xor(eax, eax);
		a.ret();
	}

	// Space check and cursor advance for a packet of a constant size; rax = packet address.
	private static void Allocate(Assembler a, Label fb, int dwords)
	{
		CheckSpace(a, fb, null, dwords);
		a.lea(r10, __[rax + dwords * 4]);
		a.mov(__qword_ptr[rdi + (long)AgcCursorUp], r10);
	}

	// value (eax) = (value & ~(3 << shift)) | (((source (esi) >> fieldShift) & 3) << shift), with r11 as scratch.
	private static void SetTwoBits(Assembler a, int fieldShift, int shift)
	{
		a.and(eax, ~(3 << shift));
		a.mov(r11d, esi);
		a.shr(r11d, (byte)fieldShift);
		a.and(r11d, 3);
		a.shl(r11d, (byte)shift);
		a.or(eax, r11d);
	}

	// sceAgc{Dcb,Acb}EventWrite(cb=rdi, type=sil, address=rdx) -> EVENT_WRITE: 4 dwords with the address for types 0x38
	// and 0x39, 2 for the others; the DCB form puts event index 4 on types 0x07, 0x0F and 0x10.
	private static void EventWrite(Assembler a, Label fb, bool dcb)
	{
		var withAddress = a.CreateLabel(); var store = a.CreateLabel();
		a.test(rdi, rdi);
		a.jz(fb);
		a.movzx(eax, sil);
		a.cmp(eax, 0x3F);
		a.ja(fb);
		a.and(eax, ~1);
		a.cmp(eax, 0x38);
		a.je(withAddress);
		Allocate(a, fb, 2);
		a.mov(__dword_ptr[rax], unchecked((int)NativePm4(2, 0x46, 0)));
		a.movzx(r10d, sil);
		if (dcb)
		{
			var indexed = a.CreateLabel();
			a.cmp(r10d, 0x07);
			a.je(indexed);
			a.cmp(r10d, 0x0F);
			a.je(indexed);
			a.cmp(r10d, 0x10);
			a.jne(store);
			a.Label(ref indexed);
			a.or(r10d, 0x400);
		}

		a.Label(ref store);
		a.mov(__dword_ptr[rax + 4], r10d);
		a.ret();
		a.Label(ref withAddress);
		Allocate(a, fb, 4);
		a.mov(__dword_ptr[rax], unchecked((int)NativePm4(4, 0x46, 0)));
		a.movzx(r10d, sil);
		a.or(r10d, 0x100);
		a.mov(__dword_ptr[rax + 4], r10d);
		a.mov(r10d, edx);
		a.and(r10d, -8);
		a.mov(__dword_ptr[rax + 8], r10d);
		a.mov(r10, rdx);
		a.shr(r10, 32);
		a.mov(__dword_ptr[rax + 12], r10d);
		a.ret();
	}

	// sceAgcSet{Cx,Sh,Uc}RegIndirectPatchSetAddress(packet=rdi, registers=rsi) -> 0; the 5-dword SET_*_REG_INDIRECT form
	// only, keeping the two low bits of the address dword.
	private static void PatchSetAddress(Assembler a, Label fb, int op)
	{
		a.test(rdi, rdi);
		a.jz(fb);
		a.test(rsi, rsi);
		a.jz(fb);
		a.mov(eax, __dword_ptr[rdi]);
		a.mov(r10d, eax);
		a.shr(r10d, 8);
		a.and(r10d, 0xFF);
		a.cmp(r10d, op);
		a.jne(fb);
		a.shr(eax, 16);
		a.and(eax, 0x3FFF);
		a.cmp(eax, 3);
		a.jne(fb);
		a.mov(eax, __dword_ptr[rdi + 4]);
		a.and(eax, 3);
		a.mov(r10d, esi);
		a.and(r10d, -4);
		a.or(eax, r10d);
		a.mov(__dword_ptr[rdi + 4], eax);
		a.mov(r10, rsi);
		a.shr(r10, 32);
		a.mov(__dword_ptr[rdi + 8], r10d);
		a.xor(eax, eax);
		a.ret();
	}

	// sceAgcDcbAcquireMem(cb=rdi, engine=sil, cbDbOp=edx, gcr=ecx, base=r8, size=r9, poll=[rsp+8]) and
	// sceAgcAcbAcquireMem(cb=rdi, gcr=esi, base=rdx, size=rcx, poll=r8d) -> 8-dword ACQUIRE_MEM. The range in 256-byte
	// units as AgcExports.TryEncloseAcquireRange (anything it rejects goes to the managed export).
	private static void AcquireMem(Assembler a, Label fb, bool dcb)
	{
		var baseRegister = dcb ? r8 : rdx;
		var sizeRegister = dcb ? r9 : rcx;
		var zeroSize = a.CreateLabel();
		a.test(rdi, rdi);
		a.jz(fb);
		if (dcb)
		{
			a.movzx(eax, sil);
			a.cmp(eax, 1);
			a.ja(fb);
		}

		CheckSpace(a, fb, null, 8);
		a.mov(r11, 1UL << 40);
		a.cmp(baseRegister, r11);
		a.jae(fb);
		a.xor(r10d, r10d);
		a.lea(r11, __[sizeRegister + 1]);
		a.cmp(r11, 1);
		a.jbe(zeroSize);
		a.mov(r11, 1UL << 40);
		a.sub(r11, baseRegister);
		a.cmp(sizeRegister, r11);
		a.ja(fb);
		a.movzx(r10d, dcb ? r8b : dl);
		a.add(r10, sizeRegister);
		a.add(r10, 0xFF);
		a.shr(r10, 8);
		a.mov(r11d, -1);
		a.cmp(r10, r11);
		a.ja(fb);
		a.Label(ref zeroSize);
		a.mov(__dword_ptr[rax + 8], r10d);
		a.lea(r10, __[rax + 32]);
		a.mov(__qword_ptr[rdi + (long)AgcCursorUp], r10);
		a.mov(__dword_ptr[rax], unchecked((int)NativePm4(8, 0x10, 0x14)));
		if (dcb)
		{
			a.movzx(r11d, sil);
			a.shl(r11d, 31);
			a.or(r11d, edx);
		}
		else
		{
			a.mov(r11d, unchecked((int)0x80000000u));
		}

		a.mov(__dword_ptr[rax + 4], r11d);
		a.mov(__dword_ptr[rax + 12], 0);
		a.mov(r11, baseRegister);
		a.shr(r11, 8);
		a.mov(__dword_ptr[rax + 16], r11d);
		a.mov(__dword_ptr[rax + 20], 0);
		if (dcb) a.mov(r11d, __dword_ptr[rsp + 8]);
		else a.mov(r11d, r8d);
		a.mov(r10d, unchecked((int)0xCCCCCCCDu)); // poll / 40 = (poll * 0xCCCCCCCD) >> 37
		a.imul(r11, r10);
		a.shr(r11, 37);
		a.mov(__dword_ptr[rax + 24], r11d);
		a.mov(__dword_ptr[rax + 28], dcb ? ecx : esi);
		a.ret();
	}

	// WAIT_REG_MEM control (AgcExports.EncodeWaitRegMem32Control / 64Control) into r10d: compare (dl), operation (op8, or
	// none), cache policy (cache8); r11 scratch.
	private static void WaitControl(Assembler a, bool is64, AssemblerRegister8? op8, AssemblerRegister8 cache8)
	{
		a.movzx(r10d, dl);
		a.or(r10d, 0x10);
		if (op8 is { } op)
		{
			a.movzx(r11d, op);
			a.and(r11d, is64 ? 1 : 3);
			a.shl(r11d, 8);
			a.or(r10d, r11d);
			a.movzx(r11d, op);
			a.and(r11d, is64 ? 6 : 0xC);
			a.shl(r11d, (byte)(is64 ? 5 : 4));
			a.or(r10d, r11d);
		}

		a.movzx(r11d, cache8);
		a.and(r11d, 3);
		a.shl(r11d, 25);
		a.or(r10d, r11d);
	}

	// poll field: min(cycles >> 4, 0xFFFF) of the dword at source into r10d; r11 scratch.
	private static void WaitPoll(Assembler a, AssemblerMemoryOperand source)
	{
		a.mov(r10d, source);
		a.shr(r10d, 4);
		a.mov(r11d, 0xFFFF);
		a.cmp(r10d, r11d);
		a.cmova(r10d, r11d);
	}

	// sceAgcDcbWaitRegMem(cb=rdi, size=sil, compare=dl, op=cl, cache=r8b, address=r9, reference=[rsp+8], mask=[rsp+16],
	// poll=[rsp+24]) -> the 7- or 9-dword wait and the 7-dword NOP that pads it to sceAgcDcbWaitOnAddressGetSize.
	private static void DcbWaitRegMem(Assembler a, Label fb)
	{
		var opOk = a.CreateLabel(); var wide = a.CreateLabel();
		a.test(rdi, rdi);
		a.jz(fb);
		a.movzx(eax, sil);
		a.cmp(eax, 1);
		a.ja(fb);
		a.movzx(eax, dl);
		a.cmp(eax, 7);
		a.ja(fb);
		a.movzx(eax, cl);
		a.cmp(eax, 1);
		a.jbe(opOk);
		a.cmp(eax, 4);
		a.jne(fb);
		a.Label(ref opOk);
		a.movzx(eax, r8b);
		a.cmp(eax, 3);
		a.ja(fb);
		a.test(sil, sil);
		a.jnz(wide);

		Allocate(a, fb, 14);
		a.mov(__dword_ptr[rax], unchecked((int)NativePm4(7, 0x10, 0x0A)));
		WaitAddress(a, r9, -4, 4);
		a.mov(r10d, __dword_ptr[rsp + 16]);
		a.mov(__dword_ptr[rax + 12], r10d);
		a.mov(r10d, __dword_ptr[rsp + 8]);
		a.mov(__dword_ptr[rax + 16], r10d);
		WaitControl(a, false, cl, r8b);
		a.mov(__dword_ptr[rax + 20], r10d);
		WaitPoll(a, __dword_ptr[rsp + 24]);
		a.mov(__dword_ptr[rax + 24], r10d);
		WaitPad(a, 28);
		a.ret();

		a.Label(ref wide);
		Allocate(a, fb, 16);
		a.mov(__dword_ptr[rax], unchecked((int)NativePm4(9, 0x10, 0x16)));
		WaitAddress(a, r9, -8, 4);
		a.mov(r10, __qword_ptr[rsp + 16]);
		a.mov(__qword_ptr[rax + 12], r10);
		a.mov(r10, __qword_ptr[rsp + 8]);
		a.mov(__qword_ptr[rax + 20], r10);
		WaitControl(a, true, cl, r8b);
		a.mov(__dword_ptr[rax + 28], r10d);
		WaitPoll(a, __dword_ptr[rsp + 24]);
		a.mov(__dword_ptr[rax + 32], r10d);
		WaitPad(a, 36);
		a.ret();
	}

	// sceAgcAcbWaitRegMem(cb=rdi, size=sil, compare=dl, cache=cl, address=r8, reference=r9, mask=[rsp+8], poll=[rsp+16]).
	private static void AcbWaitRegMem(Assembler a, Label fb)
	{
		var wide = a.CreateLabel();
		a.test(rdi, rdi);
		a.jz(fb);
		a.movzx(eax, sil);
		a.cmp(eax, 1);
		a.ja(fb);
		a.movzx(eax, dl);
		a.cmp(eax, 7);
		a.ja(fb);
		a.movzx(eax, cl);
		a.cmp(eax, 3);
		a.ja(fb);
		a.test(sil, sil);
		a.jnz(wide);

		Allocate(a, fb, 7);
		a.mov(__dword_ptr[rax], unchecked((int)NativePm4(7, 0x10, 0x0A)));
		WaitAddress(a, r8, -4, 4);
		a.mov(r10d, __dword_ptr[rsp + 8]);
		a.mov(__dword_ptr[rax + 12], r10d);
		a.mov(__dword_ptr[rax + 16], r9d);
		WaitControl(a, false, null, cl);
		a.mov(__dword_ptr[rax + 20], r10d);
		WaitPoll(a, __dword_ptr[rsp + 16]);
		a.mov(__dword_ptr[rax + 24], r10d);
		a.ret();

		a.Label(ref wide);
		Allocate(a, fb, 9);
		a.mov(__dword_ptr[rax], unchecked((int)NativePm4(9, 0x10, 0x16)));
		WaitAddress(a, r8, -8, 4);
		a.mov(r10, __qword_ptr[rsp + 8]);
		a.mov(__qword_ptr[rax + 12], r10);
		a.mov(__qword_ptr[rax + 20], r9);
		WaitControl(a, true, null, cl);
		a.mov(__dword_ptr[rax + 28], r10d);
		WaitPoll(a, __dword_ptr[rsp + 16]);
		a.mov(__dword_ptr[rax + 32], r10d);
		a.ret();
	}

	// Wait address dwords at rax + offset: low & alignMask, high & 0x3FFFF.
	private static void WaitAddress(Assembler a, AssemblerRegister64 address, int alignMask, int offset)
	{
		a.mov(r10, address);
		a.and(r10d, alignMask);
		a.mov(__dword_ptr[rax + offset], r10d);
		a.mov(r10, address);
		a.shr(r10, 32);
		a.and(r10d, 0x3FFFF);
		a.mov(__dword_ptr[rax + offset + 4], r10d);
	}

	// The 7-dword NOP after a DCB wait at rax + offset.
	private static void WaitPad(Assembler a, int offset)
	{
		a.mov(__dword_ptr[rax + offset], unchecked((int)NativePm4(7, 0x10, 0)));
		a.xor(r10d, r10d);
		for (var index = 1; index < 7; index++) a.mov(__dword_ptr[rax + offset + index * 4], r10d);
	}

	// sceAgcCbReleaseMem(cb=rdi, action=sil, gcr=dx, destination=cl, cache=r8b, address=r9, [rsp+8] data selection,
	// +16 data, +24 GDS offset, +32 GDS size, +40 interrupt, +48 interrupt context id) -> 8-dword RELEASE_MEM. The
	// validation of AgcExports.CbReleaseMem; interrupt 4 writes neither address nor data.
	private static void ReleaseMem(Assembler a, Label fb)
	{
		var gcrOk = a.CreateLabel(); var not5 = a.CreateLabel(); var not6 = a.CreateLabel();
		var noTarget = a.CreateLabel(); var targetDone = a.CreateLabel();
		a.test(rdi, rdi);
		a.jz(fb);
		a.movzx(eax, cl);
		a.cmp(eax, 1);
		a.ja(fb);
		a.movzx(eax, __byte_ptr[rsp + 8]);
		a.cmp(eax, 3);
		a.ja(fb);
		a.movzx(eax, __word_ptr[rsp + 24]);
		a.test(eax, eax);
		a.jnz(fb);
		a.movzx(eax, __word_ptr[rsp + 32]);
		a.cmp(eax, 2);
		a.ja(fb);
		a.movzx(eax, __byte_ptr[rsp + 40]);
		a.cmp(eax, 6);
		a.ja(fb);
		a.cmp(eax, 4);
		a.jb(gcrOk);
		a.test(dx, dx);
		a.jnz(fb);
		a.Label(ref gcrOk);
		a.cmp(eax, 5);
		a.jne(not5);
		a.test(r9, r9);
		a.jz(fb);
		a.test(r9b, 3);
		a.jnz(fb);
		a.Label(ref not5);
		a.cmp(eax, 6);
		a.jne(not6);
		a.test(r9, r9);
		a.jz(fb);
		a.test(r9b, 7);
		a.jnz(fb);
		a.Label(ref not6);

		Allocate(a, fb, 8);
		a.mov(__dword_ptr[rax], unchecked((int)NativePm4(8, 0x10, 0x18)));
		a.movzx(r10d, sil);
		a.movzx(r11d, r8b);
		a.shl(r11d, 8);
		a.or(r10d, r11d);
		a.mov(__dword_ptr[rax + 4], r10d);
		a.movzx(r10d, dx);
		a.movzx(r11d, __byte_ptr[rsp + 8]);
		a.shl(r11d, 16);
		a.or(r10d, r11d);
		a.movzx(r11d, __byte_ptr[rsp + 40]);
		a.shl(r11d, 24);
		a.or(r10d, r11d);
		a.mov(__dword_ptr[rax + 8], r10d);
		a.cmp(__byte_ptr[rsp + 40], 4);
		a.je(noTarget);
		a.mov(__qword_ptr[rax + 12], r9);
		a.mov(r10, __qword_ptr[rsp + 16]);
		a.mov(__qword_ptr[rax + 20], r10);
		a.jmp(targetDone);
		a.Label(ref noTarget);
		a.xor(r10d, r10d);
		a.mov(__qword_ptr[rax + 12], r10);
		a.mov(__qword_ptr[rax + 20], r10);
		a.Label(ref targetDone);
		a.mov(r10d, __dword_ptr[rsp + 48]);
		a.and(r10d, 0x07FFFFFF);
		a.mov(__dword_ptr[rax + 28], r10d);
		a.ret();
	}

	// sceAgc{Dcb,Acb}WriteData(cb=rdi, destination=sil, cache=dl, address=rcx, data=r8, count=r9d, increment=[rsp+8],
	// confirm=[rsp+16]) -> WRITE_DATA with the count dwords copied from data (overlapping source and packet go to the
	// managed export, which copies dword by dword).
	private static void WriteData(Assembler a, Label fb)
	{
		var separate = a.CreateLabel();
		a.test(rdi, rdi);
		a.jz(fb);
		a.test(r8, r8);
		a.jz(fb);
		a.cmp(r9d, 0x3FFD);
		a.ja(fb);
		a.mov(r9d, r9d); // the managed export reads only the low dword
		CheckSpace(a, fb, r9, 4);
		a.lea(r10, __[r8 + r9 * 4]);
		a.lea(r11, __[rax + 16]);
		a.cmp(r10, r11);
		a.jbe(separate);
		a.lea(r10, __[r11 + r9 * 4]);
		a.cmp(r10, r8);
		a.ja(fb);
		a.Label(ref separate);
		a.lea(r10, __[rax + r9 * 4 + 16]);
		a.mov(__qword_ptr[rdi + (long)AgcCursorUp], r10);
		a.lea(r10d, __[r9 + 2]);
		a.shl(r10d, 16);
		a.or(r10d, unchecked((int)0xC0001054u)); // Pm4(count + 4, IT_NOP, R_WRITE_DATA)
		a.mov(__dword_ptr[rax], r10d);
		a.movzx(r10d, sil);
		a.movzx(r11d, dl);
		a.shl(r11d, 8);
		a.or(r10d, r11d);
		a.movzx(r11d, __byte_ptr[rsp + 8]);
		a.shl(r11d, 16);
		a.or(r10d, r11d);
		a.movzx(r11d, __byte_ptr[rsp + 16]);
		a.shl(r11d, 24);
		a.or(r10d, r11d);
		a.mov(__dword_ptr[rax + 4], r10d);
		a.mov(__qword_ptr[rax + 8], rcx);
		a.lea(rdi, __[rax + 16]);
		a.mov(rsi, r8);
		a.mov(rcx, r9);
		a.rep.movsd();
		a.ret();
	}

	// sceAgcCbSetShRegistersDirect(cb=rdi, entries=rsi, count=edx) -> address of the first SET_SH_REG: the (offset, value)
	// entries sorted by offset as Array.Sort orders up to 16 elements (2: one compare-swap, 3: three, 4 to 16: insertion
	// sort, all keeping equal offsets in order), then one packet per run of consecutive registers, all in one allocation.
	// More than 16 entries go to the managed export.
	private static void ShRegistersDirect(Assembler a, Label fb)
	{
		var fbPop = a.CreateLabel(); var copy = a.CreateLabel(); var two = a.CreateLabel(); var three = a.CreateLabel();
		var outer = a.CreateLabel(); var inner = a.CreateLabel(); var place = a.CreateLabel(); var sorted = a.CreateLabel();
		var scan = a.CreateLabel(); var sameRun = a.CreateLabel(); var scanned = a.CreateLabel();
		var run = a.CreateLabel(); var value = a.CreateLabel(); var finish = a.CreateLabel();
		a.test(rdi, rdi);
		a.jz(fb);
		a.test(rsi, rsi);
		a.jz(fb);
		a.lea(eax, __[rdx - 1]);
		a.cmp(eax, 15);
		a.ja(fb);
		a.sub(rsp, 128);
		a.xor(r8d, r8d);
		a.Label(ref copy);
		a.mov(r9, __qword_ptr[rsi + r8 * 8]);
		a.mov(__qword_ptr[rsp + r8 * 8], r9);
		a.inc(r8d);
		a.cmp(r8d, edx);
		a.jb(copy);
		a.cmp(edx, 2);
		a.jb(sorted);
		a.je(two);
		a.cmp(edx, 3);
		a.je(three);
		// Insertion sort: t = e[i + 1]; shift the entries with a greater offset up.
		a.xor(r8d, r8d);
		a.Label(ref outer);
		a.lea(eax, __[r8 + 1]);
		a.cmp(eax, edx);
		a.jae(sorted);
		a.mov(r9, __qword_ptr[rsp + r8 * 8 + 8]);
		a.mov(r10, r8);
		a.Label(ref inner);
		a.test(r10, r10);
		a.js(place);
		a.mov(r11, __qword_ptr[rsp + r10 * 8]);
		a.cmp(r9d, r11d);
		a.jae(place);
		a.mov(__qword_ptr[rsp + r10 * 8 + 8], r11);
		a.dec(r10);
		a.jmp(inner);
		a.Label(ref place);
		a.mov(__qword_ptr[rsp + r10 * 8 + 8], r9);
		a.inc(r8d);
		a.jmp(outer);
		a.Label(ref three);
		SwapIfGreater(a, 0, 1);
		SwapIfGreater(a, 0, 2);
		SwapIfGreater(a, 1, 2);
		a.jmp(sorted);
		a.Label(ref two);
		SwapIfGreater(a, 0, 1);
		a.jmp(sorted);
		a.Label(ref sorted);
		// Runs (r9d): a new one wherever an offset is not the previous one plus one.
		a.mov(r9d, 1);
		a.mov(r10d, __dword_ptr[rsp]);
		a.mov(ecx, 1);
		a.Label(ref scan);
		a.cmp(ecx, edx);
		a.jae(scanned);
		a.mov(r11d, __dword_ptr[rsp + rcx * 8]);
		a.lea(eax, __[r10 + 1]);
		a.cmp(r11d, eax);
		a.je(sameRun);
		a.inc(r9d);
		a.Label(ref sameRun);
		a.mov(r10d, r11d);
		a.inc(ecx);
		a.jmp(scan);
		a.Label(ref scanned);
		a.lea(r9d, __[rdx + r9 * 2]);
		CheckSpace(a, fbPop, r9, 0);
		a.lea(r10, __[rax + r9 * 4]);
		a.mov(__qword_ptr[rdi + (long)AgcCursorUp], r10);
		a.mov(rsi, rsp);
		a.mov(r8, rax);
		a.mov(ecx, edx);
		a.Label(ref run);
		a.mov(r9, r8);
		a.mov(r10d, __dword_ptr[rsi]);
		a.movzx(r11d, r10w);
		a.mov(__dword_ptr[r8 + 4], r11d);
		a.add(r8, 8);
		a.xor(edx, edx);
		a.Label(ref value);
		a.mov(r11d, __dword_ptr[rsi + 4]);
		a.mov(__dword_ptr[r8], r11d);
		a.add(r8, 4);
		a.inc(edx);
		a.add(rsi, 8);
		a.dec(ecx);
		a.jz(finish);
		a.inc(r10d);
		a.cmp(__dword_ptr[rsi], r10d);
		a.je(value);
		a.mov(r11d, edx);
		a.shl(r11d, 16);
		a.or(r11d, unchecked((int)0xC0007600u)); // Pm4(values + 2, IT_SET_SH_REG, 0)
		a.mov(__dword_ptr[r9], r11d);
		a.jmp(run);
		a.Label(ref finish);
		a.mov(r11d, edx);
		a.shl(r11d, 16);
		a.or(r11d, unchecked((int)0xC0007600u));
		a.mov(__dword_ptr[r9], r11d);
		a.add(rsp, 128);
		a.ret();
		a.Label(ref fbPop);
		a.add(rsp, 128);
		a.jmp(fb);
	}

	// Entries i and j of the stack copy swap when i's offset is greater (equal offsets keep their order).
	private static void SwapIfGreater(Assembler a, int i, int j)
	{
		var keep = a.CreateLabel();
		a.mov(r9, __qword_ptr[rsp + i * 8]);
		a.mov(r10, __qword_ptr[rsp + j * 8]);
		a.cmp(r9d, r10d);
		a.jbe(keep);
		a.mov(__qword_ptr[rsp + i * 8], r10);
		a.mov(__qword_ptr[rsp + j * 8], r9);
		a.Label(ref keep);
	}

	// sceAgcCreateInterpolantMapping2(registers=rdi, gs=rsi, ps=rdx) -> 0: the 32 (SPI_PS_INPUT_CNTL_0 + i, value) pairs of
	// AgcExports.CreateInterpolantMapping2 (shader: input semantics +0x30, count +0x50; output semantics +0x38, count
	// +0x56). Errors, and semantic tables overlapping the registers, go to the managed export.
	private static void InterpolantMapping(Assembler a, Label fb)
	{
		var haveCount = a.CreateLabel(); var defaults = a.CreateLabel(); var noOutputs = a.CreateLabel();
		var inputsApart = a.CreateLabel(); var input = a.CreateLabel();
		var search = a.CreateLabel(); var found = a.CreateLabel(); var searched = a.CreateLabel();
		var modeSet = a.CreateLabel(); var mode13 = a.CreateLabel(); var mode2Unmasked = a.CreateLabel();
		var mode2Bits = a.CreateLabel(); var mode13Unmasked = a.CreateLabel(); var mode13Bits = a.CreateLabel();
		var final = a.CreateLabel(); var unmasked = a.CreateLabel(); var bit24 = a.CreateLabel(); var store = a.CreateLabel();
		var fill = a.CreateLabel(); var filled = a.CreateLabel(); var defaultFill = a.CreateLabel();
		a.test(rdi, rdi);
		a.jz(fb);
		a.xor(ecx, ecx);
		a.xor(r8d, r8d);
		a.test(rdx, rdx);
		a.jz(haveCount);
		a.mov(r8, __qword_ptr[rdx + 0x30]);
		a.mov(ecx, __dword_ptr[rdx + 0x50]);
		a.Label(ref haveCount);
		a.test(ecx, ecx);
		a.jz(defaults);
		a.test(r8, r8);
		a.jz(fb);
		a.test(rsi, rsi);
		a.jz(fb);
		a.cmp(ecx, 32);
		a.ja(fb);
		a.mov(r9, __qword_ptr[rsi + 0x38]);
		a.movzx(r10d, __word_ptr[rsi + 0x56]);
		a.test(r10d, r10d);
		a.jz(noOutputs);
		a.test(r9, r9);
		a.jz(fb);
		a.lea(rax, __[r9 + r10 * 4]);
		a.cmp(rax, rdi);
		a.jbe(noOutputs);
		a.lea(rax, __[rdi + 256]);
		a.cmp(rax, r9);
		a.ja(fb);
		a.Label(ref noOutputs);
		a.lea(rax, __[r8 + rcx * 4]);
		a.cmp(rax, rdi);
		a.jbe(inputsApart);
		a.lea(rax, __[rdi + 256]);
		a.cmp(rax, r8);
		a.ja(fb);
		a.Label(ref inputsApart);

		// rdi registers, r8 inputs, ecx input count, r9 outputs, r10d output count, edx index, esi source,
		// ebx output mask, ebp found, eax flags.
		a.push(rbx);
		a.push(rbp);
		a.push(r12);
		a.xor(edx, edx);
		a.Label(ref input);
		a.cmp(edx, ecx);
		a.jae(fill);
		a.mov(esi, __dword_ptr[r8 + rdx * 4]);
		a.xor(ebp, ebp);
		a.xor(ebx, ebx);
		a.xor(r11d, r11d);
		a.Label(ref search);
		a.cmp(r11d, r10d);
		a.jae(searched);
		a.mov(eax, __dword_ptr[r9 + r11 * 4]);
		a.cmp(al, sil);
		a.je(found);
		a.inc(r11d);
		a.jmp(search);
		a.Label(ref found);
		a.mov(ebx, eax);
		a.mov(ebp, 1);
		a.Label(ref searched);
		a.mov(eax, esi);
		a.shr(eax, 20);
		a.and(eax, 3);
		a.jnz(modeSet);
		// mode 0
		a.mov(eax, esi);
		a.shr(eax, 24);
		a.and(eax, 1);
		a.mov(r11d, ebp);
		a.xor(r11d, 1);
		a.or(eax, r11d);
		a.shl(eax, 5);
		SetTwoBits(a, 28, 8);
		a.jmp(final);
		a.Label(ref modeSet);
		a.mov(r12d, eax);
		a.mov(eax, esi);
		a.shl(eax, 4);
		a.and(eax, 0x03000000);
		a.add(eax, 0x00080000);
		a.cmp(r12d, 2);
		a.jne(mode13);
		a.and(eax, unchecked((int)0xFFEFFFDFu));
		a.test(ebp, ebp);
		a.jz(mode2Unmasked);
		a.mov(r11d, ebx);
		a.and(r11d, esi);
		a.not(r11d);
		a.shr(r11d, 16);
		a.and(r11d, 0x20);
		a.or(eax, r11d);
		a.jmp(mode2Bits);
		a.Label(ref mode2Unmasked);
		a.or(eax, 0x20);
		a.Label(ref mode2Bits);
		SetTwoBits(a, 30, 8);
		SetTwoBits(a, 30, 21);
		a.jmp(final);
		a.Label(ref mode13);
		a.test(ebp, ebp);
		a.jz(mode13Unmasked);
		a.mov(r11d, ebx);
		a.and(r11d, esi);
		a.and(eax, unchecked((int)0xFFFFFFDFu));
		a.mov(r12d, r11d);
		a.shr(r12d, 15);
		a.and(r12d, 0x20);
		a.or(eax, r12d);
		a.xor(eax, 0x20);
		a.and(eax, unchecked((int)0xFFEFFFFFu));
		a.mov(r12d, r11d);
		a.not(r12d);
		a.shr(r12d, 1);
		a.and(r12d, 0x00100000);
		a.or(eax, r12d);
		a.jmp(mode13Bits);
		a.Label(ref mode13Unmasked);
		a.or(eax, 0x00100020);
		a.Label(ref mode13Bits);
		SetTwoBits(a, 28, 8);
		SetTwoBits(a, 30, 21);
		a.Label(ref final);
		a.test(ebp, ebp);
		a.jz(unmasked);
		a.and(eax, unchecked((int)0xFFFFFFE0u));
		a.mov(r11d, ebx);
		a.shr(r11d, 8);
		a.and(r11d, 0x1F);
		a.or(eax, r11d);
		a.and(eax, unchecked((int)0xFFFFFBFFu));
		a.test(esi, 0x400000);
		a.jz(bit24);
		a.or(eax, 0x400);
		a.jmp(store);
		a.Label(ref bit24);
		a.mov(r11d, esi);
		a.shr(r11d, 14);
		a.and(r11d, 0x400);
		a.or(eax, r11d);
		a.jmp(store);
		a.Label(ref unmasked);
		a.and(eax, unchecked((int)0xFFFFFBE0u));
		a.Label(ref store);
		a.lea(r11d, __[rdx + 0x191]);
		a.mov(__dword_ptr[rdi + rdx * 8], r11d);
		a.mov(__dword_ptr[rdi + rdx * 8 + 4], eax);
		a.inc(edx);
		a.jmp(input);
		a.Label(ref fill);
		a.pop(r12);
		a.pop(rbp);
		a.pop(rbx);
		a.jmp(defaultFill);

		a.Label(ref defaults);
		a.xor(edx, edx);
		a.Label(ref defaultFill);
		a.cmp(edx, 32);
		a.jae(filled);
		a.lea(r11d, __[rdx + 0x191]);
		a.mov(__dword_ptr[rdi + rdx * 8], r11d);
		a.mov(__dword_ptr[rdi + rdx * 8 + 4], edx);
		a.inc(edx);
		a.jmp(defaultFill);
		a.Label(ref filled);
		a.xor(eax, eax);
		a.ret();
	}
}
