using Noks.Dct3.Core;
using Noks.Dct3.Firmware;
using Noks.Dct3.Messaging;

namespace Noks.Dct3.Tests;

public sealed class Dct3FirmwarePatchesTests
{
    [Fact]
    public void ApplyNhm5RussianLanguagePmmRepair_RewritesOnlyCurrentLanguageByte()
    {
        byte[] flash = new byte[0x200000];
        Array.Fill(flash, (byte)0xFF);
        byte[] settings = Nhm5RussianLanguageSettingsBlock();
        const int firstOffset = 0x3E069A - (int)Dct3Machine.FlashBase;
        const int secondOffset = 0x3E49A4 - (int)Dct3Machine.FlashBase;
        settings.CopyTo(flash, firstOffset);
        settings.CopyTo(flash, secondOffset);

        bool repaired = Dct3FirmwarePatches.ApplyNhm5RussianLanguagePmmRepair(flash);

        Assert.True(repaired);
        Assert.Equal(0x01, flash[firstOffset + 0x1A]);
        Assert.Equal(0x00, flash[firstOffset + 0x1B]);
        Assert.Equal(0x01, flash[secondOffset + 0x1A]);
        Assert.Equal(0x00, flash[secondOffset + 0x1B]);
        Assert.Equal(0x0F, flash[firstOffset + 0x08]);
        Assert.Equal(0x0F, flash[firstOffset + 0x11]);
    }

    [Fact]
    public void ApplyNhm5RussianLanguagePmmRepair_AlreadyEnglish_ReturnsFalse()
    {
        byte[] flash = new byte[0x200000];
        Array.Fill(flash, (byte)0xFF);
        byte[] settings = Nhm5RussianLanguageSettingsBlock();
        settings[0x1A] = 0x01;
        settings.CopyTo(flash, 0x3E069A - (int)Dct3Machine.FlashBase);

        bool repaired = Dct3FirmwarePatches.ApplyNhm5RussianLanguagePmmRepair(flash);

        Assert.False(repaired);
    }

    [Fact]
    public void ApplyStaleMaintenanceStatePmmRepair_ClearsFfPayload()
    {
        byte[] flash = new byte[0x200000];
        Array.Fill(flash, (byte)0xFF);
        int recordOffset = 0x1EA6D4;
        byte[] record = [0x05, 0x82, 0xFF, 0xFF, 0x04, 0x99, 0x04, 0x94];
        record.CopyTo(flash, recordOffset);

        bool repaired = Dct3FirmwarePatches.ApplyStaleMaintenanceStatePmmRepair(flash);

        Assert.True(repaired);
        Assert.Equal(0x00, flash[recordOffset + 2]);
        Assert.Equal(0xFF, flash[recordOffset + 3]);
    }

    [Fact]
    public void ApplyV418LongRingtoneBufferPatch_ExpandsAllFiveLimits()
    {
        byte[] flash = new byte[0x200000];
        Array.Fill(flash, (byte)0xFF);
        InstallV418LongRingtoneSignatures(flash);
        byte[] original = flash.ToArray();

        bool resolved = Dct3FirmwarePatches.TryResolveV418LongRingtoneBufferPatch(flash, out uint triggerPc);
        bool patched = Dct3FirmwarePatches.ApplyV418LongRingtoneBufferPatch(flash);

        Assert.True(resolved);
        Assert.Equal(0x2711F2u, triggerPc);
        Assert.True(patched);
        Assert.Equal([0x20, 0x10, 0x02, 0x00], flash[0x711EE..0x711F2]);
        Assert.Equal([0x20, 0x7E, 0x01, 0x40], flash[0x71252..0x71256]);
        Assert.Equal([0x20, 0x7F, 0x01, 0x40], flash[0x712D2..0x712D6]);
        Assert.Equal([0x20, 0x10, 0x02, 0x00], flash[0x97FAA..0x97FAE]);
        Assert.Equal([0x22, 0x7F, 0x01, 0x52], flash[0x97FBA..0x97FBE]);
        Assert.Equal(15, flash.Zip(original).Count(pair => pair.First != pair.Second));
        Assert.False(Dct3FirmwarePatches.ApplyV418LongRingtoneBufferPatch(flash));
    }

