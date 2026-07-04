// SPDX-License-Identifier: MIT
//
// C# port of SkyEmu src/arm7.h at revision
// 01516d6798e3652b583e6a366085bb51c43b528d.
// Copyright (c) 2021 Skyler "Sky" Saleh.

namespace Noks.Cpu;

public sealed partial class Arm7Tdmi
{
    enum ArmOperation : byte
    {
        Undefined,
        DataProcessing,
        Multiply,
        MultiplyLong,
        Swap,
        BranchExchange,
        HalfwordTransfer,
        SingleTransfer,
        BlockTransfer,
        Branch,
        CoprocessorDataTransfer,
        CoprocessorDataOperation,
        CoprocessorRegisterTransfer,
        SoftwareInterrupt,
        Mrs,
        Msr,
    }

    enum ThumbOperation : byte
    {
        Undefined,
        MoveShiftedRegister,
        AddSubtract,
        ImmediateAlu,
        Alu,
        HighRegister,
        PcRelativeLoad,
        RegisterOffsetTransfer,
        SignedTransfer,
        ImmediateTransfer,
        ImmediateHalfwordTransfer,
        SpRelativeTransfer,
        LoadAddress,
        AddSpOffset,
        PushPop,
        MultipleTransfer,
        ConditionalBranch,
        SoftwareInterrupt,
        Branch,
        LongBranch,
    }

    readonly record struct ArmPattern(ArmOperation Operation, string Bits);

    readonly record struct ThumbPattern(ThumbOperation Operation, string Bits);

    static readonly int[] ArmKeyBits = [4, 5, 6, 7, 20, 21, 22, 23, 24, 25, 26, 27];
    static readonly int[] ThumbKeyBits = [8, 9, 10, 11, 12, 13, 14, 15];

    static readonly ArmPattern[] ArmPatterns =
    [
        new(ArmOperation.DataProcessing, "cccc0010oooSnnnnddddrrrrOOOOOOOO"),
        new(ArmOperation.DataProcessing, "cccc00111ooSnnnnddddrrrrOOOOOOOO"),
        new(ArmOperation.DataProcessing, "cccc00110oo1nnnnddddrrrrOOOOOOOO"),
        new(ArmOperation.DataProcessing, "cccc0000oooSnnnnddddsssssss0mmmm"),
        new(ArmOperation.DataProcessing, "cccc0000oooSnnnnddddssss0tt1mmmm"),
        new(ArmOperation.DataProcessing, "cccc00011ooSnnnnddddsssssss0mmmm"),
        new(ArmOperation.DataProcessing, "cccc00011ooSnnnnddddssss0tt1mmmm"),
        new(ArmOperation.DataProcessing, "cccc00010oo1nnnnddddssss0tt1mmmm"),
        new(ArmOperation.DataProcessing, "cccc00010oo1nnnnddddsssssss0mmmm"),
        new(ArmOperation.Multiply, "cccc000000ASddddnnnnssss1001mmmm"),
        new(ArmOperation.MultiplyLong, "cccc00001UASddddnnnnssss1001mmmm"),
        new(ArmOperation.Swap, "cccc00010B00nnnndddd00001001mmmm"),
        new(ArmOperation.BranchExchange, "cccc000100101111111111110001nnnn"),
        new(ArmOperation.Undefined, "cccc000PUIW0nnnnddddoooo11S1oooo"),
        new(ArmOperation.HalfwordTransfer, "cccc000PUIWLnnnndddd00001011mmmm"),
        new(ArmOperation.HalfwordTransfer, "cccc000PUIW1nnnnddddOOOO1101OOOO"),
        new(ArmOperation.HalfwordTransfer, "cccc000PUIW1nnnnddddOOOO1111OOOO"),
        new(ArmOperation.SingleTransfer, "cccc010PUBWLnnnnddddOOOOOOOOOOOO"),
        new(ArmOperation.SingleTransfer, "cccc011PUBWLnnnnddddOOOOOOO0mmmm"),
        new(ArmOperation.Undefined, "cccc011--------------------1----"),
        new(ArmOperation.BlockTransfer, "cccc100PUSWLnnnnllllllllllllllll"),
        new(ArmOperation.Branch, "cccc1010OOOOOOOOOOOOOOOOOOOOOOOO"),
        new(ArmOperation.Branch, "cccc1011OOOOOOOOOOOOOOOOOOOOOOOO"),
        new(ArmOperation.CoprocessorDataTransfer, "cccc110PUNWLnnnndddd####OOOOOOOO"),
        new(ArmOperation.CoprocessorDataOperation, "cccc1110oooonnnndddd####ppp0mmmm"),
        new(ArmOperation.CoprocessorRegisterTransfer, "cccc1110oooLnnnndddd####ppp1mmmm"),
        new(ArmOperation.SoftwareInterrupt, "cccc1111------------------------"),
        new(ArmOperation.Mrs, "cccc00010P001111dddd000000000000"),
        new(ArmOperation.Msr, "cccc00010P10100F111100000000mmmm"),
        new(ArmOperation.Msr, "cccc00110P10100F1111oooooooooooo"),
        new(ArmOperation.Undefined, "cccc000001--------------1001----"),
        new(ArmOperation.Undefined, "cccc00011---------------1001----"),
        new(ArmOperation.Undefined, "cccc00010-1-------------1001----"),
        new(ArmOperation.Undefined, "cccc00010-01------------1001----"),
        new(ArmOperation.Undefined, "cccc00010-00------------01-0----"),
        new(ArmOperation.Undefined, "cccc00010-00------------0010----"),
        new(ArmOperation.Undefined, "cccc00010oo0nnnndddd00000101mmmm"),
        new(ArmOperation.Undefined, "cccc00010-00------------00-1----"),
        new(ArmOperation.Undefined, "cccc00010-00------------0111----"),
        new(ArmOperation.Undefined, "cccc00010oo0ddddnnnnssss1yx0mmmm"),
        new(ArmOperation.Undefined, "cccc00010110------------01-0----"),
        new(ArmOperation.Undefined, "cccc00010110------------0010----"),
        new(ArmOperation.Undefined, "cccc000101101111DDDD11110001MMMM"),
        new(ArmOperation.Undefined, "cccc00010110------------0111----"),
        new(ArmOperation.Undefined, "cccc00010110------------0011----"),
        new(ArmOperation.Undefined, "cccc00010010------------0111----"),
        new(ArmOperation.Undefined, "cccc000100101111111111110011nnnn"),
        new(ArmOperation.Undefined, "cccc00010010------------01-0----"),
        new(ArmOperation.Undefined, "cccc00010010------------0010----"),
        new(ArmOperation.Undefined, "cccc00110-000000000000001-------"),
        new(ArmOperation.Undefined, "cccc00110-0000000000000001------"),
        new(ArmOperation.Undefined, "cccc00110-00000000000000001-----"),
        new(ArmOperation.Undefined, "cccc00110-000000000000000001----"),
        new(ArmOperation.Undefined, "----00110-00------------0000----"),
    ];

