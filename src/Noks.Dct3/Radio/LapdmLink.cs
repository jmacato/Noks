using Noks.Dct3.Audio;
using Noks.Dct3.Messaging;
using Noks.Dct3.Sim;
using Noks.Dct3.State;
namespace Noks.Dct3.Radio;

internal sealed class LapdmLink
{
    public const int FrameLength = 23;
    private const int MaximumInformationLength = FrameLength - 3;

    private readonly Action<string>? trace;
    private readonly GsmNetwork network;
    private readonly Dictionary<Guid, byte> outgoingRequestSapis = [];
    private byte currentNetworkSapi;
    private bool suppressImsiPagingAfterRegistration;
    private int pendingIncomingServiceCount;
    private long currentCycles;
    private bool nextPendingCycleDirty = true;
    private long nextPendingCycle = long.MaxValue;
    private readonly LinkState[] links =
    [
        new LinkState(),
        new LinkState(),
        new LinkState(),
        new LinkState(),
        new LinkState(),
        new LinkState(),
        new LinkState(),
        new LinkState(),
    ];

    public LapdmLink(
        Action<string>? trace,
        Func<DateTimeOffset>? networkLocalTimeProvider = null,
        Action? beforeNetworkTimeInformationQueued = null,
        string pagingImsi = SimCard.DefaultImsi,
        string networkName = Dct3PhoneSettings.DefaultNetworkName,
        Action<OutgoingNetworkRequest>? outgoingNetworkRequest = null,
        Action<CallTransition>? callTransition = null,
        Action<CallAudioAnnouncement>? callAudioAnnouncement = null)
    {
        this.trace = trace;
        Action<OutgoingNetworkRequest>? publishRequest = outgoingNetworkRequest is null
            ? null
            : request =>
            {
                outgoingRequestSapis[request.RequestId] = currentNetworkSapi;
                outgoingNetworkRequest(request);
            };
        network = new GsmNetwork(
            trace,
            networkLocalTimeProvider,
            beforeNetworkTimeInformationQueued,
            pagingImsi,
            networkName,
            publishRequest,
            callTransition,
            callAudioAnnouncement);
    }

    public bool SuppressImsiPagingAfterRegistration => suppressImsiPagingAfterRegistration;

    public int PendingIncomingServiceCount => pendingIncomingServiceCount;

    public void QueueIncomingCall(string callingNumber) =>
        InvokeNetwork(target => target.QueueIncomingCall(callingNumber));

    public void QueueIncomingCall(Guid requestId, string callingNumber) =>
        InvokeNetwork(target => target.QueueIncomingCall(requestId, callingNumber));

    public void QueueIncomingSms(string originator, string text) =>
        QueueIncomingSms(originator, text, default);

    public void QueueIncomingSms(string originator, string text, DateTimeOffset sentAt) =>
        InvokeNetwork(target => target.QueueIncomingSms(originator, text, sentAt));

    public void QueueIncomingSmartMessage(
        string originator,
        ushort destinationPort,
        ReadOnlySpan<byte> payload,
        SmartMessageConcatenation concatenation = default)
    {
        byte[] copiedPayload = payload.ToArray();
        InvokeNetwork(target => target.QueueIncomingSmartMessage(
            originator,
            destinationPort,
            copiedPayload,
            concatenation));
    }

    public UplinkResult ResolveNetworkRequest(ResolveNetworkRequest resolution, long cycles = 0)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        currentCycles = cycles;
        if (!outgoingRequestSapis.TryGetValue(resolution.RequestId, out byte sapi))
        {
            _ = InvokeNetwork(target => target.ResolveNetworkRequest(resolution));
            return UplinkResult.None;
        }

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = InvokeNetwork(
            target => target.ResolveNetworkRequest(resolution));
        outgoingRequestSapis.Remove(resolution.RequestId);
        List<byte[]> frames = [];
        foreach (GsmNetwork.DownlinkMessage message in messages)
        {
            frames.AddRange(BuildDownlinkFrames(
                message,
                sapi,
                links[sapi].NextUplinkReceiveSequence));
        }

