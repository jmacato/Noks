// SPDX-License-Identifier: MIT
//
// C# port of SkyEmu src/arm7.h at revision
// 01516d6798e3652b583e6a366085bb51c43b528d.
// Copyright (c) 2021 Skyler "Sky" Saleh.

namespace Noks.Cpu;

public sealed partial class Arm7Tdmi
{
    public const uint ModeUsr = 0x10;
    public const uint ModeFiq = 0x11;
    public const uint ModeIrq = 0x12;
    public const uint ModeSvc = 0x13;
    public const uint ModeAbt = 0x17;
    public const uint ModeUnd = 0x1b;
    public const uint ModeSys = 0x1f;

    internal const int ProgramCounter = 15;
    internal const int Cpsr = 16;
    internal const int R8Fiq = 17;
    internal const int R13Fiq = 22;
    internal const int R13Irq = 24;
    internal const int R13Svc = 26;
    internal const int R13Abt = 28;
    internal const int R13Und = 30;
    internal const int SpsrFiq = 32;
    internal const int SpsrIrq = 33;
    internal const int SpsrSvc = 34;
    internal const int SpsrAbt = 35;
    internal const int SpsrUnd = 36;

    internal const uint FlagN = 1u << 31;
    internal const uint FlagZ = 1u << 30;
    internal const uint FlagC = 1u << 29;
    internal const uint FlagV = 1u << 28;
    internal const uint IrqDisableBit = 1u << 7;
    internal const uint FiqDisableBit = 1u << 6;
    internal const uint ThumbBit = 1u << 5;

    readonly IArm7Bus _bus;
    readonly uint[] _registers = new uint[37];
    readonly uint[] _pipeline = new uint[2];
    // Each bank uses seven slots to store a snapshot of its state.
    // The FIQ bank maps registers r8 to r12. The other privileged banks map only r13 and r14.
    // Noks keeps the remaining slots so it can inspect them. Data in these slots does not change the CPU state.
    readonly uint[,] _unmappedBankedRegisters = new uint[6, 7];
    uint _noSpsr;

    ArmAccess _pipelineAccess;
    bool _reloadPipeline;
    bool _latchedIrqDisable;
    bool _latchedFiqDisable;

    public Arm7Tdmi(IArm7Bus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        Reset();
    }

    public bool IrqLine { get; set; }

    public bool FiqLine { get; set; }

    // Noks exposes this property for the DCT3 idle-loop accelerator.
    // The ARM7 hardware samples these controls through its pipeline, not directly from the CPSR.
    public bool IrqAcceptanceEnabled => !_latchedIrqDisable;

    public bool FiqAcceptanceEnabled => !_latchedFiqDisable;

    public uint CpsrValue => _registers[Cpsr];

    public int UndefinedInstructionCount { get; private set; }

    public uint LastUndefinedInstructionAddress { get; private set; }

    public uint LastUndefinedInstruction { get; private set; }

    public event Action<Arm7Tdmi>? UndefinedInstructionObserved;

    public ArmAccess PipelineAccess => _pipelineAccess;

    public void Reset()
    {
        Array.Clear(_registers);
        Array.Clear(_unmappedBankedRegisters);
        _noSpsr = 0;
        _registers[Cpsr] = ModeSvc | IrqDisableBit | FiqDisableBit;
        _pipeline[0] = 0xf0000000;
        _pipeline[1] = 0xf0000000;
        _pipelineAccess = ArmAccess.Code | ArmAccess.Nonsequential;
        _reloadPipeline = false;
        _latchedIrqDisable = true;
        _latchedFiqDisable = true;
        IrqLine = false;
        FiqLine = false;
        UndefinedInstructionCount = 0;
        LastUndefinedInstructionAddress = 0;
        LastUndefinedInstruction = 0;
    }

