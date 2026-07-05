using System.Buffers.Binary;
using Noks.Dct3.Audio;
using Noks.Dct3.Core;
using Noks.Dct3.Messaging;
using Noks.Dct3.Sim;
using Noks.Dct3.State;

namespace Noks.Dct3.Radio;

public sealed class Dsp
{
    public const byte DefaultRssiMeasurement = 0xD0;
    public const byte NoSignalRssiMeasurement = 0x80;

    private const long BcchBroadcastPeriodCycles = Dct3Machine.CyclesPerSecond / 4;
    private const long ImmediateAssignmentDelayCycles = Dct3Machine.CyclesPerSecond / 10;
    private const long NmeasResultDelayCycles = Dct3Machine.CyclesPerSecond / 217;
    private const ushort DefaultNeighbourArfcn = 0x0001;
    private const int CyclesPerTdmaFrame = 60000;
    private const int Sdcch8Subchannel0UplinkFrameOffset = 15;
    private const long DedicatedBlockRequestPeriodCycles = 51L * CyclesPerTdmaFrame;
    private const long IncomingPagingRepeatCycles = GsmBlockCodec.BroadcastBsPaMfrms * DedicatedBlockRequestPeriodCycles;
    private const long IncomingPagingResponseTimeoutCycles = 10 * Dct3Machine.CyclesPerSecond;
    private const long IncomingServiceCooldownCycles = 10 * Dct3Machine.CyclesPerSecond;
    private const long MdiRcvPacketTimeoutCycles = 2 * Dct3Machine.CyclesPerSecond;
    private const long DedicatedPendingTimeoutCycles = 5 * Dct3Machine.CyclesPerSecond;
    private const int MaximumIncomingPagingBursts = 8;
    private const int FrameNumberModulus = 26 * 51 * 2048;
    private const int MdiSendBlockLayer2Offset = 2;
    private const int MdiSendQueueBytes = 0xA4;
    private const int MdiSendQueueWords = MdiSendQueueBytes / 2;

    private static readonly ushort[] InitData =
    [
        0x900F, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000,
        0x0000, 0x0000, 0xFF80, 0x0000, 0xFFFF, 0x0000, 0x0000, 0x0000, 0x0000,
    ];

    private static readonly ushort[] CodeblockSequence = [0x0014, 0x0001];

    private readonly byte[] sharedRam;
    private readonly IDct3Trace? trace;
    private readonly Queue<PendingMdiRcvPacket> pendingMdiRcv = new();
    private readonly Queue<DelayedMdiRcvPacket> delayedMdiRcv = new();
    private readonly Queue<PendingDedicatedDownlinkFrame> pendingDedicatedDownlinkFrames = new();
    private readonly Queue<IncomingGsmRequest> pendingIncomingRequests = new();
    private readonly LapdmLink lapdmLink;
    private readonly byte[] pagingRequestType1;
    private readonly byte[] sdcchPagingRequestType1;
    private readonly byte[] pagingFillRequestType1;
    private readonly byte[] locationAreaIdentity;
    private readonly int pagingGroupMultiframePhase;
    private readonly int pagingGroupFrameOffset;
    private IncomingGsmRequest? activeIncomingRequest;
    private long activeIncomingRequestStartedCycles = -1;
    private int activeIncomingPagingBursts;
    private bool activeIncomingRequestAnswered;
    private long nextIncomingServiceStartCycles = -1;
    private byte nextSmartMessageReference = 1;
    private byte pendingRandomAccessReference;
    private byte pendingRandomAccessT1Prime;
    private byte pendingRandomAccessT3;
    private byte pendingRandomAccessT2;
    private bool hasPendingRandomAccessReference;
    private byte dedicatedLogicalChannel;
    private byte dedicatedBsic;
    private ushort dedicatedArfcn;
    private long nextDedicatedDownlinkFillCycle = -1;
    private long nextDedicatedBlockRequestCycle = -1;
    private long nextIncomingPagingCycle = -1;
    private bool clearDedicatedChannelAfterNextDownlinkFrame;
    private bool suppressImsiPagingAfterRegistration;
    private bool running;
    private int exchanges;
    private bool statusPosted;
    private int blockIndex;
    private int simlBlockIndex;
    private ushort currentBlock;
    private ushort servingArfcn;
    private byte servingBsic;
    private ushort ccchArfcn;
    private byte ccchBsic;
    private ushort measurementArfcn;
    private ushort facadeSearchArfcn;
    private bool ccchConfigured;
    private long nextBcchBroadcastCycle = -1;
    private long currentCycles;
    private byte rssiMeasurement = DefaultRssiMeasurement;
    private bool facadeNetworkAvailable = true;

    public Dsp(
        byte[] sharedRam,
        IDct3Trace? trace,
        string pagingImsi = SimCard.DefaultImsi,
        string networkName = Dct3PhoneSettings.DefaultNetworkName,
        Action? beforeNetworkTimeInformationQueued = null,
        Func<DateTimeOffset>? networkLocalTimeProvider = null,
        Action<OutgoingNetworkRequest>? outgoingNetworkRequest = null,
        Action<CallTransition>? callTransition = null,
        Action<CallAudioAnnouncement>? callAudioAnnouncement = null)
    {
        this.sharedRam = sharedRam;
        this.trace = trace;
        lapdmLink = new LapdmLink(
            message => this.trace?.Event(message),
            networkLocalTimeProvider,
            beforeNetworkTimeInformationQueued: beforeNetworkTimeInformationQueued,
            pagingImsi: pagingImsi,
            networkName: networkName,
            outgoingNetworkRequest: outgoingNetworkRequest,
            callTransition: callTransition,
            callAudioAnnouncement: callAudioAnnouncement);
        pagingRequestType1 = GsmBlockCodec.BuildPagingRequestType1(pagingImsi);
        sdcchPagingRequestType1 = GsmBlockCodec.BuildPagingRequestType1(pagingImsi, channelNeeded: 0x01);
        pagingFillRequestType1 = GsmBlockCodec.BuildPagingFillRequestType1();
        locationAreaIdentity = GsmIdentity.EncodeLaiFromImsi(pagingImsi);
        (pagingGroupMultiframePhase, pagingGroupFrameOffset) = GsmBlockCodec.CalculatePagingGroup(pagingImsi);
    }

    public Action? RaiseIrq4 { get; set; }

    public Action? RaiseFiq0 { get; set; }

    public Action? PublishDecodedSimLock { get; set; }

    public Action<byte, byte, byte, byte>? PublishRandomAccessReference { get; set; }

    public Func<string>? ArmContextProvider { get; set; }

    public bool IsRunning => running;

    public DspExecutionState ExecutionState =>
        !running
            ? DspExecutionState.Stopped
            : RegisteredOnFacadeNetwork
                ? DspExecutionState.Registered
                : DspExecutionState.CellSelection;

    public bool NeedsService(long cycles) => running && NextWakeCycle(cycles) <= cycles;

    public long NextWakeCycle(long cycles)
    {
        if (!running)
        {
            return long.MaxValue;
        }

        long next = long.MaxValue;
        next = MinScheduledCycle(next, nextDedicatedDownlinkFillCycle);
        next = MinScheduledCycle(next, nextDedicatedBlockRequestCycle);
        next = MinScheduledCycle(next, nextIncomingPagingCycle);
        next = MinScheduledCycle(next, nextIncomingServiceStartCycles);
        if (delayedMdiRcv.Count > 0)
        {
            next = Math.Min(next, delayedMdiRcv.Peek().DueCycles);
        }

        if (pendingMdiRcv.Count > 0)
        {
            next = Math.Min(next, pendingMdiRcv.Peek().EnqueuedCycles + MdiRcvPacketTimeoutCycles);
        }

        if (pendingDedicatedDownlinkFrames.Count > 0)
        {
            next = Math.Min(
                next,
                pendingDedicatedDownlinkFrames.Peek().EnqueuedCycles + DedicatedPendingTimeoutCycles);
        }

        next = Math.Min(next, lapdmLink.NextPendingExpiryCycle(DedicatedPendingTimeoutCycles));

        next = Math.Min(
            next,
            NextIncomingPagingTimeoutCycle(activeIncomingRequestStartedCycles, DedicatedChannelActive));

        if (servingArfcn != 0)
        {
            next = nextBcchBroadcastCycle < 0
                ? Math.Min(next, cycles)
                : Math.Min(next, nextBcchBroadcastCycle);
        }

        return next;
    }

