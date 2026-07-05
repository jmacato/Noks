using System.Buffers.Binary;
using Noks.Dct3.Core;
using Noks.Dct3.Radio;

namespace Noks.Dct3.Firmware;

internal static class Dct3FirmwareRuntimeHooks
{
    private const uint FlashBase = Dct3Machine.FlashBase;
    private const ushort PopR4R5R6R7Pc = 0xBDF0;
    private const uint InvalidTimestamp = 0x7FFFFFFF;
    private const uint Mad2IoBase = 0x20000;
    private const int NitzDispatcherTableEntries = 15;
    private const uint MaximumDispatcherTableSpan = 0x2000;

    private static readonly byte[] DateTimeZeroSignature = [0x08, 0x02, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] CalcTimestampSignature =
    [
        0xB5, 0x10, 0x79, 0x0B, 0x79, 0x02, 0x1A, 0x9B,
        0x22, 0x3C, 0x43, 0x5A, 0x79, 0x4B, 0x18, 0x9B,
    ];
    private static readonly byte[] ThumbJumpTableDispatcherSignature =
    [
        0xA1, 0x01, // add r1, pc, #4
        0x00, 0x80, // lsls r0, r0, #2
        0x58, 0x08, // ldr r0, [r1, r0]
        0x46, 0x87, // mov pc, r0
    ];
    private static readonly byte[] IdleAliveLoopSignature =
    [
        0x46, 0x48, // mov r0, sb
        0x70, 0x30, // strb r0, [r6]
        0x48, 0x31, // ldr r0, [pc, #0xc4]
        0x70, 0x07, // strb r7, [r0]
        0x46, 0x40, // mov r0, r8
        0x73, 0x28, // strb r0, [r5, #0xc]
        0x78, 0x30, // ldrb r0, [r6]
        0x28, 0x00, // cmp r0, #0
        0xD0,       // beq exit
    ];
    private static readonly byte[] PopR4PcSignature = [0xBD, 0x10];
    private static readonly byte[] CcontRegisterConfigSignature =
    [
        0xFF, 0xFF, 0x10, 0x20, 0x18, 0xFF, 0xFF, 0xFF,
        0xFF, 0x38, 0x40, 0x48, 0x50, 0x58, 0x60, 0x68,
    ];
    internal static bool TryResolveNitzClockHook(ReadOnlySpan<byte> flash, out NitzClockRuntimeHook hook)
    {
        hook = default;

        if (!TryFindUnique(flash, DateTimeZeroSignature, out int dateTimeZeroOffset) ||
            !TryFindUnique(flash, CalcTimestampSignature, out int calcTimestampOffset) ||
            !TryFindSetTimestamp(flash, out int setTimestampOffset) ||
            !TryFindClockState(flash, out uint clockStateAddress) ||
            !TryFindCcontElapsedSourceReturn(flash, out uint ccontElapsedSourceReturnAddress) ||
            !TryFindCcontRegisterCache(flash, out uint ccontRegisterCacheAddress) ||
            !TryFindNitzIgnoredDispatcher(flash, out uint dispatcherAddress, out uint ignoredMessageHandlerAddress))
        {
            return false;
        }

        hook = new NitzClockRuntimeHook(
            dispatcherAddress,
            ignoredMessageHandlerAddress,
            FlashBase + (uint)dateTimeZeroOffset,
            FlashBase + (uint)calcTimestampOffset,
            FlashBase + (uint)setTimestampOffset,
            clockStateAddress,
            ccontElapsedSourceReturnAddress,
            ccontRegisterCacheAddress);
        return true;
    }

    internal static bool TryResolveIdleYieldHook(ReadOnlySpan<byte> flash, out IdleYieldRuntimeHook hook)
    {
        hook = default;

        if (!TryFindUnique(flash, IdleAliveLoopSignature, out int idlePreLoopOffset))
        {
            return false;
        }

        int loopOffset = idlePreLoopOffset + 12;
        uint loopAddress = FlashBase + (uint)loopOffset;
        if (!TryResolveNearbyLoadIntoRegister(
                flash,
                idlePreLoopOffset,
                register: 6,
                out uint aliveFlagAddress) ||
            !IsRamAddress(aliveFlagAddress))
        {
            return false;
        }

        if (!TryFindIdleFiqClear(flash, aliveFlagAddress, out uint fiqClearAddress))
        {
            return false;
        }

        hook = new IdleYieldRuntimeHook(
            LoopStartAddress: loopAddress,
            LoopEndAddress: loopAddress + 6u,
            LoopFetchStartAddress: loopAddress + 4u,
            LoopFetchEndAddress: loopAddress + 10u,
            AliveFlagAddress: aliveFlagAddress,
            FiqClearAddress: fiqClearAddress);
        return true;
    }