    public void Step()
    {
        if (FiqLine && !_latchedFiqDisable)
        {
            EnterInterrupt(ModeFiq, 0x1c, FiqDisableBit | IrqDisableBit, ArmBank.Fiq);
        }
        else if (IrqLine && !_latchedIrqDisable)
        {
            EnterInterrupt(ModeIrq, 0x18, IrqDisableBit, ArmBank.Irq);
        }

        // Noks exposes the sampled I and F masks to its DCT3 scheduler.
        // It samples them at this instruction boundary, after interrupt recognition.
        LatchInterruptDisable();

        bool thumb = IsThumb;
        uint instruction = _pipeline[0];
        _reloadPipeline = false;

        _pipeline[0] = _pipeline[1];
        _pipeline[1] = thumb
            ? _bus.ReadHalf(_registers[ProgramCounter] & ~1u, _pipelineAccess)
            : _bus.ReadWord(_registers[ProgramCounter] & ~3u, _pipelineAccess);
        _pipelineAccess = ArmAccess.Code | ArmAccess.Sequential;

        if (thumb)
        {
            ExecuteThumb((ushort)instruction);
        }
        else if (CheckCondition(instruction >> 28))
        {
            ExecuteArm(instruction);
        }

        if (_reloadPipeline)
        {
            ReloadPipeline();
        }
        else
        {
            _registers[ProgramCounter] += thumb ? 2u : 4u;
        }

    }

    public uint GetGpr(int index)
    {
        ValidateGpr(index);
        return ReadRegister(index);
    }

    public void SetGpr(int index, uint value)
    {
        ValidateGpr(index);
        _registers[RegisterIndex(index)] = value;
    }

    public uint GetBanked(ArmBank whichBank, int index)
    {
        return TryBankedRegisterIndex(whichBank, index, out int register)
            ? _registers[register]
            : _unmappedBankedRegisters[(int)whichBank, index];
    }

    public void SetBanked(ArmBank whichBank, int index, uint value)
    {
        if (TryBankedRegisterIndex(whichBank, index, out int register))
        {
            _registers[register] = value;
        }
        else
        {
            _unmappedBankedRegisters[(int)whichBank, index] = value;
        }
    }

    public uint GetSpsrRaw(ArmBank whichBank) =>
        whichBank == ArmBank.None ? _noSpsr : _registers[SpsrIndex(whichBank)];

    public void SetSpsrRaw(ArmBank whichBank, uint value) =>
        SetSpsrRawCore(whichBank, value);

    public uint GetPipelineOpcode(int slot)
    {
        if ((uint)slot >= _pipeline.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }
        return _pipeline[slot];
    }

    public void PrimePipeline(uint opcode0, uint opcode1, ArmAccess access)
    {
        _pipeline[0] = opcode0;
        _pipeline[1] = opcode1;
        _pipelineAccess = access;
        _reloadPipeline = false;
    }

    public void ForceStatus(uint value)
    {
        _registers[Cpsr] = value;
    }

    public static ArmBank GetBankByMode(uint mode) => mode switch
    {
        ModeFiq => ArmBank.Fiq,
        ModeIrq => ArmBank.Irq,
        ModeSvc => ArmBank.Svc,
        ModeAbt => ArmBank.Abt,
        ModeUnd => ArmBank.Und,
        _ => ArmBank.None,
    };

    internal bool IsThumb
    {
        get => (_registers[Cpsr] & ThumbBit) != 0;
        set
        {
            if (value)
            {
                _registers[Cpsr] |= ThumbBit;
            }
            else
            {
                _registers[Cpsr] &= ~ThumbBit;
            }
        }
    }

    internal uint ReadRegister(int register) => _registers[RegisterIndex(register)];

    internal uint ReadStoreRegister(int register) =>
        ReadRegister(register) + (register == ProgramCounter && !IsThumb ? 4u : 0u);

    internal uint ReadUserRegister(int register) => _registers[register];

    internal void WriteRegister(int register, uint value)
    {
        if (register == ProgramCounter)
        {
            BranchTo(IsThumb ? value & ~1u : value);
            return;
        }
        _registers[RegisterIndex(register)] = value;
    }

    internal void WriteUserRegister(int register, uint value)
    {
        if (register == ProgramCounter)
        {
            BranchTo(IsThumb ? value & ~1u : value);
            return;
        }
        _registers[register] = value;
    }

    internal uint ReadCurrentSpsr()
    {
        int index = CurrentSpsrIndex;
        return index == Cpsr ? _registers[Cpsr] | ModeUsr : _registers[index];
    }

    internal void WriteCurrentSpsr(uint value)
    {
        int index = CurrentSpsrIndex;
        if (index != Cpsr)
        {
            _registers[index] = value;
        }
    }

    internal void BranchTo(uint address)
    {
        _registers[ProgramCounter] = address;
        _reloadPipeline = true;
    }

    internal void BreakSequentialFetch() =>
        _pipelineAccess = ArmAccess.Code | ArmAccess.Nonsequential;

