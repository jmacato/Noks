using System.Buffers.Binary;
using Noks.Dct3.Core;
using Noks.Dct3.Firmware;
using Noks.Dct3.Radio;

namespace Noks.Dct3.Tests;

public sealed class Dct3FirmwareRuntimeHooksTests
{
    [Fact]
    public void TryResolveNitzClockHook_V418Shape_ResolvesWithoutMutatingFlash()
    {
        byte[] flash = BuildNitzHookFirmware(
            dateTimeZeroAddress: 0x31B380,
            calcTimestampAddress: 0x2C9520,
            setTimestampAddress: 0x240DE8,
            setTimestampInvalidLiteralAddress: 0x241104,
            dispatcherAddress: 0x2A2B64,
            ignoredMessageHandlerAddress: 0x2A35A6,
            getTimestampAddress: 0x2BC514,
            clockStateAddress: 0x119ED0,
            ccontElapsedSourceAddress: 0x2DA3F4,
            [
                0x2A2F56, 0x2A2F4C, 0x2A2F42, 0x2A2F3A, 0x2A2F36,
                0x2A35A6, 0x2A2ECE, 0x2A2EC4, 0x2A2E90, 0x2A2E2C,
                0x2A2DFC, 0x2A35A6, 0x2A35A6, 0x2A35A6, 0x2A2D98,
            ]);
        byte[] before = flash.ToArray();

        bool resolved = Dct3FirmwareRuntimeHooks.TryResolveNitzClockHook(flash, out NitzClockRuntimeHook hook);

        Assert.True(resolved);
        Assert.Equal(new NitzClockRuntimeHook(0x2A2B64, 0x2A35A6, 0x31B380, 0x2C9520, 0x240DE8, 0x119ED0, 0x2DA468, 0x11A11C), hook);
        Assert.Equal(before, flash);
    }

    [Fact]
    public void TryResolveNitzClockHook_V639Shape_ResolvesMovedFirmwareAddresses()
    {
        byte[] flash = BuildNitzHookFirmware(
            dateTimeZeroAddress: 0x32F404,
            calcTimestampAddress: 0x2D9674,
            setTimestampAddress: 0x24E4A0,
            setTimestampInvalidLiteralAddress: 0x24E74C,
            dispatcherAddress: 0x2AFB90,
            ignoredMessageHandlerAddress: 0x2B05F2,
            getTimestampAddress: 0x2CC440,
            clockStateAddress: 0x1117AC,
            ccontElapsedSourceAddress: 0x2EA9FC,
            [
                0x2AFF90, 0x2AFF7C, 0x2AFF72, 0x2AFF6A, 0x2AFF66,
                0x2B05F2, 0x2AFEFE, 0x2AFEF4, 0x2AFEC0, 0x2AFE60,
                0x2AFE34, 0x2B05F2, 0x2B05F2, 0x2AFDD0, 0x2AFDB4,
            ],
            ccontRegisterCacheAddress: 0x11B11C);

        bool resolved = Dct3FirmwareRuntimeHooks.TryResolveNitzClockHook(flash, out NitzClockRuntimeHook hook);

        Assert.True(resolved);
        Assert.Equal(new NitzClockRuntimeHook(0x2AFB90, 0x2B05F2, 0x32F404, 0x2D9674, 0x24E4A0, 0x1117AC, 0x2EAA70, 0x11B11C), hook);
    }

