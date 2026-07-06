using System.Buffers.Binary;
using Noks.Dct3.Messaging;
using Noks.Dct3.Radio;

namespace Noks.Dct3.Tests;

public sealed class DspTests
{
    [Theory]
    [InlineData(0x000, 0u, 2, false)]
    [InlineData(0x0A4, 1u, 2, true)]
    [InlineData(0x0E4, 1u, 1, true)]
    [InlineData(0x0FE, 0u, 2, true)]
    [InlineData(0x0FE, 1u, 2, false)]
    [InlineData(0x1CA, 0u, 2, true)]
    public void SharedWriteObservation_OnlyAcceptsMailboxAndHandshakeEvents(
        uint offset,
        uint value,
        int size,
        bool expected)
    {
        Assert.Equal(expected, Dsp.ObservesSharedWrite(offset, value, size));
    }

    [Fact]
    public void HostInterruptObservation_OnlyAcceptsAssertedMailboxWork()
    {
        byte[] sharedRam = new byte[0x200];
        Assert.False(Dsp.ObservesHostInterrupt(sharedRam));

        Write16(sharedRam, 0xE0, 1);
        Assert.True(Dsp.ObservesHostInterrupt(sharedRam));
        Write16(sharedRam, 0xE0, 0);

        Write16(sharedRam, 0x0A4, 1);
        Assert.True(Dsp.ObservesHostInterrupt(sharedRam));
    }

    [Fact]
    public void HostInterrupt_LatchesCompleteToneMailboxBeforeAcknowledgingDoorbell()
    {
        byte[] sharedRam = new byte[0x200];
        Dsp dsp = new(sharedRam, null);
        dsp.SetRunning(true);
        Write16(sharedRam, 0x0AC, 0x00E1);
        Write16(sharedRam, 0x0AE, 1209 * 4);
        Write16(sharedRam, 0x0B0, 697 * 4);
        Write16(sharedRam, 0x0B6, 0x4000);
        Write16(sharedRam, 0x0BA, 0);
        Write16(sharedRam, 0x0E0, 1);

        dsp.OnHostInterrupt();

        Assert.Equal(
            new DspToneState(0x00E1, 1209 * 4, 697 * 4, 0x4000, 0),
            dsp.ToneState);
        Assert.True(dsp.ToneState.Audible);
        Assert.Equal(1209, dsp.ToneState.Oscillator1Hz);
        Assert.Equal(697, dsp.ToneState.Oscillator2Hz);
        Assert.Equal(0, Read16(sharedRam, 0x0E0));

        Write16(sharedRam, 0x0AE, 900 * 4);
        Assert.Equal(1209, dsp.ToneState.Oscillator1Hz);

        Write16(sharedRam, 0x0AC, 0x00E0);
        Write16(sharedRam, 0x0E0, 1);
        dsp.OnHostInterrupt();

        Assert.False(dsp.ToneState.Audible);
    }


    [Fact]
    public void CellSelectionWithoutPendingWork_HasNoPollingDeadline()
    {
        Dsp dsp = new(new byte[0x200], null);
        Assert.Equal(DspExecutionState.Stopped, dsp.ExecutionState);

        dsp.SyncCycle(100);
        dsp.SetRunning(true);

        Assert.Equal(DspExecutionState.CellSelection, dsp.ExecutionState);
        Assert.Equal(long.MaxValue, dsp.NextWakeCycle(100));

        dsp.AdvanceTo(100);

        Assert.Equal(long.MaxValue, dsp.NextWakeCycle(100));
    }

    [Fact]
    public void IncomingPagingTimeout_DoesNotStayScheduledAfterDedicatedChannelStarts()
    {
        const long pagingStartedCycles = 1234;

        Assert.NotEqual(
            long.MaxValue,
            Dsp.NextIncomingPagingTimeoutCycle(pagingStartedCycles, dedicatedChannelActive: false));
        Assert.Equal(
            long.MaxValue,
            Dsp.NextIncomingPagingTimeoutCycle(pagingStartedCycles, dedicatedChannelActive: true));
    }