    internal static bool TryDecodeNitzDateTimeStruct(
        ReadOnlySpan<byte> semiOctets,
        Span<byte> firmwareDateTimeStruct,
        out NitzClockDateTime dateTime)
    {
        dateTime = default;

        if (semiOctets.Length < 6 || firmwareDateTimeStruct.Length < 8)
        {
            return false;
        }

        for (int i = 0; i < 6; i++)
        {
            if (!IsTimestampSemiOctet(semiOctets[i]))
            {
                return false;
            }
        }

        int twoDigitYear = DecodeTimestampSemiOctet(semiOctets[0]);
        int year = twoDigitYear < 90 ? 2000 + twoDigitYear : 1900 + twoDigitYear;
        int month = DecodeTimestampSemiOctet(semiOctets[1]);
        int day = DecodeTimestampSemiOctet(semiOctets[2]);
        int hour = DecodeTimestampSemiOctet(semiOctets[3]);
        int minute = DecodeTimestampSemiOctet(semiOctets[4]);
        int second = DecodeTimestampSemiOctet(semiOctets[5]);

        if (month is < 1 or > 12 ||
            day is < 1 or > 31 ||
            hour is > 23 ||
            minute is > 59 ||
            second is > 59)
        {
            return false;
        }

        BinaryPrimitives.WriteUInt16BigEndian(firmwareDateTimeStruct, (ushort)year);
        firmwareDateTimeStruct[2] = (byte)month;
        firmwareDateTimeStruct[3] = (byte)day;
        firmwareDateTimeStruct[4] = (byte)hour;
        firmwareDateTimeStruct[5] = (byte)minute;
        firmwareDateTimeStruct[6] = (byte)second;
        firmwareDateTimeStruct[7] = 0;

        dateTime = new NitzClockDateTime(year, month, day, hour, minute, second);
        return true;
    }

    private static int DecodeTimestampSemiOctet(byte value) =>
        (value & 0x0F) * 10 + (value >> 4);

    private static bool IsTimestampSemiOctet(byte value) =>
        (value & 0x0F) <= 9 && (value >> 4) <= 9;

    private static bool TryFindSetTimestamp(ReadOnlySpan<byte> flash, out int offset)
    {
        int foundOffset = -1;

        for (int candidate = 0; candidate <= flash.Length - 0x80; candidate++)
        {
            if (flash[candidate] != 0xB5 ||
                flash[candidate + 1] != 0x10 ||
                flash[candidate + 2] != 0x1C ||
                flash[candidate + 3] != 0x04 ||
                flash[candidate + 4] != 0x48 ||
                flash[candidate + 6] != 0x42 ||
                flash[candidate + 7] != 0x84 ||
                flash[candidate + 8] != 0xD0 ||
                flash[candidate + 9] != 0x1F)
            {
                continue;
            }

            ushort ldr = BinaryPrimitives.ReadUInt16BigEndian(flash[(candidate + 4)..]);
            uint literalAddress = ResolveThumbLiteralAddress((uint)candidate + 4u, ldr);
            if (!TryReadFlashWord(flash, literalAddress, out uint literal) ||
                literal != InvalidTimestamp ||
                flash.Slice(candidate, 0x80).IndexOf(PopR4PcSignature) < 0)
            {
                continue;
            }

            if (foundOffset >= 0)
            {
                offset = 0;
                return false;
            }

            foundOffset = candidate;
        }

        offset = foundOffset;
        return foundOffset >= 0;
    }

    private static bool TryFindCcontRegisterCache(ReadOnlySpan<byte> flash, out uint cacheAddress)
    {
        cacheAddress = 0;

        if (!TryFindUnique(flash, CcontRegisterConfigSignature, out int configOffset))
        {
            return false;
        }

        uint configAddress = FlashBase + (uint)configOffset;
        int found = 0;

        for (int offset = 0; offset <= flash.Length - 12; offset += 2)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(flash[offset..]) != configAddress ||
                BinaryPrimitives.ReadUInt32BigEndian(flash[(offset + 4)..]) != Mad2IoBase)
            {
                continue;
            }