    static readonly ThumbPattern[] ThumbPatterns =
    [
        new(ThumbOperation.MoveShiftedRegister, "00000OOOOOsssddd"),
        new(ThumbOperation.MoveShiftedRegister, "00001OOOOOsssddd"),
        new(ThumbOperation.MoveShiftedRegister, "00010OOOOOsssddd"),
        new(ThumbOperation.AddSubtract, "00011I0nnnsssddd"),
        new(ThumbOperation.AddSubtract, "00011I1nnnsssddd"),
        new(ThumbOperation.ImmediateAlu, "001oodddOOOOOOOO"),
        new(ThumbOperation.Alu, "010000oooosssddd"),
        new(ThumbOperation.HighRegister, "010001oohHsssddd"),
        new(ThumbOperation.PcRelativeLoad, "01001dddOOOOOOOO"),
        new(ThumbOperation.RegisterOffsetTransfer, "0101LB0ooobbbddd"),
        new(ThumbOperation.SignedTransfer, "0101HS1ooobbbddd"),
        new(ThumbOperation.ImmediateTransfer, "011BLOOOOObbbddd"),
        new(ThumbOperation.ImmediateHalfwordTransfer, "1000LOOOOObbbddd"),
        new(ThumbOperation.SpRelativeTransfer, "1001LdddOOOOOOOO"),
        new(ThumbOperation.LoadAddress, "1010SdddOOOOOOOO"),
        new(ThumbOperation.AddSpOffset, "10110000SOOOOOOO"),
        new(ThumbOperation.PushPop, "1011L10Rllllllll"),
        new(ThumbOperation.MultipleTransfer, "1100Lbbbllllllll"),
        new(ThumbOperation.ConditionalBranch, "11010cccOOOOOOOO"),
        new(ThumbOperation.ConditionalBranch, "110110ccOOOOOOOO"),
        new(ThumbOperation.ConditionalBranch, "1101110cOOOOOOOO"),
        new(ThumbOperation.ConditionalBranch, "11011110OOOOOOOO"),
        new(ThumbOperation.SoftwareInterrupt, "11011111OOOOOOOO"),
        new(ThumbOperation.Branch, "11100OOOOOOOOOOO"),
        new(ThumbOperation.LongBranch, "1111HOOOOOOOOOOO"),
        new(ThumbOperation.LongBranch, "11101OOOOOOOOOOO"),
        new(ThumbOperation.Undefined, "1011--1---------"),
        new(ThumbOperation.Undefined, "10110001--------"),
        new(ThumbOperation.Undefined, "1011100---------"),
    ];

    static readonly ArmOperation[] ArmDecodeTable = BuildArmDecodeTable();
    static readonly ThumbOperation[] ThumbDecodeTable = BuildThumbDecodeTable();