        MarkPendingCycleDirty();
        return new UplinkResult(frames, ReleaseAfterDownlinkFrames: false);
    }

    public UplinkResult TerminateNetworkCall(Guid requestId, long cycles = 0)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A call termination requires a non-empty request ID.", nameof(requestId));
        }

        currentCycles = cycles;
        const byte callControlSapi = 0;
        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = InvokeNetwork(
            target => target.TerminateNetworkCall(requestId));
        List<byte[]> frames = [];
        foreach (GsmNetwork.DownlinkMessage message in messages)
        {
            frames.AddRange(BuildDownlinkFrames(
                message,
                callControlSapi,
                links[callControlSapi].NextUplinkReceiveSequence));
        }

        MarkPendingCycleDirty();
        return new UplinkResult(frames, ReleaseAfterDownlinkFrames: false);
    }

    public UplinkResult ConnectNetworkCall(Guid requestId, long cycles = 0)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A call connection requires a non-empty request ID.", nameof(requestId));
        }

        currentCycles = cycles;
        const byte callControlSapi = 0;
        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = InvokeNetwork(
            target => target.ConnectNetworkCall(requestId));
        List<byte[]> frames = [];
        foreach (GsmNetwork.DownlinkMessage message in messages)
        {
            frames.AddRange(BuildDownlinkFrames(
                message,
                callControlSapi,
                links[callControlSapi].NextUplinkReceiveSequence));
        }

        MarkPendingCycleDirty();
        return new UplinkResult(frames, ReleaseAfterDownlinkFrames: false);
    }

    public void Reset()
    {
        foreach (LinkState link in links)
        {
            link.Reset();
        }

        InvokeNetwork(static target => target.Reset());
        outgoingRequestSapis.Clear();
        MarkPendingCycleDirty();
    }

    public UplinkResult HandleUplink(byte logicalChannel, ReadOnlySpan<byte> layer2) =>
        HandleUplink(logicalChannel, layer2, cycles: 0);

    public UplinkResult HandleUplink(byte logicalChannel, ReadOnlySpan<byte> layer2, long cycles)
    {
        currentCycles = cycles;
        try
        {
            return HandleUplinkCore(logicalChannel, layer2);
        }
        finally
        {
            MarkPendingCycleDirty();
        }
    }

    private UplinkResult HandleUplinkCore(byte logicalChannel, ReadOnlySpan<byte> layer2)
    {
        if (TryBuildUaForSabm(layer2, out byte sapi, out int informationLength, out byte[] uaFrame))
        {
            LinkState link = links[sapi];
            link.Reset();

            List<byte[]> frames = [uaFrame];
            trace?.Invoke($"DSP LAPDm SABM logical={logicalChannel:X2} sapi={sapi} information={informationLength} -> UA queued");

            ReadOnlySpan<byte> sabmInformation = layer2.Slice(3, informationLength);
            if (sabmInformation.Length != 0)
            {
                byte[] information = sabmInformation.ToArray();
                IReadOnlyList<GsmNetwork.DownlinkMessage> messages = InvokeNetwork(
                    target => target.HandleEstablishedLayer3(information));
                foreach (GsmNetwork.DownlinkMessage message in messages)
                {
                    frames.AddRange(BuildDownlinkFrames(message, sapi, responseReceiveSequence: 0));
                }
            }

            return new UplinkResult(frames, ReleaseAfterDownlinkFrames: false);
        }

        if (TryBuildUaForDisc(layer2, out sapi, out uaFrame))
        {
            trace?.Invoke($"DSP LAPDm DISC logical={logicalChannel:X2} sapi={sapi} -> UA queued");
            return new UplinkResult([uaFrame], ReleaseAfterDownlinkFrames: true);
        }

        if (TryGetUaResponse(layer2, out sapi))
        {
            LinkState link = links[sapi];
            trace?.Invoke($"DSP LAPDm UA logical={logicalChannel:X2} sapi={sapi}");

            if (link.PendingModeSettingAcknowledgement is not { } acknowledgement)
            {
                return UplinkResult.None;
            }

            link.PendingModeSettingAcknowledgement = null;
            link.PendingModeSettingAcknowledgementCycles = -1;
            List<byte[]> frames = [];

            IReadOnlyList<GsmNetwork.DownlinkMessage> messages = InvokeNetwork(
                target => target.HandleDownlinkAcknowledgement(acknowledgement));
            foreach (GsmNetwork.DownlinkMessage message in messages)
            {
                frames.AddRange(BuildDownlinkFrames(message, sapi, link.NextUplinkReceiveSequence));
            }

            return new UplinkResult(frames, ReleaseAfterDownlinkFrames: false);
        }

        if (TryGetInformationFrame(layer2, out sapi, out byte sendSequence, out byte receiveSequence, out bool moreData, out ReadOnlySpan<byte> iFrameInformation))
        {
            bool pollFinal = (layer2[1] & 0x10) != 0;
            LinkState link = links[sapi];
            trace?.Invoke($"DSP LAPDm I logical={logicalChannel:X2} sapi={sapi} ns={sendSequence} nr={receiveSequence} m={(moreData ? 1 : 0)} information={iFrameInformation.Length}");

            byte[]? completeInformation = null;
            if (sendSequence == link.NextUplinkReceiveSequence)
            {
                completeInformation = link.AcceptInformation(iFrameInformation, moreData, currentCycles);
                link.NextUplinkReceiveSequence = (byte)((link.NextUplinkReceiveSequence + 1) & 0x07);
            }

            List<byte[]> frames = AcknowledgePendingDownlinkFrames(link, sapi, receiveSequence, link.NextUplinkReceiveSequence);
            frames.Add(BuildReceiveReadyFrame(sapi, link.NextUplinkReceiveSequence, pollFinal));

            if (completeInformation is not null)
            {
                IReadOnlyList<GsmNetwork.DownlinkMessage> messages;
                currentNetworkSapi = sapi;
                try
                {
                    messages = InvokeNetwork(
                        target => target.HandleActiveLayer3(completeInformation));
                }
                finally
                {
                    currentNetworkSapi = 0;
                }

                foreach (GsmNetwork.DownlinkMessage message in messages)
                {
                    frames.AddRange(BuildDownlinkFrames(message, sapi, link.NextUplinkReceiveSequence));
                }
            }

            return new UplinkResult(frames, ReleaseAfterDownlinkFrames: false);
        }

        return HandleSupervisoryFrame(logicalChannel, layer2);
    }

    public long NextPendingExpiryCycle(long timeoutCycles)
    {
        if (nextPendingCycleDirty)
        {
            nextPendingCycle = long.MaxValue;

            foreach (LinkState link in links)
            {
                nextPendingCycle = Math.Min(nextPendingCycle, link.NextPendingCycle);
            }

            nextPendingCycleDirty = false;
        }

        return nextPendingCycle == long.MaxValue
            ? long.MaxValue
            : nextPendingCycle + timeoutCycles;
    }

    public bool ExpirePending(long cycles, long timeoutCycles)
    {
        bool expired = false;

        foreach (LinkState link in links)
        {
            if (link.ExpirePending(cycles, timeoutCycles))
            {
                expired = true;
            }
        }

        if (expired)
        {
            trace?.Invoke("DSP LAPDm pending state timed out");
            MarkPendingCycleDirty();
        }

        return expired;
    }

    private void MarkPendingCycleDirty()
    {
        nextPendingCycleDirty = true;
    }

    public static byte[] BuildFillFrame()
    {
        byte[] layer2 = new byte[FrameLength];
        layer2.AsSpan().Fill(0x2B);
        layer2[0] = 0x03;
        layer2[1] = 0x03;
        layer2[2] = 0x01;
        return layer2;
    }

    public static string DescribeDownlinkFrame(ReadOnlySpan<byte> layer2)
    {
        if (layer2.Length > 1 && (layer2[1] & 0xEF) == 0x63)
        {
            return "UA";
        }

        if (layer2.Length > 1 && (layer2[1] & 0x01) == 0)
        {
            return "I";
        }

        return "frame";
    }

    private UplinkResult HandleSupervisoryFrame(byte logicalChannel, ReadOnlySpan<byte> layer2)
    {
        if (layer2.Length < 3)
        {
            return UplinkResult.None;
        }

        byte address = layer2[0];
        byte control = layer2[1];
        byte lengthIndicator = layer2[2];

        if ((address & 0x01) == 0 || (control & 0x0F) != 0x01 || lengthIndicator != 0x01)
        {
            return UplinkResult.None;
        }

        byte sapi = (byte)((address >> 2) & 0x07);
        LinkState link = links[sapi];
        byte receiveSequence = (byte)(control >> 5);
        trace?.Invoke($"DSP LAPDm RR logical={logicalChannel:X2} sapi={sapi} nr={receiveSequence}");

        List<byte[]> frames = AcknowledgePendingDownlinkFrames(link, sapi, receiveSequence, link.NextUplinkReceiveSequence);

        return frames.Count == 0
            ? UplinkResult.None
            : new UplinkResult(frames, ReleaseAfterDownlinkFrames: false);
    }

    private List<byte[]> AcknowledgePendingDownlinkFrames(LinkState link, byte sapi, byte receiveSequence, byte responseReceiveSequence)
    {
        List<byte[]> frames = [];

        if (!HasPendingAcknowledgement(link, receiveSequence))
        {
            return frames;
        }

        while (link.PendingAcknowledgements.Count > 0)
        {
            PendingAcknowledgement acknowledgement = link.PendingAcknowledgements.Dequeue();

            IReadOnlyList<GsmNetwork.DownlinkMessage> messages = InvokeNetwork(
                target => target.HandleDownlinkAcknowledgement(acknowledgement.Kind));
            foreach (GsmNetwork.DownlinkMessage message in messages)
            {
                frames.AddRange(BuildDownlinkFrames(message, sapi, responseReceiveSequence));
            }

            if (acknowledgement.ReceiveSequence == receiveSequence)
            {
                break;
            }
        }

        return frames;
    }

    private static bool HasPendingAcknowledgement(LinkState link, byte receiveSequence)
    {
        foreach (PendingAcknowledgement acknowledgement in link.PendingAcknowledgements)
        {
            if (acknowledgement.ReceiveSequence == receiveSequence)
            {
                return true;
            }
        }

        return false;
    }

    private void InvokeNetwork(Action<GsmNetwork> action) =>
        InvokeNetwork(
            target =>
            {
                action(target);
                return true;
            });

    private TResult InvokeNetwork<TResult>(Func<GsmNetwork, TResult> action)
    {
        TResult result = action(network);
        suppressImsiPagingAfterRegistration = network.SuppressImsiPagingAfterRegistration;
        pendingIncomingServiceCount = network.PendingIncomingServiceCount;
        return result;
    }

    private List<byte[]> BuildDownlinkFrames(GsmNetwork.DownlinkMessage message, byte fallbackSapi, byte responseReceiveSequence)
    {
        if (message.Kind == GsmNetwork.DownlinkMessageKind.Sapi3Establishment)
        {
            byte sapi = ResolveSapi(message, fallbackSapi);
            LinkState link = links[sapi];
            link.Reset();
            link.PendingModeSettingAcknowledgement = message.Kind;
            link.PendingModeSettingAcknowledgementCycles = currentCycles;
            return [BuildSabmCommand(sapi)];
        }

        return BuildAcknowledgedIFrames(message, fallbackSapi, responseReceiveSequence);
    }

    private List<byte[]> BuildAcknowledgedIFrames(GsmNetwork.DownlinkMessage message, byte fallbackSapi, byte responseReceiveSequence)
    {
        byte sapi = ResolveSapi(message, fallbackSapi);
        LinkState link = links[sapi];
        byte receiveSequence = message.Sapi == GsmNetwork.DownlinkMessage.DefaultResponseSapi
            ? responseReceiveSequence
            : link.NextUplinkReceiveSequence;
        List<byte[]> frames = [];

        for (int offset = 0; offset < message.Information.Length || offset == 0; offset += MaximumInformationLength)
        {
            int remaining = message.Information.Length - offset;
            int count = Math.Min(Math.Max(remaining, 0), MaximumInformationLength);
            bool moreData = remaining > MaximumInformationLength;
            GsmNetwork.DownlinkMessageKind acknowledgementKind = moreData
                ? GsmNetwork.DownlinkMessageKind.Segment
                : message.Kind;
            byte[] layer2 = BuildIFrame(
                sapi,
                message.Information.AsSpan(offset, count),
                link.DownlinkSendSequence,
                receiveSequence,
                moreData);
            frames.Add(layer2);
            link.DownlinkSendSequence = (byte)((link.DownlinkSendSequence + 1) & 0x07);
            link.PendingAcknowledgements.Enqueue(new PendingAcknowledgement(link.DownlinkSendSequence, acknowledgementKind, currentCycles));

            if (remaining <= MaximumInformationLength)
            {
                break;
            }
        }

        return frames;
    }

    private static byte ResolveSapi(GsmNetwork.DownlinkMessage message, byte fallbackSapi) =>
        message.Sapi == GsmNetwork.DownlinkMessage.DefaultResponseSapi ? fallbackSapi : (byte)message.Sapi;

    private static byte[] BuildIFrame(byte sapi, ReadOnlySpan<byte> information, byte sendSequence, byte receiveSequence, bool moreData = false)
    {
        byte[] layer2 = new byte[FrameLength];
        layer2.AsSpan().Fill(0x2B);
        layer2[0] = (byte)((sapi << 2) | 0x03);
        layer2[1] = (byte)(((receiveSequence & 0x07) << 5) | ((sendSequence & 0x07) << 1));
        layer2[2] = (byte)((information.Length << 2) | (moreData ? 0x03 : 0x01));
        information.CopyTo(layer2.AsSpan(3));
        return layer2;
    }

    private static byte[] BuildSabmCommand(byte sapi)
    {
        byte[] layer2 = new byte[FrameLength];
        layer2.AsSpan().Fill(0x2B);
        layer2[0] = (byte)((sapi << 2) | 0x03);
        layer2[1] = 0x3F;
        layer2[2] = 0x01;
        return layer2;
    }

    private static byte[] BuildReceiveReadyFrame(byte sapi, byte receiveSequence, bool final = false)
    {
        byte[] layer2 = new byte[FrameLength];
        layer2.AsSpan().Fill(0x2B);
        // This acknowledges an MS command, so the BSS sends a response (C/R=0).
        layer2[0] = (byte)((sapi << 2) | 0x01);
        layer2[1] = (byte)(((receiveSequence & 0x07) << 5) | (final ? 0x11 : 0x01));
        layer2[2] = 0x01;
        return layer2;
    }

    private static bool TryGetInformationFrame(
        ReadOnlySpan<byte> layer2,
        out byte sapi,
        out byte sendSequence,
        out byte receiveSequence,
        out bool moreData,
        out ReadOnlySpan<byte> information)
    {
        sapi = 0;
        sendSequence = 0;
        receiveSequence = 0;
        moreData = false;
        information = [];

        if (layer2.Length < 3)
        {
            return false;
        }

        byte address = layer2[0];
        byte control = layer2[1];
        byte lengthIndicator = layer2[2];

        if ((address & 0x01) == 0 || (control & 0x01) != 0 || (lengthIndicator & 0x01) == 0)
        {
            return false;
        }

        sapi = (byte)((address >> 2) & 0x07);
        sendSequence = (byte)((control >> 1) & 0x07);
        receiveSequence = (byte)(control >> 5);
        moreData = (lengthIndicator & 0x02) != 0;
        int informationLength = lengthIndicator >> 2;

        if (informationLength == 0 ||
            informationLength > MaximumInformationLength ||
            layer2.Length < 3 + informationLength ||
            (moreData && informationLength != MaximumInformationLength))
        {
            return false;
        }

        information = layer2.Slice(3, informationLength);
        return true;
    }

    private static bool TryBuildUaForSabm(ReadOnlySpan<byte> layer2, out byte sapi, out int informationLength, out byte[] uaFrame)
    {
        sapi = 0;
        informationLength = 0;
        uaFrame = [];

        if (layer2.Length < 3)
        {
            return false;
        }

        byte address = layer2[0];
        byte control = layer2[1];
        byte lengthIndicator = layer2[2];

        if ((address & 0x01) == 0 || (control & 0xEF) != 0x2F || (lengthIndicator & 0x01) == 0 || (lengthIndicator & 0x02) != 0)
        {
            return false;
        }

        sapi = (byte)((address >> 2) & 0x07);
        informationLength = lengthIndicator >> 2;

        if (layer2.Length < 3 + informationLength || 3 + informationLength > FrameLength)
        {
            return false;
        }

        uaFrame = new byte[FrameLength];
        uaFrame.AsSpan().Fill(0x2B);
        uaFrame[0] = (byte)((sapi << 2) | 0x01);
        uaFrame[1] = (byte)(0x63 | (control & 0x10));
        uaFrame[2] = lengthIndicator;
        layer2.Slice(3, informationLength).CopyTo(uaFrame.AsSpan(3));
        return true;
    }

    private static bool TryBuildUaForDisc(ReadOnlySpan<byte> layer2, out byte sapi, out byte[] uaFrame)
    {
        sapi = 0;
        uaFrame = [];

        if (layer2.Length < 3)
        {
            return false;
        }

        byte address = layer2[0];
        byte control = layer2[1];
        byte lengthIndicator = layer2[2];

        if ((address & 0x01) == 0 || (control & 0xEF) != 0x43 || lengthIndicator != 0x01)
        {
            return false;
        }

        sapi = (byte)((address >> 2) & 0x07);
        uaFrame = new byte[FrameLength];
        uaFrame.AsSpan().Fill(0x2B);
        uaFrame[0] = (byte)((sapi << 2) | 0x01);
        uaFrame[1] = (byte)(0x63 | (control & 0x10));
        uaFrame[2] = 0x01;
        return true;
    }

    private static bool TryGetUaResponse(ReadOnlySpan<byte> layer2, out byte sapi)
    {
        sapi = 0;

        if (layer2.Length < 3)
        {
            return false;
        }

        byte address = layer2[0];
        byte control = layer2[1];
        byte lengthIndicator = layer2[2];

        if ((address & 0x03) != 0x03 || (control & 0xEF) != 0x63 || lengthIndicator != 0x01)
        {
            return false;
        }

        sapi = (byte)((address >> 2) & 0x07);
        return true;
    }

    public readonly record struct UplinkResult(IReadOnlyList<byte[]> DownlinkFrames, bool ReleaseAfterDownlinkFrames)
    {
        public static UplinkResult None { get; } = new(Array.Empty<byte[]>(), ReleaseAfterDownlinkFrames: false);
    }

    private readonly record struct PendingAcknowledgement(byte ReceiveSequence, GsmNetwork.DownlinkMessageKind Kind, long EnqueuedCycles);

    private readonly record struct NetworkInvocation<TResult>(
        TResult Result,
        bool SuppressImsiPagingAfterRegistration,
        int PendingIncomingServiceCount);

    private sealed class LinkState
    {
        public byte DownlinkSendSequence { get; set; }

        public byte NextUplinkReceiveSequence { get; set; }

        public Queue<PendingAcknowledgement> PendingAcknowledgements { get; } = new();

        public GsmNetwork.DownlinkMessageKind? PendingModeSettingAcknowledgement { get; set; }

        public long PendingModeSettingAcknowledgementCycles { get; set; } = -1;

        public void Reset()
        {
            DownlinkSendSequence = 0;
            NextUplinkReceiveSequence = 0;
            PendingAcknowledgements.Clear();
            PendingInformationSegments.Clear();
            PendingModeSettingAcknowledgement = null;
            PendingModeSettingAcknowledgementCycles = -1;
            PendingInformationSegmentsCycles = -1;
        }

        public List<byte> PendingInformationSegments { get; } = [];

        public long PendingInformationSegmentsCycles { get; set; } = -1;

        public long NextPendingCycle
        {
            get
            {
                long next = long.MaxValue;

                if (PendingAcknowledgements.Count > 0)
                {
                    next = Math.Min(next, PendingAcknowledgements.Peek().EnqueuedCycles);
                }

                if (PendingModeSettingAcknowledgement is not null &&
                    PendingModeSettingAcknowledgementCycles >= 0)
                {
                    next = Math.Min(next, PendingModeSettingAcknowledgementCycles);
                }

                if (PendingInformationSegments.Count > 0 &&
                    PendingInformationSegmentsCycles >= 0)
                {
                    next = Math.Min(next, PendingInformationSegmentsCycles);
                }

                return next;
            }
        }

        public bool ExpirePending(long cycles, long timeoutCycles)
        {
            bool expired = false;

            while (PendingAcknowledgements.Count > 0 &&
                cycles - PendingAcknowledgements.Peek().EnqueuedCycles >= timeoutCycles)
            {
                PendingAcknowledgements.Dequeue();
                expired = true;
            }

            if (PendingModeSettingAcknowledgement is not null &&
                PendingModeSettingAcknowledgementCycles >= 0 &&
                cycles - PendingModeSettingAcknowledgementCycles >= timeoutCycles)
            {
                PendingModeSettingAcknowledgement = null;
                PendingModeSettingAcknowledgementCycles = -1;
                expired = true;
            }

            if (PendingInformationSegments.Count > 0 &&
                PendingInformationSegmentsCycles >= 0 &&
                cycles - PendingInformationSegmentsCycles >= timeoutCycles)
            {
                PendingInformationSegments.Clear();
                PendingInformationSegmentsCycles = -1;
                expired = true;
            }

            return expired;
        }

        public byte[]? AcceptInformation(ReadOnlySpan<byte> information, bool moreData, long cycles)
        {
            if (moreData)
            {
                if (PendingInformationSegments.Count == 0)
                {
                    PendingInformationSegmentsCycles = cycles;
                }

                foreach (byte value in information)
                {
                    PendingInformationSegments.Add(value);
                }

                return null;
            }

            if (PendingInformationSegments.Count == 0)
            {
                return information.ToArray();
            }

            byte[] completeInformation = new byte[PendingInformationSegments.Count + information.Length];
            PendingInformationSegments.CopyTo(completeInformation);
            information.CopyTo(completeInformation.AsSpan(PendingInformationSegments.Count));
            PendingInformationSegments.Clear();
            PendingInformationSegmentsCycles = -1;
            return completeInformation;
        }
    }
}
