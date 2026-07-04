// SPDX-License-Identifier: Zlib
//
// C# port of the ARM7TDMI Booth multiplier reconstruction at
// https://github.com/zaydlang/multiplication-algorithm, revision
// 29cef09501154a8d6ed63c52cde9c3e8be1b5034.
// Copyright (c) 2024 zaydlang. Contributions by calc84maniac.
// This is an altered source version; see LICENSE.Multiplier.txt.

namespace Noks.Cpu;

public sealed partial class Arm7Tdmi
{
    enum MultiplyFlavor
    {
        Short,
        LongSigned,
        LongUnsigned,
    }

    readonly record struct MultiplyResult(ulong Value, bool Carry);

    readonly record struct CsaResult(ulong Output, ulong Carry);

    readonly record struct BoothTerm(ulong Value, bool Carry);

    readonly record struct AdderResult(uint Value, bool Carry);

    const ulong BoothMask34 = 0x3fffffffful;
    const ulong BoothMask33 = 0x1fffffffful;

    static MultiplyResult BoothMultiply(
        MultiplyFlavor flavor,
        uint multiplicand32,
        uint multiplier32,
        ulong accumulator)
    {
        bool signed = flavor is MultiplyFlavor.Short or MultiplyFlavor.LongSigned;
        bool longResult = flavor is not MultiplyFlavor.Short;
        ulong multiplicand = signed
            ? SignExtendBits(multiplicand32, 32, 34)
            : multiplicand32 & BoothMask33;
        ulong multiplier = signed
            ? SignExtendBits(multiplier32, 32, 34)
            : multiplier32 & BoothMask33;
        bool adderCarryIn = (multiplier & 1ul) != 0;

        CsaResult csa = new(
            accumulator,
            adderCarryIn ? ~multiplicand : 0ul);
        ulong accumulatorShiftRegister = accumulator >> 34;

        UInt128 partialSum = csa.Output & 1ul;
        UInt128 partialCarry = csa.Carry & 1ul;
        csa = new(csa.Output >> 1, csa.Carry >> 1);
        partialSum = RotateRight128(partialSum, 1);
        partialCarry = RotateRight128(partialCarry, 1);

        int iterations = 0;
        do
        {
            csa = BoothCycle(csa, multiplicand, multiplier, ref accumulatorShiftRegister);
            partialSum |= csa.Output & 0xfful;
            partialCarry |= csa.Carry & 0xfful;
            csa = new(csa.Output >> 8, csa.Carry >> 8);
            partialSum = RotateRight128(partialSum, 8);
            partialCarry = RotateRight128(partialCarry, 8);
            multiplier = ArithmeticShiftRight33(multiplier, 8);
            iterations++;
        }
        while (!ShouldTerminate(multiplier, signed));

        partialSum |= csa.Output;
        partialCarry |= csa.Carry;
        int correction = iterations switch
        {
            1 => 23,
            2 => 15,
            3 => 7,
            _ => 31,
        };
        partialSum = RotateRight128(partialSum, correction);
        partialCarry = RotateRight128(partialCarry, correction);

        ulong sumLow = (ulong)partialSum;
        ulong sumHigh = (ulong)(partialSum >> 64);
        ulong carryLow = (ulong)partialCarry;
        ulong carryHigh = (ulong)(partialCarry >> 64);

        if (longResult)
        {
            if (iterations == 4)
            {
                AdderResult low = Add32((uint)sumHigh, (uint)carryHigh, adderCarryIn);
                AdderResult high = Add32(
                    (uint)(sumHigh >> 32),
                    (uint)(carryHigh >> 32),
                    low.Carry);
                return new(((ulong)high.Value << 32) | low.Value, (carryHigh >> 63) != 0);
            }

            AdderResult lowPartial = Add32(
                (uint)(sumHigh >> 32),
                (uint)(carryHigh >> 32),
                adderCarryIn);
            int shift = 2 + 8 * iterations;
            carryLow = SignExtendBits(carryLow, shift, 64);
            sumLow |= accumulatorShiftRegister << shift;
            AdderResult highPartial = Add32(
                (uint)sumLow,
                (uint)carryLow,
                lowPartial.Carry);
            return new(
                ((ulong)highPartial.Value << 32) | lowPartial.Value,
                (carryHigh >> 63) != 0);
        }

        if (iterations == 4)
        {
            AdderResult output = Add32((uint)sumHigh, (uint)carryHigh, adderCarryIn);
            return new(output.Value, ((carryHigh >> 31) & 1ul) != 0);
        }

        AdderResult shortOutput = Add32(
            (uint)(sumHigh >> 32),
            (uint)(carryHigh >> 32),
            adderCarryIn);
        return new(shortOutput.Value, (carryHigh >> 63) != 0);
    }