    [Fact]
    public void ApplyV418LongRingtoneBufferPatch_MismatchedFirmwareIsUntouched()
    {
        byte[] flash = new byte[0x200000];
        Array.Fill(flash, (byte)0xFF);
        InstallV418LongRingtoneSignatures(flash);
        flash[0x97FB8] = 0x00;
        byte[] original = flash.ToArray();

        bool resolved = Dct3FirmwarePatches.TryResolveV418LongRingtoneBufferPatch(flash, out uint triggerPc);
        bool patched = Dct3FirmwarePatches.ApplyV418LongRingtoneBufferPatch(flash);

        Assert.False(resolved);
        Assert.Equal(0u, triggerPc);
        Assert.False(patched);
        Assert.Equal(original, flash);
    }

    [Fact]
    public void V418LongRingtoneBufferPatch_IsDeferredUntilRingtoneParserRuns()
    {
        byte[] flash = new byte[0x200000];
        Array.Fill(flash, (byte)0xFF);
        InstallV418LongRingtoneSignatures(flash);
        Dct3Machine machine = new(flash, ccontWatchdogEnabled: false);

        Assert.Equal(0x3D, machine.Flash.Data[0x711F1]);
        machine.QueueIncomingSmartMessage(
            "5551234",
            NokiaSmartMessagingRingtone.DestinationPort,
            [0x00]);
        machine.ServicePendingPeripherals();

        Assert.Equal(0x3D, machine.Flash.Data[0x711F1]);
        machine.Cpu.SetGpr(15, 0x2711F2);
        machine.Step();

        Assert.Equal([0x20, 0x10, 0x02, 0x00], machine.Flash.Data[0x711EE..0x711F2]);
        Assert.Equal([0x22, 0x7F, 0x01, 0x52], machine.Flash.Data[0x97FBA..0x97FBE]);
    }

    private static byte[] Nhm5RussianLanguageSettingsBlock() =>
    [
        0x00, 0x01, 0x00, 0x01, 0x00, 0x02, 0x00, 0x01,
        0x0F, 0x00, 0x00, 0x01, 0x00, 0x00, 0x01, 0x00,
        0x01, 0x0F, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02,
        0x00, 0x00, 0x0F, 0x00, 0x00, 0x11, 0x00, 0x00,
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1E, 0x00,
        0x00, 0x01, 0xFF, 0xFF,
    ];

    private static void InstallV418LongRingtoneSignatures(byte[] flash)
    {
        byte[] parserAllocation = [0x61, 0x60, 0x20, 0xFF, 0x30, 0x3D, 0xF0, 0x16, 0xFF, 0x69];
        byte[] parserInitialCapacity = [0x81, 0x21, 0x20, 0xFF, 0x30, 0x21, 0x81, 0x60, 0x48, 0xBD];
        byte[] parserFinalCapacity = [0xFD, 0xB5, 0x20, 0xFF, 0x30, 0x2C, 0x81, 0x60, 0x48, 0xE3];
        byte[] receivedAllocation = [0x1C, 0x06, 0x20, 0xFF, 0x30, 0x2D, 0xF7, 0xF0, 0xF8, 0x8B];
        byte[] receivedCopy = [0x19, 0x89, 0x22, 0xFF, 0x32, 0x2D, 0xF0, 0x48, 0xFA, 0x4F];

        parserAllocation.CopyTo(flash, 0x711EC);
        parserInitialCapacity.CopyTo(flash, 0x71250);
        parserFinalCapacity.CopyTo(flash, 0x712D0);
        receivedAllocation.CopyTo(flash, 0x97FA8);
        receivedCopy.CopyTo(flash, 0x97FB8);
    }

}