    internal void Idle()
    {
        _bus.Idle();
        BreakSequentialFetch();
    }

    internal uint ReadWord(uint address, ArmAccess access = ArmAccess.Nonsequential) =>
        _bus.ReadWord(address, access);

    internal uint ReadHalf(uint address, ArmAccess access = ArmAccess.Nonsequential) =>
        _bus.ReadHalf(address, access);

    internal uint ReadByte(uint address, ArmAccess access = ArmAccess.Nonsequential) =>
        _bus.ReadByte(address, access);

    internal void WriteWord(
        uint address,
        uint value,
        ArmAccess access = ArmAccess.Nonsequential) =>
        _bus.WriteWord(address, value, access);

    internal void WriteHalf(
        uint address,
        ushort value,
        ArmAccess access = ArmAccess.Nonsequential) =>
        _bus.WriteHalf(address, value, access);

    internal void WriteByte(
        uint address,
        byte value,
        ArmAccess access = ArmAccess.Nonsequential) =>
        _bus.WriteByte(address, value, access);

    internal uint ReadRotatedWord(uint address, ArmAccess access = ArmAccess.Nonsequential)
    {
        uint value = ReadWord(address, access);
        return RotateRight(value, (int)(address & 3u) * 8);
    }

    internal uint ReadRotatedHalf(uint address, ArmAccess access = ArmAccess.Nonsequential)
    {
        uint value = ReadHalf(address, access);
        return (address & 1u) == 0 ? value : RotateRight(value, 8);
    }

    internal static uint RotateRight(uint value, int amount) =>
        amount == 0 ? value : (value >> amount) | (value << (32 - amount));

    internal static uint Bits(uint value, int offset, int size) =>
        size == 32 ? value : (value >> offset) & ((1u << size) - 1u);

    internal static int SignExtend(uint value, int bits) =>
        (int)(value << (32 - bits)) >> (32 - bits);

    internal bool Flag(uint mask) => (_registers[Cpsr] & mask) != 0;

    internal void SetFlag(uint mask, bool value)
    {
        if (value)
        {
            _registers[Cpsr] |= mask;
        }
        else
        {
            _registers[Cpsr] &= ~mask;
        }
    }

    internal void SetNegativeAndZero(uint value)
    {
        SetFlag(FlagN, (value & 0x80000000u) != 0);
        SetFlag(FlagZ, value == 0);
    }

    internal void ObserveUndefined(uint instruction)
    {
        UndefinedInstructionCount++;
        LastUndefinedInstructionAddress = IsThumb
            ? _registers[ProgramCounter] - 4u
            : _registers[ProgramCounter] - 8u;
        LastUndefinedInstruction = instruction;
        UndefinedInstructionObserved?.Invoke(this);
    }

    void EnterInterrupt(uint mode, uint vector, uint disableMask, ArmBank bank)
    {
        bool thumb = IsThumb;
        if (thumb)
        {
            _bus.ReadHalf(_registers[ProgramCounter] & ~1u, _pipelineAccess);
        }
        else
        {
            _bus.ReadWord(_registers[ProgramCounter] & ~3u, _pipelineAccess);
        }

        _registers[SpsrIndex(bank)] = _registers[Cpsr];
        _registers[BankedRegisterIndex(bank, 6)] = thumb
            ? _registers[ProgramCounter]
            : _registers[ProgramCounter] - 4u;
        _registers[Cpsr] = (_registers[Cpsr] & ~0x1fu & ~ThumbBit) | mode | disableMask;
        LatchInterruptDisable();
        _registers[ProgramCounter] = vector;
        ReloadPipeline();
    }

    internal void LatchInterruptDisable()
    {
        _latchedIrqDisable = (_registers[Cpsr] & IrqDisableBit) != 0;
        _latchedFiqDisable = (_registers[Cpsr] & FiqDisableBit) != 0;
    }

