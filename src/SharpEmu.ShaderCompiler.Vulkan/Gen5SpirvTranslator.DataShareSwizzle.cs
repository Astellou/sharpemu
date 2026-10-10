// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private bool TryEmitDataShareSwizzle(Gen5ShaderInstruction instruction,
            Gen5DataShareControl control, out string error)
        {
            error = string.Empty;
            var selection = control.SingleOffsetBytes;
            if (selection >= 0xE000 || instruction.Sources.Count != 1 || instruction.Destinations.Count != 1)
            {
                error = selection >= 0xE000 ? "unsupported FFT data-share swizzle" : "invalid data-share swizzle operands";
                return false;
            }
            if (_subgroupInvocationIdInput == 0)
            {
                error = "data-share swizzle requires subgroup lane access";
                return false;
            }

            var lane = Load(_uintType, _subgroupInvocationIdInput);
            uint sourceLane;
            if (selection >= 0xC000)
            {
                var rotation = (selection >> 5) & 31;
                var displacement = (selection & 0x400) != 0 ? unchecked(0u - rotation) : rotation;
                var mask = selection & 31;
                sourceLane = BitwiseOr(BitwiseAnd(lane, UInt(mask)),
                    BitwiseAnd(IAdd(lane, UInt(displacement)), UInt(~mask & 31)));
                sourceLane = BitwiseOr(BitwiseAnd(lane, UInt(~31u)), sourceLane);
            }
            else if ((selection & 0x8000) != 0)
            {
                var selector = BitwiseAnd(ShiftRightLogical(UInt(selection),
                    ShiftLeftLogical(BitwiseAnd(lane, UInt(3)), UInt(1))), UInt(3));
                sourceLane = BitwiseOr(BitwiseAnd(lane, UInt(~3u)), selector);
            }
            else
            {
                var selector = BitwiseXor(BitwiseOr(BitwiseAnd(lane, UInt(selection & 31)),
                    UInt((selection >> 5) & 31)), UInt((selection >> 10) & 31));
                sourceLane = BitwiseOr(BitwiseAnd(lane, UInt(~31u)), selector);
            }

            // A ballot excludes inactive host lanes as well as lanes outside EXEC.
            var ballot = _module.AddInstruction(SpirvOp.GroupNonUniformBallot, _uvec4Type,
                UInt(3), Load(_boolType, _exec));
            var sourceWord = _module.AddInstruction(SpirvOp.VectorExtractDynamic, _uintType,
                ballot, ShiftRightLogical(sourceLane, UInt(5)));
            var sourceActive = IsNotZero(BitwiseAnd(ShiftRightLogical(sourceWord,
                BitwiseAnd(sourceLane, UInt(31))), UInt(1)));
            var safeSourceLane = _module.AddInstruction(SpirvOp.Select, _uintType, sourceActive, sourceLane, lane);
            var value = _module.AddInstruction(SpirvOp.GroupNonUniformShuffle, _uintType,
                UInt(3), GetRawSource(instruction, 0), safeSourceLane);
            StoreV(instruction.Destinations[0].Value,
                _module.AddInstruction(SpirvOp.Select, _uintType, sourceActive, value, UInt(0)));
            return true;
        }

        private bool TryEmitDataShareBpermute(Gen5ShaderInstruction instruction,
            Gen5DataShareControl control, out string error)
        {
            error = string.Empty;
            if (instruction.Sources.Count != 2 || instruction.Destinations.Count != 1)
            {
                error = "invalid data-share bpermute operands";
                return false;
            }
            if (_subgroupInvocationIdInput == 0)
            {
                error = "data-share bpermute requires subgroup lane access";
                return false;
            }

            var lane = Load(_uintType, _subgroupInvocationIdInput);
            var address = IAdd(GetRawSource(instruction, 0), UInt(control.SingleOffsetBytes));
            var index = BitwiseAnd(ShiftRightLogical(address, UInt(2)), UInt(31));
            var sourceLane = BitwiseOr(BitwiseAnd(lane, UInt(~31u)), index);

            // DS_BPERMUTE reads only from active lanes in the current 32-lane half-wave.
            var ballot = _module.AddInstruction(SpirvOp.GroupNonUniformBallot, _uvec4Type,
                UInt(3), Load(_boolType, _exec));
            var sourceWord = _module.AddInstruction(SpirvOp.VectorExtractDynamic, _uintType,
                ballot, ShiftRightLogical(sourceLane, UInt(5)));
            var sourceActive = IsNotZero(BitwiseAnd(ShiftRightLogical(sourceWord,
                BitwiseAnd(sourceLane, UInt(31))), UInt(1)));
            var safeSourceLane = _module.AddInstruction(SpirvOp.Select, _uintType,
                sourceActive, sourceLane, lane);
            var value = _module.AddInstruction(SpirvOp.GroupNonUniformShuffle, _uintType,
                UInt(3), GetRawSource(instruction, 1), safeSourceLane);
            StoreV(instruction.Destinations[0].Value,
                _module.AddInstruction(SpirvOp.Select, _uintType, sourceActive, value, UInt(0)));
            return true;
        }

        // DS_PERMUTE_B32 is the forward form of DS_BPERMUTE_B32: every active lane sends its
        // DATA0 to the lane its address names, within its 32-lane half-wave. A lane nobody
        // sends to reads 0, and when several lanes target one lane the highest sender wins.
        // Subgroup operations only pull, so each lane pulls every sender's target and value
        // in ascending lane order and keeps the last one aimed at it.
        private bool TryEmitDataSharePermute(Gen5ShaderInstruction instruction,
            Gen5DataShareControl control, out string error)
        {
            error = string.Empty;
            if (instruction.Sources.Count != 2 || instruction.Destinations.Count != 1)
            {
                error = "invalid data-share permute operands";
                return false;
            }
            if (_subgroupInvocationIdInput == 0)
            {
                error = "data-share permute requires subgroup lane access";
                return false;
            }

            var lane = Load(_uintType, _subgroupInvocationIdInput);
            var halfWave = BitwiseAnd(lane, UInt(~31u));
            var laneInHalf = BitwiseAnd(lane, UInt(31));
            var address = IAdd(GetRawSource(instruction, 0), UInt(control.SingleOffsetBytes));
            var target = BitwiseAnd(ShiftRightLogical(address, UInt(2)), UInt(31));
            var data = GetRawSource(instruction, 1);
            var ballot = _module.AddInstruction(SpirvOp.GroupNonUniformBallot, _uvec4Type,
                UInt(3), Load(_boolType, _exec));
            var activeSenders = _module.AddInstruction(SpirvOp.VectorExtractDynamic, _uintType,
                ballot, ShiftRightLogical(lane, UInt(5)));
            var result = UInt(0);
            for (var sender = 0u; sender < 32; sender++)
            {
                var senderActive = IsNotZero(BitwiseAnd(activeSenders, UInt(1u << (int)sender)));
                // Every lane takes part in both shuffles; an inactive sender is read as this lane.
                var senderLane = _module.AddInstruction(SpirvOp.Select, _uintType,
                    senderActive, BitwiseOr(halfWave, UInt(sender)), lane);
                var senderTarget = _module.AddInstruction(SpirvOp.GroupNonUniformShuffle, _uintType,
                    UInt(3), target, senderLane);
                var senderValue = _module.AddInstruction(SpirvOp.GroupNonUniformShuffle, _uintType,
                    UInt(3), data, senderLane);
                var aimedHere = LogicalAnd(senderActive,
                    _module.AddInstruction(SpirvOp.IEqual, _boolType, senderTarget, laneInHalf));
                result = _module.AddInstruction(SpirvOp.Select, _uintType, aimedHere, senderValue, result);
            }

            StoreV(instruction.Destinations[0].Value, result);
            return true;
        }
    }
}