    static CsaResult BoothCycle(
        CsaResult previous,
        ulong multiplicand,
        ulong multiplier,
        ref ulong accumulatorShiftRegister)
    {
        CsaResult current = previous;
        CsaResult final = default;
        for (int index = 0; index < 4; index++)
        {
            ulong previousCarry = current.Carry & BoothMask33;
            current = new(current.Output & BoothMask33, previousCarry);
            BoothTerm term = BoothRecode(multiplicand, (int)((multiplier >> (2 * index)) & 7ul));
            CsaResult result = CarrySaveAdd(current.Output, term.Value & BoothMask33, current.Carry);
            result = new(result.Output, (result.Carry << 1) | (term.Carry ? 1ul : 0ul));

            final = new(
                final.Output | ((result.Output & 3ul) << (2 * index)),
                final.Carry | ((result.Carry & 3ul) << (2 * index)));
            result = new(result.Output >> 2, result.Carry >> 2);

            ulong magic = Bit(accumulatorShiftRegister, 0) +
                (Bit(previousCarry, 32) == 0 ? 1ul : 0ul) +
                (Bit(term.Value, 33) == 0 ? 1ul : 0ul);
            result = new(
                result.Output | (magic << 31),
                result.Carry | ((Bit(accumulatorShiftRegister, 1) == 0 ? 1ul : 0ul) << 32));
            accumulatorShiftRegister >>= 2;
            current = result;
        }

        return new(
            final.Output | (current.Output << 8),
            final.Carry | (current.Carry << 8));
    }

    static BoothTerm BoothRecode(ulong input, int chunk)
    {
        (ulong value, bool carry) = chunk switch
        {
            0 => (0ul, false),
            1 or 2 => (input, false),
            3 => (2ul * input, false),
            4 => (~(2ul * input), true),
            5 or 6 => (~input, true),
            _ => (0ul, false),
        };
        return new(value & BoothMask34, carry);
    }

    static CsaResult CarrySaveAdd(ulong left, ulong middle, ulong right) => new(
        left ^ middle ^ right,
        (left & middle) | (middle & right) | (right & left));

    static AdderResult Add32(uint left, uint right, bool carry)
    {
        ulong full = (ulong)left + right + (carry ? 1ul : 0ul);
        return new((uint)full, full > uint.MaxValue);
    }

    static bool ShouldTerminate(ulong multiplier, bool signed) =>
        multiplier == 0 || (signed && multiplier == BoothMask33);

    static ulong ArithmeticShiftRight33(ulong value, int amount)
    {
        value &= BoothMask33;
        if ((value & (1ul << 32)) != 0)
        {
            value |= ~BoothMask33;
        }
        return unchecked((ulong)((long)value >> amount)) & BoothMask33;
    }

    static ulong SignExtendBits(ulong value, int sourceBits, int destinationBits)
    {
        ulong sourceMask = sourceBits == 64 ? ulong.MaxValue : (1ul << sourceBits) - 1ul;
        ulong destinationMask = destinationBits == 64
            ? ulong.MaxValue
            : (1ul << destinationBits) - 1ul;
        value &= sourceMask;
        if ((value & (1ul << (sourceBits - 1))) != 0)
        {
            value |= destinationMask & ~sourceMask;
        }
        return value & destinationMask;
    }

    static ulong Bit(ulong value, int bit) => (value >> bit) & 1ul;

    static UInt128 RotateRight128(UInt128 value, int amount) =>
        (value >> amount) | (value << (128 - amount));
}