            uint candidateCacheAddress = BinaryPrimitives.ReadUInt32BigEndian(flash[(offset + 8)..]);
            if (!IsRamAddress(candidateCacheAddress))
            {
                continue;
            }

            found++;
            cacheAddress = candidateCacheAddress;
            if (found > 1)
            {
                cacheAddress = 0;
                return false;
            }
        }

        return found == 1;
    }

    private static bool TryFindClockState(ReadOnlySpan<byte> flash, out uint clockStateAddress)
    {
        clockStateAddress = 0;
        int found = 0;

        for (int offset = 0; offset <= flash.Length - 0xA0; offset += 2)
        {
            if (!LooksLikeGetTimestamp(flash, offset, out uint candidateClockStateAddress))
            {
                continue;
            }

            found++;
            clockStateAddress = candidateClockStateAddress;
            if (found > 1)
            {
                clockStateAddress = 0;
                return false;
            }
        }

        return found == 1;
    }

    private static bool LooksLikeGetTimestamp(ReadOnlySpan<byte> flash, int offset, out uint clockStateAddress)
    {
        clockStateAddress = 0;

        if (offset < 0 || offset > flash.Length - 0xA0)
        {
            return false;
        }

        ushort clockStateLoad = BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 20)..]);
        if (BinaryPrimitives.ReadUInt16BigEndian(flash[offset..]) != 0xB510 ||
            BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 2)..]) != 0xB082 ||
            !IsThumbPcRelativeLoadIntoRegister(BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 4)..]), register: 0) ||
            BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 6)..]) != 0x2100 ||
            !IsThumbBlPrefix(BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 8)..])) ||
            !IsThumbBlSuffix(BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 10)..])) ||
            !IsThumbPcRelativeLoadIntoRegister(BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 12)..]), register: 0) ||
            BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 14)..]) != 0x7800 ||
            BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 16)..]) != 0x282A ||
            !IsThumbConditionalBranch(BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 18)..])) ||
            !IsThumbPcRelativeLoadIntoRegister(clockStateLoad, register: 4) ||
            BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 22)..]) != 0x7960 ||
            BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 24)..]) != 0x0941 ||
            !IsThumbConditionalBranch(BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 26)..])) ||
            BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 28)..]) != 0x0900 ||
            !IsThumbConditionalBranch(BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 30)..])) ||
            !IsThumbBlPrefix(BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 32)..])) ||
            !IsThumbBlSuffix(BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 34)..])))
        {
            return false;
        }

        bool hasReturn = false;
        for (int candidate = offset + 0x80; candidate <= offset + 0xC0 && candidate <= flash.Length - 4; candidate += 2)
        {
            if (BinaryPrimitives.ReadUInt16BigEndian(flash[candidate..]) == 0xB002 &&
                BinaryPrimitives.ReadUInt16BigEndian(flash[(candidate + 2)..]) == 0xBD10)
            {
                hasReturn = true;
                break;
            }
        }

        if (!hasReturn)
        {
            return false;
        }

        uint literalAddress = ResolveThumbLiteralAddress((uint)(offset + 20), clockStateLoad);
        if (!TryReadFlashWord(flash, literalAddress, out uint candidateClockStateAddress) ||
            !IsRamAddress(candidateClockStateAddress) ||
            candidateClockStateAddress > 0x17FFEC)
        {
            return false;
        }

        clockStateAddress = candidateClockStateAddress;
        return true;
    }

    private static bool TryFindCcontElapsedSourceReturn(ReadOnlySpan<byte> flash, out uint returnAddress)
    {
        returnAddress = 0;
        int found = 0;

        for (int offset = 0; offset <= flash.Length - 0x80; offset += 2)
        {
            if (!LooksLikeCcontElapsedSource(flash, offset, out int returnOffset))
            {
                continue;
            }

            found++;
            returnAddress = FlashBase + (uint)returnOffset;
            if (found > 1)
            {
                returnAddress = 0;
                return false;
            }
        }

        return found == 1;
    }

    private static bool LooksLikeCcontElapsedSource(ReadOnlySpan<byte> flash, int offset, out int returnOffset)
    {
        returnOffset = 0;

        if (offset < 0 || offset > flash.Length - 0x80)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(flash[offset..]) != 0xB5F0 ||
            !IsThumbPcRelativeLoadIntoRegister(BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 2)..]), register: 0) ||
            !IsThumbBlPrefix(BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 4)..])) ||
            !IsThumbBlSuffix(BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 6)..])) ||
            BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 8)..]) != 0x283F ||
            !IsThumbConditionalBranch(BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 10)..])) ||
            BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 12)..]) != 0x2603 ||
            BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 20)..]) != 0x21E1 ||
            BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 22)..]) != 0x010C ||
            BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 24)..]) != 0x4344 ||
            BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 32)..]) != 0x253C ||
            BinaryPrimitives.ReadUInt16BigEndian(flash[(offset + 34)..]) != 0x4345)
        {
            return false;
        }

        for (int candidate = offset + 0x40; candidate <= offset + 0x100 && candidate <= flash.Length - 16; candidate += 2)
        {
            if (BinaryPrimitives.ReadUInt16BigEndian(flash[candidate..]) == 0x4284 &&
                IsThumbConditionalBranch(BinaryPrimitives.ReadUInt16BigEndian(flash[(candidate + 2)..])) &&
                BinaryPrimitives.ReadUInt16BigEndian(flash[(candidate + 4)..]) == 0x3E01 &&
                BinaryPrimitives.ReadUInt16BigEndian(flash[(candidate + 6)..]) == 0x2E00 &&
                IsThumbConditionalBranch(BinaryPrimitives.ReadUInt16BigEndian(flash[(candidate + 8)..])) &&
                BinaryPrimitives.ReadUInt16BigEndian(flash[(candidate + 10)..]) == 0x24FF &&
                BinaryPrimitives.ReadUInt16BigEndian(flash[(candidate + 12)..]) == 0x1C20 &&
                BinaryPrimitives.ReadUInt16BigEndian(flash[(candidate + 14)..]) == PopR4R5R6R7Pc)
            {
                returnOffset = candidate + 14;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindNitzIgnoredDispatcher(
        ReadOnlySpan<byte> flash,
        out uint dispatcherAddress,
        out uint ignoredMessageHandlerAddress)
    {
        dispatcherAddress = 0;
        ignoredMessageHandlerAddress = 0;
        int found = 0;
        int searchStart = 0;

        while (searchStart <= flash.Length - ThumbJumpTableDispatcherSignature.Length)
        {
            int relative = flash[searchStart..].IndexOf(ThumbJumpTableDispatcherSignature);
            if (relative < 0)
            {
                break;
            }

            int matchOffset = searchStart + relative;
            int tableOffset = matchOffset + ThumbJumpTableDispatcherSignature.Length;

            if (IsNitzDispatcherTableCandidate(flash, tableOffset, out uint handlerAddress))
            {
                found++;
                dispatcherAddress = FlashBase + (uint)matchOffset;
                ignoredMessageHandlerAddress = handlerAddress;

                if (found > 1)
                {
                    dispatcherAddress = 0;
                    ignoredMessageHandlerAddress = 0;
                    return false;
                }
            }

            searchStart = matchOffset + 1;
        }

        return found == 1;
    }

    private static bool IsNitzDispatcherTableCandidate(
        ReadOnlySpan<byte> flash,
        int tableOffset,
        out uint ignoredMessageHandlerAddress)
    {
        ignoredMessageHandlerAddress = 0;

        if (tableOffset < 0 || tableOffset > flash.Length - NitzDispatcherTableEntries * 4)
        {
            return false;
        }

        Span<uint> entries = stackalloc uint[NitzDispatcherTableEntries];
        uint min = uint.MaxValue;
        uint max = 0;

        for (int i = 0; i < entries.Length; i++)
        {
            uint entry = BinaryPrimitives.ReadUInt32BigEndian(flash[(tableOffset + i * 4)..]);
            if (!IsFlashAddress(flash, entry, 2))
            {
                return false;
            }

            entries[i] = entry;
            min = Math.Min(min, entry);
            max = Math.Max(max, entry);
        }

        uint entry5 = entries[5];
        if (entries[11] != entry5 ||
            entries[12] != entry5 ||
            max - min > MaximumDispatcherTableSpan ||
            !TryReadFlashHalf(flash, entry5, out ushort entry5Instruction) ||
            entry5Instruction != PopR4R5R6R7Pc)
        {
            return false;
        }

        ignoredMessageHandlerAddress = entry5;
        return true;
    }

    private static bool TryFindUnique(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, out int offset)
    {
        int foundOffset = -1;
        int searchStart = 0;

        while (searchStart <= data.Length - signature.Length)
        {
            int relative = data[searchStart..].IndexOf(signature);
            if (relative < 0)
            {
                break;
            }

            int candidate = searchStart + relative;
            if (foundOffset >= 0)
            {
                offset = 0;
                return false;
            }

            foundOffset = candidate;
            searchStart = candidate + 1;
        }

        offset = foundOffset;
        return foundOffset >= 0;
    }

    private static uint ResolveThumbLiteralAddress(uint flashOffset, ushort ldr)
    {
        uint instructionAddress = FlashBase + flashOffset;
        uint pcRelativeBase = (instructionAddress + 4u) & ~3u;
        return pcRelativeBase + (uint)(ldr & 0x00FF) * 4u;
    }

    private static bool IsThumbPcRelativeLoadIntoRegister(ushort instruction, int register) =>
        (instruction & 0xF800) == 0x4800 &&
        ((instruction >> 8) & 0x7) == register;

    private static bool IsThumbBlPrefix(ushort instruction) =>
        (instruction & 0xF800) == 0xF000;

    private static bool IsThumbBlSuffix(ushort instruction) =>
        (instruction & 0xF800) == 0xF800;

    private static bool IsThumbConditionalBranch(ushort instruction) =>
        (instruction & 0xF000) == 0xD000 &&
        (instruction & 0x0F00) != 0x0F00;

    private static bool TryResolveNearbyLoadIntoRegister(
        ReadOnlySpan<byte> flash,
        int beforeOffset,
        int register,
        out uint literal)
    {
        literal = 0;

        int searchStart = Math.Max(0, beforeOffset - 0x100);
        for (int offset = beforeOffset - 2; offset >= searchStart; offset -= 2)
        {
            ushort instruction = BinaryPrimitives.ReadUInt16BigEndian(flash[offset..]);
            if ((instruction & 0xF800) != 0x4800 ||
                ((instruction >> 8) & 0x7) != register)
            {
                continue;
            }

            uint literalAddress = ResolveThumbLiteralAddress((uint)offset, instruction);
            return TryReadFlashWord(flash, literalAddress, out literal);
        }

        return false;
    }

    private static bool TryFindIdleFiqClear(ReadOnlySpan<byte> flash, uint aliveFlagAddress, out uint fiqClearAddress)
    {
        fiqClearAddress = 0;
        bool found = false;
        int searchStart = 0;

        while (searchStart <= flash.Length - 8)
        {
            int candidate = searchStart;
            if (flash[candidate] != 0xB5 ||
                flash[candidate + 1] != 0x10 ||
                flash[candidate + 2] != 0x49 ||
                flash[candidate + 4] != 0x20 ||
                flash[candidate + 5] != 0x00 ||
                flash[candidate + 6] != 0x70 ||
                flash[candidate + 7] != 0x08)
            {
                searchStart++;
                continue;
            }

            ushort ldr = BinaryPrimitives.ReadUInt16BigEndian(flash[(candidate + 2)..]);
            uint literalAddress = ResolveThumbLiteralAddress((uint)candidate + 2u, ldr);
            if (TryReadFlashWord(flash, literalAddress, out uint literal) &&
                literal == aliveFlagAddress)
            {
                found = true;
                fiqClearAddress = FlashBase + (uint)candidate;
            }

            searchStart++;
        }

        return found;
    }

    private static bool TryReadFlashWord(ReadOnlySpan<byte> flash, uint address, out uint value)
    {
        value = 0;
        if (!IsFlashAddress(flash, address, 4))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32BigEndian(flash[(int)(address - FlashBase)..]);
        return true;
    }

    private static bool TryReadFlashHalf(ReadOnlySpan<byte> flash, uint address, out ushort value)
    {
        value = 0;
        if (!IsFlashAddress(flash, address, 2))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt16BigEndian(flash[(int)(address - FlashBase)..]);
        return true;
    }

    private static bool FlashHasBytes(ReadOnlySpan<byte> flash, uint address, ReadOnlySpan<byte> expected) =>
        IsFlashAddress(flash, address, expected.Length) &&
        flash.Slice((int)(address - FlashBase), expected.Length).SequenceEqual(expected);

    private static bool IsFlashAddress(ReadOnlySpan<byte> flash, uint address, int length) =>
        length >= 0 &&
        length <= flash.Length &&
        address >= FlashBase &&
        address - FlashBase <= (uint)(flash.Length - length);

    private static bool IsRamAddress(uint address) => address < 0x180000;
}