    [Fact]
    public void QueueIncomingSmartMessage_LargePayloadQueuesEverySmsPart()
    {
        Dsp dsp = new(new byte[0x200], null);

        dsp.QueueIncomingSmartMessage("5551234", NokiaSmartMessagingRingtone.DestinationPort, new byte[300]);

        Assert.Equal(3, dsp.PendingIncomingServiceCount);
    }

    [Fact]
    public void MdiSend_InvalidQueueIndex_FlushesQueue()
    {
        byte[] sharedRam = new byte[0x200];
        Dsp dsp = new(sharedRam, null);
        dsp.SetRunning(true);
        Write16(sharedRam, 0x0A4, 0xFFFF);

        dsp.OnSharedWrite(0x0A4, 0xFFFF, size: 2);

        Assert.Equal(0xFFFF, Read16(sharedRam, 0x0A6));
    }

    [Fact]
    public void MdiSend_CorruptPacketCycle_IsBoundedAndFlushed()
    {
        byte[] sharedRam = new byte[0x200];
        for (int word = 0; word < 82; word++)
        {
            sharedRam[word * 2] = 2;
            sharedRam[word * 2 + 1] = 0xFF;
        }

        Dsp dsp = new(sharedRam, null);
        dsp.SetRunning(true);
        Write16(sharedRam, 0x0A4, 1);

        dsp.OnSharedWrite(0x0A4, 1, size: 2);

        Assert.Equal(1, Read16(sharedRam, 0x0A6));
    }

    [Fact]
    public void ChannelConfigure_PostsRequestedChannelChangedConfirmFlag()
    {
        byte[] sharedRam = new byte[0x200];
        Dsp dsp = new(sharedRam, null);
        dsp.SetRunning(true);

        WriteMdiSend(
            sharedRam,
            type: 0x02,
            [
                0x04, 0x00, 0x00, 0x02, 0x00, 0x00, 0x01, 0x11, 0x60, 0x00,
                0x03, 0xEC, 0x00, 0x00, 0x00, 0x00, 0x00, 0x29, 0x70, 0x00,
            ]);
        dsp.OnSharedWrite(0x0A4, Read16(sharedRam, 0x0A4), size: 2);

        Assert.Equal(0x82, Read16(sharedRam, 0x1C8));
        Assert.Equal(0x01, sharedRam[0x100]);
        Assert.Equal(0x89, sharedRam[0x101]);
        Assert.Equal(0x01, sharedRam[0x102]);
    }

    [Fact]
    public void CommonControlChannelConfigure_PostsServingCellSchAfterConfirm()
    {
        byte[] sharedRam = new byte[0x200];
        Dsp dsp = new(sharedRam, null);
        dsp.SetRunning(true);

        WriteMdiSend(
            sharedRam,
            type: 0x02,
            [
                0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x50, 0x50, 0x00,
                0x03, 0xEC, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x26,
            ]);
        dsp.OnSharedWrite(0x0A4, Read16(sharedRam, 0x0A4), size: 2);
        AcknowledgeMdiRcv(sharedRam, dsp);
        AcknowledgeMdiRcv(sharedRam, dsp);
        AcknowledgeMdiRcv(sharedRam, dsp);
        AcknowledgeMdiRcv(sharedRam, dsp);

        WriteMdiSend(
            sharedRam,
            type: 0x02,
            [
                0x04, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x11, 0x60, 0x00,
                0x03, 0xEC, 0x00, 0x00, 0x00, 0x00, 0x00, 0x29, 0x70, 0x00,
            ]);
        dsp.OnSharedWrite(0x0A4, Read16(sharedRam, 0x0A4), size: 2);

        Assert.Equal(0x89, ReadMdiRcvByte(sharedRam, 1));
        Assert.Equal(0x00, ReadMdiRcvByte(sharedRam, 2));

        AcknowledgeMdiRcv(sharedRam, dsp);

        Assert.Equal(0x80, ReadMdiRcvByte(sharedRam, 1));
        Assert.Equal(0x40, ReadMdiRcvByte(sharedRam, 2));
    }

