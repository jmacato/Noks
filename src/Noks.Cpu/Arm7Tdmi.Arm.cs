// SPDX-License-Identifier: MIT
//
// C# port of SkyEmu src/arm7.h at revision
// 01516d6798e3652b583e6a366085bb51c43b528d.
// Copyright (c) 2021 Skyler "Sky" Saleh.

namespace Noks.Cpu;

public sealed partial class Arm7Tdmi
{
    void ExecuteArmSwap(uint instruction)
    {
        bool byteTransfer = Bits(instruction, 22, 1) != 0;
        int rn = (int)Bits(instruction, 16, 4);
        int rd = (int)Bits(instruction, 12, 4);
        int rm = (int)Bits(instruction, 0, 4);
        uint address = ReadRegister(rn) + (rn == ProgramCounter ? 4u : 0u);

        BreakSequentialFetch();
        uint value;
        if (byteTransfer)
        {
            value = ReadByte(address);
            WriteByte(address, (byte)ReadStoreRegister(rm), ArmAccess.Nonsequential | ArmAccess.Lock);
        }
        else
        {
            value = ReadRotatedWord(address);
            WriteWord(address, ReadStoreRegister(rm), ArmAccess.Nonsequential | ArmAccess.Lock);
        }
        Idle();
        WriteRegister(rd, value);
    }

    void ExecuteArmBranchExchange(uint instruction)
    {
        uint target = ReadRegister((int)Bits(instruction, 0, 4));
        IsThumb = (target & 1u) != 0;
        BranchTo(IsThumb ? target & ~1u : target);
    }

    void ExecuteArmHalfwordTransfer(uint instruction)
    {
        bool preIndex = Bits(instruction, 24, 1) != 0;
        bool addOffset = Bits(instruction, 23, 1) != 0;
        bool immediate = Bits(instruction, 22, 1) != 0;
        bool writeBack = Bits(instruction, 21, 1) != 0;
        bool load = Bits(instruction, 20, 1) != 0;
        int rn = (int)Bits(instruction, 16, 4);
        int rd = (int)Bits(instruction, 12, 4);
        bool signed = Bits(instruction, 6, 1) != 0;
        bool halfword = Bits(instruction, 5, 1) != 0;

        uint offset = immediate
            ? (Bits(instruction, 8, 4) << 4) | Bits(instruction, 0, 4)
            : ReadRegister((int)Bits(instruction, 0, 4));
        uint baseAddress = ReadRegister(rn);
        uint indexedAddress = addOffset
            ? unchecked(baseAddress + offset)
            : unchecked(baseAddress - offset);
        uint address = preIndex ? indexedAddress : baseAddress;
        uint finalAddress = preIndex ? indexedAddress : indexedAddress;
        if (!preIndex)
        {
            writeBack = true;
        }

        BreakSequentialFetch();
        if (!load)
        {
            uint value = ReadStoreRegister(rd);
            if (halfword)
            {
                WriteHalf(address, (ushort)value);
            }
            else
            {
                WriteByte(address, (byte)value);
            }
        }

        if (writeBack)
        {
            WriteRegister(rn, finalAddress + (rn == ProgramCounter ? 4u : 0u));
        }

        if (!load)
        {
            return;
        }

        uint data;
        if (halfword)
        {
            uint raw = ReadHalf(address);
            if ((address & 1u) != 0)
            {
                data = RotateRight(raw, 8);
                if (signed)
                {
                    data &= 0xffu;
                    if ((data & 0x80u) != 0)
                    {
                        data |= 0xffffff00u;
                    }
                }
            }
            else
            {
                data = raw;
                if (signed && (data & 0x8000u) != 0)
                {
                    data |= 0xffff0000u;
                }
            }
        }
        else
        {
            data = ReadByte(address);
            if (signed && (data & 0x80u) != 0)
            {
                data |= 0xffffff00u;
            }
        }
        Idle();
        WriteRegister(rd, data);
    }

