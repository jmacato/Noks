// SPDX-License-Identifier: MIT
//
// C# port of SkyEmu src/arm7.h at revision
// 01516d6798e3652b583e6a366085bb51c43b528d.
// Copyright (c) 2021 Skyler "Sky" Saleh.

namespace Noks.Cpu;

public sealed partial class Arm7Tdmi
{
    void ExecuteArmDataProcessing(uint instruction)
    {
        int opcode = (int)Bits(instruction, 21, 4);
        bool setFlags = Bits(instruction, 20, 1) != 0;
        int rn = (int)Bits(instruction, 16, 4);
        int rd = (int)Bits(instruction, 12, 4);
        bool immediate = Bits(instruction, 25, 1) != 0;

        uint operand2;
        bool? shifterCarry;
        int pcExtra = 0;
        if (immediate)
        {
            int rotation = (int)Bits(instruction, 8, 4) * 2;
            operand2 = RotateRight(Bits(instruction, 0, 8), rotation);
            shifterCarry = rotation == 0 ? null : (operand2 & 0x80000000u) != 0;
        }
        else
        {
            bool registerShift = Bits(instruction, 4, 1) != 0;
            uint amount;
            if (registerShift)
            {
                int rs = (int)Bits(instruction, 8, 4);
                amount = ReadRegister(rs) & 0xffu;
                pcExtra = 4;
                Idle();
            }
            else
            {
                amount = Bits(instruction, 7, 5);
            }

            int rm = (int)Bits(instruction, 0, 4);
            uint value = ReadOperand(rm, pcExtra);
            operand2 = Shift(
                value,
                (int)Bits(instruction, 5, 2),
                amount,
                registerShift,
                out shifterCarry);
        }

        uint operand1 = ReadOperand(rn, pcExtra);
        uint result;
        bool arithmetic = false;
        bool arithmeticCarry = false;
        bool arithmeticOverflow = false;
        bool writesResult = opcode is not (8 or 9 or 10 or 11);

        switch (opcode)
        {
            case 0: result = operand1 & operand2; break;
            case 1: result = operand1 ^ operand2; break;
            case 2:
                result = AddWithCarry(operand1, ~operand2, true, out arithmeticCarry, out arithmeticOverflow);
                arithmetic = true;
                break;
            case 3:
                result = AddWithCarry(operand2, ~operand1, true, out arithmeticCarry, out arithmeticOverflow);
                arithmetic = true;
                break;
            case 4:
                result = AddWithCarry(operand1, operand2, false, out arithmeticCarry, out arithmeticOverflow);
                arithmetic = true;
                break;
            case 5:
                result = AddWithCarry(operand1, operand2, Flag(FlagC), out arithmeticCarry, out arithmeticOverflow);
                arithmetic = true;
                break;
            case 6:
                result = AddWithCarry(operand1, ~operand2, Flag(FlagC), out arithmeticCarry, out arithmeticOverflow);
                arithmetic = true;
                break;
            case 7:
                result = AddWithCarry(operand2, ~operand1, Flag(FlagC), out arithmeticCarry, out arithmeticOverflow);
                arithmetic = true;
                break;
            case 8: result = operand1 & operand2; break;
            case 9: result = operand1 ^ operand2; break;
            case 10:
                result = AddWithCarry(operand1, ~operand2, true, out arithmeticCarry, out arithmeticOverflow);
                arithmetic = true;
                break;
            case 11:
                result = AddWithCarry(operand1, operand2, false, out arithmeticCarry, out arithmeticOverflow);
                arithmetic = true;
                break;
            case 12: result = operand1 | operand2; break;
            case 13: result = operand2; break;
            case 14: result = operand1 & ~operand2; break;
            default: result = ~operand2; break;
        }

        if (writesResult)
        {
            WriteRegister(rd, result);
        }

        if (!setFlags)
        {
            return;
        }

        SetNegativeAndZero(result);
        if (arithmetic)
        {
            SetFlag(FlagC, arithmeticCarry);
            SetFlag(FlagV, arithmeticOverflow);
        }
        else if (shifterCarry.HasValue)
        {
            SetFlag(FlagC, shifterCarry.Value);
        }

        // The Rd field still selects SPSR-to-CPSR restoration for the test
        // operations. TST, TEQ, CMP, and CMN do not write to Rd.
        if (rd == ProgramCounter)
        {
            _registers[Cpsr] = ReadCurrentSpsr();
            LatchInterruptDisable();
        }
    }

    void ExecuteArmMultiply(uint instruction)
    {
        bool accumulate = Bits(instruction, 21, 1) != 0;
        bool setFlags = Bits(instruction, 20, 1) != 0;
        int rd = (int)Bits(instruction, 16, 4);
        int rn = (int)Bits(instruction, 12, 4);
        uint multiplier = ReadOperand((int)Bits(instruction, 8, 4), 4);
        uint multiplicand = ReadOperand((int)Bits(instruction, 0, 4), 4);
        ulong accumulator = accumulate ? ReadOperand(rn, 4) : 0u;

        IdleMultiply(multiplier, signed: true, longMultiply: false);
        MultiplyResult multiplication = BoothMultiply(
            MultiplyFlavor.Short,
            multiplicand,
            multiplier,
            accumulator);
        uint result = (uint)multiplication.Value;
        if (accumulate)
        {
            Idle();
        }
        WriteRegister(rd, result);

        if (setFlags)
        {
            SetNegativeAndZero(result);
            SetFlag(FlagC, multiplication.Carry);
        }
    }

