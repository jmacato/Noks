using Noks.Dct3.Audio;
using Noks.Dct3.Messaging;
using Noks.Dct3.Sim;
using Noks.Dct3.State;
namespace Noks.Dct3.Radio;

internal sealed class GsmNetwork
{
    private const byte SmsProtocolDiscriminator = 0x09;
    private const byte LocationUpdatingAcceptMessageType = 0x02;
    private const byte LocationUpdatingRequestMessageType = 0x08;
    private const byte CipheringModeCompleteMessageType = 0x32;
    private const byte PagingResponseMessageType = 0x27;
    private const byte CmServiceRequestMessageType = 0x24;
    private const byte AlertingMessageType = 0x01;
    private const byte EmergencySetupMessageType = 0x0E;
    private const byte CallProceedingMessageType = 0x02;
    private const byte CallConfirmedMessageType = 0x08;
    private const byte ConnectMessageType = 0x07;
    private const byte ConnectAcknowledgeMessageType = 0x0F;
    private const byte DisconnectMessageType = 0x25;
    private const byte ReleaseMessageType = 0x2D;
    private const byte ReleaseCompleteMessageType = 0x2A;
    private const byte RpDataNetworkToMobileMessageType = 0x01;
    private const byte MobileTerminatedSmsTransactionAndProtocolDiscriminator = SmsProtocolDiscriminator;
    private const byte DefaultSmsReference = 0x40;
    private const int RoutableNoksNumberDigits = 13;
    private const string InvalidNumberAnnouncement = "The number you dialed is invalid.";
    private const string EmergencyCallsUnsupportedAnnouncement =
        "This emulated network does not support emergency calls. " +
        "Please use an actual mobile phone for emergencies.";
    private static readonly HashSet<string> EmergencyNumbers =
    [
        "000",
        "112",
        "911",
        "999",
    ];

    private readonly Action<string>? trace;
    private readonly Func<DateTimeOffset> networkLocalTimeProvider;
    private readonly Action? beforeNetworkTimeInformationQueued;
    private readonly Action<OutgoingNetworkRequest>? outgoingNetworkRequest;
    private readonly Action<CallTransition>? callTransition;
    private readonly Action<CallAudioAnnouncement>? callAudioAnnouncement;
    private readonly byte[] locationAreaIdentity;
    private readonly string networkName;
    private readonly object inputLock = new();
    private readonly Queue<IncomingService> pendingIncomingServices = new();
    private IncomingService activeIncomingService;
    private byte nextSmsReference = DefaultSmsReference;
    private bool networkTimeQueuedOnActiveConnection;
    private PendingOutgoingNetworkRequest? pendingOutgoingRequest;
    private ActiveCall? activeCall;

    public RegistrationState State { get; private set; }

    public CmServiceType ActiveService { get; private set; }

    public PostRegistrationEmulationMode PostRegistrationMode => PostRegistrationEmulationMode.BroadcastSystemInformationWithNoIdentityPagingFill;

    public OutgoingCallEmulationMode OutgoingCallMode => OutgoingCallEmulationMode.KeepConnectedUntilPhoneDisconnects;

    public bool SuppressImsiPagingAfterRegistration =>
        State == RegistrationState.Released &&
        PostRegistrationMode == PostRegistrationEmulationMode.BroadcastSystemInformationWithNoIdentityPagingFill;

    public int PendingIncomingServiceCount => pendingIncomingServices.Count;

    public GsmNetwork(
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
        locationAreaIdentity = GsmIdentity.EncodeLaiFromImsi(pagingImsi);
        this.networkName = SanitizeNetworkName(networkName);
        this.networkLocalTimeProvider = networkLocalTimeProvider ?? (() => DateTimeOffset.Now);
        this.beforeNetworkTimeInformationQueued = beforeNetworkTimeInformationQueued;
        this.outgoingNetworkRequest = outgoingNetworkRequest;
        this.callTransition = callTransition;
        this.callAudioAnnouncement = callAudioAnnouncement;
    }

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

