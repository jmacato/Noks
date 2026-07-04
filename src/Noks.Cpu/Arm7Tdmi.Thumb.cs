// SPDX-License-Identifier: MIT
//
// C# port of SkyEmu src/arm7.h at revision
// 01516d6798e3652b583e6a366085bb51c43b528d.
// Copyright (c) 2021 Skyler "Sky" Saleh.

namespace Noks.Cpu;

public sealed partial class Arm7Tdmi
{
    void ExecuteThumbMoveShifted(ushort instruction)
    {
        int operation = (int)Bits(instruction, 11, 2);
        uint amount = Bits(instruction, 6, 5);
        int source = (int)Bits(instruction, 3, 3);
        int destination = (int)Bits(instruction, 0, 3);
        uint result = Shift(
            ReadRegister(source),
            operation,
            amount,
            registerSpecified: false,
            out bool? carry);
        WriteRegister(destination, result);
        SetNegativeAndZero(result);
        if (carry.HasValue)
        {
            SetFlag(FlagC, carry.Value);
        }
    }

    void ExecuteThumbAddSubtract(ushort instruction)
    {
        bool immediate = Bits(instruction, 10, 1) != 0;
        bool subtract = Bits(instruction, 9, 1) != 0;
        int operand = (int)Bits(instruction, 6, 3);
        int source = (int)Bits(instruction, 3, 3);
        int destination = (int)Bits(instruction, 0, 3);
        uint left = ReadRegister(source);
        uint right = immediate ? (uint)operand : ReadRegister(operand);
        uint result = subtract
            ? AddWithCarry(left, ~right, true, out bool carry, out bool overflow)
            : AddWithCarry(left, right, false, out carry, out overflow);
        WriteRegister(destination, result);
        SetArithmeticFlags(result, carry, overflow);
    }

    void ExecuteThumbImmediateAlu(ushort instruction)
    {
        int operation = (int)Bits(instruction, 11, 2);
        int destination = (int)Bits(instruction, 8, 3);
        uint immediate = Bits(instruction, 0, 8);
        uint left = ReadRegister(destination);
        uint result;

        switch (operation)
        {
            case 0:
                result = immediate;
                WriteRegister(destination, result);
                SetNegativeAndZero(result);
                return;
            case 1:
                result = AddWithCarry(left, ~immediate, true, out bool carry, out bool overflow);
                SetArithmeticFlags(result, carry, overflow);
                return;
            case 2:
                result = AddWithCarry(left, immediate, false, out carry, out overflow);
                WriteRegister(destination, result);
                SetArithmeticFlags(result, carry, overflow);
                return;
            default:
                result = AddWithCarry(left, ~immediate, true, out carry, out overflow);
                WriteRegister(destination, result);
                SetArithmeticFlags(result, carry, overflow);
                return;
        }
    }

    void ExecuteThumbAlu(ushort instruction)
    {
        int operation = (int)Bits(instruction, 6, 4);
        int source = (int)Bits(instruction, 3, 3);
        int destination = (int)Bits(instruction, 0, 3);
        uint left = ReadRegister(destination);
        uint right = ReadRegister(source);
        uint result;

        switch (operation)
        {
            case 0:
                result = left & right;
                WriteLogicalResult(destination, result);
                return;
            case 1:
                result = left ^ right;
                WriteLogicalResult(destination, result);
                return;
            case 2:
            case 3:
            case 4:
            case 7:
                Idle();
                int shiftType = operation == 7 ? 3 : operation - 2;
                result = Shift(left, shiftType, right & 0xffu, registerSpecified: true, out bool? shiftCarry);
                WriteRegister(destination, result);
                SetNegativeAndZero(result);
                if (shiftCarry.HasValue)
                {
                    SetFlag(FlagC, shiftCarry.Value);
                }
                return;
            case 5:
                result = AddWithCarry(left, right, Flag(FlagC), out bool carry, out bool overflow);
                WriteRegister(destination, result);
                SetArithmeticFlags(result, carry, overflow);
                return;
            case 6:
                result = AddWithCarry(left, ~right, Flag(FlagC), out carry, out overflow);
                WriteRegister(destination, result);
                SetArithmeticFlags(result, carry, overflow);
                return;
            case 8:
                SetNegativeAndZero(left & right);
                return;
            case 9:
                result = AddWithCarry(0, ~right, true, out carry, out overflow);
                WriteRegister(destination, result);
                SetArithmeticFlags(result, carry, overflow);
                return;
            case 10:
                result = AddWithCarry(left, ~right, true, out carry, out overflow);
                SetArithmeticFlags(result, carry, overflow);
                return;
            case 11:
                result = AddWithCarry(left, right, false, out carry, out overflow);
                SetArithmeticFlags(result, carry, overflow);
                return;
            case 12:
                WriteLogicalResult(destination, left | right);
                return;
            case 13:
                IdleMultiply(left, signed: true, longMultiply: false);
                MultiplyResult multiplication = BoothMultiply(
                    MultiplyFlavor.Short,
                    right,
                    left,
                    0);
                result = (uint)multiplication.Value;
                WriteRegister(destination, result);
                SetNegativeAndZero(result);
                SetFlag(FlagC, multiplication.Carry);
                return;
            case 14:
                WriteLogicalResult(destination, left & ~right);
                return;
            default:
                WriteLogicalResult(destination, ~right);
                return;
        }
    }