    private static long MinScheduledCycle(long current, long scheduled) =>
        scheduled < 0 ? current : Math.Min(current, scheduled);

    internal static long NextIncomingPagingTimeoutCycle(long pagingStartedCycles, bool dedicatedChannelActive) =>
        pagingStartedCycles >= 0 && !dedicatedChannelActive
            ? pagingStartedCycles + IncomingPagingResponseTimeoutCycles
            : long.MaxValue;

    public void SyncCycle(long cycles) => currentCycles = cycles;

    public void CaptureArmContext()
    {
    }

    public byte RssiMeasurement => rssiMeasurement;

    public bool FacadeNetworkAvailable => facadeNetworkAvailable;

    public bool RegisteredOnFacadeNetwork =>
        facadeNetworkAvailable && suppressImsiPagingAfterRegistration;

    public bool DedicatedChannelActive => dedicatedLogicalChannel != 0;

    public DspToneState ToneState { get; private set; } = DspToneState.Off;

    public int PendingIncomingServiceCount =>
        pendingIncomingRequests.Count +
        (activeIncomingRequest.HasValue ? 1 : 0) +
        lapdmLink.PendingIncomingServiceCount;

    public void QueueIncomingCall(string callingNumber)
    {
        QueueIncomingCall(Guid.NewGuid(), callingNumber);
    }

