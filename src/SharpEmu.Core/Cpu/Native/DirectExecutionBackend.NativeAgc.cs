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
}