    [Fact]
    public void TryResolveNitzClockHook_MissingIgnoredDispatcher_ReturnsFalse()
    {
        byte[] flash = BuildNitzHookFirmware(
            dateTimeZeroAddress: 0x31B380,
            calcTimestampAddress: 0x2C9520,
            setTimestampAddress: 0x240DE8,
            setTimestampInvalidLiteralAddress: 0x241104,
            dispatcherAddress: 0x2A2B64,
            ignoredMessageHandlerAddress: 0x2A35A6,
            getTimestampAddress: 0x2BC514,
            clockStateAddress: 0x119ED0,
            ccontElapsedSourceAddress: 0x2DA3F4,
            [
                0x2A2F56, 0x2A2F4C, 0x2A2F42, 0x2A2F3A, 0x2A2F36,
                0x2A35A6, 0x2A2ECE, 0x2A2EC4, 0x2A2E90, 0x2A2E2C,
                0x2A2DFC, 0x2A35A6, 0x2A2F56, 0x2A35A6, 0x2A2D98,
            ]);

        bool resolved = Dct3FirmwareRuntimeHooks.TryResolveNitzClockHook(flash, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryResolveIdleYieldHook_AliveLoopAndFiqClearShape_ResolvesInterceptionPoint()
    {
        byte[] flash = BuildIdleYieldHookFirmware(
            loopFunctionAddress: 0x2D1620,
            loopPrefixAddress: 0x2D16BE,
            r6LoadAddress: 0x2D1644,
            r6LiteralAddress: 0x2D1784,
            aliveFlagAddress: 0x11A294,
            fiqClearAddress: 0x2D73BA,
            fiqClearLiteralAddress: 0x2D7468);

        bool resolved = Dct3FirmwareRuntimeHooks.TryResolveIdleYieldHook(flash, out IdleYieldRuntimeHook hook);

        Assert.True(resolved);
        Assert.Equal(
            new IdleYieldRuntimeHook(
                LoopStartAddress: 0x2D16CA,
                LoopEndAddress: 0x2D16D0,
                LoopFetchStartAddress: 0x2D16CE,
                LoopFetchEndAddress: 0x2D16D4,
                AliveFlagAddress: 0x11A294,
                FiqClearAddress: 0x2D73BA),
            hook);
    }

    [Fact]
    public void TryResolveIdleYieldHook_FiqClearDoesNotMatchPolledFlag_ReturnsFalse()
    {
        byte[] flash = BuildIdleYieldHookFirmware(
            loopFunctionAddress: 0x2D1620,
            loopPrefixAddress: 0x2D16BE,
            r6LoadAddress: 0x2D1644,
            r6LiteralAddress: 0x2D1784,
            aliveFlagAddress: 0x11A294,
            fiqClearAddress: 0x2D73BA,
            fiqClearLiteralAddress: 0x2D7468);
        WriteWord(flash, 0x2D7468, 0x11A295);

        bool resolved = Dct3FirmwareRuntimeHooks.TryResolveIdleYieldHook(flash, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryDecodeNitzDateTimeStruct_DecodesGsmSemiOctetsAsLowDigitThenHighDigit()
    {
        Span<byte> firmwareDateTimeStruct = stackalloc byte[8];

        bool decoded = Dct3FirmwareRuntimeHooks.TryDecodeNitzDateTimeStruct(
            [0x62, 0x70, 0x60, 0x51, 0x02, 0x84],
            firmwareDateTimeStruct,
            out NitzClockDateTime dateTime);

        Assert.True(decoded);
        Assert.Equal(new NitzClockDateTime(2026, 7, 6, 15, 20, 48), dateTime);
        Assert.Equal([0x07, 0xEA, 0x07, 0x06, 0x0F, 0x14, 0x30, 0x00], firmwareDateTimeStruct.ToArray());
    }

    [Fact]
    public void TryDecodeNitzDateTimeStruct_InvalidMonth_ReturnsFalse()
    {
        Span<byte> firmwareDateTimeStruct = stackalloc byte[8];

        bool decoded = Dct3FirmwareRuntimeHooks.TryDecodeNitzDateTimeStruct(
            [0x62, 0x00, 0x60, 0x51, 0x02, 0x84],
            firmwareDateTimeStruct,
            out _);

        Assert.False(decoded);
    }

    [Fact]
    public void TryDecodeNitzDateTimeStruct_InvalidBcdNibble_ReturnsFalse()
    {
        Span<byte> firmwareDateTimeStruct = stackalloc byte[8];

        bool decoded = Dct3FirmwareRuntimeHooks.TryDecodeNitzDateTimeStruct(
            [0x6A, 0x70, 0x60, 0x51, 0x02, 0x84],
            firmwareDateTimeStruct,
            out _);

        Assert.False(decoded);
    }

    private static byte[] BuildNitzHookFirmware(
        uint dateTimeZeroAddress,
        uint calcTimestampAddress,
        uint setTimestampAddress,
        uint setTimestampInvalidLiteralAddress,
        uint dispatcherAddress,
        uint ignoredMessageHandlerAddress,
        uint getTimestampAddress,
        uint clockStateAddress,
        uint ccontElapsedSourceAddress,
        uint[] dispatcherEntries,
        uint ccontRegisterCacheAddress = 0x11A11C)
    {
        byte[] flash = new byte[0x200000];
        Array.Fill(flash, (byte)0xFF);

        WriteBytes(flash, dateTimeZeroAddress, [0x08, 0x02, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00]);
        WriteBytes(flash, calcTimestampAddress,
        [
            0xB5, 0x10, 0x79, 0x0B, 0x79, 0x02, 0x1A, 0x9B,
            0x22, 0x3C, 0x43, 0x5A, 0x79, 0x4B, 0x18, 0x9B,
        ]);

        WriteSetTimestampSignature(flash, setTimestampAddress, setTimestampInvalidLiteralAddress);
        WriteGetTimestampShape(flash, getTimestampAddress, clockStateAddress);
        WriteCcontElapsedSourceShape(flash, ccontElapsedSourceAddress);
        WriteCcontRegisterCacheResolverShape(flash, ccontRegisterCacheAddress);
        WriteDispatcher(flash, dispatcherAddress, dispatcherEntries);
        WriteHalf(flash, ignoredMessageHandlerAddress, 0xBDF0);
        return flash;
    }

    private static byte[] BuildIdleYieldHookFirmware(
        uint loopFunctionAddress,
        uint loopPrefixAddress,
        uint r6LoadAddress,
        uint r6LiteralAddress,
        uint aliveFlagAddress,
        uint fiqClearAddress,
        uint fiqClearLiteralAddress)
    {
        byte[] flash = new byte[0x200000];
        Array.Fill(flash, (byte)0xFF);

        WriteHalf(flash, loopFunctionAddress, 0xB5F0);
        WriteLiteralLoad(flash, r6LoadAddress, register: 6, r6LiteralAddress);
        WriteWord(flash, r6LiteralAddress, aliveFlagAddress);
        WriteBytes(flash, loopPrefixAddress,
        [
            0x46, 0x48, // mov r0, sb
            0x70, 0x30, // strb r0, [r6]
            0x48, 0x31, // ldr r0, [pc, #0xc4]
            0x70, 0x07, // strb r7, [r0]
            0x46, 0x40, // mov r0, r8
            0x73, 0x28, // strb r0, [r5, #0xc]
            0x78, 0x30, // ldrb r0, [r6]
            0x28, 0x00, // cmp r0, #0
            0xD0, 0xBE, // beq
            0xE7, 0xFB, // b loop
        ]);

        WriteBytes(flash, fiqClearAddress,
        [
            0xB5, 0x10, // push {r4, lr}
            0x49, 0x2A, // ldr r1, [pc, #0xa8]
            0x20, 0x00, // movs r0, #0
            0x70, 0x08, // strb r0, [r1]
        ]);
        WriteWord(flash, fiqClearLiteralAddress, aliveFlagAddress);
        return flash;
    }

    private static void WriteSetTimestampSignature(
        byte[] flash,
        uint setTimestampAddress,
        uint invalidLiteralAddress)
    {
        uint ldrAddress = setTimestampAddress + 4;
        uint pcRelativeBase = (ldrAddress + 4) & ~3u;
        uint imm = (invalidLiteralAddress - pcRelativeBase) / 4;

        WriteBytes(flash, setTimestampAddress, [0xB5, 0x10, 0x1C, 0x04]);
        WriteHalf(flash, ldrAddress, (ushort)(0x4800 | imm));
        WriteBytes(flash, setTimestampAddress + 6, [0x42, 0x84, 0xD0, 0x1F]);
        WriteHalf(flash, setTimestampAddress + 0x50, 0xBD10);
        WriteWord(flash, invalidLiteralAddress, 0x7FFFFFFF);
    }

    private static void WriteCcontRegisterCacheResolverShape(byte[] flash, uint ccontRegisterCacheAddress)
    {
        const uint configAddress = 0x31C48C;
        const uint literalTableAddress = 0x2DA340;

        WriteBytes(flash, configAddress,
        [
            0xFF, 0xFF, 0x10, 0x20, 0x18, 0xFF, 0xFF, 0xFF,
            0xFF, 0x38, 0x40, 0x48, 0x50, 0x58, 0x60, 0x68,
        ]);
        WriteWord(flash, literalTableAddress, configAddress);
        WriteWord(flash, literalTableAddress + 4, 0x00020000);
        WriteWord(flash, literalTableAddress + 8, ccontRegisterCacheAddress);
    }

    private static void WriteCcontElapsedSourceShape(byte[] flash, uint address)
    {
        WriteBytes(flash, address,
        [
            0xB5, 0xF0, // push {r4, r5, r6, r7, lr}
            0x48, 0x65, // ldr r0, [pc, ...]
            0xF7, 0xFF, 0xFF, 0x0E, // bl register-field helper
            0x28, 0x3F, // cmp r0, #0x3f
            0xD0, 0x31, // beq invalid
            0x26, 0x03, // movs r6, #3
            0x48, 0x63, // ldr r0, [pc, ...]
            0xF7, 0xFF, 0xFF, 0x08, // bl register-field helper
            0x21, 0xE1, // movs r1, #0xe1
            0x01, 0x0C, // lsls r4, r1, #4
            0x43, 0x44, // muls r4, r0, r4
            0x48, 0x61, // ldr r0, [pc, ...]
            0xF7, 0xFF, 0xFF, 0x02, // bl register-field helper
            0x25, 0x3C, // movs r5, #0x3c
            0x43, 0x45, // muls r5, r0, r5
        ]);

        WriteBytes(flash, address + 0x66,
        [
            0x42, 0x84, // cmp r4, r0
            0xD0, 0x03, // beq stable
            0x3E, 0x01, // subs r6, #1
            0x2E, 0x00, // cmp r6, #0
            0xD1, 0xCE, // bne retry
            0x24, 0xFF, // movs r4, #0xff
            0x1C, 0x20, // adds r0, r4, #0
            0xBD, 0xF0, // pop {r4, r5, r6, r7, pc}
        ]);
    }

    private static void WriteGetTimestampShape(byte[] flash, uint address, uint clockStateAddress)
    {
        uint clockStateLiteralAddress = address + 0xC0;

        WriteHalf(flash, address, 0xB510);
        WriteHalf(flash, address + 2, 0xB082);
        WriteLiteralLoad(flash, address + 4, register: 0, address + 0xC4);
        WriteHalf(flash, address + 6, 0x2100);
        WriteHalf(flash, address + 8, 0xF000);
        WriteHalf(flash, address + 10, 0xF800);
        WriteLiteralLoad(flash, address + 12, register: 0, address + 0xC8);
        WriteHalf(flash, address + 14, 0x7800);
        WriteHalf(flash, address + 16, 0x282A);
        WriteHalf(flash, address + 18, 0xD10A);
        WriteLiteralLoad(flash, address + 20, register: 4, clockStateLiteralAddress);
        WriteHalf(flash, address + 22, 0x7960);
        WriteHalf(flash, address + 24, 0x0941);
        WriteHalf(flash, address + 26, 0xD233);
        WriteHalf(flash, address + 28, 0x0900);
        WriteHalf(flash, address + 30, 0xD22B);
        WriteHalf(flash, address + 32, 0xF000);
        WriteHalf(flash, address + 34, 0xF800);
        WriteHalf(flash, address + 0x94, 0xB002);
        WriteHalf(flash, address + 0x96, 0xBD10);
        WriteWord(flash, clockStateLiteralAddress, clockStateAddress);
    }

    private static void WriteLiteralLoad(byte[] flash, uint address, int register, uint literalAddress)
    {
        uint pcRelativeBase = (address + 4) & ~3u;
        uint imm = (literalAddress - pcRelativeBase) / 4;
        WriteHalf(flash, address, (ushort)(0x4800u | ((uint)register << 8) | imm));
    }

    private static void WriteDispatcher(byte[] flash, uint dispatcherAddress, uint[] entries)
    {
        WriteBytes(flash, dispatcherAddress, [0xA1, 0x01, 0x00, 0x80, 0x58, 0x08, 0x46, 0x87]);

        for (int i = 0; i < entries.Length; i++)
        {
            WriteWord(flash, dispatcherAddress + 8u + (uint)i * 4u, entries[i]);
        }
    }

    private static void WriteBytes(byte[] flash, uint address, ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(flash.AsSpan((int)(address - Dct3Machine.FlashBase)));
    }

    private static void WriteHalf(byte[] flash, uint address, ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(flash.AsSpan((int)(address - Dct3Machine.FlashBase), 2), value);
    }

    private static void WriteWord(byte[] flash, uint address, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(flash.AsSpan((int)(address - Dct3Machine.FlashBase), 4), value);
    }
}