    [Fact]
    public void CommonControlChannelConfigure_UsesPagingImsiHomePlmnInSystemInformation()
    {
        byte[] sharedRam = new byte[0x200];
        Dsp dsp = new(sharedRam, null, pagingImsi: "001010000000001");
        dsp.SetRunning(true);

        WriteMdiSend(
            sharedRam,
            type: 0x02,
            [
                0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x50, 0x50, 0x00,
                0x00, 0x58, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x26,
            ]);
        dsp.OnSharedWrite(0x0A4, Read16(sharedRam, 0x0A4), size: 2);
        AcknowledgeMdiRcv(sharedRam, dsp);
        AcknowledgeMdiRcv(sharedRam, dsp);

        Assert.Equal(0x80, ReadMdiRcvByte(sharedRam, 1));
        Assert.Equal(0x50, ReadMdiRcvByte(sharedRam, 2));
        Assert.Equal(0x49, ReadMdiRcvByte(sharedRam, 0x0C));
        Assert.Equal(0x00, ReadMdiRcvByte(sharedRam, 0x11));
        Assert.Equal(0xF1, ReadMdiRcvByte(sharedRam, 0x12));
        Assert.Equal(0x10, ReadMdiRcvByte(sharedRam, 0x13));
    }

    [Fact]
    public void MsiRequest_PostsServingCellRssiResult()
    {
        byte[] sharedRam = new byte[0x200];
        Dsp dsp = new(sharedRam, null);
        dsp.SetRunning(true);

        WriteMdiSend(sharedRam, type: 0x46, [0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00]);
        dsp.OnSharedWrite(0x0A4, Read16(sharedRam, 0x0A4), size: 2);

        Assert.Equal(0x84, Read16(sharedRam, 0x1C8));
        Assert.Equal(0x06, sharedRam[0x100]);
        Assert.Equal(0x83, sharedRam[0x101]);
        Assert.Equal(0xD0, sharedRam[0x104]);
        Assert.Equal(0xD0, sharedRam[0x105]);
        Assert.Equal(0x03, sharedRam[0x106]);
        Assert.Equal(0xEC, sharedRam[0x107]);
    }

    [Fact]
    public void FacadeNetworkAvailability_DrivesNoSignalAndCellDiscovery()
    {
        byte[] sharedRam = new byte[0x200];
        Dsp dsp = new(sharedRam, null);
        dsp.SetFacadeNetworkAvailable(false);
        dsp.SetRssiMeasurement(Dsp.DefaultRssiMeasurement);
        dsp.SetRunning(true);

        Assert.False(dsp.FacadeNetworkAvailable);
        Assert.False(dsp.RegisteredOnFacadeNetwork);
        Assert.Equal(Dsp.NoSignalRssiMeasurement, dsp.RssiMeasurement);

        WriteMdiSend(sharedRam, type: 0x46, [0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00]);
        dsp.OnSharedWrite(0x0A4, Read16(sharedRam, 0x0A4), size: 2);

        Assert.Equal(Dsp.NoSignalRssiMeasurement, sharedRam[0x104]);
        Assert.Equal(Dsp.NoSignalRssiMeasurement, sharedRam[0x105]);
        AcknowledgeMdiRcv(sharedRam, dsp);

        WriteMdiSend(sharedRam, type: 0x56, [0x03, 0xEC]);
        dsp.OnSharedWrite(0x0A4, Read16(sharedRam, 0x0A4), size: 2);

        Assert.Equal(Read16(sharedRam, 0x1CA), Read16(sharedRam, 0x1C8));

        dsp.SetFacadeNetworkAvailable(true);

        Assert.True(dsp.FacadeNetworkAvailable);
        Assert.Equal(Dsp.DefaultRssiMeasurement, dsp.RssiMeasurement);
        Assert.Equal(Dsp.DefaultRssiMeasurement, ReadMdiRcvByte(sharedRam, 4));
        AcknowledgeMdiRcv(sharedRam, dsp);

        Assert.Equal(0x80, ReadMdiRcvByte(sharedRam, 1));
        Assert.Equal(0x40, ReadMdiRcvByte(sharedRam, 2));
    }

