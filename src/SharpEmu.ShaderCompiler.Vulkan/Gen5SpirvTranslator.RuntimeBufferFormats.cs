// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        // A buffer whose format the dispatch binds converts with the same format for every
        // access of the dispatch, so its layout and conversion parameters are decoded once,
        // at the program's entry; an access only extracts, converts and places its bits.
        private sealed class RuntimeBufferFormat
        {
            public required uint Bound;
            public required uint Signed;
            public required uint Snorm;
            public required uint SignedNormalized;
            public required uint Uint;
            public required uint Sint;
            public required uint ToFloat;
            public required uint IsFloat;
            public required uint PackedFloat;
            public required uint One;
            public required uint[] Selectors;
            public required RuntimeFormatComponent[] Components;
        }

        private sealed class RuntimeFormatComponent
        {
            public required uint ByteOffset;
            public required uint BitOffset;
            public required uint BitCount;
            public required uint Present;
            public required uint Wide;
            public required uint LastByte;
            public required uint Mask;
            public required uint Reciprocal;
            public required uint HalfShift;
            public required uint Minimum;
            public required uint Maximum;
            public required uint Scale;
            public required uint SignedMinimum;
            public required uint SignedMaximum;
            public required uint Bit;
            public required uint End;
            public required uint[] Placed;
        }

        private readonly Dictionary<int, RuntimeBufferFormat> _runtimeBufferFormats = new();

        // Decodes the format of every buffer an untyped formatted access reads at runtime.
        private void PrepareRuntimeBufferFormats()
        {
            var buffers = _request.Resources.Info.Buffers;
            var bindings = new SortedSet<int>();
            foreach (var entry in _request.Memory.Entries)
            {
                if (entry.Formatted && !entry.Typed && !entry.PlanningOnly &&
                    entry.Resource < (uint)buffers.Count && buffers[(int)entry.Resource].RuntimeFormat)
                {
                    bindings.Add((int)entry.Resource);
                }
            }

            foreach (var binding in bindings)
            {
                _runtimeBufferFormats[binding] = DecodeRuntimeBufferFormat(RuntimeFormatWord3(binding));
            }
        }

        private uint RuntimeFormatWord3(int binding) =>
            LoadShaderDataDword(UInt(_request.Bindings.BufferFormatDword + (uint)binding));

        // The entry's decode, or one at the access for a binding it did not see (a candidate table arm).
        private RuntimeBufferFormat RuntimeBufferFormatOf(int binding, uint descriptorWord3) =>
            _runtimeBufferFormats.TryGetValue(binding, out var format) ? format : DecodeRuntimeBufferFormat(descriptorWord3);

        private RuntimeBufferFormat DecodeRuntimeBufferFormat(uint descriptorWord3)
        {
            uint SelectU(uint condition, uint whenTrue, uint whenFalse) =>
                _module.AddInstruction(SpirvOp.Select, _uintType, condition, whenTrue, whenFalse);
            uint SelectF(uint condition, uint whenTrue, uint whenFalse) =>
                _module.AddInstruction(SpirvOp.Select, _floatType, condition, whenTrue, whenFalse);
            uint ToFloat(uint unsigned) => _module.AddInstruction(SpirvOp.ConvertUToF, _floatType, unsigned);
            uint Equal(uint left, uint right) => _module.AddInstruction(SpirvOp.IEqual, _boolType, left, right);

            var unifiedFormat = BitwiseAnd(ShiftRightLogical(descriptorWord3, UInt(12)), UInt(0x7F));
            var (dataFormat, numberFormat) = DecodeGfx10BufferFormat(unifiedFormat);
            uint Is(uint format) => Equal(numberFormat, UInt(format));
            var unorm = Is(0);
            var snorm = Is(1);
            var uscaled = Is(2);
            var sscaled = Is(3);
            var sint = Is(5);
            var components = new RuntimeFormatComponent[4];
            for (var component = 0; component < components.Length; component++)
            {
                var (byteOffset, bitOffset, bitCount) = Gfx10ComponentLayout(dataFormat, component);
                var present = _module.AddInstruction(SpirvOp.INotEqual, _boolType, bitCount, UInt(0));
                var wide = Equal(bitCount, UInt(32));
                var mask = SelectU(wide, UInt(uint.MaxValue), ISubU(ShiftLeftLogical(UInt(1), bitCount), UInt(1)));
                var signedMaximum = ShiftRightLogical(mask, UInt(1));
                var divisor = SelectF(unorm, ToFloat(mask), SelectF(snorm, ToFloat(signedMaximum), Float(1f)));
                var bit = IAdd(ShiftLeftLogical(BitwiseAnd(byteOffset, UInt(3)), UInt(3)), bitOffset);
                var dword = ShiftRightLogical(byteOffset, UInt(2));
                var placedMask = ShiftLeftLogical(mask, bit);
                var placed = new uint[4];
                for (uint index = 0; index < placed.Length; index++)
                {
                    placed[index] = SelectU(LogicalAnd(present, Equal(dword, UInt(index))), placedMask, UInt(0));
                }

                var componentBytes = ShiftRightLogical(IAdd(IAdd(bitOffset, bitCount), UInt(7)), UInt(3));
                components[component] = new RuntimeFormatComponent
                {
                    ByteOffset = byteOffset,
                    BitOffset = bitOffset,
                    BitCount = bitCount,
                    Present = present,
                    Wide = wide,
                    LastByte = ISubU(componentBytes, UInt(1)),
                    Mask = mask,
                    Reciprocal = RoundedReciprocal(divisor),
                    // A 10 or 11 bit unsigned mini-float shares the half's exponent: shifted to the
                    // half's top bits it is a half, denormals and specials included.
                    HalfShift = SelectU(
                        _module.AddInstruction(SpirvOp.ULessThan, _boolType, bitCount, UInt(16)),
                        ISubU(UInt(15), bitCount),
                        UInt(0)),
                    // unorm [0, 1] x mask, snorm [-1, 1] x max, uscaled [0, mask], sscaled [min, max].
                    Minimum = SelectF(snorm, Float(-1f), SelectF(sscaled,
                        _module.AddInstruction(SpirvOp.FNegate, _floatType,
                            _module.AddInstruction(SpirvOp.FAdd, _floatType, ToFloat(signedMaximum), Float(1f))),
                        Float(0f))),
                    Maximum = SelectF(LogicalOr(unorm, snorm), Float(1f), SelectF(uscaled, ToFloat(mask), ToFloat(signedMaximum))),
                    Scale = SelectF(unorm, ToFloat(mask), SelectF(snorm, ToFloat(signedMaximum), Float(1f))),
                    SignedMinimum = _module.AddInstruction(SpirvOp.SNegate, _intType,
                        IAddSigned(Bitcast(_intType, signedMaximum), 1)),
                    SignedMaximum = Bitcast(_intType, signedMaximum),
                    Bit = bit,
                    End = SelectU(present, IAdd(byteOffset, componentBytes), UInt(0)),
                    Placed = placed,
                };
            }

            var selectors = new uint[4];
            for (uint destination = 0; destination < selectors.Length; destination++)
            {
                selectors[destination] = BitwiseAnd(ShiftRightLogical(descriptorWord3, UInt(destination * 3)), UInt(7));
            }

            return new RuntimeBufferFormat
            {
                Bound = IsDescriptorBound(descriptorWord3),
                Signed = LogicalOr(LogicalOr(snorm, sscaled), sint),
                Snorm = snorm,
                SignedNormalized = LogicalOr(snorm, sscaled),
                Uint = Is(4),
                Sint = sint,
                ToFloat = _module.AddInstruction(SpirvOp.ULessThanEqual, _boolType, numberFormat, UInt(3)),
                IsFloat = Is(7),
                PackedFloat = LogicalAnd(Is(7), LogicalOr(Equal(dataFormat, UInt(6)), Equal(dataFormat, UInt(7)))),
                One = Gfx10FormatOne(numberFormat),
                Selectors = selectors,
                Components = components,
            };
        }

        // An untyped formatted load of a runtime format: each component reads its bits at the
        // decoded layout and converts with the decoded parameters.
        private void EmitRuntimeBufferFormatLoad(int bindingIndex, uint byteAddress, uint descriptorWord3, uint vectorData, uint componentCount)
        {
            var format = RuntimeBufferFormatOf(bindingIndex, descriptorWord3);
            var canonical = new uint[4];
            var componentBounds = new uint[4];
            for (var component = 0; component < canonical.Length; component++)
            {
                var part = format.Components[component];
                var address = IAdd(byteAddress, part.ByteOffset);
                var packed = LoadUnalignedBufferWord(bindingIndex, address);
                componentBounds[component] = LogicalOr(
                    LogicalNot(part.Present),
                    IsBufferElementInRange(bindingIndex, address, part.LastByte));
                canonical[component] = _module.AddInstruction(
                    SpirvOp.Select,
                    _uintType,
                    part.Present,
                    ConvertRuntimeComponent(format, part, packed),
                    component == 3 ? format.One : UInt(0));
            }

            StoreRuntimeSwizzledComponents(vectorData, componentCount, format.Selectors[..(int)componentCount], canonical, componentBounds, format.One);
        }

        private uint ConvertRuntimeComponent(RuntimeBufferFormat format, RuntimeFormatComponent part, uint packed)
        {
            uint SelectU(uint condition, uint whenTrue, uint whenFalse) =>
                _module.AddInstruction(SpirvOp.Select, _uintType, condition, whenTrue, whenFalse);

            var raw = _module.AddInstruction(SpirvOp.BitFieldUExtract, _uintType, packed, part.BitOffset, part.BitCount);
            var signedRaw = _module.AddInstruction(SpirvOp.BitFieldSExtract, _intType, Bitcast(_intType, packed), part.BitOffset, part.BitCount);
            var numerator = _module.AddInstruction(
                SpirvOp.Select,
                _floatType,
                format.Signed,
                _module.AddInstruction(SpirvOp.ConvertSToF, _floatType, signedRaw),
                _module.AddInstruction(SpirvOp.ConvertUToF, _floatType, raw));

            var quotient = _module.AddInstruction(SpirvOp.FMul, _floatType, numerator, part.Reciprocal);
            var normalized = _module.AddInstruction(
                SpirvOp.Select, _floatType, format.Snorm, Ext(40, _floatType, quotient, Float(-1f)), quotient);

            var integer = SelectU(format.Sint, Bitcast(_uintType, signedRaw), raw);
            var half = _module.AddInstruction(
                SpirvOp.CompositeExtract,
                _floatType,
                Ext(62, _vec2Type, BitwiseAnd(ShiftLeftLogical(raw, part.HalfShift), UInt(0xFFFF))),
                0);
            var floatBits = SelectU(part.Wide, raw, Bitcast(_uintType, half));
            return SelectU(format.IsFloat, floatBits, SelectU(format.ToFloat, Bitcast(_uintType, normalized), integer));
        }

        // A conversion divides by multiplying with the divisor's rounded reciprocal, as the
        // driver compiles a division by a constant. A runtime divisor's reciprocal is only
        // approximate (3 / 3 read as 0.99999994), so one Newton step rounds it first.
        private uint RoundedReciprocal(uint divisor)
        {
            var estimate = _module.AddInstruction(SpirvOp.FDiv, _floatType, Float(1f), divisor);
            var error = Ext(50, _floatType, _module.AddInstruction(SpirvOp.FNegate, _floatType, divisor), estimate, Float(1f));
            return Ext(50, _floatType, error, estimate, estimate);
        }

        // A formatted store of a runtime format: each component encodes with the decoded
        // parameters and lands in its dword through the decoded placement masks.
        private void EmitRuntimeBufferFormatStore(int bindingIndex, uint byteAddress, Gen5BufferMemoryControl control, uint descriptorWord3)
        {
            var format = RuntimeBufferFormatOf(bindingIndex, descriptorWord3);
            var count = Math.Min((int)control.DwordCount, 4);
            var values = new uint[4];
            var masks = new uint[4];
            Array.Fill(values, UInt(0));
            Array.Fill(masks, UInt(0));
            var elementBytes = UInt(0);
            for (var component = 0; component < count; component++)
            {
                var part = format.Components[component];
                var encoded = ShiftLeftLogical(
                    EncodeRuntimeComponent(format, part, LoadV(control.VectorData + (uint)component)),
                    part.Bit);
                for (var index = 0; index < values.Length; index++)
                {
                    values[index] = BitwiseOr(values[index], BitwiseAnd(encoded, part.Placed[index]));
                    masks[index] = component == 0 ? part.Placed[index] : BitwiseOr(masks[index], part.Placed[index]);
                }

                elementBytes = component == 0 ? part.End : Ext(41, _uintType, elementBytes, part.End);
            }

            var allowed = LogicalAnd(
                LogicalAnd(format.Bound, _module.AddInstruction(SpirvOp.INotEqual, _boolType, elementBytes, UInt(0))),
                IsBufferElementInRange(bindingIndex, byteAddress, ISubU(elementBytes, UInt(1))));
            EmitConditional(allowed, () => StoreBufferElementBits(
                bindingIndex,
                byteAddress,
                [(values[0], masks[0]), (values[1], masks[1]), (values[2], masks[2]), (values[3], masks[3])]));
        }

        // EncodeGfx10BufferComponent with the decoded parameters: one clamp, scale and round
        // for the normalized and scaled formats, a clamp for integers, and the float formats'
        // mini-float encodings only behind the dispatch-uniform packed-float branch.
        private uint EncodeRuntimeComponent(RuntimeBufferFormat format, RuntimeFormatComponent part, uint value)
        {
            uint SelectU(uint condition, uint whenTrue, uint whenFalse) =>
                _module.AddInstruction(SpirvOp.Select, _uintType, condition, whenTrue, whenFalse);

            var input = Bitcast(_floatType, value);
            input = _module.AddInstruction(SpirvOp.Select, _floatType, _module.AddInstruction(SpirvOp.IsNan, _boolType, input), Float(0f), input);
            var rounded = Ext(2, _floatType, _module.AddInstruction(
                SpirvOp.FMul, _floatType, Ext(43, _floatType, input, part.Minimum, part.Maximum), part.Scale));
            var normalized = SelectU(
                format.SignedNormalized,
                Bitcast(_uintType, _module.AddInstruction(SpirvOp.ConvertFToS, _intType, rounded)),
                _module.AddInstruction(SpirvOp.ConvertFToU, _uintType, rounded));
            var signedClamped = Bitcast(_uintType, Ext(45, _intType, Bitcast(_intType, value), part.SignedMinimum, part.SignedMaximum));
            var integer = SelectU(format.Uint, Ext(38, _uintType, value, part.Mask), SelectU(format.Sint, signedClamped, value));
            var half = BitwiseAnd(Ext(58, _uintType, _module.AddInstruction(
                SpirvOp.CompositeConstruct, _vec2Type, Bitcast(_floatType, value), Float(0f))), UInt(0xFFFF));
            var encoded = SelectU(format.IsFloat, half, SelectU(format.ToFloat, normalized, integer));

            if (_formatConversionScratch == 0)
            {
                _formatConversionScratch = _module.AddGlobalVariable(_privateUintPointer, SpirvStorageClass.Private);
                _module.AddName(_formatConversionScratch, "formatConversion");
                _interfaces.Add(_formatConversionScratch);
            }

            var result = _formatConversionScratch;
            Store(result, encoded);
            EmitConditional(format.PackedFloat, () => Store(result, SelectU(
                _module.AddInstruction(SpirvOp.IEqual, _boolType, part.BitCount, UInt(10)),
                EncodeUnsignedMiniFloat(value, 10),
                EncodeUnsignedMiniFloat(value, 11))));

            // A 32-bit component keeps its register bits, whatever the number format.
            return BitwiseAnd(SelectU(part.Wide, value, Load(_uintType, result)), part.Mask);
        }
    }
}