    void ExecuteThumbHighRegister(ushort instruction)
    {
        int operation = (int)Bits(instruction, 8, 2);
        int source = (int)Bits(instruction, 3, 3) + (int)(Bits(instruction, 6, 1) << 3);
        int destination = (int)Bits(instruction, 0, 3) + (int)(Bits(instruction, 7, 1) << 3);

        if (operation == 3)
        {
            uint target = ReadRegister(source);
            IsThumb = (target & 1u) != 0;
            BranchTo(IsThumb ? target & ~1u : target);
            return;
        }

        uint right = ReadRegister(source);
        if (operation == 0)
        {
            WriteRegister(destination, unchecked(ReadRegister(destination) + right));
        }
        else if (operation == 1)
        {
            uint result = AddWithCarry(
                ReadRegister(destination),
                ~right,
                true,
                out bool carry,
                out bool overflow);
            SetArithmeticFlags(result, carry, overflow);
        }
        else
        {
            WriteRegister(destination, right);
        }
    }

    void ExecuteThumbPcRelativeLoad(ushort instruction)
    {
        int destination = (int)Bits(instruction, 8, 3);
        uint address = (_registers[ProgramCounter] & ~3u) + Bits(instruction, 0, 8) * 4u;
        BreakSequentialFetch();
        uint value = ReadWord(address);
        Idle();
        WriteRegister(destination, value);
    }

    void ExecuteThumbRegisterTransfer(ushort instruction)
    {
        bool load = Bits(instruction, 11, 1) != 0;
        bool byteTransfer = Bits(instruction, 10, 1) != 0;
        int offsetRegister = (int)Bits(instruction, 6, 3);
        int baseRegister = (int)Bits(instruction, 3, 3);
        int destination = (int)Bits(instruction, 0, 3);
        uint address = ReadRegister(baseRegister) + ReadRegister(offsetRegister);
        BreakSequentialFetch();

        if (load)
        {
            uint value = byteTransfer ? ReadByte(address) : ReadRotatedWord(address);
            Idle();
            WriteRegister(destination, value);
        }
        else if (byteTransfer)
        {
            WriteByte(address, (byte)ReadRegister(destination));
        }
        else
        {
            WriteWord(address, ReadRegister(destination));
        }
    }

    void ExecuteThumbSignedTransfer(ushort instruction)
    {
        int operation = (int)Bits(instruction, 10, 2);
        int offsetRegister = (int)Bits(instruction, 6, 3);
        int baseRegister = (int)Bits(instruction, 3, 3);
        int destination = (int)Bits(instruction, 0, 3);
        uint address = ReadRegister(baseRegister) + ReadRegister(offsetRegister);
        BreakSequentialFetch();

        switch (operation)
        {
            case 0:
                WriteHalf(address, (ushort)ReadRegister(destination));
                return;
            case 1:
            {
                uint value = ReadByte(address);
                if ((value & 0x80u) != 0)
                {
                    value |= 0xffffff00u;
                }
                Idle();
                WriteRegister(destination, value);
                return;
            }
            case 2:
            {
                uint value = ReadRotatedHalf(address);
                Idle();
                WriteRegister(destination, value);
                return;
            }
            default:
            {
                uint raw = ReadHalf(address);
                uint value;
                if ((address & 1u) != 0)
                {
                    value = (raw >> 8) & 0xffu;
                    if ((value & 0x80u) != 0)
                    {
                        value |= 0xffffff00u;
                    }
                }
                else
                {
                    value = raw;
                    if ((value & 0x8000u) != 0)
                    {
                        value |= 0xffff0000u;
                    }
                }
                Idle();
                WriteRegister(destination, value);
                return;
            }
        }
    }

    void ExecuteThumbImmediateTransfer(ushort instruction)
    {
        bool byteTransfer = Bits(instruction, 12, 1) != 0;
        bool load = Bits(instruction, 11, 1) != 0;
        uint offset = Bits(instruction, 6, 5);
        int baseRegister = (int)Bits(instruction, 3, 3);
        int destination = (int)Bits(instruction, 0, 3);
        uint address = ReadRegister(baseRegister) + (byteTransfer ? offset : offset * 4u);
        BreakSequentialFetch();

        if (load)
        {
            uint value = byteTransfer ? ReadByte(address) : ReadRotatedWord(address);
            Idle();
            WriteRegister(destination, value);
        }
        else if (byteTransfer)
        {
            WriteByte(address, (byte)ReadRegister(destination));
        }
        else
        {
            WriteWord(address, ReadRegister(destination));
        }
    }