    [Fact]
    public void FacadeNetworkLoss_InvalidatesQueuedCellDiscoveryBeforeNoSignal()
    {
        byte[] sharedRam = new byte[0x200];
        Dsp dsp = new(sharedRam, null);
        dsp.SetRunning(true);
        Write16(sharedRam, 0x1C8, 0x80);
        Write16(sharedRam, 0x1CA, 0x80);

        WriteMdiSend(sharedRam, type: 0x56, [0x03, 0xEC]);
        dsp.OnSharedWrite(0x0A4, Read16(sharedRam, 0x0A4), size: 2);
        Assert.Equal(0x80, ReadMdiRcvByte(sharedRam, 1));
        Assert.Equal(0x40, ReadMdiRcvByte(sharedRam, 2));
        ushort postedHead = Read16(sharedRam, 0x1CA);

        dsp.SetFacadeNetworkAvailable(false);

        Assert.False(dsp.FacadeNetworkAvailable);
        Assert.False(dsp.RegisteredOnFacadeNetwork);
        ushort replacementHead = Read16(sharedRam, 0x1CA);
        ushort replacementTail = Read16(sharedRam, 0x1C8);
        Assert.Equal(postedHead, replacementHead);
        Assert.InRange(replacementHead, (ushort)0x80, (ushort)0xE3);
        Assert.InRange(replacementTail, (ushort)0x80, (ushort)0xE3);
        Assert.Equal((ushort)0x84, replacementTail);
        Assert.Equal(0x83, ReadMdiRcvByte(sharedRam, 1));
        Assert.Equal(Dsp.NoSignalRssiMeasurement, ReadMdiRcvByte(sharedRam, 4));
        AcknowledgeMdiRcv(sharedRam, dsp);
        Assert.Equal(Read16(sharedRam, 0x1CA), Read16(sharedRam, 0x1C8));
    }

    [Fact]
    public void FacadeNetworkRestore_ReplacesPostedNoSignalWithOnlineRssi()
    {
        byte[] sharedRam = new byte[0x200];
        Dsp dsp = new(sharedRam, null);
        dsp.SetRunning(true);
        Write16(sharedRam, 0x1C8, 0x80);
        Write16(sharedRam, 0x1CA, 0x80);

        dsp.SetFacadeNetworkAvailable(false);

        ushort noSignalHead = Read16(sharedRam, 0x1CA);
        ushort noSignalTail = Read16(sharedRam, 0x1C8);
        Assert.InRange(noSignalHead, (ushort)0x80, (ushort)0xE3);
        Assert.InRange(noSignalTail, (ushort)0x80, (ushort)0xE3);
        Assert.NotEqual(noSignalHead, noSignalTail);
        Assert.Equal(0x83, ReadMdiRcvByte(sharedRam, 1));
        Assert.Equal(Dsp.NoSignalRssiMeasurement, ReadMdiRcvByte(sharedRam, 4));

        dsp.SetFacadeNetworkAvailable(true);

        ushort onlineHead = Read16(sharedRam, 0x1CA);
        ushort onlineTail = Read16(sharedRam, 0x1C8);
        Assert.Equal(noSignalHead, onlineHead);
        Assert.InRange(onlineTail, (ushort)0x80, (ushort)0xE3);
        Assert.Equal((ushort)0x84, onlineTail);
        Assert.Equal(0x83, ReadMdiRcvByte(sharedRam, 1));
        Assert.Equal(Dsp.DefaultRssiMeasurement, ReadMdiRcvByte(sharedRam, 4));
        AcknowledgeMdiRcv(sharedRam, dsp);
        Assert.Equal(Read16(sharedRam, 0x1CA), Read16(sharedRam, 0x1C8));
    }