    public void QueueIncomingCall(Guid requestId, string callingNumber)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("An incoming call requires a non-empty request ID.", nameof(requestId));
        }

        pendingIncomingRequests.Enqueue(new IncomingGsmRequest(
            IncomingGsmRequestKind.Call,
            callingNumber,
            "",
            DestinationPort: 0,
            Payload: [],
            RequestId: requestId));
        trace?.Event($"DSP GSM incoming call requested from {RadioTraceFormat.SanitizeTraceText(callingNumber)}");
        TryStartNextIncomingPaging();
        PumpMdiRcv();
    }

    public void QueueIncomingSms(string originator, string text)
    {
        QueueIncomingSms(originator, text, default);
    }

    public void QueueIncomingSms(string originator, string text, DateTimeOffset sentAt)
    {
        pendingIncomingRequests.Enqueue(new IncomingGsmRequest(
            IncomingGsmRequestKind.Sms,
            originator,
            text,
            DestinationPort: 0,
            Payload: [],
            SentAt: sentAt));
        trace?.Event($"DSP GSM incoming SMS requested from {RadioTraceFormat.SanitizeTraceText(originator)} len={text.Length}");
        TryStartNextIncomingPaging();
        PumpMdiRcv();
    }

    public void QueueIncomingSmartMessage(string originator, ushort destinationPort, ReadOnlySpan<byte> payload)
    {
        int partCount = SmartMessageSms.GetPartCount(payload.Length);
        byte reference = partCount > 1 ? TakeNextSmartMessageReference() : (byte)0;
        foreach (SmartMessagePart part in SmartMessageSms.Split(payload, reference))
        {
            pendingIncomingRequests.Enqueue(new IncomingGsmRequest(
                IncomingGsmRequestKind.Sms,
                originator,
                "",
                destinationPort,
                part.Payload,
                part.Concatenation));
        }

        trace?.Event(
            $"DSP GSM incoming Smart Messaging SMS requested from {RadioTraceFormat.SanitizeTraceText(originator)} " +
            $"port={destinationPort:X4} len={payload.Length} parts={partCount}");
        TryStartNextIncomingPaging();
        PumpMdiRcv();
    }

    public void ResolveNetworkRequest(ResolveNetworkRequest resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        QueueDedicatedDownlink(lapdmLink.ResolveNetworkRequest(resolution, currentCycles));
    }

    public void ConnectNetworkCall(Guid requestId)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A call connection requires a non-empty request ID.", nameof(requestId));
        }

        QueueDedicatedDownlink(lapdmLink.ConnectNetworkCall(requestId, currentCycles));
    }

    public void TerminateNetworkCall(Guid requestId)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A call termination requires a non-empty request ID.", nameof(requestId));
        }

        QueueDedicatedDownlink(lapdmLink.TerminateNetworkCall(requestId, currentCycles));
    }

    private byte TakeNextSmartMessageReference()
    {
        byte reference = nextSmartMessageReference++;
        if (nextSmartMessageReference == 0)
        {
            nextSmartMessageReference = 1;
        }

        return reference;
    }

    public void SetRssiMeasurement(byte measurement, bool postResult = true)
    {
        if (!facadeNetworkAvailable)
        {
            measurement = NoSignalRssiMeasurement;
        }

        if (measurement == rssiMeasurement)
        {
            return;
        }

        rssiMeasurement = measurement;
        trace?.Event($"DSP RSSI measurement {rssiMeasurement:X2}");

        if (!postResult || !running)
        {
            return;
        }

        EnqueueRssiResults();
        PumpMdiRcv();
    }

    public void SetFacadeNetworkAvailable(bool available)
    {
        if (facadeNetworkAvailable == available)
        {
            return;
        }

        facadeNetworkAvailable = available;
        trace?.Event($"DSP facade network {(available ? "available" : "unavailable")}");
        InvalidateFacadeRadioPackets();

        if (available)
        {
            RefreshFacadeRssi();
            ResumeFacadeCellDiscovery();
            return;
        }

        suppressImsiPagingAfterRegistration = false;
        servingArfcn = 0;
        servingBsic = 0;
        ccchArfcn = 0;
        ccchBsic = 0;
        ccchConfigured = false;
        nextBcchBroadcastCycle = -1;
        hasPendingRandomAccessReference = false;

        if (dedicatedLogicalChannel != 0)
        {
            ClearDedicatedChannel("DSP dedicated released after facade network loss");
        }
        else
        {
            pendingDedicatedDownlinkFrames.Clear();
            clearDedicatedChannelAfterNextDownlinkFrame = false;
            lapdmLink.Reset();
            ClearActiveIncomingRequest();
        }

        RefreshFacadeRssi();
    }

    public void ReapplyFacadeNetworkAvailability()
    {
        trace?.Event($"DSP facade network {(facadeNetworkAvailable ? "available" : "unavailable")} reapplied");
        InvalidateFacadeRadioPackets();
        RefreshFacadeRssi();

        if (facadeNetworkAvailable)
        {
            ResumeFacadeCellDiscovery();
        }
    }

    private void RefreshFacadeRssi()
    {
        byte expectedRssi = facadeNetworkAvailable
            ? DefaultRssiMeasurement
            : NoSignalRssiMeasurement;
        bool rssiChanged = rssiMeasurement != expectedRssi;
        SetRssiMeasurement(expectedRssi);

        if (running && !rssiChanged)
        {
            EnqueueRssiResults();
            PumpMdiRcv();
        }
    }

    public void Reset()
    {
        running = false;
        exchanges = 0;
        statusPosted = false;
        blockIndex = 0;
        simlBlockIndex = 0;
        currentBlock = 0;
        pendingMdiRcv.Clear();
        delayedMdiRcv.Clear();
        pendingDedicatedDownlinkFrames.Clear();
        pendingIncomingRequests.Clear();
        ClearActiveIncomingRequest();
        nextIncomingServiceStartCycles = -1;
        nextSmartMessageReference = 1;
        hasPendingRandomAccessReference = false;
        dedicatedLogicalChannel = 0;
        dedicatedBsic = 0;
        dedicatedArfcn = 0;
        nextDedicatedDownlinkFillCycle = -1;
        nextDedicatedBlockRequestCycle = -1;
        nextIncomingPagingCycle = -1;
        clearDedicatedChannelAfterNextDownlinkFrame = false;
        suppressImsiPagingAfterRegistration = false;
        lapdmLink.Reset();
        servingArfcn = 0;
        servingBsic = 0;
        ccchArfcn = 0;
        ccchBsic = 0;
        measurementArfcn = 0;
        // facadeSearchArfcn models the host radio environment and survives a guest DSP
        // restart. It stays separate from measurementArfcn, which channel configuration can overwrite.
        ccchConfigured = false;
        nextBcchBroadcastCycle = -1;
        currentCycles = 0;
        ToneState = DspToneState.Off;
    }

    public void AdvanceTo(long cycles)
    {
        currentCycles = cycles;

        if (!running)
        {
            return;
        }

        ExpireStalePendingState(cycles);
        PumpDelayedMdiRcv(cycles);
        PumpDedicatedDownlinkFill(cycles);
        PumpDedicatedBlockRequest(cycles);
        TryStartNextIncomingPaging();
        PumpIncomingPaging(cycles);
        if (servingArfcn == 0)
        {
            return;
        }

        if (nextBcchBroadcastCycle < 0)
        {
            nextBcchBroadcastCycle = cycles + BcchBroadcastPeriodCycles;
            return;
        }

        if (cycles < nextBcchBroadcastCycle)
        {
            return;
        }

        EnqueueNextServingCellSystemInformation();
        nextBcchBroadcastCycle += BcchBroadcastPeriodCycles;
        PumpMdiRcv();
    }

    public void SetRunning(bool run)
    {
        if (run && !running)
        {
            Boot();
            running = true;
            ushort pending = Read16(0x0E4);

            if (pending != 0)
            {
                HandleCodeblockReply(pending);
            }

            ProcessMdiSnd();
            PumpMdiRcv();
        }

        running = run;

        if (!run)
        {
            ToneState = DspToneState.Off;
        }
    }

    public void OnSharedWrite(uint offset, uint value, int size)
    {
        if (!running || !ObservesSharedWrite(offset, value, size))
        {
            return;
        }

        if (WriteOverlaps(offset, size, 0x1CA))
        {
            PumpMdiRcv();
            return;
        }

        if (WriteOverlaps(offset, size, 0x0E4))
        {
            ushort pending = Read16(0x0E4);
            if (pending != 0)
            {
                HandleCodeblockReply(pending);
            }

            return;
        }

        if (WriteOverlaps(offset, size, 0x0A4))
        {
            ProcessMdiSnd();
            return;
        }

        if (size != 2)
        {
            return;
        }

        if (value != 0)
        {
            return;
        }

        uint? partner = offset switch
        {
            0x0FE => 0x100,
            0x100 => 0x0FE,
            _ => null,
        };

        if (partner is uint reply)
        {
            Write16(reply, 0xFFFF);
            trace?.InterfaceAccess("DSPBOX", true, reply, 0xFFFF);
            exchanges++;

            if (exchanges >= 0x73 && !statusPosted)
            {
                statusPosted = true;
                Write16(0x000, 0x0001);
                Write16(0x002, 0x0001);
                trace?.Event("DSP handshake complete, status posted");
            }
        }
    }

    private static bool WriteOverlaps(uint offset, int size, uint registerOffset) =>
        offset < registerOffset + 2 && offset + (uint)size > registerOffset;

    internal static bool ObservesSharedWrite(uint offset, uint value, int size) =>
        WriteOverlaps(offset, size, 0x1CA) ||
        WriteOverlaps(offset, size, 0x0E4) ||
        WriteOverlaps(offset, size, 0x0A4) ||
        (size == 2 && value == 0 && offset is 0x0FE or 0x100);

    internal static bool ObservesHostInterrupt(ReadOnlySpan<byte> sharedRam) =>
        sharedRam.Length >= 0xE2 &&
        (BinaryPrimitives.ReadUInt16BigEndian(sharedRam[0xE0..]) != 0 ||
            BinaryPrimitives.ReadUInt16BigEndian(sharedRam[0xDC..]) != 0 ||
            (BinaryPrimitives.ReadUInt16BigEndian(sharedRam[0xCC..]) & 1) != 0 ||
            BinaryPrimitives.ReadUInt16BigEndian(sharedRam[0x0A4..]) !=
                BinaryPrimitives.ReadUInt16BigEndian(sharedRam[0x0A6..]));

    public void OnHostInterrupt()
    {
        if (!running)
        {
            return;
        }

        ushort cobba = Read16(0xE0);

        if (cobba != 0)
        {
            DspToneState nextToneState = new(
                Read16(0x0AC),
                Read16(0x0AE),
                Read16(0x0B0),
                Read16(0x0B6),
                Read16(0x0BA));

            if (nextToneState != ToneState)
            {
                ToneState = nextToneState;
                trace?.Event(
                    $"DSP tone enable={nextToneState.ToneEnable:X4} " +
                    $"osc1={nextToneState.Oscillator1Hz:F2}Hz " +
                    $"osc2={nextToneState.Oscillator2Hz:F2}Hz " +
                    $"amp={nextToneState.Amplitude:X4} command={nextToneState.AudioCommandKind:X2}");
            }

            Write16(0xE0, 0);
            trace?.Event($"DSP acked cobba command {cobba:X4}");
        }

        ushort shortMdi = Read16(0xDC);

        if (shortMdi != 0)
        {
            Write16(0xDC, 0);
            trace?.Event($"DSP acked short MDI {shortMdi:X4} (type {shortMdi & 0xFF:X2} param {shortMdi >> 8:X2})");
            HandleShortMdi(shortMdi);
        }

        ushort flags = Read16(0xCC);

        if ((flags & 1) != 0)
        {
            Write16(0xCC, (ushort)(flags & ~1));
        }

        ProcessMdiSnd();
        PumpMdiRcv();
    }

    private void ProcessMdiSnd()
    {
        ushort tail = Read16(0x0A4);
        ushort head = Read16(0x0A6);

        if (head == tail)
        {
            return;
        }

        if (head >= MdiSendQueueWords || tail >= MdiSendQueueWords)
        {
            trace?.Event($"DSP MDISND invalid queue indices head={head:X4} tail={tail:X4}, flushing queue");
            Write16(0x0A6, tail);
            return;
        }

        int consumedWords = 0;
        while (head != tail)
        {
            int byteOffset = head * 2;
            byte len = ReadQueueByte(byteOffset);
            byte type = ReadQueueByte(byteOffset + 1);
            int total = 2 + len + (len & 1);
            int packetWords = total / 2;

            if (total > MdiSendQueueBytes || packetWords > MdiSendQueueWords - consumedWords)
            {
                trace?.Event($"DSP MDISND malformed packet at {byteOffset:X3} len={len:X2} type={type:X2}, flushing queue");
                head = tail;
                break;
            }

            byte[] payload = new byte[len];

            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = ReadQueueByte(byteOffset + 2 + i);
            }

            HandleMdiPacket(type, payload);
            consumedWords += packetWords;
            head = (ushort)((head + packetWords) % MdiSendQueueWords);
        }

        Write16(0x0A6, head);
        PumpMdiRcv();
    }

    private void HandleMdiPacket(byte type, ReadOnlySpan<byte> payload)
    {
        switch (type)
        {
            case 0x70:
                byte local = payload[0];
                if (local is >= 0x13 and <= 0x18)
                {
                    string dump = string.Join(' ', payload.ToArray().Select(value => $"{value:X2}"));
                    trace?.Event($"DSP MDI local {local:X2} len={payload.Length} payload=[{dump}]");
                }
                else
                {
                    trace?.Event($"DSP MDI local {local:X2} len={payload.Length}");
                }

                if (local == 0x0D)
                {
                    EnqueueMdiRcv([0x02, 0x74, 0x0D, 0x00]);
                }
                else if (local == 0x13)
                {
                    EnqueueMdiRcv([0x10, 0x74, 0x34, 0x0E, 0x00, 0x83, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xAC, 0xAD, 0xAB, 0x00]);
                }
                else if (local == 0x16)
                {
                    EnqueueMdiRcv(GsmBlockCodec.BuildSimlReadbackReply(payload, simlBlockIndex));
                    simlBlockIndex = Math.Min(simlBlockIndex + 1, 9);
                }
                else if (local == 0x17)
                {
                    PublishDecodedSimLock?.Invoke();
                    EnqueueMdiRcv([0x03, 0x74, 0x36, 0x00, 0x00]);
                }
                break;
            case 0x05:
                trace?.FbusFrame(transmitted: true, payload);
                trace?.Event($"DSP MDI fbus frame len={payload.Length} [{payload[0]:X2} {payload[1]:X2} {payload[2]:X2} {payload[3]:X2}]");
                break;
            case 0x02:
                trace?.Event($"DSP MDI channel configure len={payload.Length} payload=[{RadioTraceFormat.DumpPayload(payload)}]");
                EnqueueMdiRcv(GsmBlockCodec.BuildChannelChangedConfirm(payload));
                CaptureMeasurementCarrier(payload);
                if (!facadeNetworkAvailable)
                {
                    break;
                }
                CaptureDedicatedChannel(payload);
                if (payload.Length >= 12 && payload[8] == 0x60)
                {
                    ccchConfigured = true;
                    ccchBsic = payload[1];
                    ccchArfcn = (ushort)((payload[10] << 8) | payload[11]);
                    EnqueueServingCellSchBlock();
                    EnqueueServingCellSystemInformation();
                }

                if (CaptureServingCell(payload))
                {
                    EnqueueServingCellSystemInformation();
                }
                break;
            case 0x0C:
                trace?.Event($"DSP MDI RACH request len={payload.Length} payload=[{RadioTraceFormat.DumpPayload(payload)}]");
                EnqueueImmediateAssignment(payload);
                break;
            case 0x1B:
                trace?.Event($"DSP MDI send block len={payload.Length} payload=[{RadioTraceFormat.DumpPayload(payload)}]");
                HandleMdiSendBlock(payload);
                break;
            case 0x0F:
                trace?.Event($"DSP MDI neighbour list len={payload.Length} payload=[{RadioTraceFormat.DumpPayload(payload)}]");
                EnqueueRssiResults();
                break;
            case 0x11:
                trace?.Event($"DSP MDI nmeas instructions len={payload.Length} payload=[{RadioTraceFormat.DumpPayload(payload)}]");
                EnqueueNeighbourTimingOffset(payload);
                break;
            case 0x46:
                trace?.Event($"DSP MDI MSI len={payload.Length} payload=[{RadioTraceFormat.DumpPayload(payload)}]");
                EnqueueRssiResults();
                break;
            case 0x56:
                trace?.Event($"DSP MDI search list len={payload.Length} payload=[{RadioTraceFormat.DumpPayload(payload)}]");
                PostSchBlock(payload);
                break;
            case 0x57:
                trace?.Event($"DSP MDI type {type:X2} len={payload.Length} payload=[{RadioTraceFormat.DumpPayload(payload)}]");
                EnqueueServingCellSchBlock();
                break;
            default:
                trace?.Event($"DSP MDI type {type:X2} len={payload.Length} payload=[{RadioTraceFormat.DumpPayload(payload)}]");
                break;
        }
    }

    private void HandleShortMdi(ushort command)
    {
        byte type = (byte)command;
        byte param = (byte)(command >> 8);

        if (type == 0x45 && param != 0)
        {
            EnqueueRssiResults();
            EnqueueRaInfo();
        }
        else if (type == 0x4B)
        {
            EnqueueRssiResults();
            EnqueueAllRssiResults();
            EnqueueRaInfo();
        }
    }

    private void PostSchBlock(ReadOnlySpan<byte> payload)
    {
        for (int i = 0; i + 1 < payload.Length; i += 2)
        {
            ushort arfcn = (ushort)((payload[i] << 8) | payload[i + 1]);
            if (arfcn is 0x0000 or 0xFFFF)
            {
                continue;
            }

            facadeSearchArfcn = arfcn;
            measurementArfcn = facadeSearchArfcn;
            if (!facadeNetworkAvailable)
            {
                trace?.Event($"DSP facade network retained search ARFCN={arfcn:X4} while unavailable");
                return;
            }

            servingArfcn = measurementArfcn;
            EnqueueServingCellSchBlock();
            EnqueueServingCellSystemInformation();
            return;
        }
    }

    private void ResumeFacadeCellDiscovery()
    {
        if (!running || servingArfcn != 0 || facadeSearchArfcn == 0)
        {
            return;
        }

        servingArfcn = facadeSearchArfcn;
        measurementArfcn = servingArfcn;
        trace?.Event($"DSP facade network resumed search ARFCN={servingArfcn:X4}");
        EnqueueServingCellSchBlock();
        EnqueueServingCellSystemInformation();
        PumpMdiRcv();
    }

    private void EnqueueServingCellSchBlock()
    {
        if (!facadeNetworkAvailable || servingArfcn == 0)
        {
            return;
        }

        int fn = LastSchFrameNumber();
        trace?.Event($"DSP serving SCH FN={fn} T3={fn % 51}");
        EnqueueMdiRcv(BuildReceivedBlock(0x40, servingBsic, servingArfcn, fn, GsmBlockCodec.BuildSchInformation(servingBsic, fn)));
    }

    private int LastSchFrameNumber()
    {
        int fn = CurrentFrameNumber;
        int block = fn / 51;
        int t3 = fn % 51;
        int aligned = t3 >= 41 ? 41 : t3 >= 31 ? 31 : t3 >= 21 ? 21 : t3 >= 11 ? 11 : t3 >= 1 ? 1 : -1;

        if (aligned < 0)
        {
            block--;
            aligned = 41;
        }

        return (block % BlocksPerHyperframe + BlocksPerHyperframe) % BlocksPerHyperframe * 51 + aligned;
    }

    private bool CaptureServingCell(ReadOnlySpan<byte> payload)
    {
        if (!facadeNetworkAvailable || payload.Length < 12 || payload[8] != 0x50)
        {
            return false;
        }

        servingBsic = payload[1];
        servingArfcn = (ushort)((payload[10] << 8) | payload[11]);
        measurementArfcn = servingArfcn;
        return true;
    }

    private void CaptureMeasurementCarrier(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12)
        {
            return;
        }

        measurementArfcn = (ushort)((payload[10] << 8) | payload[11]);
    }

    private void CaptureDedicatedChannel(ReadOnlySpan<byte> payload)
    {
        if (!facadeNetworkAvailable || payload.Length < 9)
        {
            return;
        }

        if (payload[8] != 0x80)
        {
            if (dedicatedLogicalChannel != 0 && pendingDedicatedDownlinkFrames.Count == 0)
            {
                ClearDedicatedChannel($"DSP dedicated released by channel configure logical={payload[8]:X2}");
            }

            return;
        }

        if (dedicatedLogicalChannel != payload[8])
        {
            trace?.Event($"DSP dedicated logical channel {payload[8]:X2}");
        }

        dedicatedLogicalChannel = payload[8];
        dedicatedBsic = payload[1];
        dedicatedArfcn = payload.Length >= 12 ? (ushort)((payload[10] << 8) | payload[11]) : CcchArfcn;
        nextDedicatedDownlinkFillCycle = NextCycleForFrameOffset(0);
        nextDedicatedBlockRequestCycle = NextCycleForFrameOffset(Sdcch8Subchannel0UplinkFrameOffset);
        nextIncomingPagingCycle = -1;
        pendingDedicatedDownlinkFrames.Clear();
        clearDedicatedChannelAfterNextDownlinkFrame = false;
        lapdmLink.Reset();

        if (activeIncomingRequest is { } incomingRequest)
        {
            QueueActiveIncomingRequest(incomingRequest);
        }

        if (nextDedicatedBlockRequestCycle < nextDedicatedDownlinkFillCycle)
        {
            nextDedicatedBlockRequestCycle += DedicatedBlockRequestPeriodCycles;
        }
    }

    private void PumpDedicatedDownlinkFill(long cycles)
    {
        if (dedicatedLogicalChannel == 0 || nextDedicatedDownlinkFillCycle < 0 || cycles < nextDedicatedDownlinkFillCycle)
        {
            return;
        }

        if (HasExpiredPendingDedicatedDownlinkFrame(cycles))
        {
            ClearDedicatedChannel("DSP dedicated released after pending downlink timeout");
            return;
        }

        int frameNumber = (int)(nextDedicatedDownlinkFillCycle / CyclesPerTdmaFrame % FrameNumberModulus);
        bool clearAfterSend = false;

        if (pendingDedicatedDownlinkFrames.Count == 0)
        {
            trace?.Event($"DSP dedicated fill logical={dedicatedLogicalChannel:X2} FN={frameNumber} T3={frameNumber % 51}");
            EnqueueMdiRcv(BuildReceivedBlock(dedicatedLogicalChannel, dedicatedBsic, dedicatedArfcn, frameNumber, LapdmLink.BuildFillFrame()));
        }
        else
        {
            byte[] queuedFrame = pendingDedicatedDownlinkFrames.Dequeue().Frame;
            string frameKind = LapdmLink.DescribeDownlinkFrame(queuedFrame);
            trace?.Event($"DSP dedicated {frameKind} logical={dedicatedLogicalChannel:X2} FN={frameNumber} T3={frameNumber % 51}");
            EnqueueMdiRcv(BuildReceivedBlock(dedicatedLogicalChannel, dedicatedBsic, dedicatedArfcn, frameNumber, queuedFrame));
            clearAfterSend = clearDedicatedChannelAfterNextDownlinkFrame && pendingDedicatedDownlinkFrames.Count == 0;
            clearDedicatedChannelAfterNextDownlinkFrame = false;
        }

        nextDedicatedDownlinkFillCycle += DedicatedBlockRequestPeriodCycles;
        PumpMdiRcv();

        if (clearAfterSend)
        {
            ClearDedicatedChannel("DSP dedicated released after DISC UA");
        }
    }

    private void HandleMdiSendBlock(ReadOnlySpan<byte> payload)
    {
        if (!facadeNetworkAvailable || payload.Length <= MdiSendBlockLayer2Offset)
        {
            return;
        }

        byte logicalChannel = payload[1];
        LapdmLink.UplinkResult result = lapdmLink.HandleUplink(logicalChannel, payload[MdiSendBlockLayer2Offset..], currentCycles);

        QueueDedicatedDownlink(result);

        if (!suppressImsiPagingAfterRegistration && lapdmLink.SuppressImsiPagingAfterRegistration)
        {
            suppressImsiPagingAfterRegistration = true;
            trace?.Event("DSP IMSI paging suppressed after registration release");
            TryStartNextIncomingPaging();
        }

        if (result.ReleaseAfterDownlinkFrames)
        {
            clearDedicatedChannelAfterNextDownlinkFrame = true;
        }
    }

    private void QueueDedicatedDownlink(LapdmLink.UplinkResult result)
    {
        foreach (byte[] frame in result.DownlinkFrames)
        {
            pendingDedicatedDownlinkFrames.Enqueue(new PendingDedicatedDownlinkFrame(frame, currentCycles));
        }

        if (result.DownlinkFrames.Count > 0 && nextDedicatedDownlinkFillCycle < 0 && dedicatedLogicalChannel != 0)
        {
            nextDedicatedDownlinkFillCycle = currentCycles;
        }
    }

    private void ClearDedicatedChannel(string reason)
    {
        bool continueSmartMessageImmediately = HasPendingSmartMessageContinuation();
        trace?.Event(reason);
        dedicatedLogicalChannel = 0;
        dedicatedBsic = 0;
        dedicatedArfcn = 0;
        nextDedicatedDownlinkFillCycle = -1;
        nextDedicatedBlockRequestCycle = -1;
        pendingDedicatedDownlinkFrames.Clear();
        clearDedicatedChannelAfterNextDownlinkFrame = false;
        lapdmLink.Reset();
        ClearActiveIncomingRequest();
        ScheduleIncomingServiceCooldown(continueSmartMessageImmediately);
        TryStartNextIncomingPaging();
    }

    private bool HasPendingSmartMessageContinuation()
    {
        if (activeIncomingRequest is not { } current ||
            !current.Concatenation.IsMultipart ||
            !pendingIncomingRequests.TryPeek(out IncomingGsmRequest next))
        {
            return false;
        }

        return next.Kind == IncomingGsmRequestKind.Sms &&
            next.Address == current.Address &&
            next.DestinationPort == current.DestinationPort &&
            next.Concatenation.Reference == current.Concatenation.Reference &&
            next.Concatenation.PartCount == current.Concatenation.PartCount &&
            next.Concatenation.PartNumber == current.Concatenation.PartNumber + 1;
    }

    private void ExpireStalePendingState(long cycles)
    {
        ExpirePendingMdiRcv(cycles);
        ExpireIncomingPaging(cycles);

        if (dedicatedLogicalChannel == 0)
        {
            return;
        }

        if (lapdmLink.ExpirePending(cycles, DedicatedPendingTimeoutCycles))
        {
            ClearDedicatedChannel("DSP dedicated released after LAPDm pending timeout");
            return;
        }

        if (HasExpiredPendingDedicatedDownlinkFrame(cycles))
        {
            ClearDedicatedChannel("DSP dedicated released after pending downlink timeout");
        }
    }

    private void ExpireIncomingPaging(long cycles)
    {
        if (activeIncomingRequest is not { } request ||
            dedicatedLogicalChannel != 0 ||
            activeIncomingRequestStartedCycles < 0 ||
            cycles - activeIncomingRequestStartedCycles < IncomingPagingResponseTimeoutCycles)
        {
            return;
        }

        string reason = activeIncomingRequestAnswered
            ? "paging answered but dedicated setup timed out"
            : "paging timed out";
        DeferActiveIncomingPaging(request, reason);
    }

    private void ScheduleIncomingServiceCooldown(bool startImmediately = false)
    {
        nextIncomingServiceStartCycles = pendingIncomingRequests.Count == 0
            ? -1
            : currentCycles + (startImmediately ? 0 : IncomingServiceCooldownCycles);
    }

    private bool HasExpiredPendingDedicatedDownlinkFrame(long cycles) =>
        pendingDedicatedDownlinkFrames.Count > 0 &&
        cycles - pendingDedicatedDownlinkFrames.Peek().EnqueuedCycles >= DedicatedPendingTimeoutCycles;

    private void PumpDedicatedBlockRequest(long cycles)
    {
        if (dedicatedLogicalChannel == 0 || nextDedicatedBlockRequestCycle < 0 || cycles < nextDedicatedBlockRequestCycle)
        {
            return;
        }

        EnqueueBlockRequest(dedicatedLogicalChannel);
        nextDedicatedBlockRequestCycle += DedicatedBlockRequestPeriodCycles;
        PumpMdiRcv();
    }

    private void EnqueueBlockRequest(byte logicalChannel)
    {
        int frameNumber = CurrentFrameNumber;
        trace?.Event($"DSP block request logical={logicalChannel:X2} FN={frameNumber} T3={frameNumber % 51}");
        EnqueueMdiRcv([0x01, 0x86, logicalChannel]);
    }

    private long NextCycleForFrameOffset(int frameOffset)
    {
        long frameStart = currentCycles / CyclesPerTdmaFrame * CyclesPerTdmaFrame;
        int deltaFrames = (frameOffset - CurrentFrameNumber % 51 + 51) % 51;
        long nextCycle = frameStart + deltaFrames * CyclesPerTdmaFrame;
        return nextCycle > currentCycles ? nextCycle : nextCycle + DedicatedBlockRequestPeriodCycles;
    }

    private void EnqueueRssiResults()
    {
        ushort arfcn = RssiArfcn();
        byte[] packet = new byte[8];
        packet[0] = 0x06;
        packet[1] = 0x83;
        packet[2] = (byte)(arfcn >> 8);
        packet[3] = 0x01;
        packet[4] = rssiMeasurement;
        packet[5] = rssiMeasurement;
        packet[6] = (byte)(arfcn >> 8);
        packet[7] = (byte)arfcn;

        EnqueueMdiRcv(packet);
    }

    private void EnqueueAllRssiResults()
    {
        byte[] packet = new byte[0xA4];
        packet[0] = 0xA2;
        packet[1] = 0x8B;

        ushort arfcn = RssiArfcn();
        packet[4] = (byte)(arfcn >> 8);
        packet[5] = (byte)arfcn;
        packet[7] = rssiMeasurement;

        for (int offset = 8; offset < packet.Length; offset += 4)
        {
            packet[offset + 3] = NoSignalRssiMeasurement;
        }

        EnqueueMdiRcv(packet);
    }

    private ushort RssiArfcn()
    {
        if (servingArfcn != 0)
        {
            return servingArfcn;
        }

        return measurementArfcn != 0 ? measurementArfcn : (ushort)0x03EC;
    }

    private void EnqueueRaInfo()
    {
        EnqueueMdiRcv([0x01, 0x84, 0x00]);
    }

    private void EnqueueNeighbourTimingOffset(ReadOnlySpan<byte> request)
    {
        if (!facadeNetworkAvailable)
        {
            return;
        }

        ushort carrier = request.Length >= 8 ? (ushort)((request[6] << 8) | request[7]) : measurementArfcn;
        if (carrier == 0)
        {
            carrier = measurementArfcn != 0 ? measurementArfcn : servingArfcn != 0 ? servingArfcn : DefaultNeighbourArfcn;
        }

        int frameNumber = CurrentFrameNumber;

        EnqueueMdiRcvAfter(NmeasResultDelayCycles,
        [
            0x0A, 0x88,
            0x01,
            (byte)(frameNumber >> 16),
            (byte)(frameNumber >> 8),
            (byte)frameNumber,
            (byte)(carrier >> 8),
            (byte)carrier,
            0x00, 0x00,
            0x00,
            0x01,
        ]);
    }

    private void EnqueueServingCellSystemInformation()
    {
        if (!facadeNetworkAvailable || servingArfcn == 0)
        {
            return;
        }

        EnqueueMdiRcv(BuildReceivedBlock(0x50, servingBsic, servingArfcn, LastBcchFrameNumber(1), GsmBlockCodec.BuildSystemInformation2()));
        EnqueueMdiRcv(BuildReceivedBlock(0x50, servingBsic, servingArfcn, LastBcchFrameNumber(2), BuildSystemInformation3()));
        EnqueueMdiRcv(BuildReceivedBlock(0x50, servingBsic, servingArfcn, LastBcchFrameNumber(3), BuildSystemInformation4()));
        nextBcchBroadcastCycle = -1;
    }

    private void EnqueueImmediateAssignment(ReadOnlySpan<byte> rachPayload)
    {
        if (!facadeNetworkAvailable)
        {
            return;
        }

        byte requestReference = rachPayload.Length >= 3 ? rachPayload[2] : (byte)0;
        ushort arfcn = CcchArfcn;
        byte bsic = CcchBsic;
        MarkIncomingPagingAnswered();
        int assignmentFrameNumber = (int)((currentCycles + ImmediateAssignmentDelayCycles) / CyclesPerTdmaFrame % FrameNumberModulus);
        int frameNumber = NextCcchBlockFrameNumber(assignmentFrameNumber);

        ushort requestFrameNumber = rachPayload.Length >= 6 && rachPayload[1] == 0x01
            ? (ushort)((rachPayload[4] << 8) | rachPayload[5])
            : (ushort)((CurrentFrameNumber + (rachPayload.Length >= 4 ? rachPayload[3] : 0)) % 42432);
        CapturePendingRandomAccessReference(requestReference, requestFrameNumber);
        byte[] assignment = GsmBlockCodec.BuildImmediateAssignment(requestReference, requestFrameNumber, bsic, arfcn);
        trace?.Event($"DSP immediate assignment RA={requestReference:X2} FN={requestFrameNumber} ARFCN={arfcn:X4}");
        EnqueueMdiRcvAfter(ImmediateAssignmentDelayCycles, BuildReceivedBlock(0x60, bsic, arfcn, frameNumber, assignment));
    }

    private static int NextCcchBlockFrameNumber(int minimumFrameNumber)
    {
        int multiframe = minimumFrameNumber / 51;
        int t3 = minimumFrameNumber % 51;

        foreach (int offset in GsmBlockCodec.CcchBlockOffsets)
        {
            if (offset >= t3)
            {
                return multiframe * 51 + offset;
            }
        }

        return (multiframe + 1) * 51 + GsmBlockCodec.CcchBlockOffsets[0];
    }

    private void CapturePendingRandomAccessReference(byte requestReference, ushort frameNumber)
    {
        GsmBlockCodec.DecodeRequestReferenceFrame(frameNumber, out pendingRandomAccessT1Prime, out pendingRandomAccessT3, out pendingRandomAccessT2);
        pendingRandomAccessReference = requestReference;
        hasPendingRandomAccessReference = true;
    }

    private void PublishPendingRandomAccessReference()
    {
        if (!hasPendingRandomAccessReference)
        {
            return;
        }

        PublishRandomAccessReference?.Invoke(
            pendingRandomAccessReference,
            pendingRandomAccessT1Prime,
            pendingRandomAccessT3,
            pendingRandomAccessT2);
        hasPendingRandomAccessReference = false;
    }

    private void EnqueueNextServingCellSystemInformation()
    {
        if (!facadeNetworkAvailable)
        {
            return;
        }

        int tc = CurrentFrameNumber / 51 % 8;
        byte[] layer2 = tc switch
        {
            2 or 6 => BuildSystemInformation3(),
            3 or 7 => BuildSystemInformation4(),
            _ => GsmBlockCodec.BuildSystemInformation2(),
        };

        EnqueueMdiRcv(BuildReceivedBlock(0x50, servingBsic, servingArfcn, LastBcchFrameNumber(tc), layer2));

        if (ccchConfigured && dedicatedLogicalChannel == 0)
        {
            int multiframe = LastCompleteMultiframe();
            byte[] pagingRequest = suppressImsiPagingAfterRegistration ? pagingFillRequestType1 : pagingRequestType1;

            foreach (int offset in GsmBlockCodec.CcchBlockOffsets)
            {
                EnqueueMdiRcv(BuildReceivedBlock(0x60, CcchBsic, CcchArfcn, multiframe * 51 + offset, pagingRequest));
            }
        }
    }

    private void TryStartNextIncomingPaging()
    {
        if (!running ||
            !facadeNetworkAvailable ||
            !suppressImsiPagingAfterRegistration ||
            dedicatedLogicalChannel != 0 ||
            !ccchConfigured ||
            activeIncomingRequest.HasValue ||
            pendingIncomingRequests.Count == 0 ||
            nextIncomingServiceStartCycles >= 0 && currentCycles < nextIncomingServiceStartCycles)
        {
            return;
        }

        nextIncomingServiceStartCycles = -1;
        activeIncomingRequest = pendingIncomingRequests.Dequeue();
        activeIncomingRequestStartedCycles = currentCycles;
        activeIncomingPagingBursts = 0;
        activeIncomingRequestAnswered = false;
        trace?.Event($"DSP GSM incoming {IncomingGsmRequestName(activeIncomingRequest.Value.Kind)} paging queued");
        nextIncomingPagingCycle = currentCycles;
        PumpIncomingPaging(currentCycles);
    }

    private void PumpIncomingPaging(long cycles)
    {
        if (activeIncomingRequest is not { } request ||
            !facadeNetworkAvailable ||
            dedicatedLogicalChannel != 0 ||
            activeIncomingRequestAnswered ||
            !suppressImsiPagingAfterRegistration ||
            !ccchConfigured ||
            cycles < nextIncomingPagingCycle)
        {
            return;
        }

        if (activeIncomingPagingBursts >= MaximumIncomingPagingBursts)
        {
            DeferActiveIncomingPaging(request, $"paging deferred after {MaximumIncomingPagingBursts} unanswered bursts");
            return;
        }

        trace?.Event($"DSP GSM incoming {IncomingGsmRequestName(request.Kind)} paging burst");
        EnqueuePagingRequestBurst();
        activeIncomingPagingBursts++;
        nextIncomingPagingCycle = cycles + IncomingPagingRepeatCycles;
        PumpMdiRcv();
    }

    private void MarkIncomingPagingAnswered()
    {
        if (activeIncomingRequest is not { } request || activeIncomingRequestAnswered)
        {
            return;
        }

        activeIncomingRequestAnswered = true;
        nextIncomingPagingCycle = -1;
        trace?.Event($"DSP GSM incoming {IncomingGsmRequestName(request.Kind)} paging answered");
    }

    private void DeferActiveIncomingPaging(IncomingGsmRequest request, string reason)
    {
        trace?.Event($"DSP GSM incoming {IncomingGsmRequestName(request.Kind)} {reason}");
        pendingIncomingRequests.Enqueue(request);
        ClearActiveIncomingRequest();
        ScheduleIncomingServiceCooldown();
        TryStartNextIncomingPaging();
    }

    private void ClearActiveIncomingRequest()
    {
        activeIncomingRequest = null;
        activeIncomingRequestStartedCycles = -1;
        activeIncomingPagingBursts = 0;
        activeIncomingRequestAnswered = false;
        nextIncomingPagingCycle = -1;
    }

    private void QueueActiveIncomingRequest(IncomingGsmRequest request)
    {
        if (request.Kind == IncomingGsmRequestKind.Call)
        {
            lapdmLink.QueueIncomingCall(request.RequestId, request.Address);
        }
        else
        {
            if (request.Payload.Length > 0)
            {
                lapdmLink.QueueIncomingSmartMessage(
                    request.Address,
                    request.DestinationPort,
                    request.Payload,
                    request.Concatenation);
            }
            else
            {
                lapdmLink.QueueIncomingSms(request.Address, request.Text, request.SentAt);
            }
        }
    }

    private void EnqueuePagingRequestBurst()
    {
        long dueCycles = NextCycleForPagingGroup();
        int frameNumber = (int)(dueCycles / CyclesPerTdmaFrame % FrameNumberModulus);
        trace?.Event($"DSP GSM incoming paging block FN={frameNumber} T3={frameNumber % 51}");
        EnqueueMdiRcvAt(
            dueCycles,
            BuildReceivedBlock(0x60, CcchBsic, CcchArfcn, frameNumber, sdcchPagingRequestType1));
    }

    private const int BlocksPerHyperframe = FrameNumberModulus / 51;

    private int CurrentFrameNumber => (int)(currentCycles / CyclesPerTdmaFrame % FrameNumberModulus);

    private long NextCycleForPagingGroup()
    {
        long currentFrame = currentCycles / CyclesPerTdmaFrame;
        long currentMultiframe = currentFrame / 51;
        long deltaMultiframes = (pagingGroupMultiframePhase - currentMultiframe % GsmBlockCodec.BroadcastBsPaMfrms + GsmBlockCodec.BroadcastBsPaMfrms) %
            GsmBlockCodec.BroadcastBsPaMfrms;
        long targetFrame = (currentMultiframe + deltaMultiframes) * 51 + pagingGroupFrameOffset;
        long targetCycle = targetFrame * CyclesPerTdmaFrame;

        if (targetCycle <= currentCycles)
        {
            targetCycle += IncomingPagingRepeatCycles;
        }

        return targetCycle;
    }

    private int LastBcchFrameNumber(int tc)
    {
        int fn = CurrentFrameNumber;
        int block = fn / 51 - (fn / 51 % 8 - tc + 8) % 8;

        if (block * 51 + 2 > fn)
        {
            block -= 8;
        }

        return (block % BlocksPerHyperframe + BlocksPerHyperframe) % BlocksPerHyperframe * 51 + 2;
    }



    private int LastCompleteMultiframe()
    {
        int block = CurrentFrameNumber / 51 - 1;
        return (block % BlocksPerHyperframe + BlocksPerHyperframe) % BlocksPerHyperframe;
    }

    private byte CcchBsic => ccchArfcn != 0 ? ccchBsic : servingBsic;

    private ushort CcchArfcn => ccchArfcn != 0 ? ccchArfcn : servingArfcn;

    private byte[] BuildReceivedBlock(byte logicalChannel, byte bsic, ushort arfcn, int frameNumber, ReadOnlySpan<byte> layer2)
    {
        const int blockPrefixLength = 10;

        int payloadLength = blockPrefixLength + layer2.Length;
        byte[] packet = new byte[2 + payloadLength];
        packet[0] = (byte)payloadLength;
        packet[1] = 0x80;
        packet[2] = logicalChannel;
        packet[3] = bsic;
        packet[4] = 0x00;

        packet[5] = (byte)(frameNumber >> 16);
        packet[6] = (byte)(frameNumber >> 8);
        packet[7] = (byte)frameNumber;
        packet[8] = (byte)(arfcn >> 8);
        packet[9] = (byte)arfcn;
        packet[10] = 0x00;
        packet[11] = 0x00;
        layer2.CopyTo(packet.AsSpan(12));
        return packet;
    }

    private byte[] BuildSystemInformation3()
    {
        return
        [
            0x49, 0x06, 0x1B,
            0x00, 0x01,
            locationAreaIdentity[0],
            locationAreaIdentity[1],
            locationAreaIdentity[2],
            locationAreaIdentity[3],
            locationAreaIdentity[4],
            0x00, 0x00, 0x00,
            0x00,
            0x00, 0x00,
            0x00, 0x00, 0x00,
            0x2B, 0x2B, 0x2B, 0x2B,
        ];
    }

    private byte[] BuildSystemInformation4()
    {
        return
        [
            0x31, 0x06, 0x1C,
            locationAreaIdentity[0],
            locationAreaIdentity[1],
            locationAreaIdentity[2],
            locationAreaIdentity[3],
            locationAreaIdentity[4],
            0x00, 0x00,
            0x00, 0x00, 0x00,
            0x2B, 0x2B, 0x2B, 0x2B, 0x2B,
            0x2B, 0x2B, 0x2B, 0x2B, 0x2B,
        ];
    }




    private byte ReadQueueByte(int offset)
    {
        return sharedRam[offset % MdiSendQueueBytes];
    }

    private void EnqueueMdiRcv(ReadOnlySpan<byte> packet)
    {
        pendingMdiRcv.Enqueue(new PendingMdiRcvPacket(packet.ToArray(), currentCycles));
    }

    private void InvalidateFacadeRadioPackets()
    {
        int pendingRemoved = RemoveFacadeRadioPackets(pendingMdiRcv);
        int delayedRemoved = RemoveFacadeRadioPackets(delayedMdiRcv);
        bool postedRetracted = RetractPostedFacadeRadioPacket();
        if (pendingRemoved != 0 || delayedRemoved != 0 || postedRetracted)
        {
            trace?.Event(
                $"DSP facade network invalidated radio packets " +
                $"posted={(postedRetracted ? 1 : 0)} pending={pendingRemoved} delayed={delayedRemoved}");
        }
    }

    private bool RetractPostedFacadeRadioPacket()
    {
        ushort tail = Read16(0x1C8);
        ushort head = Read16(0x1CA);
        if (tail is < 0x80 or > 0xE3 ||
            head is < 0x80 or > 0xE3 ||
            tail == head)
        {
            return false;
        }

        byte type = ReadMdiRcvQueueByte(head, 1);
        if (!GsmBlockCodec.IsFacadeRadioPacket(type))
        {
            return false;
        }

        // The guest owns the consumer head. Retraction moves the producer tail back to
        // that head, so the replacement packet can overwrite the stale slot.
        Write16(0x1C8, head);
        return true;
    }

    private byte ReadMdiRcvQueueByte(ushort head, int index) =>
        sharedRam[0x100 + ((head - 0x80) * 2 + index) % 200];

    private static int RemoveFacadeRadioPackets(Queue<PendingMdiRcvPacket> packets)
    {
        int removed = 0;
        int count = packets.Count;
        for (int i = 0; i < count; i++)
        {
            PendingMdiRcvPacket packet = packets.Dequeue();
            if (GsmBlockCodec.IsFacadeRadioPacket(packet.Packet))
            {
                removed++;
            }
            else
            {
                packets.Enqueue(packet);
            }
        }

        return removed;
    }

    private static int RemoveFacadeRadioPackets(Queue<DelayedMdiRcvPacket> packets)
    {
        int removed = 0;
        int count = packets.Count;
        for (int i = 0; i < count; i++)
        {
            DelayedMdiRcvPacket packet = packets.Dequeue();
            if (GsmBlockCodec.IsFacadeRadioPacket(packet.Packet))
            {
                removed++;
            }
            else
            {
                packets.Enqueue(packet);
            }
        }

        return removed;
    }


    private void EnqueueMdiRcvAfter(long delayCycles, ReadOnlySpan<byte> packet)
    {
        EnqueueMdiRcvAt(currentCycles + delayCycles, packet);
    }

    private void EnqueueMdiRcvAt(long dueCycles, ReadOnlySpan<byte> packet)
    {
        DelayedMdiRcvPacket item = new(dueCycles, packet.ToArray());

        if (delayedMdiRcv.Count == 0 || delayedMdiRcv.Last().DueCycles <= dueCycles)
        {
            delayedMdiRcv.Enqueue(item);
            return;
        }

        List<DelayedMdiRcvPacket> items = [.. delayedMdiRcv, item];
        items.Sort((left, right) => left.DueCycles.CompareTo(right.DueCycles));
        delayedMdiRcv.Clear();

        foreach (DelayedMdiRcvPacket sorted in items)
        {
            delayedMdiRcv.Enqueue(sorted);
        }
    }

    private void PumpDelayedMdiRcv(long cycles)
    {
        while (delayedMdiRcv.Count > 0 && delayedMdiRcv.Peek().DueCycles <= cycles)
        {
            DelayedMdiRcvPacket item = delayedMdiRcv.Dequeue();
            byte[] packet = item.Packet;
            if (GsmBlockCodec.IsImmediateAssignment(packet))
            {
                PublishPendingRandomAccessReference();
            }

            pendingMdiRcv.Enqueue(new PendingMdiRcvPacket(packet, item.DueCycles));
        }

        PumpMdiRcv();
    }

    private void PumpMdiRcv()
    {
        ExpirePendingMdiRcv(currentCycles);

        if (pendingMdiRcv.Count == 0)
        {
            return;
        }

        if (CanPostMdiRcv())
        {
            PostMdiRcv(pendingMdiRcv.Dequeue().Packet);
        }
    }

    private void ExpirePendingMdiRcv(long cycles)
    {
        while (pendingMdiRcv.Count > 0 &&
            cycles - pendingMdiRcv.Peek().EnqueuedCycles >= MdiRcvPacketTimeoutCycles)
        {
            PendingMdiRcvPacket expired = pendingMdiRcv.Dequeue();
            trace?.Event($"DSP MDIRCV pending packet timed out type {expired.Type:X2} len={expired.Length:X2}");
        }
    }

    private bool CanPostMdiRcv()
    {
        ushort tail = Read16(0x1C8);
        ushort head = Read16(0x1CA);

        if (tail is < 0x80 or > 0xE3 || head is < 0x80 or > 0xE3)
        {
            return true;
        }

        return tail == head;
    }

    private void PostMdiRcv(ReadOnlySpan<byte> packet)
    {
        ushort tail = Read16(0x1C8);

        if (tail is < 0x80 or > 0xE3)
        {
            trace?.Event($"DSP MDIRCV tail uninitialized ({tail:X4}), forcing 0080");
            tail = 0x80;
        }

        int words = (packet.Length + 1) / 2;

        for (int i = 0; i < words * 2; i++)
        {
            sharedRam[0x100 + ((tail - 0x80) * 2 + i) % 200] = i < packet.Length ? packet[i] : (byte)0;
        }

        Write16(0x1C8, (ushort)((tail - 0x80 + words) % 100 + 0x80));
        trace?.Event($"DSP MDIRCV posted type {packet[1]:X2} len={packet[0]:X2} at word {tail:X2}");
        RaiseFiq0?.Invoke();
    }

    private void HandleCodeblockReply(ushort value)
    {
        Write16(0x0E4, 0);

        if (value == 0x0004)
        {
            currentBlock = 0;
        }

        if (currentBlock != 0)
        {
            Write16(0x0E2, currentBlock);
            trace?.Event($"DSP requests more of codeblock {currentBlock:X2}");
        }
        else if (blockIndex < CodeblockSequence.Length)
        {
            currentBlock = CodeblockSequence[blockIndex++];
            Write16(0x0E2, currentBlock);
            trace?.Event($"DSP requests codeblock {currentBlock:X2}");
        }
        else
        {
            trace?.Event("DSP codeblock upload complete, main code running");
        }

        RaiseIrq4?.Invoke();
    }

    private void Boot()
    {
        for (int i = 0; i < InitData.Length; i++)
        {
            Write16((uint)(0xA8 + i * 2), InitData[i]);
        }

        trace?.Event("DSP boot");
    }

    private void Write16(uint offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(sharedRam.AsSpan((int)offset, 2), value);
    }

    private ushort Read16(uint offset)
    {
        return BinaryPrimitives.ReadUInt16BigEndian(sharedRam.AsSpan((int)offset, 2));
    }

    private static string IncomingGsmRequestName(IncomingGsmRequestKind kind) =>
        kind == IncomingGsmRequestKind.Call ? "call" : "SMS";

    private enum IncomingGsmRequestKind
    {
        Call,
        Sms,
    }

    private readonly record struct PendingMdiRcvPacket(byte[] Packet, long EnqueuedCycles)
    {
        public byte Length => Packet.Length > 0 ? Packet[0] : (byte)0;

        public byte Type => Packet.Length > 1 ? Packet[1] : (byte)0;
    }

    private readonly record struct DelayedMdiRcvPacket(long DueCycles, byte[] Packet);

    private readonly record struct PendingDedicatedDownlinkFrame(byte[] Frame, long EnqueuedCycles);

    private readonly record struct IncomingGsmRequest(
        IncomingGsmRequestKind Kind,
        string Address,
        string Text,
        ushort DestinationPort,
        byte[] Payload,
        SmartMessageConcatenation Concatenation = default,
        Guid RequestId = default,
        DateTimeOffset SentAt = default);
}