    void ExecuteThumbImmediateHalfwordTransfer(ushort instruction)
    {
        bool load = Bits(instruction, 11, 1) != 0;
        uint address = ReadRegister((int)Bits(instruction, 3, 3)) + Bits(instruction, 6, 5) * 2u;
        int destination = (int)Bits(instruction, 0, 3);
        BreakSequentialFetch();
        if (load)
        {
            uint value = ReadRotatedHalf(address);
            Idle();
            WriteRegister(destination, value);
        }
        else
        {
            WriteHalf(address, (ushort)ReadRegister(destination));
        }
    }

    void ExecuteThumbSpRelativeTransfer(ushort instruction)
    {
        bool load = Bits(instruction, 11, 1) != 0;
        int destination = (int)Bits(instruction, 8, 3);
        uint address = ReadRegister(13) + Bits(instruction, 0, 8) * 4u;
        BreakSequentialFetch();
        if (load)
        {
            uint value = ReadRotatedWord(address);
            Idle();
            WriteRegister(destination, value);
        }
        else
        {
            WriteWord(address, ReadRegister(destination));
        }
    }

    void ExecuteThumbLoadAddress(ushort instruction)
    {
        bool fromStack = Bits(instruction, 11, 1) != 0;
        int destination = (int)Bits(instruction, 8, 3);
        uint value = fromStack ? ReadRegister(13) : _registers[ProgramCounter] & ~3u;
        WriteRegister(destination, value + Bits(instruction, 0, 8) * 4u);
    }

    void ExecuteThumbAddSpOffset(ushort instruction)
    {
        uint offset = Bits(instruction, 0, 7) * 4u;
        _registers[RegisterIndex(13)] = Bits(instruction, 7, 1) != 0
            ? unchecked(ReadRegister(13) - offset)
            : unchecked(ReadRegister(13) + offset);
    }

    void ExecuteThumbPushPop(ushort instruction)
    {
        bool pop = Bits(instruction, 11, 1) != 0;
        bool includeHighRegister = Bits(instruction, 8, 1) != 0;
        uint registerList = Bits(instruction, 0, 8);
        if (includeHighRegister)
        {
            registerList |= pop ? 1u << ProgramCounter : 1u << 14;
        }
        uint armInstruction = 0xe8000000u |
            (pop ? 1u << 23 : 1u << 24) |
            1u << 21 |
            (pop ? 1u << 20 : 0u) |
            13u << 16 |
            registerList;
        ExecuteArmBlockTransfer(armInstruction);
    }

    void ExecuteThumbMultipleTransfer(ushort instruction)
    {
        bool load = Bits(instruction, 11, 1) != 0;
        uint armInstruction = 0xe8000000u |
            1u << 23 |
            1u << 21 |
            (load ? 1u << 20 : 0u) |
            Bits(instruction, 8, 3) << 16 |
            Bits(instruction, 0, 8);
        ExecuteArmBlockTransfer(armInstruction);
    }

    void ExecuteThumbConditionalBranch(ushort instruction)
    {
        uint condition = Bits(instruction, 8, 4);
        if (CheckCondition(condition))
        {
            int offset = SignExtend(Bits(instruction, 0, 8), 8) << 1;
            BranchTo(unchecked(_registers[ProgramCounter] + (uint)offset));
        }
    }

    void ExecuteThumbBranch(ushort instruction)
    {
        int offset = SignExtend(Bits(instruction, 0, 11), 11) << 1;
        BranchTo(unchecked(_registers[ProgramCounter] + (uint)offset));
    }

    void ExecuteThumbLongBranch(ushort instruction)
    {
        bool secondHalf = Bits(instruction, 11, 1) != 0;
        if (!secondHalf)
        {
            int offset = SignExtend(Bits(instruction, 0, 11), 11) << 12;
            WriteRegister(14, unchecked(_registers[ProgramCounter] + (uint)offset));
            return;
        }

        uint target = ReadRegister(14) + (Bits(instruction, 0, 11) << 1);
        uint returnAddress = (_registers[ProgramCounter] - 2u) | 1u;
        bool remainThumb = Bits(instruction, 12, 1) != 0;
        IsThumb = remainThumb;
        WriteRegister(14, returnAddress);
        BranchTo(remainThumb ? target & ~1u : target);
    }

    void ExecuteThumbUndefined(ushort instruction)
    {
        ObserveUndefined(instruction);
        EnterException(ModeUnd, 0x04, ArmBank.Und);
    }

    void WriteLogicalResult(int destination, uint result)
    {
        WriteRegister(destination, result);
        SetNegativeAndZero(result);
    }

    void SetArithmeticFlags(uint result, bool carry, bool overflow)
    {
        SetNegativeAndZero(result);
        SetFlag(FlagC, carry);
        SetFlag(FlagV, overflow);
    }
}
