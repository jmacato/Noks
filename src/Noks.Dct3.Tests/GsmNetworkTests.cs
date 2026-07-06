using Noks.Dct3.Audio;
using Noks.Dct3.Messaging;
using Noks.Dct3.Radio;
namespace Noks.Dct3.Tests;

public sealed class GsmNetworkTests
{
    [Fact]
    public void HandleEstablishedLayer3_LocationUpdatingRequest_QueuesAcceptAndWaitsForAcknowledgement()
    {
        GsmNetwork network = new(null);

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = network.HandleEstablishedLayer3(LocationUpdatingRequest());

        GsmNetwork.DownlinkMessage message = Assert.Single(messages);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.LocationUpdatingAccept, message.Kind);
        Assert.Equal([0x05, 0x02, 0x02, 0xF8, 0x10, 0x00, 0x01], message.Information);
        Assert.Equal(GsmNetwork.RegistrationState.AwaitingLocationUpdatingAcceptAcknowledgement, network.State);
    }

    [Fact]
    public void HandleEstablishedLayer3_LocationUpdatingRequest_UsesImsiHomePlmn()
    {
        GsmNetwork network = new(null, pagingImsi: "001010000000001");

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = network.HandleEstablishedLayer3(LocationUpdatingRequest());

        GsmNetwork.DownlinkMessage message = Assert.Single(messages);
        Assert.Equal([0x05, 0x02, 0x00, 0xF1, 0x10, 0x00, 0x01], message.Information);
    }

    [Fact]
    public void HandleDownlinkAcknowledgement_LocationUpdatingAcceptAck_QueuesMmInformationAndChannelRelease()
    {
        GsmNetwork network = new(null, FixedNetworkTime);
        network.HandleEstablishedLayer3(LocationUpdatingRequest());

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages =
            network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.LocationUpdatingAccept);

        Assert.Equal(2, messages.Count);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.MmInformation, messages[0].Kind);
        Assert.Equal([0x05, 0x32, 0x47, 0x62, 0x70, 0x60, 0x41, 0x35, 0x92, 0x23], messages[0].Information);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.ChannelRelease, messages[1].Kind);
        Assert.Equal([0x06, 0x0D, 0x00], messages[1].Information);
        Assert.Equal(GsmNetwork.RegistrationState.AwaitingChannelReleaseAcknowledgement, network.State);
    }

    [Fact]
    public void HandleDownlinkAcknowledgement_LocationUpdatingAcceptAck_InvokesNetworkTimeCallback()
    {
        int callbackCount = 0;
        GsmNetwork network = new(null, FixedNetworkTime, () => callbackCount++);
        network.HandleEstablishedLayer3(LocationUpdatingRequest());

        network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.LocationUpdatingAccept);

        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public void HandleDownlinkAcknowledgement_LocationUpdatingAcceptAck_IncludesNetworkName()
    {
        GsmNetwork network = new(null, FixedNetworkTime, networkName: "TEST");
        network.HandleEstablishedLayer3(LocationUpdatingRequest());

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages =
            network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.LocationUpdatingAccept);

        byte[] information = messages[0].Information;
        Assert.Equal(0x43, information[2]);
        Assert.Equal((int)information[3], information[4..9].Length);
        Assert.Equal(0x47, information[9]);
    }

    [Fact]
    public void HandleDownlinkAcknowledgement_LocationUpdatingAcceptAck_EncodesNegativeNetworkTimeZone()
    {
        GsmNetwork network = new(null, () => new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(-5)));
        network.HandleEstablishedLayer3(LocationUpdatingRequest());

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages =
            network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.LocationUpdatingAccept);

        Assert.Equal(GsmNetwork.DownlinkMessageKind.MmInformation, messages[0].Kind);
        Assert.Equal([0x05, 0x32, 0x47, 0x62, 0x10, 0x20, 0x30, 0x40, 0x50, 0x0A], messages[0].Information);
    }

    [Fact]
    public void HandleDownlinkAcknowledgement_ChannelReleaseAck_MarksSessionReleased()
    {
        GsmNetwork network = new(null);
        network.HandleEstablishedLayer3(LocationUpdatingRequest());
        network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.LocationUpdatingAccept);

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages =
            network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.ChannelRelease);

        Assert.Empty(messages);
        Assert.Equal(GsmNetwork.RegistrationState.Released, network.State);
    }

    [Fact]
    public void SuppressImsiPagingAfterRegistration_OnlyAfterReleasedUnderNoIdentityPagingFillMode()
    {
        GsmNetwork network = new(null);

        Assert.Equal(GsmNetwork.PostRegistrationEmulationMode.BroadcastSystemInformationWithNoIdentityPagingFill, network.PostRegistrationMode);
        Assert.False(network.SuppressImsiPagingAfterRegistration);

        network.HandleEstablishedLayer3(LocationUpdatingRequest());
        Assert.False(network.SuppressImsiPagingAfterRegistration);

        network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.LocationUpdatingAccept);
        Assert.False(network.SuppressImsiPagingAfterRegistration);

        network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.ChannelRelease);
        Assert.True(network.SuppressImsiPagingAfterRegistration);
    }

    [Fact]
    public void HandleDownlinkAcknowledgement_OutOfOrderAcknowledgement_IsIgnored()
    {
        GsmNetwork network = new(null);
        network.HandleEstablishedLayer3(LocationUpdatingRequest());

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages =
            network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.ChannelRelease);

        Assert.Empty(messages);
        Assert.Equal(GsmNetwork.RegistrationState.AwaitingLocationUpdatingAcceptAcknowledgement, network.State);
    }

    [Fact]
    public void Reset_ReturnsNetworkToIdle()
    {
        GsmNetwork network = new(null);
        network.HandleEstablishedLayer3(LocationUpdatingRequest());

        network.Reset();

        Assert.Equal(GsmNetwork.RegistrationState.Idle, network.State);
    }

    [Fact]
    public void HandleEstablishedLayer3_NonLocationUpdatingMessage_DoesNotLeaveIdle()
    {
        GsmNetwork network = new(null);

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = network.HandleEstablishedLayer3([0x06, 0x00]);

        Assert.Empty(messages);
        Assert.Equal(GsmNetwork.RegistrationState.Idle, network.State);
    }

    [Theory]
    [InlineData(0x01, 1)]
    [InlineData(0x02, 5)]
    [InlineData(0x04, 2)]
    public void HandleEstablishedLayer3_CmServiceRequest_QueuesCipheringModeCommandAndWaitsForAcknowledgement(
        byte serviceType,
        int expectedServiceType)
    {
        GsmNetwork network = new(null);

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = network.HandleEstablishedLayer3(CmServiceRequest(serviceType));

        GsmNetwork.DownlinkMessage message = Assert.Single(messages);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.CipheringModeCommand, message.Kind);
        Assert.Equal([0x06, 0x35, 0x01], message.Information);
        Assert.Equal((GsmNetwork.CmServiceType)expectedServiceType, network.ActiveService);
        Assert.Equal(GsmNetwork.RegistrationState.AwaitingCipheringModeCommandAcknowledgement, network.State);
    }

    [Fact]
    public void HandleDownlinkAcknowledgement_CipheringModeCommandAck_WaitsForCipheringModeComplete()
    {
        GsmNetwork network = new(null);
        network.HandleEstablishedLayer3(CmServiceRequest(serviceType: 0x04));

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages =
            network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.CipheringModeCommand);

        Assert.Empty(messages);
        Assert.Equal(GsmNetwork.CmServiceType.ShortMessage, network.ActiveService);
        Assert.Equal(GsmNetwork.RegistrationState.AwaitingCipheringModeComplete, network.State);
    }

    [Fact]
    public void HandleActiveLayer3_CipheringModeComplete_MarksMmConnectionActive()
    {
        GsmNetwork network = new(null, FixedNetworkTime);
        network.HandleEstablishedLayer3(CmServiceRequest(serviceType: 0x04));
        network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.CipheringModeCommand);

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = network.HandleActiveLayer3([0x06, 0x32]);

        GsmNetwork.DownlinkMessage message = Assert.Single(messages);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.MmInformation, message.Kind);
        Assert.Equal([0x05, 0x32, 0x47, 0x62, 0x70, 0x60, 0x41, 0x35, 0x92, 0x23], message.Information);
        Assert.Equal(GsmNetwork.CmServiceType.ShortMessage, network.ActiveService);
        Assert.Equal(GsmNetwork.RegistrationState.MmConnectionActive, network.State);
    }

    [Fact]
    public void HandleActiveLayer3_CipheringModeComplete_InvokesNetworkTimeCallback()
    {
        int callbackCount = 0;
        GsmNetwork network = new(null, FixedNetworkTime, () => callbackCount++);
        network.HandleEstablishedLayer3(CmServiceRequest(serviceType: 0x04));
        network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.CipheringModeCommand);

        network.HandleActiveLayer3([0x06, 0x32]);

        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public void HandleActiveLayer3_CpData_QueuesCpAckWithFlippedTransactionIdentifierFlag()
    {
        GsmNetwork network = new(null);
        EstablishCmService(network, serviceType: 0x04);

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = network.HandleActiveLayer3([0x09, 0x01, 0x02, 0x00, 0x42]);

        Assert.Equal(2, messages.Count);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.CpAck, messages[0].Kind);
        Assert.Equal([0x89, 0x04], messages[0].Information);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.RpAckCpData, messages[1].Kind);
        Assert.Equal([0x89, 0x01, 0x02, 0x03, 0x42], messages[1].Information);
    }

    [Fact]
    public void HandleActiveLayer3_FinalSmsCpAck_QueuesChannelRelease()
    {
        GsmNetwork network = new(null);
        EstablishCmService(network, serviceType: 0x04);

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = network.HandleActiveLayer3([0x09, 0x04]);

        GsmNetwork.DownlinkMessage message = Assert.Single(messages);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.ChannelRelease, message.Kind);
        Assert.Equal([0x06, 0x0D, 0x00], message.Information);
        Assert.Equal(0, message.Sapi);
        Assert.Equal(GsmNetwork.RegistrationState.AwaitingChannelReleaseAcknowledgement, network.State);
    }

    [Theory]
    [InlineData(0x05)]
    [InlineData(0x45)]
    public void HandleActiveLayer3_Setup_QueuesCallProceedingAlertingAndConnectWithFlippedTransactionIdentifierFlag(byte setupMessageType)
    {
        GsmNetwork network = new(null);
        EstablishCmService(network, serviceType: 0x01);

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = network.HandleActiveLayer3([0x03, setupMessageType, 0x04]);

        Assert.Equal(3, messages.Count);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.CallProceeding, messages[0].Kind);
        Assert.Equal([0x83, 0x02], messages[0].Information);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.Alerting, messages[1].Kind);
        Assert.Equal([0x83, 0x01], messages[1].Information);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.Connect, messages[2].Kind);
        Assert.Equal([0x83, 0x07], messages[2].Information);
    }

    [Fact]
    public void HandleActiveLayer3_DecodedCall_WaitsForMatchingHostResolutionExactlyOnce()
    {
        List<OutgoingNetworkRequest> requests = [];
        GsmNetwork network = new(null, outgoingNetworkRequest: requests.Add);
        EstablishCmService(network, serviceType: 0x01);

        IReadOnlyList<GsmNetwork.DownlinkMessage> pending =
            network.HandleActiveLayer3(MobileOriginatedCallSetup("1234567890123"));

        Assert.Empty(pending);
        OutgoingNetworkRequest request = Assert.Single(requests);
        Assert.Equal(NetworkRequestKind.Call, request.Kind);
        Assert.Equal("1234567890123", request.NormalizedDestination);
        Assert.Equal("", request.SmsText);
        Assert.Empty(network.ResolveNetworkRequest(new ResolveNetworkRequest(Guid.NewGuid(), NetworkRequestDecision.Accept)));

        IReadOnlyList<GsmNetwork.DownlinkMessage> accepted = network.ResolveNetworkRequest(
            new ResolveNetworkRequest(request.RequestId, NetworkRequestDecision.Accept));

        Assert.Equal(
            [
                GsmNetwork.DownlinkMessageKind.CallProceeding,
                GsmNetwork.DownlinkMessageKind.Alerting,
            ],
            accepted.Select(message => message.Kind));
        Assert.Empty(network.ConnectNetworkCall(Guid.NewGuid()));
        GsmNetwork.DownlinkMessage connect = Assert.Single(network.ConnectNetworkCall(request.RequestId));
        Assert.Equal(GsmNetwork.DownlinkMessageKind.Connect, connect.Kind);
        Assert.Equal([0x83, 0x07], connect.Information);
        Assert.Empty(network.ConnectNetworkCall(request.RequestId));
        Assert.Empty(network.ResolveNetworkRequest(
            new ResolveNetworkRequest(request.RequestId, NetworkRequestDecision.Accept)));
    }

    [Fact]
    public void HandleActiveLayer3_CapturedThirteenDigitSetup_EmitsAndResolvesRequestExactlyOnce()
    {
        List<OutgoingNetworkRequest> requests = [];
        GsmNetwork network = new(null, outgoingNetworkRequest: requests.Add);
        EstablishCmService(network, serviceType: 0x01);
        byte[] capturedSetup = Convert.FromHexString(
            "03450404600200815E0881562981986114F8150101");

        Assert.Empty(network.HandleActiveLayer3(capturedSetup));
        Assert.Empty(network.HandleActiveLayer3(capturedSetup));
        OutgoingNetworkRequest request = Assert.Single(requests);
        Assert.Equal(NetworkRequestKind.Call, request.Kind);
        Assert.Equal("6592188916418", request.NormalizedDestination);

        IReadOnlyList<GsmNetwork.DownlinkMessage> accepted = network.ResolveNetworkRequest(
            new ResolveNetworkRequest(request.RequestId, NetworkRequestDecision.Accept));

        Assert.Equal(
            [
                GsmNetwork.DownlinkMessageKind.CallProceeding,
                GsmNetwork.DownlinkMessageKind.Alerting,
            ],
            accepted.Select(message => message.Kind));
        GsmNetwork.DownlinkMessage connect = Assert.Single(network.ConnectNetworkCall(request.RequestId));
        Assert.Equal(GsmNetwork.DownlinkMessageKind.Connect, connect.Kind);
        Assert.Equal([0x83, 0x07], connect.Information);
        Assert.Empty(network.ResolveNetworkRequest(
            new ResolveNetworkRequest(request.RequestId, NetworkRequestDecision.Accept)));
    }

    [Theory]
    [InlineData(NetworkRequestDecision.Reject)]
    [InlineData(NetworkRequestDecision.Timeout)]
    public void ResolveNetworkRequest_RejectedCall_UsesNativeReleasePath(NetworkRequestDecision decision)
    {
        List<OutgoingNetworkRequest> requests = [];
        GsmNetwork network = new(null, outgoingNetworkRequest: requests.Add);
        EstablishCmService(network, serviceType: 0x01);
        network.HandleActiveLayer3(MobileOriginatedCallSetup("1234567890123"));

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = network.ResolveNetworkRequest(
            new ResolveNetworkRequest(Assert.Single(requests).RequestId, decision));

        Assert.Equal(GsmNetwork.DownlinkMessageKind.ReleaseComplete, messages[0].Kind);
        Assert.Equal([0x83, 0x2A], messages[0].Information);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.ChannelRelease, messages[1].Kind);
        Assert.Equal(GsmNetwork.RegistrationState.AwaitingChannelReleaseAcknowledgement, network.State);
    }

    [Fact]
    public void TerminateNetworkCall_QueuesOneNativeReleaseForMatchingOutgoingCall()
    {
        List<OutgoingNetworkRequest> requests = [];
        GsmNetwork network = new(null, outgoingNetworkRequest: requests.Add);
        EstablishCmService(network, serviceType: 0x01);
        network.HandleActiveLayer3(MobileOriginatedCallSetup("1234567890123"));
        Guid requestId = Assert.Single(requests).RequestId;
        network.ResolveNetworkRequest(new ResolveNetworkRequest(requestId, NetworkRequestDecision.Accept));

        Assert.Empty(network.TerminateNetworkCall(Guid.NewGuid()));
        GsmNetwork.DownlinkMessage release = Assert.Single(network.TerminateNetworkCall(requestId));

        Assert.Equal(GsmNetwork.DownlinkMessageKind.Release, release.Kind);
        Assert.Equal([0x83, 0x2D], release.Information);
        Assert.Empty(network.TerminateNetworkCall(requestId));
    }

    [Fact]
    public void TerminateNetworkCall_UsesIncomingCallTransactionIdentifier()
    {
        Guid requestId = Guid.NewGuid();
        GsmNetwork network = new(null);
        EstablishIncomingCall(network, requestId);

        GsmNetwork.DownlinkMessage release = Assert.Single(network.TerminateNetworkCall(requestId));

        Assert.Equal(GsmNetwork.DownlinkMessageKind.Release, release.Kind);
        Assert.Equal([0x03, 0x2D], release.Information);
    }

    [Theory]
    [InlineData("5551234", false)]
    [InlineData("1234567890123", true)]
    [InlineData("123456789012345", false)]
    [InlineData("1234567890123456", false)]
    [InlineData("12345678901234567890", false)]
    public void HandleActiveLayer3_InvalidNumber_ConnectsImmediatelyAndQueuesCarrierAnnouncement(
        string destination,
        bool international)
    {
        List<OutgoingNetworkRequest> requests = [];
        List<CallAudioAnnouncement> announcements = [];
        GsmNetwork network = new(
            null,
            outgoingNetworkRequest: requests.Add,
            callAudioAnnouncement: announcements.Add);
        EstablishCmService(network, serviceType: 0x01);

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = network.HandleActiveLayer3(
            MobileOriginatedCallSetup(destination, international));

        Assert.Empty(requests);
        Assert.Equal(
            [
                GsmNetwork.DownlinkMessageKind.CallProceeding,
                GsmNetwork.DownlinkMessageKind.Connect,
            ],
            messages.Select(message => message.Kind));
        Assert.Empty(announcements);

        network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.Connect);
        network.HandleActiveLayer3([0x03, 0x0F]);

        CallAudioAnnouncement announcement = Assert.Single(announcements);
        Assert.Equal(CallAudioAnnouncementKind.InvalidNumber, announcement.Kind);
        Assert.Equal("The number you dialed is invalid.", announcement.Text);
    }

    [Theory]
    [InlineData("000")]
    [InlineData("112")]
    [InlineData("911")]
    [InlineData("999")]
    public void HandleActiveLayer3_EmergencyNumber_UsesExplicitUnsupportedAnnouncement(string destination)
    {
        List<OutgoingNetworkRequest> requests = [];
        List<CallAudioAnnouncement> announcements = [];
        GsmNetwork network = new(
            null,
            outgoingNetworkRequest: requests.Add,
            callAudioAnnouncement: announcements.Add);
        EstablishCmService(network, serviceType: 0x01);

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = network.HandleActiveLayer3(
            MobileOriginatedCallSetup(destination));
        network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.Connect);
        network.HandleActiveLayer3([0x03, 0x0F]);

        Assert.Empty(requests);
        Assert.Equal(
            [
                GsmNetwork.DownlinkMessageKind.CallProceeding,
                GsmNetwork.DownlinkMessageKind.Connect,
            ],
            messages.Select(message => message.Kind));
        CallAudioAnnouncement announcement = Assert.Single(announcements);
        Assert.Equal(CallAudioAnnouncementKind.EmergencyCallsUnsupported, announcement.Kind);
        Assert.Equal(
            "This emulated network does not support emergency calls. " +
            "Please use an actual mobile phone for emergencies.",
            announcement.Text);
    }

    [Fact]
    public void HandleActiveLayer3_EmergencySetup_ConnectsAndUsesUnsupportedAnnouncement()
    {
        List<OutgoingNetworkRequest> requests = [];
        List<CallAudioAnnouncement> announcements = [];
        GsmNetwork network = new(
            null,
            outgoingNetworkRequest: requests.Add,
            callAudioAnnouncement: announcements.Add);
        EstablishCmService(network, serviceType: 0x02);

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = network.HandleActiveLayer3(
            [0x03, 0x0E, 0x04, 0x04, 0x60, 0x02, 0x00, 0x81]);
        network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.Connect);
        network.HandleActiveLayer3([0x03, 0x0F]);

        Assert.Empty(requests);
        Assert.Equal(
            [
                GsmNetwork.DownlinkMessageKind.CallProceeding,
                GsmNetwork.DownlinkMessageKind.Connect,
            ],
            messages.Select(message => message.Kind));
        CallAudioAnnouncement announcement = Assert.Single(announcements);
        Assert.Equal(CallAudioAnnouncementKind.EmergencyCallsUnsupported, announcement.Kind);
        Assert.Equal(
            "This emulated network does not support emergency calls. " +
            "Please use an actual mobile phone for emergencies.",
            announcement.Text);
    }

    [Fact]
    public void HandleActiveLayer3_DecodedSms_AcknowledgesCpAndWaitsForHostRoute()
    {
        List<OutgoingNetworkRequest> requests = [];
        GsmNetwork network = new(null, outgoingNetworkRequest: requests.Add);
        EstablishCmService(network, serviceType: 0x04);

        IReadOnlyList<GsmNetwork.DownlinkMessage> pending = network.HandleActiveLayer3(
            MobileOriginatedSmsCpData("1234567890123", "hello", rpReference: 0x42));

        GsmNetwork.DownlinkMessage cpAck = Assert.Single(pending);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.CpAck, cpAck.Kind);
        OutgoingNetworkRequest request = Assert.Single(requests);
        Assert.Equal(NetworkRequestKind.Sms, request.Kind);
        Assert.Equal("1234567890123", request.NormalizedDestination);
        Assert.Equal("hello", request.SmsText);

        GsmNetwork.DownlinkMessage rpAck = Assert.Single(network.ResolveNetworkRequest(
            new ResolveNetworkRequest(request.RequestId, NetworkRequestDecision.Accept)));
        Assert.Equal(GsmNetwork.DownlinkMessageKind.RpAckCpData, rpAck.Kind);
        Assert.Equal([0x89, 0x01, 0x02, 0x03, 0x42], rpAck.Information);
    }

    [Fact]
    public void ResolveNetworkRequest_RejectedSms_ReturnsRpError()
    {
        List<OutgoingNetworkRequest> requests = [];
        GsmNetwork network = new(null, outgoingNetworkRequest: requests.Add);
        EstablishCmService(network, serviceType: 0x04);
        network.HandleActiveLayer3(MobileOriginatedSmsCpData("1234567890123", "hello", rpReference: 0x42));

        GsmNetwork.DownlinkMessage error = Assert.Single(network.ResolveNetworkRequest(
            new ResolveNetworkRequest(Assert.Single(requests).RequestId, NetworkRequestDecision.Reject)));

        Assert.Equal(GsmNetwork.DownlinkMessageKind.RpErrorCpData, error.Kind);
        Assert.Equal([0x89, 0x01, 0x04, 0x05, 0x42, 0x01, 0x15], error.Information);
    }

    [Fact]
    public void HandleActiveLayer3_InvalidSmsNumber_IsAcknowledgedWithoutRouting()
    {
        List<OutgoingNetworkRequest> requests = [];
        GsmNetwork network = new(null, outgoingNetworkRequest: requests.Add);
        EstablishCmService(network, serviceType: 0x04);

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = network.HandleActiveLayer3(
            MobileOriginatedSmsCpData("5551234", "hello", rpReference: 0x42));

        Assert.Empty(requests);
        Assert.Equal(
            [GsmNetwork.DownlinkMessageKind.CpAck, GsmNetwork.DownlinkMessageKind.RpAckCpData],
            messages.Select(message => message.Kind));
    }

    [Theory]
    [InlineData(0x0F)]
    [InlineData(0x4F)]
    public void HandleActiveLayer3_ConnectAcknowledge_KeepsConnectedCallActiveUntilPhoneDisconnects(byte connectAcknowledgeMessageType)
    {
        GsmNetwork network = new(null);
        EstablishCmService(network, serviceType: 0x01);

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = network.HandleActiveLayer3([0x03, connectAcknowledgeMessageType]);

        Assert.Empty(messages);
        Assert.Equal(GsmNetwork.OutgoingCallEmulationMode.KeepConnectedUntilPhoneDisconnects, network.OutgoingCallMode);
        Assert.Equal(GsmNetwork.CmServiceType.MobileOriginatingCall, network.ActiveService);
        Assert.Equal(GsmNetwork.RegistrationState.MmConnectionActive, network.State);
    }

    [Theory]
    [InlineData(0x25)]
    [InlineData(0x65)]
    public void HandleActiveLayer3_Disconnect_QueuesRelease(byte disconnectMessageType)
    {
        GsmNetwork network = new(null);
        EstablishCmService(network, serviceType: 0x01);

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = network.HandleActiveLayer3([0x03, disconnectMessageType, 0x08, 0x02, 0x90]);

        GsmNetwork.DownlinkMessage message = Assert.Single(messages);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.Release, message.Kind);
        Assert.Equal([0x83, 0x2D], message.Information);
        Assert.Equal(GsmNetwork.RegistrationState.MmConnectionActive, network.State);
    }

    [Theory]
    [InlineData(0x2A)]
    [InlineData(0x6A)]
    public void HandleActiveLayer3_ReleaseComplete_QueuesChannelRelease(byte releaseCompleteMessageType)
    {
        GsmNetwork network = new(null);
        EstablishCmService(network, serviceType: 0x01);

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = network.HandleActiveLayer3([0x03, releaseCompleteMessageType]);

        GsmNetwork.DownlinkMessage message = Assert.Single(messages);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.ChannelRelease, message.Kind);
        Assert.Equal([0x06, 0x0D, 0x00], message.Information);
        Assert.Equal(GsmNetwork.RegistrationState.AwaitingChannelReleaseAcknowledgement, network.State);
    }

    [Fact]
    public void HandleEstablishedLayer3_PagingResponseForIncomingCall_QueuesCipheringThenSetup()
    {
        GsmNetwork network = new(null);
        network.QueueIncomingCall("5551234");

        IReadOnlyList<GsmNetwork.DownlinkMessage> cipheringMessages = network.HandleEstablishedLayer3(PagingResponse());

        GsmNetwork.DownlinkMessage ciphering = Assert.Single(cipheringMessages);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.CipheringModeCommand, ciphering.Kind);
        Assert.Equal([0x06, 0x35, 0x01], ciphering.Information);
        Assert.Equal(GsmNetwork.CmServiceType.MobileTerminatedCall, network.ActiveService);
        Assert.Equal(GsmNetwork.RegistrationState.AwaitingCipheringModeCommandAcknowledgement, network.State);

        network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.CipheringModeCommand);
        IReadOnlyList<GsmNetwork.DownlinkMessage> setupMessages = network.HandleActiveLayer3([0x06, 0x32]);

        Assert.Equal(2, setupMessages.Count);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.MmInformation, setupMessages[0].Kind);
        GsmNetwork.DownlinkMessage setup = setupMessages[1];
        Assert.Equal(GsmNetwork.DownlinkMessageKind.IncomingCallSetup, setup.Kind);
        Assert.Equal([0x03, 0x05, 0x04, 0x04, 0x60, 0x02, 0x00, 0x81, 0x34, 0x01, 0x5C, 0x05, 0x81, 0x55, 0x15, 0x32, 0xF4], setup.Information);
        Assert.Equal(GsmNetwork.RegistrationState.MmConnectionActive, network.State);
    }

    [Fact]
    public void HandleActiveLayer3_IncomingCallConnect_QueuesConnectAcknowledge()
    {
        GsmNetwork network = new(null);
        EstablishIncomingService(network, incomingCall: true);

        Assert.Empty(network.HandleActiveLayer3([0x03, 0x08]));
        Assert.Empty(network.HandleActiveLayer3([0x03, 0x01]));

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = network.HandleActiveLayer3([0x03, 0x07]);

        GsmNetwork.DownlinkMessage message = Assert.Single(messages);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.ConnectAcknowledge, message.Kind);
        Assert.Equal([0x83, 0x0F], message.Information);
        Assert.Equal(GsmNetwork.RegistrationState.MmConnectionActive, network.State);
    }

    [Fact]
    public void IncomingCall_EmitsAnswerConnectAndHangupTransitionsWithCorrelationId()
    {
        Guid requestId = Guid.NewGuid();
        List<CallTransition> transitions = [];
        GsmNetwork network = new(null, callTransition: transitions.Add);
        EstablishIncomingCall(network, requestId);

        network.HandleActiveLayer3([0x03, 0x07]);
        network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.ConnectAcknowledge);
        network.HandleActiveLayer3([0x03, 0x25]);

        Assert.Equal(
            [
                CallTransitionKind.Answer,
                CallTransitionKind.Connect,
                CallTransitionKind.Hangup,
            ],
            transitions.Select(transition => transition.Kind));
        Assert.All(transitions, transition => Assert.Equal(requestId, transition.RequestId));
        Assert.All(transitions, transition => Assert.Equal(CallDirection.Incoming, transition.Direction));
        Assert.All(transitions, transition => Assert.Equal("5551234", transition.NormalizedRemoteNumber));
    }

    [Fact]
    public void IncomingCall_DisconnectBeforeConnect_EmitsRejectTransition()
    {
        Guid requestId = Guid.NewGuid();
        List<CallTransition> transitions = [];
        GsmNetwork network = new(null, callTransition: transitions.Add);
        EstablishIncomingCall(network, requestId);

        network.HandleActiveLayer3([0x03, 0x25]);

        CallTransition transition = Assert.Single(transitions);
        Assert.Equal(requestId, transition.RequestId);
        Assert.Equal(CallTransitionKind.Reject, transition.Kind);
    }

    [Fact]
    public void HandleEstablishedLayer3_PagingResponseForIncomingSms_QueuesSapi3EstablishmentThenCpData()
    {
        GsmNetwork network = new(null, FixedNetworkTime);
        network.QueueIncomingSms("5551234", "hello");

        IReadOnlyList<GsmNetwork.DownlinkMessage> cipheringMessages = network.HandleEstablishedLayer3(PagingResponse());

        GsmNetwork.DownlinkMessage ciphering = Assert.Single(cipheringMessages);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.CipheringModeCommand, ciphering.Kind);
        Assert.Equal(GsmNetwork.CmServiceType.MobileTerminatedShortMessage, network.ActiveService);

        network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.CipheringModeCommand);
        IReadOnlyList<GsmNetwork.DownlinkMessage> setupMessages = network.HandleActiveLayer3([0x06, 0x32]);

        Assert.Equal(2, setupMessages.Count);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.MmInformation, setupMessages[0].Kind);
        GsmNetwork.DownlinkMessage sapi3 = setupMessages[1];
        Assert.Equal(GsmNetwork.DownlinkMessageKind.Sapi3Establishment, sapi3.Kind);
        Assert.Empty(sapi3.Information);
        Assert.Equal(3, sapi3.Sapi);

        IReadOnlyList<GsmNetwork.DownlinkMessage> cpDataMessages =
            network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.Sapi3Establishment);

        GsmNetwork.DownlinkMessage cpData = Assert.Single(cpDataMessages);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.MobileTerminatedSmsCpData, cpData.Kind);
        Assert.Equal(3, cpData.Sapi);
        Assert.True(cpData.Information.Length > 3);
        Assert.Equal(0x09, cpData.Information[0]);
        Assert.Equal(0x01, cpData.Information[1]);
        Assert.Equal(cpData.Information.Length - 3, cpData.Information[2]);
        Assert.Equal(0x01, cpData.Information[3]);
        Assert.Equal(
            [0x62, 0x70, 0x60, 0x41, 0x35, 0x92, 0x23],
            cpData.Information.AsSpan(23, 7).ToArray());
        Assert.Contains<byte>(0x40, cpData.Information);
    }

    [Fact]
    public void IncomingSmsUsesOriginalSentTimeInsteadOfDeliveryTime()
    {
        DateTimeOffset sentAt = new(2024, 12, 31, 23, 58, 47, TimeSpan.FromMinutes(330));
        GsmNetwork network = new(null, FixedNetworkTime);
        network.QueueIncomingSms("5551234", "hello", sentAt);
        network.HandleEstablishedLayer3(PagingResponse());
        network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.CipheringModeCommand);
        network.HandleActiveLayer3([0x06, 0x32]);

        GsmNetwork.DownlinkMessage cpData = Assert.Single(
            network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.Sapi3Establishment));

        Assert.Equal(
            [0x42, 0x21, 0x13, 0x32, 0x85, 0x74, 0x22],
            cpData.Information.AsSpan(23, 7).ToArray());
    }

    [Fact]
    public void IncomingSmartMessage_UsesEightBitDcsAndRingtonePortHeader()
    {
        GsmNetwork network = new(null, FixedNetworkTime);
        byte[] ringtone = NokiaSmartMessagingRingtone.EncodeDemoRingtone();
        network.QueueIncomingSmartMessage("5551234", NokiaSmartMessagingRingtone.DestinationPort, ringtone);

        network.HandleEstablishedLayer3(PagingResponse());
        network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.CipheringModeCommand);
        network.HandleActiveLayer3([0x06, 0x32]);
        GsmNetwork.DownlinkMessage cpData = Assert.Single(
            network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.Sapi3Establishment));

        ReadOnlySpan<byte> rpdu = cpData.Information.AsSpan(3);
        int serviceCentreAddressLength = rpdu[2];
        int tpduLengthOffset = 3 + serviceCentreAddressLength + 1;
        int tpduLength = rpdu[tpduLengthOffset];
        ReadOnlySpan<byte> tpdu = rpdu.Slice(tpduLengthOffset + 1, tpduLength);
        int originatorAddressBytes = (tpdu[1] + 1) / 2;
        int protocolIdentifierOffset = 3 + originatorAddressBytes;
        int userDataLengthOffset = protocolIdentifierOffset + 2 + 7;
        ReadOnlySpan<byte> userData = tpdu[(userDataLengthOffset + 1)..];

        Assert.Equal(0x44, tpdu[0]);
        Assert.Equal(0x00, tpdu[protocolIdentifierOffset]);
        Assert.Equal(0xF5, tpdu[protocolIdentifierOffset + 1]);
        Assert.Equal(7 + ringtone.Length, tpdu[userDataLengthOffset]);
        Assert.Equal([0x06, 0x05, 0x04, 0x15, 0x81, 0x00, 0x00], userData[..7].ToArray());
        Assert.Equal(ringtone, userData[7..].ToArray());
    }

    [Theory]
    [InlineData(2, 0x40)]
    [InlineData(3, 0x44)]
    public void IncomingMultipartSmartMessage_UsesNokiaLongRingtoneHeaderAndMoreMessagesFlag(
        byte partNumber,
        byte expectedFirstOctet)
    {
        GsmNetwork network = new(null, FixedNetworkTime);
        byte[] payload = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
        SmartMessageConcatenation concatenation = new(
            Reference: 0x7A,
            PartCount: 3,
            PartNumber: partNumber);
        network.QueueIncomingSmartMessage(
            "5551234",
            NokiaSmartMessagingRingtone.DestinationPort,
            payload,
            concatenation);

        network.HandleEstablishedLayer3(PagingResponse());
        network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.CipheringModeCommand);
        network.HandleActiveLayer3([0x06, 0x32]);
        GsmNetwork.DownlinkMessage cpData = Assert.Single(
            network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.Sapi3Establishment));

        ReadOnlySpan<byte> rpdu = cpData.Information.AsSpan(3);
        int serviceCentreAddressLength = rpdu[2];
        int tpduLengthOffset = 3 + serviceCentreAddressLength + 1;
        int tpduLength = rpdu[tpduLengthOffset];
        ReadOnlySpan<byte> tpdu = rpdu.Slice(tpduLengthOffset + 1, tpduLength);
        int originatorAddressBytes = (tpdu[1] + 1) / 2;
        int protocolIdentifierOffset = 3 + originatorAddressBytes;
        int userDataLengthOffset = protocolIdentifierOffset + 2 + 7;
        ReadOnlySpan<byte> userData = tpdu[(userDataLengthOffset + 1)..];

        Assert.Equal(expectedFirstOctet, tpdu[0]);
        Assert.Equal(0xF5, tpdu[protocolIdentifierOffset + 1]);
        Assert.Equal(140, tpdu[userDataLengthOffset]);
        Assert.Equal(
            [0x0B, 0x05, 0x04, 0x15, 0x81, 0x00, 0x00, 0x00, 0x03, 0x7A, 0x03, partNumber],
            userData[..12].ToArray());
        Assert.Equal(payload, userData[12..].ToArray());
    }

    [Fact]
    public void HandleActiveLayer3_IncomingSmsRpAck_QueuesCpAckAndChannelRelease()
    {
        GsmNetwork network = new(null);
        EstablishIncomingService(network, incomingCall: false);

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = network.HandleActiveLayer3([0x09, 0x01, 0x01, 0x02, 0x02, 0x40]);

        Assert.Equal(2, messages.Count);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.CpAck, messages[0].Kind);
        Assert.Equal([0x89, 0x04], messages[0].Information);
        Assert.Equal(GsmNetwork.DownlinkMessageKind.ChannelRelease, messages[1].Kind);
        Assert.Equal([0x06, 0x0D, 0x00], messages[1].Information);
        Assert.Equal(0, messages[1].Sapi);
        Assert.Equal(GsmNetwork.RegistrationState.AwaitingChannelReleaseAcknowledgement, network.State);
    }

    [Fact]
    public void HandleEstablishedLayer3_UnsupportedCmServiceRequest_DoesNotLeaveIdle()
    {
        GsmNetwork network = new(null);

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = network.HandleEstablishedLayer3(CmServiceRequest(serviceType: 0x0F));

        Assert.Empty(messages);
        Assert.Equal(GsmNetwork.CmServiceType.None, network.ActiveService);
        Assert.Equal(GsmNetwork.RegistrationState.Idle, network.State);
    }

    [Fact]
    public void HandleEstablishedLayer3_DuplicateLocationUpdatingRequestWhilePending_IsIgnored()
    {
        GsmNetwork network = new(null);
        network.HandleEstablishedLayer3(LocationUpdatingRequest());

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = network.HandleEstablishedLayer3(LocationUpdatingRequest());

        Assert.Empty(messages);
        Assert.Equal(GsmNetwork.RegistrationState.AwaitingLocationUpdatingAcceptAcknowledgement, network.State);
    }

    [Fact]
    public void HandleEstablishedLayer3_LocationUpdatingRequestAfterRelease_IsIgnored()
    {
        GsmNetwork network = new(null);
        network.HandleEstablishedLayer3(LocationUpdatingRequest());
        network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.LocationUpdatingAccept);
        network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.ChannelRelease);

        IReadOnlyList<GsmNetwork.DownlinkMessage> messages = network.HandleEstablishedLayer3(LocationUpdatingRequest());

        Assert.Empty(messages);
        Assert.Equal(GsmNetwork.RegistrationState.Released, network.State);
    }

    private static byte[] LocationUpdatingRequest() =>
    [
        0x05, 0x08, 0x70, 0x00, 0xF0, 0x00, 0xFF, 0xFE, 0x33, 0x08, 0x92,
        0x80, 0x10, 0x00, 0x00, 0x00, 0x00, 0x10,
    ];

    private static byte[] CmServiceRequest(byte serviceType) =>
    [
        0x05, 0x24, serviceType,
        0x02, 0x00,
        0x01, 0x29,
    ];

    private static byte[] PagingResponse() =>
    [
        0x06, 0x27,
        0x02, 0x00,
        0x08, 0x92, 0x80, 0x10, 0x00, 0x00, 0x00, 0x00,
    ];

    private static byte[] MobileOriginatedCallSetup(string destination, bool international = false)
    {
        byte[] bcd = EncodeSemiOctets(destination);
        return
        [
            0x03,
            0x05,
            0x04, 0x04, 0x60, 0x02, 0x00, 0x81,
            0x5E,
            (byte)(bcd.Length + 1),
            international ? (byte)0x91 : (byte)0x81,
            .. bcd,
        ];
    }

    private static byte[] MobileOriginatedSmsCpData(string destination, string text, byte rpReference)
    {
        byte[] destinationBcd = EncodeSemiOctets(destination);
        byte[] packedText = PackAsciiGsm7(text);
        byte[] tpdu =
        [
            0x01,
            0x22,
            (byte)destination.Length,
            0x81,
            .. destinationBcd,
            0x00,
            0x00,
            (byte)text.Length,
            .. packedText,
        ];
        byte[] rpdu =
        [
            0x00,
            rpReference,
            0x00,
            0x00,
            (byte)tpdu.Length,
            .. tpdu,
        ];
        return [0x09, 0x01, (byte)rpdu.Length, .. rpdu];
    }

    private static byte[] EncodeSemiOctets(string digits)
    {
        byte[] encoded = new byte[(digits.Length + 1) / 2];
        for (int index = 0; index < digits.Length; index++)
        {
            encoded[index / 2] |= (byte)((digits[index] - '0') << ((index & 1) * 4));
        }

        if ((digits.Length & 1) != 0)
        {
            encoded[^1] |= 0xF0;
        }

        return encoded;
    }

    private static byte[] PackAsciiGsm7(string text)
    {
        byte[] packed = new byte[(text.Length * 7 + 7) / 8];
        for (int index = 0; index < text.Length; index++)
        {
            int bitOffset = index * 7;
            int byteOffset = bitOffset / 8;
            int shift = bitOffset % 8;
            int septet = text[index] & 0x7F;
            packed[byteOffset] |= (byte)(septet << shift);
            if (shift > 1 && byteOffset + 1 < packed.Length)
            {
                packed[byteOffset + 1] |= (byte)(septet >> (8 - shift));
            }
        }

        return packed;
    }

    private static void EstablishCmService(GsmNetwork network, byte serviceType)
    {
        network.HandleEstablishedLayer3(CmServiceRequest(serviceType));
        network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.CipheringModeCommand);
        network.HandleActiveLayer3([0x06, 0x32]);
    }

    private static void EstablishIncomingService(GsmNetwork network, bool incomingCall)
    {
        if (incomingCall)
        {
            network.QueueIncomingCall("5551234");
        }
        else
        {
            network.QueueIncomingSms("5551234", "hello");
        }

        network.HandleEstablishedLayer3(PagingResponse());
        network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.CipheringModeCommand);
        network.HandleActiveLayer3([0x06, 0x32]);

        network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.MmInformation);
        GsmNetwork.DownlinkMessageKind initialKind = incomingCall
            ? GsmNetwork.DownlinkMessageKind.IncomingCallSetup
            : GsmNetwork.DownlinkMessageKind.Sapi3Establishment;
        network.HandleDownlinkAcknowledgement(initialKind);
    }

    private static void EstablishIncomingCall(GsmNetwork network, Guid requestId)
    {
        network.QueueIncomingCall(requestId, "5551234");
        network.HandleEstablishedLayer3(PagingResponse());
        network.HandleDownlinkAcknowledgement(GsmNetwork.DownlinkMessageKind.CipheringModeCommand);
        network.HandleActiveLayer3([0x06, 0x32]);
    }

    private static DateTimeOffset FixedNetworkTime() =>
        new(2026, 7, 6, 14, 53, 29, TimeSpan.FromHours(8));
}
