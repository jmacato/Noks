using Noks.Dct3.Core;
using Noks.Dct3.Memory;
using Noks.Dct3.Sim;
using Noks.Dct3.State;
namespace Noks.Dct3.Tests;

public sealed class Dct3PersistenceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MachinePersistence_IgnoresLegacySnapshots(int version)
    {
        byte[] image = new byte[0x200000];
        Array.Fill(image, (byte)0xFF);
        Dct3PersistenceSnapshot legacy = new(
            Version: version,
            [new FlashOverlayBlock(0x1D0000, [0x12, 0x34])],
            []);

        Dct3Machine machine = new(image, persistenceSnapshot: legacy);

        Assert.Equal(0xFFFF, machine.Flash.ReadDevice(0x1D0000));
        Assert.Empty(machine.CreatePersistenceSnapshot().FlashBlocks);
    }

    [Fact]
    public void FlashOverlay_PreservesProgrammedBytesOverBaseImage()
    {
        byte[] image = new byte[0x20000];
        Array.Fill(image, (byte)0xFF);
        IntelFlash16 flash = new(image, image.Length, trace: null);
        flash.CapturePersistenceBaseline();

        flash.WriteDevice(0, 0x40);
        flash.WriteDevice(0, 0x1234);

        FlashOverlayBlock[] overlay = flash.CreateOverlay();

        Assert.Single(overlay);
        Assert.Equal(0, overlay[0].Offset);
        Assert.Equal(0x12, overlay[0].Data[0]);
        Assert.Equal(0x34, overlay[0].Data[1]);

        IntelFlash16 restored = new(image, image.Length, trace: null);
        restored.CapturePersistenceBaseline();
        restored.ApplyOverlay(overlay);

        Assert.Equal(0x1234, restored.ReadDevice(0));
    }

    [Fact]
    public void MachinePersistence_DoesNotReloadRuntimeFlashOverlays()
    {
        byte[] image = new byte[0x200000];
        Array.Fill(image, (byte)0xFF);
        Dct3PersistenceSnapshot snapshot = new(
            Dct3PersistenceSnapshot.CurrentVersion,
            [
                new FlashOverlayBlock(0, [0x12, 0x34]),
                new FlashOverlayBlock(0x1D0000, [0x56, 0x78]),
            ],
            []);

        Dct3Machine machine = new(image, persistenceSnapshot: snapshot);

        Assert.Equal(0xFFFF, machine.Flash.ReadDevice(0));
        Assert.Equal(0xFFFF, machine.Flash.ReadDevice(0x1D0000));
        Assert.Empty(machine.CreatePersistenceSnapshot().FlashBlocks);
    }

    [Fact]
    public void MachinePersistence_DoesNotPersistRuntimeFlashWrites()
    {
        byte[] image = new byte[0x200000];
        Array.Fill(image, (byte)0xFF);
        Dct3Machine machine = new(image);

        machine.Flash.WriteDevice(0, 0x40);
        machine.Flash.WriteDevice(0, 0x1234);
        machine.Flash.WriteDevice(0x1E0000, 0x40);
        machine.Flash.WriteDevice(0x1E0000, 0x5678);

        Assert.Empty(machine.CreatePersistenceSnapshot().FlashBlocks);
    }

    [Fact]
    public void MachinePersistence_ReportsRestoredSimOverlayToConstructionObserver()
    {
        byte[] adn = Enumerable.Repeat(
            (byte)0xFF,
            SimCard.AdnRecordCount * SimPhonebookCodec.RecordLength).ToArray();
        byte[] contact = SimPhonebookCodec.Encode("Receiver", "1234567890123");
        contact.CopyTo(adn, 0);
        Dct3PersistenceSnapshot snapshot = new(
            Dct3PersistenceSnapshot.CurrentVersion,
            [],
            [new SimFileOverlay(0x7F10, 0x6F3A, adn)]);
        List<SimMutation> mutations = [];

        _ = new Dct3Machine(
            new byte[0x200000],
            persistenceSnapshot: snapshot,
            simMutation: mutations.Add);

        SimMutation restoredAdn = Assert.Single(mutations, mutation =>
            mutation.ParentFileId == 0x7F10 &&
            mutation.FileId == 0x6F3A &&
            mutation.RecordNumber == 0 &&
            mutation.Origin == SimMutationOrigin.PersistenceRestore);
        Assert.Equal(contact, restoredAdn.NewValue.AsSpan(0, contact.Length).ToArray());
    }

    [Fact]
    public void SimOverlay_PreservesUpdatedSmsRecordOverDefaultFilesystem()
    {
        SimCard sim = new(trace: null);
        SelectSmsStorage(sim);
        byte[] record = Enumerable.Repeat((byte)0xFF, 176).ToArray();
        record[0] = 0x03;
        record[1] = 0x06;
        record[2] = 0x91;

        _ = SendApdu(sim, [0xA0, 0xDC, 0x01, 0x04, 0xB0, .. record]);

        SimFileOverlay[] overlay = sim.CreateOverlay();

        SimFileOverlay sms = Assert.Single(overlay);
        Assert.Equal(0x7F10, sms.Parent);
        Assert.Equal(0x6F3C, sms.Id);

        SimCard restored = new(trace: null);
        restored.ApplyOverlay(overlay);
        SelectSmsStorage(restored);

        byte[] readResponse = SendApdu(restored, 0xA0, 0xB2, 0x01, 0x04, 0xB0);
        Assert.Equal(record, readResponse.AsSpan(1, 176).ToArray());
    }

    [Fact]
    public void SimOverlay_DoesNotPersistVolatileLocationInformation()
    {
        byte[] loci = Convert.FromHexString("112233445566778899AA00");
        SimCard sim = new(trace: null);
        SelectGsmFile(sim, 0x6F7E);

        _ = SendApdu(sim, [0xA0, 0xD6, 0x00, 0x00, 0x0B, .. loci]);

        Assert.Empty(sim.CreateOverlay());

        SimCard restored = new(trace: null);
        restored.ApplyOverlay([new SimFileOverlay(0x7F20, 0x6F7E, loci)]);
        SelectGsmFile(restored, 0x6F7E);

        byte[] readResponse = SendApdu(restored, 0xA0, 0xB0, 0x00, 0x00, 0x0B);
        Assert.Equal(Convert.FromHexString("FFFFFFFF99999999F9FF01"), readResponse.AsSpan(1, 11).ToArray());
    }

    private static byte[] SendApdu(SimCard sim, params byte[] bytes)
    {
        List<byte> responseBytes = [];

        foreach (byte value in bytes)
        {
            SimCardResponse? response = sim.Transmit(value);

            if (response is not null)
            {
                responseBytes.AddRange(response.Value.Data);
            }
        }

        return responseBytes.ToArray();
    }

    private static void SelectSmsStorage(SimCard sim)
    {
        SendApdu(sim, 0xA0, 0xA4, 0x00, 0x00, 0x02, 0x7F, 0x10);
        SendApdu(sim, 0xA0, 0xA4, 0x00, 0x00, 0x02, 0x6F, 0x3C);
    }

    private static void SelectGsmFile(SimCard sim, ushort fileId)
    {
        SendApdu(sim, 0xA0, 0xA4, 0x00, 0x00, 0x02, 0x7F, 0x20);
        SendApdu(sim, 0xA0, 0xA4, 0x00, 0x00, 0x02, (byte)(fileId >> 8), (byte)fileId);
    }
}