    void ExecuteArm(uint instruction)
    {
        int key = (int)(((instruction >> 4) & 0x0f) | ((instruction >> 16) & 0xff0));
        switch (ArmDecodeTable[key])
        {
            case ArmOperation.DataProcessing: ExecuteArmDataProcessing(instruction); break;
            case ArmOperation.Multiply: ExecuteArmMultiply(instruction); break;
            case ArmOperation.MultiplyLong: ExecuteArmMultiplyLong(instruction); break;
            case ArmOperation.Swap: ExecuteArmSwap(instruction); break;
            case ArmOperation.BranchExchange: ExecuteArmBranchExchange(instruction); break;
            case ArmOperation.HalfwordTransfer: ExecuteArmHalfwordTransfer(instruction); break;
            case ArmOperation.SingleTransfer: ExecuteArmSingleTransfer(instruction); break;
            case ArmOperation.BlockTransfer: ExecuteArmBlockTransfer(instruction); break;
            case ArmOperation.Branch: ExecuteArmBranch(instruction); break;
            case ArmOperation.CoprocessorDataTransfer:
            case ArmOperation.CoprocessorDataOperation:
            case ArmOperation.CoprocessorRegisterTransfer:
                ExecuteArmUndefined(instruction);
                break;
            case ArmOperation.SoftwareInterrupt: ExecuteSoftwareInterrupt(instruction); break;
            case ArmOperation.Mrs: ExecuteArmMrs(instruction); break;
            case ArmOperation.Msr: ExecuteArmMsr(instruction); break;
            default: ExecuteArmUndefined(instruction); break;
        }
    }

    void ExecuteThumb(ushort instruction)
    {
        switch (ThumbDecodeTable[instruction >> 8])
        {
            case ThumbOperation.MoveShiftedRegister: ExecuteThumbMoveShifted(instruction); break;
            case ThumbOperation.AddSubtract: ExecuteThumbAddSubtract(instruction); break;
            case ThumbOperation.ImmediateAlu: ExecuteThumbImmediateAlu(instruction); break;
            case ThumbOperation.Alu: ExecuteThumbAlu(instruction); break;
            case ThumbOperation.HighRegister: ExecuteThumbHighRegister(instruction); break;
            case ThumbOperation.PcRelativeLoad: ExecuteThumbPcRelativeLoad(instruction); break;
            case ThumbOperation.RegisterOffsetTransfer: ExecuteThumbRegisterTransfer(instruction); break;
            case ThumbOperation.SignedTransfer: ExecuteThumbSignedTransfer(instruction); break;
            case ThumbOperation.ImmediateTransfer: ExecuteThumbImmediateTransfer(instruction); break;
            case ThumbOperation.ImmediateHalfwordTransfer: ExecuteThumbImmediateHalfwordTransfer(instruction); break;
            case ThumbOperation.SpRelativeTransfer: ExecuteThumbSpRelativeTransfer(instruction); break;
            case ThumbOperation.LoadAddress: ExecuteThumbLoadAddress(instruction); break;
            case ThumbOperation.AddSpOffset: ExecuteThumbAddSpOffset(instruction); break;
            case ThumbOperation.PushPop: ExecuteThumbPushPop(instruction); break;
            case ThumbOperation.MultipleTransfer: ExecuteThumbMultipleTransfer(instruction); break;
            case ThumbOperation.ConditionalBranch: ExecuteThumbConditionalBranch(instruction); break;
            case ThumbOperation.SoftwareInterrupt: ExecuteSoftwareInterrupt(instruction); break;
            case ThumbOperation.Branch: ExecuteThumbBranch(instruction); break;
            case ThumbOperation.LongBranch: ExecuteThumbLongBranch(instruction); break;
            default: ExecuteThumbUndefined(instruction); break;
        }
    }

    static ArmOperation[] BuildArmDecodeTable()
    {
        var table = new ArmOperation[4096];
        for (int key = 0; key < table.Length; key++)
        {
            ArmOperation operation = ArmOperation.Undefined;
            foreach (ArmPattern pattern in ArmPatterns)
            {
                if (Matches(pattern.Bits, key, ArmKeyBits, 31))
                {
                    operation = pattern.Operation;
                }
            }
            table[key] = operation;
        }
        return table;
    }

    static ThumbOperation[] BuildThumbDecodeTable()
    {
        var table = new ThumbOperation[256];
        for (int key = 0; key < table.Length; key++)
        {
            ThumbOperation operation = ThumbOperation.Undefined;
            foreach (ThumbPattern pattern in ThumbPatterns)
            {
                if (Matches(pattern.Bits, key, ThumbKeyBits, 15))
                {
                    operation = pattern.Operation;
                }
            }
            table[key] = operation;
        }
        return table;
    }

    static bool Matches(string pattern, int key, int[] keyBits, int mostSignificantBit)
    {
        for (int keyBit = 0; keyBit < keyBits.Length; keyBit++)
        {
            char expected = pattern[mostSignificantBit - keyBits[keyBit]];
            bool actual = ((key >> keyBit) & 1) != 0;
            if ((expected == '1' && !actual) || (expected == '0' && actual))
            {
                return false;
            }
        }
        return true;
    }
}