    void ExecuteArmSingleTransfer(uint instruction)
    {
        bool registerOffset = Bits(instruction, 25, 1) != 0;
        bool preIndex = Bits(instruction, 24, 1) != 0;
        bool addOffset = Bits(instruction, 23, 1) != 0;
        bool byteTransfer = Bits(instruction, 22, 1) != 0;
        bool writeBack = Bits(instruction, 21, 1) != 0;
        bool load = Bits(instruction, 20, 1) != 0;
        int rn = (int)Bits(instruction, 16, 4);
        int rd = (int)Bits(instruction, 12, 4);

        uint offset;
        if (registerOffset)
        {
            uint value = ReadRegister((int)Bits(instruction, 0, 4));
            offset = Shift(
                value,
                (int)Bits(instruction, 5, 2),
                Bits(instruction, 7, 5),
                registerSpecified: false,
                out _);
        }
        else
        {
            offset = Bits(instruction, 0, 12);
        }

        uint baseAddress = ReadRegister(rn);
        uint indexedAddress = addOffset
            ? unchecked(baseAddress + offset)
            : unchecked(baseAddress - offset);
        uint address = preIndex ? indexedAddress : baseAddress;
        uint finalAddress = indexedAddress;
        if (!preIndex)
        {
            writeBack = true;
        }

        BreakSequentialFetch();
        if (!load)
        {
            uint value = ReadStoreRegister(rd);
            if (byteTransfer)
            {
                WriteByte(address, (byte)value);
            }
            else
            {
                WriteWord(address, value);
            }
        }

        if (writeBack)
        {
            WriteRegister(rn, finalAddress + (rn == ProgramCounter ? 4u : 0u));
        }

        if (!load)
        {
            return;
        }

        uint data = byteTransfer ? ReadByte(address) : ReadRotatedWord(address);
        Idle();
        WriteRegister(rd, data);
    }

    void ExecuteArmBlockTransfer(uint instruction)
    {
        bool preIndex = Bits(instruction, 24, 1) != 0;
        bool increment = Bits(instruction, 23, 1) != 0;
        bool userOrRestore = Bits(instruction, 22, 1) != 0;
        bool writeBack = Bits(instruction, 21, 1) != 0;
        bool load = Bits(instruction, 20, 1) != 0;
        int rn = (int)Bits(instruction, 16, 4);
        uint registerList = Bits(instruction, 0, 16);

        bool emptyRegisterList = registerList == 0;
        int transferCount = System.Numerics.BitOperations.PopCount(registerList);
        if (emptyRegisterList)
        {
            registerList = 1u << ProgramCounter;
            transferCount = 16;
        }

        uint baseAddress = ReadRegister(rn);
        uint byteCount = (uint)transferCount * 4u;
        uint finalAddress = increment
            ? unchecked(baseAddress + byteCount)
            : unchecked(baseAddress - byteCount);
        uint address;
        if (increment)
        {
            address = preIndex ? baseAddress + 4u : baseAddress;
        }
        else
        {
            address = preIndex ? baseAddress - byteCount : baseAddress - byteCount + 4u;
        }

        bool pcInList = (registerList & (1u << ProgramCounter)) != 0;
        bool useUserBank = userOrRestore && (!load || !pcInList);
        ArmAccess access = ArmAccess.Nonsequential;
        bool firstTransfer = true;
        BreakSequentialFetch();

        for (int register = 0; register < 16; register++)
        {
            if ((registerList & (1u << register)) == 0)
            {
                continue;
            }

            uint alignedAddress = address;
            if (!load)
            {
                uint value = useUserBank
                    ? ReadUserRegister(register)
                    : ReadRegister(register);
                if (register == ProgramCounter)
                {
                    value += IsThumb
                        ? 2u
                        : writeBack && rn == ProgramCounter ? 0u : 4u;
                }
                WriteWord(alignedAddress, value, access);
            }

            if (firstTransfer && writeBack)
            {
                if (useUserBank)
                {
                    WriteUserRegister(rn, finalAddress);
                }
                else
                {
                    WriteRegister(rn, finalAddress);
                }
            }

            if (load)
            {
                uint value = ReadWord(alignedAddress, access);
                if (useUserBank)
                {
                    WriteUserRegister(register, value);
                }
                else
                {
                    if (emptyRegisterList && register == ProgramCounter)
                    {
                        BranchTo(value);
                    }
                    else
                    {
                        WriteRegister(register, value);
                    }
                }
            }

            firstTransfer = false;
            access = ArmAccess.Sequential;
            address += 4u;
        }

        if (load)
        {
            Idle();
            if (userOrRestore && pcInList)
            {
                _registers[Cpsr] = ReadCurrentSpsr();
                LatchInterruptDisable();
            }
        }
    }