    void ExecuteArmMultiplyLong(uint instruction)
    {
        bool signed = Bits(instruction, 22, 1) != 0;
        bool accumulate = Bits(instruction, 21, 1) != 0;
        bool setFlags = Bits(instruction, 20, 1) != 0;
        int rdHigh = (int)Bits(instruction, 16, 4);
        int rdLow = (int)Bits(instruction, 12, 4);
        uint multiplier = ReadOperand((int)Bits(instruction, 8, 4), 4);
        uint multiplicand = ReadOperand((int)Bits(instruction, 0, 4), 4);

        ulong addend = accumulate
            ? ((ulong)ReadOperand(rdHigh, 4) << 32) | ReadOperand(rdLow, 4)
            : 0ul;
        MultiplyResult multiplication = BoothMultiply(
            signed ? MultiplyFlavor.LongSigned : MultiplyFlavor.LongUnsigned,
            multiplicand,
            multiplier,
            addend);
        ulong result = multiplication.Value;
        IdleMultiply(multiplier, signed, longMultiply: true);
        if (accumulate) Idle();

        WriteRegister(rdLow, (uint)result);
        WriteRegister(rdHigh, (uint)(result >> 32));
        if (setFlags)
        {
            SetFlag(FlagN, (result & 0x8000000000000000ul) != 0);
            SetFlag(FlagZ, result == 0);
            SetFlag(FlagC, multiplication.Carry);
        }
    }

    uint ReadOperand(int register, int pcExtra) =>
        ReadRegister(register) + (register == ProgramCounter ? (uint)pcExtra : 0u);

    uint Shift(
        uint value,
        int type,
        uint amount,
        bool registerSpecified,
        out bool? carry)
    {
        carry = null;
        switch (type)
        {
            case 0:
                if (amount == 0)
                {
                    return value;
                }
                if (amount < 32)
                {
                    carry = ((value >> (int)(32 - amount)) & 1u) != 0;
                    return value << (int)amount;
                }
                carry = amount == 32 && (value & 1u) != 0;
                return 0;

            case 1:
                if (amount == 0 && registerSpecified)
                {
                    return value;
                }
                if (amount == 0)
                {
                    amount = 32;
                }
                if (amount < 32)
                {
                    carry = ((value >> (int)(amount - 1)) & 1u) != 0;
                    return value >> (int)amount;
                }
                carry = amount == 32 && (value & 0x80000000u) != 0;
                return 0;

            case 2:
                if (amount == 0 && registerSpecified)
                {
                    return value;
                }
                if (amount == 0)
                {
                    amount = 32;
                }
                if (amount < 32)
                {
                    carry = ((value >> (int)(amount - 1)) & 1u) != 0;
                    return (uint)((int)value >> (int)amount);
                }
                carry = (value & 0x80000000u) != 0;
                return carry.Value ? uint.MaxValue : 0;

            default:
                if (amount == 0 && registerSpecified)
                {
                    return value;
                }
                if (amount == 0)
                {
                    bool oldCarry = Flag(FlagC);
                    carry = (value & 1u) != 0;
                    return (value >> 1) | (oldCarry ? 0x80000000u : 0u);
                }
                int rotation = (int)(amount & 31u);
                uint result = RotateRight(value, rotation);
                carry = (result & 0x80000000u) != 0;
                return result;
        }
    }

    static uint AddWithCarry(
        uint left,
        uint right,
        bool carryIn,
        out bool carry,
        out bool overflow)
    {
        ulong unsignedResult = (ulong)left + right + (carryIn ? 1ul : 0ul);
        long signedResult = (long)(int)left + (int)right + (carryIn ? 1L : 0L);
        carry = unsignedResult > uint.MaxValue;
        overflow = signedResult > int.MaxValue || signedResult < int.MinValue;
        return (uint)unsignedResult;
    }

    void IdleMultiply(uint multiplier, bool signed, bool longMultiply)
    {
        int cycles;
        if ((multiplier & 0xffffff00u) == 0 ||
            (signed && (multiplier & 0xffffff00u) == 0xffffff00u))
        {
            cycles = 1;
        }
        else if ((multiplier & 0xffff0000u) == 0 ||
                 (signed && (multiplier & 0xffff0000u) == 0xffff0000u))
        {
            cycles = 2;
        }
        else if ((multiplier & 0xff000000u) == 0 ||
                 (signed && (multiplier & 0xff000000u) == 0xff000000u))
        {
            cycles = 3;
        }
        else
        {
            cycles = 4;
        }

        if (longMultiply)
        {
            cycles++;
        }
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            Idle();
        }
    }
}