    void ReloadPipeline()
    {
        uint branchAddress = _registers[ProgramCounter];
        if (IsThumb)
        {
            uint address = branchAddress & ~1u;
            _pipeline[0] = _bus.ReadHalf(
                address,
                ArmAccess.Code | ArmAccess.Nonsequential);
            _pipeline[1] = _bus.ReadHalf(
                address + 2u,
                ArmAccess.Code | ArmAccess.Sequential);
            _registers[ProgramCounter] = branchAddress + 4u;
        }
        else
        {
            uint address = branchAddress & ~3u;
            _pipeline[0] = _bus.ReadWord(
                address,
                ArmAccess.Code | ArmAccess.Nonsequential);
            _pipeline[1] = _bus.ReadWord(
                address + 4u,
                ArmAccess.Code | ArmAccess.Sequential);
            // The ARM7TDMI aligns instruction fetches.
            // It keeps bit 1 in the architectural PC after an interworking branch or a PC load.
            _registers[ProgramCounter] = branchAddress + 8u;
        }
        _pipelineAccess = ArmAccess.Code | ArmAccess.Sequential;
        _reloadPipeline = false;
    }

    bool CheckCondition(uint condition)
    {
        if (condition is 0xe or 0xf)
        {
            return true;
        }

        bool n = Flag(FlagN);
        bool z = Flag(FlagZ);
        bool c = Flag(FlagC);
        bool v = Flag(FlagV);
        return condition switch
        {
            0x0 => z,
            0x1 => !z,
            0x2 => c,
            0x3 => !c,
            0x4 => n,
            0x5 => !n,
            0x6 => v,
            0x7 => !v,
            0x8 => c && !z,
            0x9 => !c || z,
            0xa => n == v,
            0xb => n != v,
            0xc => !z && n == v,
            0xd => z || n != v,
            _ => false,
        };
    }

    int RegisterIndex(int register)
    {
        if (register < 8 || register is ProgramCounter or Cpsr)
        {
            return register;
        }

        uint mode = _registers[Cpsr] & 0x1f;
        if (register is >= 8 and <= 12 && mode == ModeFiq)
        {
            return R8Fiq + register - 8;
        }
        if (register <= 12)
        {
            return register;
        }
        if (register is 13 or 14)
        {
            return mode switch
            {
                ModeFiq => R13Fiq + register - 13,
                ModeIrq => R13Irq + register - 13,
                ModeSvc => R13Svc + register - 13,
                ModeAbt => R13Abt + register - 13,
                ModeUnd => R13Und + register - 13,
                _ => register,
            };
        }
        throw new ArgumentOutOfRangeException(nameof(register));
    }

    int CurrentSpsrIndex => (_registers[Cpsr] & 0x1f) switch
    {
        ModeFiq => SpsrFiq,
        ModeIrq => SpsrIrq,
        ModeSvc => SpsrSvc,
        ModeAbt => SpsrAbt,
        ModeUnd => SpsrUnd,
        _ => Cpsr,
    };

    static bool TryBankedRegisterIndex(ArmBank bank, int index, out int register)
    {
        if ((uint)index > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        switch (bank)
        {
            case ArmBank.None:
                register = 8 + index;
                return true;
            case ArmBank.Fiq:
                register = R8Fiq + index;
                return true;
            case ArmBank.Irq when index >= 5:
                register = R13Irq + index - 5;
                return true;
            case ArmBank.Svc when index >= 5:
                register = R13Svc + index - 5;
                return true;
            case ArmBank.Abt when index >= 5:
                register = R13Abt + index - 5;
                return true;
            case ArmBank.Und when index >= 5:
                register = R13Und + index - 5;
                return true;
            case ArmBank.Irq or ArmBank.Svc or ArmBank.Abt or ArmBank.Und:
                register = 0;
                return false;
            default:
                throw new ArgumentOutOfRangeException(nameof(bank));
        }
    }

    static int BankedRegisterIndex(ArmBank bank, int index)
    {
        if (TryBankedRegisterIndex(bank, index, out int register))
        {
            return register;
        }

        throw new ArgumentOutOfRangeException(nameof(index));
    }

    static int SpsrIndex(ArmBank bank) => bank switch
    {
        ArmBank.Fiq => SpsrFiq,
        ArmBank.Irq => SpsrIrq,
        ArmBank.Svc => SpsrSvc,
        ArmBank.Abt => SpsrAbt,
        ArmBank.Und => SpsrUnd,
        _ => throw new ArgumentOutOfRangeException(nameof(bank)),
    };

    void SetSpsrRawCore(ArmBank bank, uint value)
    {
        if (bank == ArmBank.None)
        {
            _noSpsr = value;
            return;
        }

        _registers[SpsrIndex(bank)] = value;
    }

    static void ValidateGpr(int index)
    {
        if ((uint)index >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}