        SubmitInput(GsmNetworkInput.QueueIncomingCall(requestId, callingNumber));
    }

    public void QueueIncomingSms(string originator, string text)
    {
        QueueIncomingSms(originator, text, default);
    }

    public void QueueIncomingSms(string originator, string text, DateTimeOffset sentAt)
    {
        SubmitInput(GsmNetworkInput.QueueIncomingSms(originator, text, sentAt));
    }

    public void QueueIncomingSmartMessage(
        string originator,
        ushort destinationPort,
        ReadOnlySpan<byte> payload,
        SmartMessageConcatenation concatenation = default)
    {
        SubmitInput(GsmNetworkInput.QueueIncomingSmartMessage(
            originator,
            destinationPort,
            payload,
            concatenation));
    }

    public void Reset()
    {
        SubmitInput(GsmNetworkInput.Reset());
    }

    public IReadOnlyList<DownlinkMessage> HandleEstablishedLayer3(ReadOnlySpan<byte> information)
    {
        return SubmitInput(GsmNetworkInput.EstablishedLayer3(information.ToArray()));
    }

    public IReadOnlyList<DownlinkMessage> HandleDownlinkAcknowledgement(DownlinkMessageKind kind)
    {
        return SubmitInput(GsmNetworkInput.DownlinkAcknowledgement(kind));
    }

    public IReadOnlyList<DownlinkMessage> HandleActiveLayer3(ReadOnlySpan<byte> information)
    {
        return SubmitInput(GsmNetworkInput.ActiveLayer3(information.ToArray()));
    }

    public IReadOnlyList<DownlinkMessage> ResolveNetworkRequest(ResolveNetworkRequest resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        lock (inputLock)
        {
            return ProcessNetworkResolution(resolution);
        }
    }

    public IReadOnlyList<DownlinkMessage> ConnectNetworkCall(Guid requestId)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A call connection requires a non-empty request ID.", nameof(requestId));
        }

        lock (inputLock)
        {
            if (activeCall is not { } call ||
                call.RequestId != requestId ||
                call.Direction != CallDirection.Outgoing ||
                call.Connected ||
                call.ConnectQueued ||
                call.TerminationQueued)
            {
                return [];
            }

            activeCall = call with { ConnectQueued = true };
            trace?.Invoke($"DSP CC remote call accepted {requestId} -> CONNECT queued");
            return
            [
                new DownlinkMessage(
                    [call.NetworkTransactionAndProtocolDiscriminator, ConnectMessageType],
                    DownlinkMessageKind.Connect),
            ];
        }
    }

    public IReadOnlyList<DownlinkMessage> TerminateNetworkCall(Guid requestId)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A call termination requires a non-empty request ID.", nameof(requestId));
        }

        lock (inputLock)
        {
            if (activeCall is not { } call ||
                call.RequestId != requestId ||
                call.TerminationQueued)
            {
                return [];
            }

            activeCall = call with { TerminationQueued = true };
            trace?.Invoke($"DSP CC remote call termination {requestId} -> RELEASE queued");
            return
            [
                new DownlinkMessage(
                    [call.NetworkTransactionAndProtocolDiscriminator, ReleaseMessageType],
                    DownlinkMessageKind.Release),
            ];
        }
    }

    private IReadOnlyList<DownlinkMessage> SubmitInput(GsmNetworkInput input)
    {
        lock (inputLock)
        {
            return ProcessInput(input);
        }
    }

    private IReadOnlyList<DownlinkMessage> ProcessInput(GsmNetworkInput input)
    {
        return input.Kind switch
        {
            GsmNetworkInputKind.Reset => ProcessReset(),
            GsmNetworkInputKind.QueueIncomingCall => ProcessIncomingCallRequest(input.RequestId, input.Address),
            GsmNetworkInputKind.QueueIncomingSms =>
                ProcessIncomingSmsRequest(
                    input.Address,
                    input.Text,
                    input.DestinationPort,
                    input.Payload,
                    input.Concatenation,
                    input.SentAt),
            GsmNetworkInputKind.EstablishedLayer3 => ProcessEstablishedLayer3(input.Information),
            GsmNetworkInputKind.ActiveLayer3 => ProcessActiveLayer3(input.Information),
            GsmNetworkInputKind.DownlinkAcknowledgement => ProcessDownlinkAcknowledgement(input.DownlinkKind),
            _ => [],
        };
    }

    private IReadOnlyList<DownlinkMessage> ProcessReset()
    {
        State = RegistrationState.Idle;
        ActiveService = CmServiceType.None;
        activeIncomingService = default;
        pendingIncomingServices.Clear();
        nextSmsReference = DefaultSmsReference;
        networkTimeQueuedOnActiveConnection = false;
        pendingOutgoingRequest = null;
        activeCall = null;
        return [];
    }

    private IReadOnlyList<DownlinkMessage> ProcessIncomingCallRequest(Guid requestId, string callingNumber)
    {
        string sanitized = GsmAlphabet.SanitizeDialableAddress(callingNumber);
        pendingIncomingServices.Enqueue(new IncomingService(
            IncomingServiceKind.MobileTerminatedCall,
            sanitized,
            "",
            DestinationPort: 0,
            Payload: [],
            RequestId: requestId));
        trace?.Invoke($"DSP GSM incoming call queued from {sanitized}");
        return [];
    }

    private IReadOnlyList<DownlinkMessage> ProcessIncomingSmsRequest(
        string originator,
        string text,
        ushort destinationPort,
        byte[] payload,
        SmartMessageConcatenation concatenation,
        DateTimeOffset sentAt)
    {
        string sanitizedOriginator = GsmAlphabet.SanitizeDialableAddress(originator);
        if (payload.Length > 0)
        {
            pendingIncomingServices.Enqueue(new IncomingService(
                IncomingServiceKind.MobileTerminatedShortMessage,
                sanitizedOriginator,
                "",
                destinationPort,
                payload,
                concatenation,
                SentAt: sentAt));
            trace?.Invoke(
                $"DSP GSM incoming Smart Messaging SMS queued from {sanitizedOriginator} " +
                $"port={destinationPort:X4} len={payload.Length}" +
                (concatenation.IsMultipart
                    ? $" part={concatenation.PartNumber}/{concatenation.PartCount} ref={concatenation.Reference:X2}"
                    : ""));
        }
        else
        {
            string sanitizedText = GsmAlphabet.SanitizeSmsText(text);
            pendingIncomingServices.Enqueue(new IncomingService(
                IncomingServiceKind.MobileTerminatedShortMessage,
                sanitizedOriginator,
                sanitizedText,
                DestinationPort: 0,
                Payload: [],
                SentAt: sentAt));
            trace?.Invoke($"DSP GSM incoming SMS queued from {sanitizedOriginator} len={sanitizedText.Length}");
        }

        return [];
    }

    private IReadOnlyList<DownlinkMessage> ProcessEstablishedLayer3(ReadOnlySpan<byte> information)
    {
        Layer3Message message = DecodeLayer3Message(information);

        if (State != RegistrationState.Idle)
        {
            return [];
        }

        return message.Kind switch
        {
            Layer3MessageKind.LocationUpdatingRequest => QueueLocationUpdatingAccept(message),
            Layer3MessageKind.CmServiceRequest => QueueCipheringModeCommand(message),
            Layer3MessageKind.PagingResponse => QueueIncomingCipheringModeCommand(),
            _ => [],
        };
    }

    private IReadOnlyList<DownlinkMessage> ProcessDownlinkAcknowledgement(DownlinkMessageKind kind)
    {
        return (kind, State) switch
        {
            (DownlinkMessageKind.LocationUpdatingAccept, RegistrationState.AwaitingLocationUpdatingAcceptAcknowledgement) => QueueNetworkTimeAndChannelRelease(),
            (DownlinkMessageKind.MmInformation, RegistrationState.AwaitingChannelReleaseAcknowledgement) => AcknowledgeMmInformation(),
            (DownlinkMessageKind.MmInformation, RegistrationState.MmConnectionActive) => AcknowledgeMmInformation(),
            (DownlinkMessageKind.ChannelRelease, RegistrationState.AwaitingChannelReleaseAcknowledgement) => AcknowledgeChannelRelease(),
            (DownlinkMessageKind.CipheringModeCommand, RegistrationState.AwaitingCipheringModeCommandAcknowledgement) => AcknowledgeCipheringModeCommand(),
            (DownlinkMessageKind.IncomingCallSetup, RegistrationState.MmConnectionActive) => AcknowledgeIncomingCallSetup(),
            (DownlinkMessageKind.Sapi3Establishment, RegistrationState.MmConnectionActive) => QueueMobileTerminatedSmsCpData(),
            (DownlinkMessageKind.MobileTerminatedSmsCpData, RegistrationState.MmConnectionActive) => AcknowledgeMobileTerminatedSmsCpData(),
            (DownlinkMessageKind.ConnectAcknowledge, RegistrationState.MmConnectionActive) => AcknowledgeConnectAcknowledge(),
            (DownlinkMessageKind.CpAck, RegistrationState.MmConnectionActive) => AcknowledgeCpAck(),
            (DownlinkMessageKind.RpAckCpData, RegistrationState.MmConnectionActive) => AcknowledgeRpAckCpData(),
            (DownlinkMessageKind.CallProceeding, RegistrationState.MmConnectionActive) => AcknowledgeCallProceeding(),
            (DownlinkMessageKind.Alerting, RegistrationState.MmConnectionActive) => AcknowledgeAlerting(),
            (DownlinkMessageKind.Connect, RegistrationState.MmConnectionActive) => AcknowledgeConnect(),
            (DownlinkMessageKind.Release, RegistrationState.MmConnectionActive) => AcknowledgeCallRelease(),
            _ => [],
        };
    }

    private IReadOnlyList<DownlinkMessage> ProcessActiveLayer3(ReadOnlySpan<byte> information)
    {
        if (information.Length < 2)
        {
            return [];
        }

        byte protocolDiscriminator = (byte)(information[0] & 0x0F);
        byte messageType = information[1];
        byte callControlMessageType = Layer3MessageCodec.StripSendSequenceNumber(messageType);

        if (State == RegistrationState.AwaitingCipheringModeComplete)
        {
            if (protocolDiscriminator == GsmProtocol.RadioResourceProtocolDiscriminator && messageType == CipheringModeCompleteMessageType)
            {
                trace?.Invoke("DSP RR CIPHERING MODE COMPLETE received");
                State = RegistrationState.MmConnectionActive;
                trace?.Invoke($"DSP MM connection active ({CmServiceTypeName(ActiveService)})");
                return QueueInitialActiveServiceMessages();
            }

            trace?.Invoke($"DSP L3 ciphering message pd={protocolDiscriminator:X1} type={messageType:X2} len={information.Length}");
            return [];
        }

        if (State != RegistrationState.MmConnectionActive)
        {
            return [];
        }

        if (protocolDiscriminator == SmsProtocolDiscriminator && messageType == GsmProtocol.CpDataMessageType)
        {
            if (ActiveService == CmServiceType.MobileTerminatedShortMessage)
            {
                return HandleMobileTerminatedSmsCpData(information);
            }

            List<DownlinkMessage> messages = [new DownlinkMessage(SmsTpduCodec.BuildCpAck(information[0]), DownlinkMessageKind.CpAck)];

            if (SmsTpduCodec.TryGetMobileOriginatedRpDataReference(information, out byte messageReference))
            {
                trace?.Invoke($"DSP SMS RP-DATA ref={messageReference:X2}");
                if (outgoingNetworkRequest is not null &&
                    TryDecodeMobileOriginatedSms(
                        information,
                        out string destination,
                        out string text,
                        out bool international) &&
                    IsRoutableNoksNumber(destination, international))
                {
                    if (pendingOutgoingRequest is null)
                    {
                        OutgoingNetworkRequest request = new(
                            Guid.NewGuid(),
                            NetworkRequestKind.Sms,
                            destination,
                            text);
                        pendingOutgoingRequest = new PendingOutgoingNetworkRequest(
                            request,
                            information[0],
                            messageReference);
                        outgoingNetworkRequest(request);
                        trace?.Invoke($"DSP SMS submission {request.RequestId} to {destination} awaiting host route");
                    }

                    return messages;
                }

                messages.Add(new DownlinkMessage(SmsTpduCodec.BuildRpAckCpData(information[0], messageReference), DownlinkMessageKind.RpAckCpData));
            }

            trace?.Invoke($"DSP SMS CP-DATA len={information.Length} -> CP-ACK queued");
            return messages;
        }
        else if (protocolDiscriminator == SmsProtocolDiscriminator && messageType == GsmProtocol.CpAckMessageType)
        {
            trace?.Invoke("DSP SMS CP-ACK received");
            if (ActiveService == CmServiceType.MobileTerminatedShortMessage)
            {
                trace?.Invoke("DSP SMS MT CP-DATA acknowledged");
            }
            else if (ActiveService == CmServiceType.ShortMessage)
            {
                return QueueChannelRelease();
            }
        }
        else if (protocolDiscriminator == GsmProtocol.CallControlProtocolDiscriminator &&
            callControlMessageType == EmergencySetupMessageType &&
            ActiveService == CmServiceType.EmergencyCall)
        {
            activeCall = new ActiveCall(
                Guid.NewGuid(),
                CallDirection.Outgoing,
                "emergency",
                (byte)(information[0] ^ 0x80),
                Connected: false,
                ConnectQueued: true,
                AnnouncementKind: CallAudioAnnouncementKind.EmergencyCallsUnsupported,
                AnnouncementText: EmergencyCallsUnsupportedAnnouncement,
                AnnouncementPublished: false,
                TerminationQueued: false);
            trace?.Invoke("DSP CC EMERGENCY SETUP -> unsupported-emergency carrier intercept call");
            return QueueCarrierInterceptCall(information[0]);
        }
        else if (protocolDiscriminator == GsmProtocol.CallControlProtocolDiscriminator &&
            callControlMessageType == GsmProtocol.SetupMessageType &&
            ActiveService == CmServiceType.MobileOriginatingCall)
        {
            if (Layer3MessageCodec.TryDecodeCalledPartyNumber(
                    information,
                    out string destination,
                    out bool international))
            {
                if (outgoingNetworkRequest is not null &&
                    IsRoutableNoksNumber(destination, international))
                {
                    if (pendingOutgoingRequest is null)
                    {
                        OutgoingNetworkRequest request = new(
                            Guid.NewGuid(),
                            NetworkRequestKind.Call,
                            destination,
                            "");
                        pendingOutgoingRequest = new PendingOutgoingNetworkRequest(
                            request,
                            information[0],
                            MessageReference: 0);
                        outgoingNetworkRequest(request);
                        trace?.Invoke($"DSP CC SETUP {request.RequestId} to {destination} awaiting host route");
                    }

                    return [];
                }

                if (!IsRoutableNoksNumber(destination, international))
                {
                    CallAudioAnnouncementKind announcementKind =
                        !international && EmergencyNumbers.Contains(destination)
                            ? CallAudioAnnouncementKind.EmergencyCallsUnsupported
                            : CallAudioAnnouncementKind.InvalidNumber;
                    activeCall = new ActiveCall(
                        Guid.NewGuid(),
                        CallDirection.Outgoing,
                        destination,
                        (byte)(information[0] ^ 0x80),
                        Connected: false,
                        ConnectQueued: true,
                        AnnouncementKind: announcementKind,
                        AnnouncementText: announcementKind == CallAudioAnnouncementKind.EmergencyCallsUnsupported
                            ? EmergencyCallsUnsupportedAnnouncement
                            : InvalidNumberAnnouncement,
                        AnnouncementPublished: false,
                        TerminationQueued: false);
                    trace?.Invoke($"DSP CC invalid destination {destination} -> carrier intercept call");
                    return QueueCarrierInterceptCall(information[0]);
                }
            }

            return QueueAcceptedCall(information[0]);
        }
        else if (protocolDiscriminator == GsmProtocol.CallControlProtocolDiscriminator &&
            callControlMessageType == CallConfirmedMessageType &&
            ActiveService == CmServiceType.MobileTerminatedCall)
        {
            trace?.Invoke("DSP CC CALL CONFIRMED received for incoming call");
        }
        else if (protocolDiscriminator == GsmProtocol.CallControlProtocolDiscriminator &&
            callControlMessageType == AlertingMessageType &&
            ActiveService == CmServiceType.MobileTerminatedCall)
        {
            trace?.Invoke("DSP CC ALERTING received for incoming call");
        }
        else if (protocolDiscriminator == GsmProtocol.CallControlProtocolDiscriminator &&
            callControlMessageType == ConnectMessageType &&
            ActiveService == CmServiceType.MobileTerminatedCall)
        {
            trace?.Invoke("DSP CC CONNECT received for incoming call -> CONNECT ACKNOWLEDGE queued");
            PublishCallTransition(CallTransitionKind.Answer);
            return [new DownlinkMessage(Layer3MessageCodec.BuildCallControlMessage(information[0], ConnectAcknowledgeMessageType), DownlinkMessageKind.ConnectAcknowledge)];
        }
        else if (protocolDiscriminator == GsmProtocol.CallControlProtocolDiscriminator && callControlMessageType == ConnectAcknowledgeMessageType)
        {
            trace?.Invoke("DSP CC CONNECT ACKNOWLEDGE received. The call stays active until the phone disconnects.");
            PublishCallTransition(CallTransitionKind.Connect);
        }
        else if (protocolDiscriminator == GsmProtocol.CallControlProtocolDiscriminator && callControlMessageType == DisconnectMessageType)
        {
            trace?.Invoke($"DSP CC DISCONNECT len={information.Length} -> RELEASE queued");
            PublishTerminalCallTransition();
            return [new DownlinkMessage(Layer3MessageCodec.BuildCallControlMessage(information[0], ReleaseMessageType), DownlinkMessageKind.Release)];
        }
        else if (protocolDiscriminator == GsmProtocol.CallControlProtocolDiscriminator && callControlMessageType == ReleaseCompleteMessageType)
        {
            trace?.Invoke("DSP CC RELEASE COMPLETE received");
            if (ActiveService is
                CmServiceType.MobileOriginatingCall or
                CmServiceType.EmergencyCall or
                CmServiceType.MobileTerminatedCall)
            {
                PublishTerminalCallTransition();
                return QueueChannelRelease();
            }
        }
        else
        {
            trace?.Invoke($"DSP L3 active message pd={protocolDiscriminator:X1} type={messageType:X2} len={information.Length}");
        }

        return [];
    }

    private IReadOnlyList<DownlinkMessage> ProcessNetworkResolution(ResolveNetworkRequest resolution)
    {
        if (pendingOutgoingRequest is not { } pending ||
            pending.Request.RequestId != resolution.RequestId)
        {
            trace?.Invoke($"DSP host route ignored for unknown request {resolution.RequestId}");
            return [];
        }

        pendingOutgoingRequest = null;
        trace?.Invoke(
            $"DSP host route {resolution.Decision} for {pending.Request.Kind} request {resolution.RequestId}");

        if (resolution.Decision == NetworkRequestDecision.Accept)
        {
            if (pending.Request.Kind == NetworkRequestKind.Call)
            {
                activeCall = new ActiveCall(
                    pending.Request.RequestId,
                    CallDirection.Outgoing,
                    pending.Request.NormalizedDestination,
                    (byte)(pending.TransactionAndProtocolDiscriminator ^ 0x80),
                    Connected: false,
                    ConnectQueued: false,
                    AnnouncementKind: CallAudioAnnouncementKind.InvalidNumber,
                    AnnouncementText: "",
                    AnnouncementPublished: false,
                    TerminationQueued: false);
            }

            return pending.Request.Kind == NetworkRequestKind.Call
                ? QueueRoutedCallAlerting(pending.TransactionAndProtocolDiscriminator)
                :
                [
                    new DownlinkMessage(
                        SmsTpduCodec.BuildRpAckCpData(
                            pending.TransactionAndProtocolDiscriminator,
                            pending.MessageReference),
                        DownlinkMessageKind.RpAckCpData),
                ];
        }

        if (pending.Request.Kind == NetworkRequestKind.Sms)
        {
            return
            [
                new DownlinkMessage(
                    SmsTpduCodec.BuildRpErrorCpData(
                        pending.TransactionAndProtocolDiscriminator,
                        pending.MessageReference),
                    DownlinkMessageKind.RpErrorCpData),
            ];
        }

        callTransition?.Invoke(new CallTransition(
            pending.Request.RequestId,
            CallDirection.Outgoing,
            CallTransitionKind.Reject,
            pending.Request.NormalizedDestination));

        List<DownlinkMessage> rejectedCall =
        [
            new DownlinkMessage(
                Layer3MessageCodec.BuildCallControlMessage(
                    pending.TransactionAndProtocolDiscriminator,
                    ReleaseCompleteMessageType),
                DownlinkMessageKind.ReleaseComplete,
                sapi: 0),
        ];
        rejectedCall.AddRange(QueueChannelRelease());
        return rejectedCall;
    }

    private void PublishCallTransition(CallTransitionKind kind)
    {
        if (activeCall is not { } call)
        {
            return;
        }

        if (kind == CallTransitionKind.Connect)
        {
            if (call.Connected)
            {
                return;
            }

            call = call with { Connected = true };
            if (!call.AnnouncementPublished && call.AnnouncementText.Length > 0)
            {
                callAudioAnnouncement?.Invoke(new CallAudioAnnouncement(
                    call.RequestId,
                    call.AnnouncementKind,
                    call.AnnouncementText));
                call = call with { AnnouncementPublished = true };
            }

            activeCall = call;
        }

        callTransition?.Invoke(new CallTransition(
            call.RequestId,
            call.Direction,
            kind,
            call.NormalizedRemoteNumber));
    }

    private void PublishTerminalCallTransition()
    {
        if (activeCall is not { } call)
        {
            return;
        }

        activeCall = null;
        callTransition?.Invoke(new CallTransition(
            call.RequestId,
            call.Direction,
            call.Connected ? CallTransitionKind.Hangup : CallTransitionKind.Reject,
            call.NormalizedRemoteNumber));
    }

    private IReadOnlyList<DownlinkMessage> QueueAcceptedCall(byte transactionAndProtocolDiscriminator)
    {
        trace?.Invoke("DSP CC SETUP -> CALL PROCEEDING/ALERTING/CONNECT queued");
        return
        [
            new DownlinkMessage(Layer3MessageCodec.BuildCallControlMessage(transactionAndProtocolDiscriminator, CallProceedingMessageType), DownlinkMessageKind.CallProceeding),
            new DownlinkMessage(Layer3MessageCodec.BuildCallControlMessage(transactionAndProtocolDiscriminator, AlertingMessageType), DownlinkMessageKind.Alerting),
            new DownlinkMessage(Layer3MessageCodec.BuildCallControlMessage(transactionAndProtocolDiscriminator, ConnectMessageType), DownlinkMessageKind.Connect),
        ];
    }

    private IReadOnlyList<DownlinkMessage> QueueRoutedCallAlerting(byte transactionAndProtocolDiscriminator)
    {
        trace?.Invoke("DSP CC routed SETUP -> CALL PROCEEDING/ALERTING queued pending remote answer");
        return
        [
            new DownlinkMessage(Layer3MessageCodec.BuildCallControlMessage(transactionAndProtocolDiscriminator, CallProceedingMessageType), DownlinkMessageKind.CallProceeding),
            new DownlinkMessage(Layer3MessageCodec.BuildCallControlMessage(transactionAndProtocolDiscriminator, AlertingMessageType), DownlinkMessageKind.Alerting),
        ];
    }

    private IReadOnlyList<DownlinkMessage> QueueCarrierInterceptCall(byte transactionAndProtocolDiscriminator)
    {
        trace?.Invoke("DSP CC carrier intercept -> CALL PROCEEDING/CONNECT queued");
        return
        [
            new DownlinkMessage(Layer3MessageCodec.BuildCallControlMessage(transactionAndProtocolDiscriminator, CallProceedingMessageType), DownlinkMessageKind.CallProceeding),
            new DownlinkMessage(Layer3MessageCodec.BuildCallControlMessage(transactionAndProtocolDiscriminator, ConnectMessageType), DownlinkMessageKind.Connect),
        ];
    }

    private IReadOnlyList<DownlinkMessage> QueueLocationUpdatingAccept(Layer3Message message)
    {
        State = RegistrationState.AwaitingLocationUpdatingAcceptAcknowledgement;
        trace?.Invoke($"DSP MM location updating request ({message.Name}) type={message.UpdateType:X2} -> {DownlinkMessageName(DownlinkMessageKind.LocationUpdatingAccept)} queued");
        return [new DownlinkMessage(BuildLocationUpdatingAccept(), DownlinkMessageKind.LocationUpdatingAccept)];
    }

    private IReadOnlyList<DownlinkMessage> QueueCipheringModeCommand(Layer3Message message)
    {
        if (message.CmServiceType == CmServiceType.Unsupported)
        {
            trace?.Invoke("DSP MM cm service request (unsupported service) ignored");
            return [];
        }

        State = RegistrationState.AwaitingCipheringModeCommandAcknowledgement;
        ActiveService = message.CmServiceType;
        trace?.Invoke($"DSP MM cm service request ({CmServiceTypeName(message.CmServiceType)}) -> {DownlinkMessageName(DownlinkMessageKind.CipheringModeCommand)} queued");
        return [new DownlinkMessage(Layer3MessageCodec.BuildCipheringModeCommand(), DownlinkMessageKind.CipheringModeCommand)];
    }

    private IReadOnlyList<DownlinkMessage> QueueIncomingCipheringModeCommand()
    {
        if (!pendingIncomingServices.TryDequeue(out activeIncomingService))
        {
            trace?.Invoke("DSP RR paging response ignored without pending incoming service");
            return [];
        }

        ActiveService = activeIncomingService.Kind switch
        {
            IncomingServiceKind.MobileTerminatedCall => CmServiceType.MobileTerminatedCall,
            IncomingServiceKind.MobileTerminatedShortMessage => CmServiceType.MobileTerminatedShortMessage,
            _ => CmServiceType.Unsupported,
        };

        if (activeIncomingService.Kind == IncomingServiceKind.MobileTerminatedCall)
        {
            activeCall = new ActiveCall(
                activeIncomingService.RequestId,
                CallDirection.Incoming,
                activeIncomingService.Address,
                GsmProtocol.MobileTerminatedCallTransactionAndProtocolDiscriminator,
                Connected: false,
                ConnectQueued: false,
                AnnouncementKind: CallAudioAnnouncementKind.InvalidNumber,
                AnnouncementText: "",
                AnnouncementPublished: false,
                TerminationQueued: false);
        }

        State = RegistrationState.AwaitingCipheringModeCommandAcknowledgement;
        trace?.Invoke($"DSP RR paging response ({CmServiceTypeName(ActiveService)}) -> {DownlinkMessageName(DownlinkMessageKind.CipheringModeCommand)} queued");
        return [new DownlinkMessage(Layer3MessageCodec.BuildCipheringModeCommand(), DownlinkMessageKind.CipheringModeCommand)];
    }

    private IReadOnlyList<DownlinkMessage> QueueInitialActiveServiceMessages()
    {
        List<DownlinkMessage> messages = [];

        if (!networkTimeQueuedOnActiveConnection)
        {
            networkTimeQueuedOnActiveConnection = true;
            messages.Add(QueueNetworkTimeInformation());
        }

        messages.AddRange(ActiveService switch
        {
            CmServiceType.MobileTerminatedCall =>
            [
                new DownlinkMessage(
                    Layer3MessageCodec.BuildMobileTerminatedCallSetup(activeIncomingService.Address),
                    DownlinkMessageKind.IncomingCallSetup),
            ],
            CmServiceType.MobileTerminatedShortMessage =>
            [
                new DownlinkMessage([], DownlinkMessageKind.Sapi3Establishment, sapi: 3),
            ],
            _ => [],
        });

        return messages;
    }

    private IReadOnlyList<DownlinkMessage> QueueChannelRelease()
    {
        State = RegistrationState.AwaitingChannelReleaseAcknowledgement;
        trace?.Invoke($"DSP RR {DownlinkMessageName(DownlinkMessageKind.ChannelRelease)} queued");
        return [new DownlinkMessage(Layer3MessageCodec.BuildChannelRelease(), DownlinkMessageKind.ChannelRelease, sapi: 0)];
    }

    private IReadOnlyList<DownlinkMessage> QueueNetworkTimeAndChannelRelease()
    {
        State = RegistrationState.AwaitingChannelReleaseAcknowledgement;
        trace?.Invoke($"DSP RR {DownlinkMessageName(DownlinkMessageKind.ChannelRelease)} queued");
        return
        [
            QueueNetworkTimeInformation(),
            new DownlinkMessage(Layer3MessageCodec.BuildChannelRelease(), DownlinkMessageKind.ChannelRelease, sapi: 0),
        ];
    }

    private DownlinkMessage QueueNetworkTimeInformation()
    {
        beforeNetworkTimeInformationQueued?.Invoke();
        DateTimeOffset networkLocalTime = networkLocalTimeProvider();
        trace?.Invoke($"DSP MM INFORMATION network time queued {networkLocalTime:O} name=\"{networkName}\"");
        return new DownlinkMessage(Layer3MessageCodec.BuildMmInformation(networkLocalTime, networkName), DownlinkMessageKind.MmInformation);
    }

    private IReadOnlyList<DownlinkMessage> AcknowledgeMmInformation()
    {
        trace?.Invoke("DSP MM INFORMATION acknowledged");
        return [];
    }

    private IReadOnlyList<DownlinkMessage> AcknowledgeChannelRelease()
    {
        State = RegistrationState.Released;
        trace?.Invoke($"DSP RR {DownlinkMessageName(DownlinkMessageKind.ChannelRelease)} acknowledged");
        return [];
    }

    private IReadOnlyList<DownlinkMessage> AcknowledgeCipheringModeCommand()
    {
        State = RegistrationState.AwaitingCipheringModeComplete;
        trace?.Invoke("DSP RR CIPHERING MODE COMMAND acknowledged");
        return [];
    }

    private IReadOnlyList<DownlinkMessage> AcknowledgeIncomingCallSetup()
    {
        trace?.Invoke("DSP CC incoming SETUP acknowledged");
        return [];
    }

    private IReadOnlyList<DownlinkMessage> QueueMobileTerminatedSmsCpData()
    {
        byte reference = nextSmsReference++;
        DateTimeOffset serviceCentreTime = activeIncomingService.SentAt == default
            ? networkLocalTimeProvider()
            : activeIncomingService.SentAt;
        trace?.Invoke($"DSP SMS MT SAPI3 established -> CP-DATA queued ref={reference:X2}");
        return
        [
            new DownlinkMessage(
                BuildMobileTerminatedSmsCpData(activeIncomingService, reference, serviceCentreTime),
                DownlinkMessageKind.MobileTerminatedSmsCpData,
                sapi: 3),
        ];
    }

    private IReadOnlyList<DownlinkMessage> AcknowledgeMobileTerminatedSmsCpData()
    {
        trace?.Invoke("DSP SMS MT CP-DATA acknowledged by LAPDm");
        return [];
    }

    private IReadOnlyList<DownlinkMessage> AcknowledgeConnectAcknowledge()
    {
        trace?.Invoke("DSP CC CONNECT ACKNOWLEDGE acknowledged");
        PublishCallTransition(CallTransitionKind.Connect);
        return [];
    }

    private IReadOnlyList<DownlinkMessage> AcknowledgeCpAck()
    {
        trace?.Invoke("DSP SMS CP-ACK acknowledged");
        return [];
    }

    private IReadOnlyList<DownlinkMessage> AcknowledgeRpAckCpData()
    {
        trace?.Invoke("DSP SMS RP-ACK CP-DATA acknowledged");
        return [];
    }

    private IReadOnlyList<DownlinkMessage> AcknowledgeCallProceeding()
    {
        trace?.Invoke("DSP CC CALL PROCEEDING acknowledged");
        return [];
    }

    private IReadOnlyList<DownlinkMessage> AcknowledgeAlerting()
    {
        trace?.Invoke("DSP CC ALERTING acknowledged");
        return [];
    }

    private IReadOnlyList<DownlinkMessage> AcknowledgeConnect()
    {
        trace?.Invoke("DSP CC CONNECT acknowledged");
        if (activeCall is { Direction: CallDirection.Outgoing })
        {
            PublishCallTransition(CallTransitionKind.Connect);
        }

        return [];
    }

    private IReadOnlyList<DownlinkMessage> AcknowledgeCallRelease()
    {
        trace?.Invoke("DSP CC RELEASE acknowledged");
        return [];
    }

    private IReadOnlyList<DownlinkMessage> HandleMobileTerminatedSmsCpData(ReadOnlySpan<byte> information)
    {
        if (SmsTpduCodec.TryGetMobileTerminatedRpAckReference(information, out byte messageReference))
        {
            trace?.Invoke($"DSP SMS MT RP-ACK ref={messageReference:X2}");
        }
        else
        {
            trace?.Invoke($"DSP SMS MT CP-DATA len={information.Length}");
        }

        List<DownlinkMessage> messages = [new DownlinkMessage(SmsTpduCodec.BuildCpAck(information[0]), DownlinkMessageKind.CpAck)];
        messages.AddRange(QueueChannelRelease());
        return messages;
    }

    private byte[] BuildLocationUpdatingAccept() =>
    [
        GsmProtocol.MobilityManagementProtocolDiscriminator, LocationUpdatingAcceptMessageType,
        locationAreaIdentity[0],
        locationAreaIdentity[1],
        locationAreaIdentity[2],
        locationAreaIdentity[3],
        locationAreaIdentity[4],
    ];

    private static string SanitizeNetworkName(string networkName)
    {
        string trimmed = string.IsNullOrWhiteSpace(networkName)
            ? Dct3PhoneSettings.DefaultNetworkName
            : networkName.Trim();
        string sanitized = new(trimmed.Where(ch => ch is >= ' ' and <= '~').Take(16).ToArray());
        return sanitized.Length == 0 ? Dct3PhoneSettings.DefaultNetworkName : sanitized;
    }

    private static bool TryDecodeMobileOriginatedSms(
        ReadOnlySpan<byte> cpData,
        out string normalizedDestination,
        out string text,
        out bool international)
    {
        normalizedDestination = "";
        text = "";
        international = false;
        if (!SmsTpduCodec.TryGetCpUserData(cpData, out ReadOnlySpan<byte> rpdu) ||
            rpdu.Length < 5 ||
            (rpdu[0] & 0x07) != GsmProtocol.RpDataMobileToNetworkMessageType)
        {
            return false;
        }

        int offset = 2;
        if (!SmsTpduCodec.TrySkipLengthPrefixed(rpdu, ref offset) ||
            !SmsTpduCodec.TrySkipLengthPrefixed(rpdu, ref offset) ||
            offset >= rpdu.Length)
        {
            return false;
        }

        int tpduLength = rpdu[offset++];
        if (tpduLength < 7 || offset + tpduLength > rpdu.Length)
        {
            return false;
        }

        return SmsTpduCodec.TryDecodeSmsSubmitTpdu(
            rpdu.Slice(offset, tpduLength),
            out normalizedDestination,
            out text,
            out international);
    }

    private static bool IsRoutableNoksNumber(string normalizedDestination, bool international) =>
        !international &&
        normalizedDestination.Length == RoutableNoksNumberDigits &&
        normalizedDestination.All(value => value is >= '0' and <= '9');



    private static Layer3Message DecodeLayer3Message(ReadOnlySpan<byte> information)
    {
        if (information.Length < 2)
        {
            return Layer3Message.Unknown;
        }

        byte protocolDiscriminator = (byte)(information[0] & 0x0F);
        byte messageType = information[1];

        if (protocolDiscriminator == GsmProtocol.MobilityManagementProtocolDiscriminator &&
            messageType == LocationUpdatingRequestMessageType &&
            information.Length >= 3)
        {
            return new Layer3Message(
                Layer3MessageKind.LocationUpdatingRequest,
                "LOCATION UPDATING REQUEST",
                UpdateType: information[2],
                CmServiceType.None);
        }

        if (protocolDiscriminator == GsmProtocol.MobilityManagementProtocolDiscriminator &&
            messageType == CmServiceRequestMessageType &&
            information.Length >= 3)
        {
            return new Layer3Message(
                Layer3MessageKind.CmServiceRequest,
                "CM SERVICE REQUEST",
                UpdateType: 0,
                DecodeCmServiceType(information[2]));
        }

        if (protocolDiscriminator == GsmProtocol.RadioResourceProtocolDiscriminator &&
            messageType == PagingResponseMessageType)
        {
            return new Layer3Message(
                Layer3MessageKind.PagingResponse,
                "PAGING RESPONSE",
                UpdateType: 0,
                CmServiceType.None);
        }

        return Layer3Message.Unknown;
    }

    private static CmServiceType DecodeCmServiceType(byte serviceTypeAndCipherKeySequence)
    {
        return (serviceTypeAndCipherKeySequence & 0x0F) switch
        {
            0x01 => CmServiceType.MobileOriginatingCall,
            0x02 => CmServiceType.EmergencyCall,
            0x04 => CmServiceType.ShortMessage,
            _ => CmServiceType.Unsupported,
        };
    }

    private static string DownlinkMessageName(DownlinkMessageKind kind) => kind switch
    {
        DownlinkMessageKind.LocationUpdatingAccept => "LOCATION UPDATING ACCEPT",
        DownlinkMessageKind.MmInformation => "MM INFORMATION",
        DownlinkMessageKind.ChannelRelease => "channel release",
        DownlinkMessageKind.CipheringModeCommand => "CIPHERING MODE COMMAND",
        DownlinkMessageKind.IncomingCallSetup => "incoming SETUP",
        DownlinkMessageKind.Sapi3Establishment => "SAPI 3 establishment",
        DownlinkMessageKind.MobileTerminatedSmsCpData => "MT SMS CP-DATA",
        _ => "message",
    };

    private static string CmServiceTypeName(CmServiceType serviceType) => serviceType switch
    {
        CmServiceType.MobileOriginatingCall => "mobile originating call",
        CmServiceType.EmergencyCall => "emergency call",
        CmServiceType.MobileTerminatedCall => "mobile terminated call",
        CmServiceType.ShortMessage => "short message",
        CmServiceType.MobileTerminatedShortMessage => "mobile terminated short message",
        CmServiceType.Unsupported => "unsupported service",
        _ => "none",
    };

    public readonly record struct DownlinkMessage
    {
        public const int DefaultResponseSapi = -1;

        public DownlinkMessage(byte[] information, DownlinkMessageKind kind, int sapi = DefaultResponseSapi)
        {
            Information = information;
            Kind = kind;
            Sapi = sapi == DefaultResponseSapi && kind == DownlinkMessageKind.ChannelRelease ? 0 : sapi;
        }

        public byte[] Information { get; }

        public DownlinkMessageKind Kind { get; }

        public int Sapi { get; }
    }

    private readonly record struct Layer3Message(Layer3MessageKind Kind, string Name, byte UpdateType, CmServiceType CmServiceType)
    {
        public static Layer3Message Unknown { get; } = new(Layer3MessageKind.Unknown, "UNKNOWN", UpdateType: 0, CmServiceType.None);
    }

    private readonly record struct PendingOutgoingNetworkRequest(
        OutgoingNetworkRequest Request,
        byte TransactionAndProtocolDiscriminator,
        byte MessageReference);

    private readonly record struct ActiveCall(
        Guid RequestId,
        CallDirection Direction,
        string NormalizedRemoteNumber,
        byte NetworkTransactionAndProtocolDiscriminator,
        bool Connected,
        bool ConnectQueued,
        CallAudioAnnouncementKind AnnouncementKind,
        string AnnouncementText,
        bool AnnouncementPublished,
        bool TerminationQueued);

    private readonly record struct GsmNetworkInput(
        GsmNetworkInputKind Kind,
        byte[] Information,
        DownlinkMessageKind DownlinkKind,
        string Address,
        string Text,
        ushort DestinationPort,
        byte[] Payload,
        SmartMessageConcatenation Concatenation = default,
        Guid RequestId = default,
        DateTimeOffset SentAt = default)
    {
        public static GsmNetworkInput Reset() =>
            new(GsmNetworkInputKind.Reset, [], DownlinkMessageKind.Segment, "", "", 0, []);

        public static GsmNetworkInput QueueIncomingCall(Guid requestId, string address) =>
            new(
                GsmNetworkInputKind.QueueIncomingCall,
                [],
                DownlinkMessageKind.Segment,
                address,
                "",
                0,
                [],
                RequestId: requestId);

        public static GsmNetworkInput QueueIncomingSms(
            string address,
            string text,
            DateTimeOffset sentAt) =>
            new(
                GsmNetworkInputKind.QueueIncomingSms,
                [],
                DownlinkMessageKind.Segment,
                address,
                text,
                0,
                [],
                SentAt: sentAt);

        public static GsmNetworkInput QueueIncomingSmartMessage(
            string address,
            ushort destinationPort,
            ReadOnlySpan<byte> payload,
            SmartMessageConcatenation concatenation) =>
            new(
                GsmNetworkInputKind.QueueIncomingSms,
                [],
                DownlinkMessageKind.Segment,
                address,
                "",
                destinationPort,
                payload.ToArray(),
                concatenation);

        public static GsmNetworkInput EstablishedLayer3(byte[] information) =>
            new(GsmNetworkInputKind.EstablishedLayer3, information, DownlinkMessageKind.Segment, "", "", 0, []);

        public static GsmNetworkInput ActiveLayer3(byte[] information) =>
            new(GsmNetworkInputKind.ActiveLayer3, information, DownlinkMessageKind.Segment, "", "", 0, []);

        public static GsmNetworkInput DownlinkAcknowledgement(DownlinkMessageKind kind) =>
            new(GsmNetworkInputKind.DownlinkAcknowledgement, [], kind, "", "", 0, []);
    }

    public enum DownlinkMessageKind
    {
        LocationUpdatingAccept,
        MmInformation,
        ChannelRelease,
        CipheringModeCommand,
        IncomingCallSetup,
        Sapi3Establishment,
        MobileTerminatedSmsCpData,
        ConnectAcknowledge,
        CpAck,
        RpAckCpData,
        RpErrorCpData,
        CallProceeding,
        Alerting,
        Connect,
        Release,
        ReleaseComplete,
        Segment,
    }

    private enum Layer3MessageKind
    {
        Unknown,
        LocationUpdatingRequest,
        CmServiceRequest,
        PagingResponse,
    }

    private enum GsmNetworkInputKind
    {
        Reset,
        QueueIncomingCall,
        QueueIncomingSms,
        EstablishedLayer3,
        ActiveLayer3,
        DownlinkAcknowledgement,
    }

    public enum RegistrationState
    {
        Idle,
        AwaitingLocationUpdatingAcceptAcknowledgement,
        AwaitingChannelReleaseAcknowledgement,
        Released,
        AwaitingCipheringModeCommandAcknowledgement,
        AwaitingCipheringModeComplete,
        MmConnectionActive,
    }

    public enum PostRegistrationEmulationMode
    {
        BroadcastSystemInformationWithNoIdentityPagingFill,
    }

    public enum OutgoingCallEmulationMode
    {
        KeepConnectedUntilPhoneDisconnects,
    }

    public enum CmServiceType
    {
        None,
        MobileOriginatingCall,
        ShortMessage,
        MobileTerminatedCall,
        MobileTerminatedShortMessage,
        EmergencyCall,
        Unsupported,
    }

    private enum IncomingServiceKind
    {
        None,
        MobileTerminatedCall,
        MobileTerminatedShortMessage,
    }

    private static byte[] BuildMobileTerminatedSmsCpData(
        IncomingService service,
        byte messageReference,
        DateTimeOffset serviceCentreTime)
    {
        byte[] serviceCentreAddress = GsmAlphabet.BuildBcdNumberContents("1234567890", international: true);
        byte[] tpdu = service.Payload.Length > 0
            ? SmsTpduCodec.BuildSmartMessageDeliverTpdu(
                service.Address,
                service.DestinationPort,
                service.Payload,
                service.Concatenation,
                serviceCentreTime)
            : SmsTpduCodec.BuildSmsDeliverTpdu(service.Address, service.Text, serviceCentreTime);
        List<byte> rpdu =
        [
            RpDataNetworkToMobileMessageType,
            messageReference,
            (byte)serviceCentreAddress.Length,
        ];
        rpdu.AddRange(serviceCentreAddress);
        rpdu.Add(0x00);
        rpdu.Add((byte)tpdu.Length);
        rpdu.AddRange(tpdu);

        return
        [
            MobileTerminatedSmsTransactionAndProtocolDiscriminator,
            GsmProtocol.CpDataMessageType,
            (byte)rpdu.Count,
            .. rpdu,
        ];
    }

    private readonly record struct IncomingService(
        IncomingServiceKind Kind,
        string Address,
        string Text,
        ushort DestinationPort,
        byte[] Payload,
        SmartMessageConcatenation Concatenation = default,
        Guid RequestId = default,
        DateTimeOffset SentAt = default);

}