    [Fact]
    public void FacadeNetworkLoss_PreservesPostedChannelChangedConfirmBeforeNoSignal()
    {
        byte[] sharedRam = new byte[0x200];
        Dsp dsp = new(sharedRam, null);
        dsp.SetRunning(true);
        Write16(sharedRam, 0x1C8, 0x80);
        Write16(sharedRam, 0x1CA, 0x80);

        WriteMdiSend(
            sharedRam,
            type: 0x02,
            [
                0x04, 0x00, 0x00, 0x02, 0x00, 0x00, 0x01, 0x11, 0x60, 0x00,
                0x03, 0xEC, 0x00, 0x00, 0x00, 0x00, 0x00, 0x29, 0x70, 0x00,
            ]);
        dsp.OnSharedWrite(0x0A4, Read16(sharedRam, 0x0A4), size: 2);
        Assert.Equal(0x89, ReadMdiRcvByte(sharedRam, 1));
        ushort confirmHead = Read16(sharedRam, 0x1CA);
        ushort confirmTail = Read16(sharedRam, 0x1C8);

        dsp.SetFacadeNetworkAvailable(false);

        Assert.Equal(confirmHead, Read16(sharedRam, 0x1CA));
        Assert.Equal(confirmTail, Read16(sharedRam, 0x1C8));
        Assert.Equal(0x89, ReadMdiRcvByte(sharedRam, 1));
        AcknowledgeMdiRcv(sharedRam, dsp);
        Assert.Equal(0x83, ReadMdiRcvByte(sharedRam, 1));
        Assert.Equal(Dsp.NoSignalRssiMeasurement, ReadMdiRcvByte(sharedRam, 4));
    }

    [Fact]
    public void FacadeNetworkReapply_AfterResetReplacesPostedRadioPacket()
    {
        byte[] sharedRam = new byte[0x200];
        Dsp dsp = new(sharedRam, null);
        dsp.SetRunning(true);
        Write16(sharedRam, 0x1C8, 0x80);
        Write16(sharedRam, 0x1CA, 0x80);

        WriteMdiSend(sharedRam, type: 0x56, [0x03, 0xEC]);
        dsp.OnSharedWrite(0x0A4, Read16(sharedRam, 0x0A4), size: 2);
        Assert.Equal(0x80, ReadMdiRcvByte(sharedRam, 1));
        Assert.Equal(0x40, ReadMdiRcvByte(sharedRam, 2));
        ushort postedHead = Read16(sharedRam, 0x1CA);

        dsp.Reset();
        dsp.SetRunning(true);
        dsp.ReapplyFacadeNetworkAvailability();

        Assert.Equal(postedHead, Read16(sharedRam, 0x1CA));
        Assert.Equal((ushort)0x84, Read16(sharedRam, 0x1C8));
        Assert.Equal(0x83, ReadMdiRcvByte(sharedRam, 1));
        Assert.Equal(Dsp.DefaultRssiMeasurement, ReadMdiRcvByte(sharedRam, 4));
        AcknowledgeMdiRcv(sharedRam, dsp);
        Assert.Equal(0x80, ReadMdiRcvByte(sharedRam, 1));
        Assert.Equal(0x40, ReadMdiRcvByte(sharedRam, 2));
    }

    private static void WriteMdiSend(byte[] sharedRam, byte type, ReadOnlySpan<byte> payload)
    {
        ushort tail = Read16(sharedRam, 0x0A4);
        int byteOffset = tail * 2;
        WriteMdiSendByte(sharedRam, byteOffset, (byte)payload.Length);
        WriteMdiSendByte(sharedRam, byteOffset + 1, type);

        for (int i = 0; i < payload.Length; i++)
        {
            WriteMdiSendByte(sharedRam, byteOffset + 2 + i, payload[i]);
        }

        if ((payload.Length & 1) != 0)
        {
            WriteMdiSendByte(sharedRam, byteOffset + 2 + payload.Length, 0);
        }

        int totalBytes = 2 + payload.Length + (payload.Length & 1);
        Write16(sharedRam, 0x0A4, (ushort)((tail + totalBytes / 2) % 82));
    }

    private static void WriteMdiSendByte(byte[] sharedRam, int offset, byte value) =>
        sharedRam[offset % 0xA4] = value;

    private static ushort Read16(byte[] sharedRam, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(sharedRam.AsSpan(offset, 2));

    private static byte ReadMdiRcvByte(byte[] sharedRam, int index)
    {
        ushort head = Read16(sharedRam, 0x1CA);
        return sharedRam[0x100 + ((head - 0x80) * 2 + index) % 200];
    }

    private static void AcknowledgeMdiRcv(byte[] sharedRam, Dsp dsp)
    {
        ushort tail = Read16(sharedRam, 0x1C8);
        Write16(sharedRam, 0x1CA, tail);
        dsp.OnSharedWrite(0x1CA, tail, size: 2);
    }

    private static void Write16(byte[] sharedRam, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(sharedRam.AsSpan(offset, 2), value);
}