    void ExecuteArmBranch(uint instruction)
    {
        if (Bits(instruction, 24, 1) != 0)
        {
            WriteRegister(14, _registers[ProgramCounter] - 4u);
        }
        int offset = SignExtend(Bits(instruction, 0, 24), 24) << 2;
        BranchTo(unchecked(_registers[ProgramCounter] + (uint)offset));
    }

    void ExecuteArmUndefined(uint instruction)
    {
        ObserveUndefined(instruction);
        EnterException(ModeUnd, 0x04, ArmBank.Und);
        Idle();
    }

    void ExecuteSoftwareInterrupt(uint instruction)
    {
        EnterException(ModeSvc, 0x08, ArmBank.Svc);
    }

    void ExecuteArmMrs(uint instruction)
    {
        bool spsr = Bits(instruction, 22, 1) != 0;
        int rd = (int)Bits(instruction, 12, 4);
        // MRS with Rd=r15 is architecturally unpredictable. ARM7TDMI writes
        // the register file entry without taking the normal PC-write path.
        _registers[RegisterIndex(rd)] = spsr ? ReadCurrentSpsr() : _registers[Cpsr];
    }

    void ExecuteArmMsr(uint instruction)
    {
        bool spsr = Bits(instruction, 22, 1) != 0;
        bool immediate = Bits(instruction, 25, 1) != 0;
        uint mode = _registers[Cpsr] & 0x1fu;
        if (spsr && mode is ModeUsr or ModeSys)
        {
            return;
        }

        uint mask = 0;
        if (Bits(instruction, 19, 1) != 0) mask |= 0xff000000u;
        if (Bits(instruction, 18, 1) != 0) mask |= 0x00ff0000u;
        if (Bits(instruction, 17, 1) != 0) mask |= 0x0000ff00u;
        if (Bits(instruction, 16, 1) != 0) mask |= 0x000000ffu;
        if (!spsr && mode == ModeUsr)
        {
            mask &= 0xff000000u;
        }

        uint value;
        if (immediate)
        {
            value = RotateRight(
                Bits(instruction, 0, 8),
                (int)Bits(instruction, 8, 4) * 2);
        }
        else
        {
            value = ReadRegister((int)Bits(instruction, 0, 4));
        }

        if (spsr)
        {
            uint old = ReadCurrentSpsr();
            WriteCurrentSpsr((old & ~mask) | (value & mask));
        }
        else
        {
            _registers[Cpsr] = ((_registers[Cpsr] & ~mask) | (value & mask)) | ModeUsr;
            LatchInterruptDisable();
        }
    }

    void EnterException(uint mode, uint vector, ArmBank bank)
    {
        bool thumb = IsThumb;
        uint instructionAddress = _registers[ProgramCounter] - (thumb ? 4u : 8u);
        _registers[SpsrIndex(bank)] = _registers[Cpsr];
        _registers[BankedRegisterIndex(bank, 6)] = instructionAddress + (thumb ? 2u : 4u);
        _registers[Cpsr] = (_registers[Cpsr] & ~0x1fu & ~ThumbBit) | mode | IrqDisableBit;
        LatchInterruptDisable();
        BranchTo(vector);
    }
}
