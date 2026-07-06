using Noks.Dct3.Audio;
using Noks.Dct3.Radio;
namespace Noks.Dct3.Tests;

public sealed class LapdmLinkTests
{
    [Fact]
    public void HandleUplink_SabmWithLocationUpdatingRequest_QueuesUaAndLocationUpdatingAccept()
    {
        LapdmLink link = new(null);

        LapdmLink.UplinkResult result = link.HandleUplink(0x80, BuildSabm(LocationUpdatingRequest()));

        Assert.False(result.ReleaseAfterDownlinkFrames);
        Assert.Equal(2, result.DownlinkFrames.Count);
        AssertFrame(
            result.DownlinkFrames[0],
            [
                0x01, 0x73, 0x49,
                0x05, 0x08, 0x70, 0x00, 0xF0, 0x00, 0xFF, 0xFE, 0x33, 0x08, 0x92,
                0x80, 0x10, 0x00, 0x00, 0x00, 0x00, 0x10,
            ]);
        AssertFrame(
            result.DownlinkFrames[1],
            [
                0x03, 0x00, 0x1D,
                0x05, 0x02,
                0x02, 0xF8, 0x10, 0x00, 0x01,
            ]);
    }

    [Fact]
    public void HandleUplink_RrAcknowledgesLocationUpdatingAccept_QueuesMmInformationAndChannelRelease()
    {
        LapdmLink link = new(null, FixedNetworkTime);
        link.HandleUplink(0x80, BuildSabm(LocationUpdatingRequest()));

        LapdmLink.UplinkResult result = link.HandleUplink(0x80, BuildReceiveReady(receiveSequence: 1));

        Assert.False(result.ReleaseAfterDownlinkFrames);
        Assert.Equal(2, result.DownlinkFrames.Count);
        AssertFrame(
            result.DownlinkFrames[0],
            [
                0x03, 0x02, 0x29,
                0x05, 0x32, 0x47, 0x62, 0x70, 0x60, 0x41, 0x35, 0x92, 0x23,
            ]);
        AssertFrame(
            result.DownlinkFrames[1],
            [
                0x03, 0x04, 0x0D,
                0x06, 0x0D, 0x00,
            ]);
    }

    [Fact]
    public void HandleUplink_RrAcknowledgesChannelRelease_DoesNotQueueMoreFrames()
    {
        LapdmLink link = new(null, FixedNetworkTime);
        link.HandleUplink(0x80, BuildSabm(LocationUpdatingRequest()));
        link.HandleUplink(0x80, BuildReceiveReady(receiveSequence: 1));

        LapdmLink.UplinkResult result = link.HandleUplink(0x80, BuildReceiveReady(receiveSequence: 3));

        Assert.False(result.ReleaseAfterDownlinkFrames);
        Assert.Empty(result.DownlinkFrames);
        Assert.True(link.SuppressImsiPagingAfterRegistration);
    }

    [Fact]
    public void ExpirePending_DropsStaleDownlinkAcknowledgement()
    {
        List<string> trace = [];
        LapdmLink link = new(trace.Add);
        link.HandleUplink(0x80, BuildSabm(LocationUpdatingRequest()), cycles: 0);

        Assert.True(link.ExpirePending(cycles: 6, timeoutCycles: 5));

        LapdmLink.UplinkResult result = link.HandleUplink(0x80, BuildReceiveReady(receiveSequence: 1), cycles: 7);

        Assert.False(result.ReleaseAfterDownlinkFrames);
        Assert.Empty(result.DownlinkFrames);
        Assert.False(link.SuppressImsiPagingAfterRegistration);
        Assert.Contains(trace, message => message.Contains("DSP LAPDm pending state timed out", StringComparison.Ordinal));
    }

    [Fact]
    public void HandleUplink_Disc_QueuesUaAndRequestsReleaseAfterDownlink()
    {
        LapdmLink link = new(null);

        LapdmLink.UplinkResult result = link.HandleUplink(0x80, BuildDisconnect());

        Assert.True(result.ReleaseAfterDownlinkFrames);
        byte[] ua = Assert.Single(result.DownlinkFrames);
        AssertFrame(ua, [0x01, 0x73, 0x01]);
    }

    [Fact]
    public void HandleUplink_SabmWithNonLocationUpdatingInformation_QueuesOnlyUa()
    {
        LapdmLink link = new(null);

        LapdmLink.UplinkResult result = link.HandleUplink(0x80, BuildSabm([0x06, 0x00]));

        Assert.False(result.ReleaseAfterDownlinkFrames);
        byte[] ua = Assert.Single(result.DownlinkFrames);
        AssertFrame(ua, [0x01, 0x73, 0x09, 0x06, 0x00]);
    }

    [Fact]
    public void HandleUplink_SabmWithoutInformation_QueuesOnlyUa()
    {
        LapdmLink link = new(null);

        LapdmLink.UplinkResult result = link.HandleUplink(0x80, BuildSabm([], sapi: 3));

        Assert.False(result.ReleaseAfterDownlinkFrames);
        byte[] ua = Assert.Single(result.DownlinkFrames);
        AssertFrame(ua, [0x0D, 0x73, 0x01]);
    }

    [Fact]
    public void HandleUplink_SabmWithCmServiceRequest_QueuesUaAndCipheringModeCommand()
    {
        LapdmLink link = new(null);

        LapdmLink.UplinkResult result = link.HandleUplink(0x80, BuildSabm(CmServiceRequest(serviceType: 0x04)));

        Assert.False(result.ReleaseAfterDownlinkFrames);
        Assert.Equal(2, result.DownlinkFrames.Count);
        AssertFrame(
            result.DownlinkFrames[0],
            [
                0x01, 0x73, 0x1D,
                0x05, 0x24, 0x04, 0x02, 0x00, 0x01, 0x29,
            ]);
        AssertFrame(
            result.DownlinkFrames[1],
            [
                0x03, 0x00, 0x0D,
                0x06, 0x35, 0x01,
            ]);
    }

    [Fact]
    public void HandleUplink_IFrameWithSmsCpData_AcknowledgesAndLogsMessage()
    {
        List<string> trace = [];
        LapdmLink link = new(trace.Add);
        EstablishCmService(link, serviceType: 0x04);

        LapdmLink.UplinkResult result = link.HandleUplink(0x80, BuildInformationFrame([0x09, 0x01, 0x02, 0x00, 0x42], sendSequence: 1, receiveSequence: 2));

        Assert.False(result.ReleaseAfterDownlinkFrames);
        Assert.Equal(3, result.DownlinkFrames.Count);
        AssertFrame(result.DownlinkFrames[0], [0x01, 0x41, 0x01]);
        AssertFrame(result.DownlinkFrames[1], [0x03, 0x44, 0x09, 0x89, 0x04]);
        AssertFrame(result.DownlinkFrames[2], [0x03, 0x46, 0x15, 0x89, 0x01, 0x02, 0x03, 0x42]);
        Assert.Contains(trace, message => message.Contains("DSP SMS RP-DATA", StringComparison.Ordinal));
        Assert.Contains(trace, message => message.Contains("DSP SMS CP-DATA", StringComparison.Ordinal));

        LapdmLink.UplinkResult cpAckResult = link.HandleUplink(0x80, BuildInformationFrame([0x09, 0x04], sendSequence: 2, receiveSequence: 4));

        Assert.Equal(2, cpAckResult.DownlinkFrames.Count);
        AssertFrame(cpAckResult.DownlinkFrames[0], [0x01, 0x61, 0x01]);
        AssertFrame(cpAckResult.DownlinkFrames[1], [0x03, 0x68, 0x0D, 0x06, 0x0D, 0x00]);
        Assert.Contains(trace, message => message.Contains("DSP SMS CP-ACK received", StringComparison.Ordinal));
    }

    [Fact]
    public void HandleUplink_IFrameWithSmsCpDataOnSapi3_AcknowledgesAndRespondsOnSapi3()
    {
        List<string> trace = [];
        LapdmLink link = new(trace.Add);
        EstablishCmService(link, serviceType: 0x04);

        LapdmLink.UplinkResult sabmResult = link.HandleUplink(0x80, BuildSabm([], sapi: 3));

        byte[] ua = Assert.Single(sabmResult.DownlinkFrames);
        AssertFrame(ua, [0x0D, 0x73, 0x01]);

        LapdmLink.UplinkResult result = link.HandleUplink(0x80, BuildInformationFrame([0x09, 0x01, 0x02, 0x00, 0x42], sapi: 3));

        Assert.False(result.ReleaseAfterDownlinkFrames);
        Assert.Equal(3, result.DownlinkFrames.Count);
        AssertFrame(result.DownlinkFrames[0], [0x0D, 0x21, 0x01]);
        AssertFrame(result.DownlinkFrames[1], [0x0F, 0x20, 0x09, 0x89, 0x04]);
        AssertFrame(result.DownlinkFrames[2], [0x0F, 0x22, 0x15, 0x89, 0x01, 0x02, 0x03, 0x42]);
        Assert.Contains(trace, message => message.Contains("DSP SMS RP-DATA", StringComparison.Ordinal));
        Assert.Contains(trace, message => message.Contains("DSP SMS CP-DATA", StringComparison.Ordinal));

        LapdmLink.UplinkResult cpAckResult = link.HandleUplink(0x80, BuildInformationFrame([0x09, 0x04], sendSequence: 1, receiveSequence: 2, sapi: 3));

        Assert.Equal(2, cpAckResult.DownlinkFrames.Count);
        AssertFrame(cpAckResult.DownlinkFrames[0], [0x0D, 0x41, 0x01]);
        AssertFrame(cpAckResult.DownlinkFrames[1], [0x03, 0x24, 0x0D, 0x06, 0x0D, 0x00]);
        Assert.Contains(trace, message => message.Contains("DSP SMS CP-ACK received", StringComparison.Ordinal));

        LapdmLink.UplinkResult channelReleaseAckResult = link.HandleUplink(0x80, BuildReceiveReady(receiveSequence: 3));

        Assert.Empty(channelReleaseAckResult.DownlinkFrames);
        Assert.True(link.SuppressImsiPagingAfterRegistration);
    }

    [Fact]
    public void HandleUplink_SegmentedIFrameWithSmsCpData_ReassemblesBeforeLayer3Handling()
    {
        List<string> trace = [];
        List<OutgoingNetworkRequest> requests = [];
        LapdmLink link = new(trace.Add, outgoingNetworkRequest: requests.Add);
        EstablishCmService(link, serviceType: 0x04);
        link.HandleUplink(0x80, BuildSabm([], sapi: 3));
        byte[] cpData =
        [
            // Stock v4.18 firmware SMS-SUBMIT for text "A" to 1234567890123.
            0x39, 0x01, 0x1B, 0x00, 0x01, 0x00, 0x06, 0x91, 0x21, 0x43,
            0x65, 0x87, 0x09, 0x10, 0x11, 0x05, 0x0D, 0x81, 0x21, 0x43,
            0x65, 0x87, 0x09, 0x21, 0xF3, 0x00, 0x00, 0xA7, 0x01, 0x41,
        ];

        LapdmLink.UplinkResult firstSegment = link.HandleUplink(0x80, BuildInformationFrame(cpData.AsSpan(0, 20), sapi: 3, moreData: true));

        byte[] firstSegmentRr = Assert.Single(firstSegment.DownlinkFrames);
        AssertFrame(firstSegmentRr, [0x0D, 0x21, 0x01]);
        Assert.DoesNotContain(trace, message => message.Contains("DSP SMS CP-DATA", StringComparison.Ordinal));

        LapdmLink.UplinkResult polledDuplicate = link.HandleUplink(
            0x80,
            BuildInformationFrame(
                cpData.AsSpan(0, 20),
                sapi: 3,
                moreData: true,
                pollFinal: true));

        byte[] duplicateRr = Assert.Single(polledDuplicate.DownlinkFrames);
        AssertFrame(duplicateRr, [0x0D, 0x31, 0x01]);
        Assert.DoesNotContain(trace, message => message.Contains("DSP SMS CP-DATA", StringComparison.Ordinal));

        LapdmLink.UplinkResult secondSegment = link.HandleUplink(0x80, BuildInformationFrame(cpData.AsSpan(20), sendSequence: 1, sapi: 3));

        Assert.Equal(2, secondSegment.DownlinkFrames.Count);
        AssertFrame(secondSegment.DownlinkFrames[0], [0x0D, 0x41, 0x01]);
        AssertFrame(secondSegment.DownlinkFrames[1], [0x0F, 0x40, 0x09, 0xB9, 0x04]);
        OutgoingNetworkRequest request = Assert.Single(requests);
        Assert.Equal(NetworkRequestKind.Sms, request.Kind);
        Assert.Equal("1234567890123", request.NormalizedDestination);
        Assert.Equal("A", request.SmsText);
        Assert.Contains(trace, message => message.Contains("DSP SMS RP-DATA ref=01", StringComparison.Ordinal));
        Assert.Contains(trace, message => message.Contains("DSP SMS submission", StringComparison.Ordinal));

        LapdmLink.UplinkResult accepted = link.ResolveNetworkRequest(
            new ResolveNetworkRequest(request.RequestId, NetworkRequestDecision.Accept));

        byte[] rpAck = Assert.Single(accepted.DownlinkFrames);
        AssertFrame(rpAck, [0x0F, 0x42, 0x15, 0xB9, 0x01, 0x02, 0x03, 0x01]);
    }

    [Fact]
    public void HandleUplink_IFrameWithCallSetup_AcknowledgesAndLogsMessage()
    {
        List<string> trace = [];
        LapdmLink link = new(trace.Add);
        EstablishCmService(link, serviceType: 0x01);

        LapdmLink.UplinkResult result = link.HandleUplink(0x80, BuildInformationFrame([0x03, 0x45, 0x04], sendSequence: 1, receiveSequence: 2));

        Assert.False(result.ReleaseAfterDownlinkFrames);
        Assert.Equal(4, result.DownlinkFrames.Count);
        AssertFrame(result.DownlinkFrames[0], [0x01, 0x41, 0x01]);
        AssertFrame(result.DownlinkFrames[1], [0x03, 0x44, 0x09, 0x83, 0x02]);
        AssertFrame(result.DownlinkFrames[2], [0x03, 0x46, 0x09, 0x83, 0x01]);
        AssertFrame(result.DownlinkFrames[3], [0x03, 0x48, 0x09, 0x83, 0x07]);
        Assert.Contains(trace, message => message.Contains("DSP CC SETUP", StringComparison.Ordinal));

        LapdmLink.UplinkResult connectAckResult = link.HandleUplink(0x80, BuildInformationFrame([0x03, 0x4F], sendSequence: 2, receiveSequence: 5));

        byte[] rr = Assert.Single(connectAckResult.DownlinkFrames);
        AssertFrame(rr, [0x01, 0x61, 0x01]);
        Assert.Contains(trace, message => message.Contains("DSP CC CONNECT ACKNOWLEDGE received", StringComparison.Ordinal));

        LapdmLink.UplinkResult disconnectResult = link.HandleUplink(0x80, BuildInformationFrame([0x03, 0x65, 0x08, 0x02, 0x90], sendSequence: 3, receiveSequence: 5));

        Assert.Equal(2, disconnectResult.DownlinkFrames.Count);
        AssertFrame(disconnectResult.DownlinkFrames[0], [0x01, 0x81, 0x01]);
        AssertFrame(disconnectResult.DownlinkFrames[1], [0x03, 0x8A, 0x09, 0x83, 0x2D]);
        Assert.Contains(trace, message => message.Contains("DSP CC DISCONNECT", StringComparison.Ordinal));

        LapdmLink.UplinkResult releaseAckResult = link.HandleUplink(0x80, BuildReceiveReady(receiveSequence: 6));

        Assert.Empty(releaseAckResult.DownlinkFrames);
        Assert.Contains(trace, message => message.Contains("DSP CC RELEASE acknowledged", StringComparison.Ordinal));

        LapdmLink.UplinkResult releaseCompleteResult = link.HandleUplink(0x80, BuildInformationFrame([0x03, 0x6A], sendSequence: 4, receiveSequence: 6));

        Assert.Equal(2, releaseCompleteResult.DownlinkFrames.Count);
        AssertFrame(releaseCompleteResult.DownlinkFrames[0], [0x01, 0xA1, 0x01]);
        AssertFrame(releaseCompleteResult.DownlinkFrames[1], [0x03, 0xAC, 0x0D, 0x06, 0x0D, 0x00]);
        Assert.Contains(trace, message => message.Contains("DSP CC RELEASE COMPLETE received", StringComparison.Ordinal));

        LapdmLink.UplinkResult channelReleaseAckResult = link.HandleUplink(0x80, BuildReceiveReady(receiveSequence: 7));

        Assert.Empty(channelReleaseAckResult.DownlinkFrames);
        Assert.True(link.SuppressImsiPagingAfterRegistration);
    }

    [Fact]
    public void HandleUplink_SegmentedTwentyDigitCall_UsesInvalidNumberCarrierIntercept()
    {
        List<OutgoingNetworkRequest> requests = [];
        List<CallAudioAnnouncement> announcements = [];
        LapdmLink link = new(
            null,
            outgoingNetworkRequest: requests.Add,
            callAudioAnnouncement: announcements.Add);
        EstablishCmService(link, serviceType: 0x01);
        byte[] setup = MobileOriginatedCallSetup("12345678901234567890");
        Assert.True(setup.Length > 20);

        LapdmLink.UplinkResult firstSegment = link.HandleUplink(
            0x80,
            BuildInformationFrame(
                setup.AsSpan(0, 20),
                sendSequence: 1,
                receiveSequence: 2,
                moreData: true));

        byte[] firstSegmentRr = Assert.Single(firstSegment.DownlinkFrames);
        AssertFrame(firstSegmentRr, [0x01, 0x41, 0x01]);
        Assert.Empty(requests);

        LapdmLink.UplinkResult finalSegment = link.HandleUplink(
            0x80,
            BuildInformationFrame(
                setup.AsSpan(20),
                sendSequence: 2,
                receiveSequence: 2));

        Assert.Equal(3, finalSegment.DownlinkFrames.Count);
        AssertFrame(finalSegment.DownlinkFrames[0], [0x01, 0x61, 0x01]);
        Assert.Empty(requests);

        link.HandleUplink(
            0x80,
            BuildInformationFrame(
                [0x03, 0x0F],
                sendSequence: 3,
                receiveSequence: 5));

        CallAudioAnnouncement announcement = Assert.Single(announcements);
        Assert.Equal(CallAudioAnnouncementKind.InvalidNumber, announcement.Kind);
        Assert.Equal("The number you dialed is invalid.", announcement.Text);
    }

    [Fact]
    public void HandleUplink_PolledInformationFrame_SetsFinalBitOnReceiveReadyResponse()
    {
        LapdmLink link = new(null);

        LapdmLink.UplinkResult result = link.HandleUplink(
            0x80,
            BuildInformationFrame([0x06, 0x32], pollFinal: true));

        byte[] receiveReady = Assert.Single(result.DownlinkFrames);
        AssertFrame(receiveReady, [0x01, 0x31, 0x01]);
    }

    [Fact]
    public void ResolveNetworkRequest_QueuesDeferredCallFramesOnOriginalLink()
    {
        List<OutgoingNetworkRequest> requests = [];
        LapdmLink link = new(null, outgoingNetworkRequest: requests.Add);
        EstablishCmService(link, serviceType: 0x01);

        LapdmLink.UplinkResult pending = link.HandleUplink(
            0x80,
            BuildInformationFrame(
                MobileOriginatedCallSetup("1234567890123"),
                sendSequence: 1,
                receiveSequence: 2));

        byte[] rr = Assert.Single(pending.DownlinkFrames);
        AssertFrame(rr, [0x01, 0x41, 0x01]);
        OutgoingNetworkRequest request = Assert.Single(requests);

        LapdmLink.UplinkResult accepted = link.ResolveNetworkRequest(
            new ResolveNetworkRequest(request.RequestId, NetworkRequestDecision.Accept),
            cycles: 123);

        Assert.Equal(2, accepted.DownlinkFrames.Count);
        AssertFrame(accepted.DownlinkFrames[0], [0x03, 0x44, 0x09, 0x83, 0x02]);
        AssertFrame(accepted.DownlinkFrames[1], [0x03, 0x46, 0x09, 0x83, 0x01]);
        LapdmLink.UplinkResult connected = link.ConnectNetworkCall(request.RequestId, cycles: 456);
        AssertFrame(Assert.Single(connected.DownlinkFrames), [0x03, 0x48, 0x09, 0x83, 0x07]);
        Assert.Empty(link.ConnectNetworkCall(request.RequestId).DownlinkFrames);
        Assert.Empty(link.ResolveNetworkRequest(
            new ResolveNetworkRequest(request.RequestId, NetworkRequestDecision.Accept)).DownlinkFrames);
    }

    [Fact]
    public void TerminateNetworkCall_QueuesReleaseOnCallControlLinkExactlyOnce()
    {
        List<OutgoingNetworkRequest> requests = [];
        LapdmLink link = new(null, outgoingNetworkRequest: requests.Add);
        EstablishCmService(link, serviceType: 0x01);
        link.HandleUplink(
            0x80,
            BuildInformationFrame(
                MobileOriginatedCallSetup("1234567890123"),
                sendSequence: 1,
                receiveSequence: 2));
        Guid requestId = Assert.Single(requests).RequestId;
        link.ResolveNetworkRequest(
            new ResolveNetworkRequest(requestId, NetworkRequestDecision.Accept),
            cycles: 123);

        LapdmLink.UplinkResult termination = link.TerminateNetworkCall(requestId, cycles: 456);

        byte[] release = Assert.Single(termination.DownlinkFrames);
        AssertFrame(release, [0x03, 0x48, 0x09, 0x83, 0x2D]);
        Assert.Empty(link.TerminateNetworkCall(requestId).DownlinkFrames);
    }

    [Fact]
    public void HandleUplink_SabmWithPagingResponseForIncomingCall_QueuesCipheringAndSetup()
    {
        List<string> trace = [];
        LapdmLink link = new(trace.Add);
        link.QueueIncomingCall("5551234");

        LapdmLink.UplinkResult sabmResult = link.HandleUplink(0x80, BuildSabm(PagingResponse()));

        Assert.Equal(2, sabmResult.DownlinkFrames.Count);
        AssertFrame(
            sabmResult.DownlinkFrames[0],
            [
                0x01, 0x73, 0x31,
                0x06, 0x27, 0x02, 0x00, 0x08, 0x92, 0x80, 0x10, 0x00, 0x00, 0x00, 0x00,
            ]);
        AssertFrame(sabmResult.DownlinkFrames[1], [0x03, 0x00, 0x0D, 0x06, 0x35, 0x01]);

        LapdmLink.UplinkResult cipheringAckResult = link.HandleUplink(0x80, BuildReceiveReady(receiveSequence: 1));
        Assert.Empty(cipheringAckResult.DownlinkFrames);

        LapdmLink.UplinkResult cipheringCompleteResult = link.HandleUplink(0x80, BuildInformationFrame([0x06, 0x32], receiveSequence: 1));

        Assert.Equal(3, cipheringCompleteResult.DownlinkFrames.Count);
        AssertFrame(cipheringCompleteResult.DownlinkFrames[0], [0x01, 0x21, 0x01]);
        AssertFramePrefix(cipheringCompleteResult.DownlinkFrames[1], [0x03, 0x22, 0x29, 0x05, 0x32, 0x47]);
        AssertFrame(
            cipheringCompleteResult.DownlinkFrames[2],
            [
                0x03, 0x24, 0x45,
                0x03, 0x05, 0x04, 0x04, 0x60, 0x02, 0x00, 0x81, 0x34, 0x01, 0x5C, 0x05, 0x81, 0x55, 0x15, 0x32, 0xF4,
            ]);
        Assert.Contains(trace, message => message.Contains("DSP RR paging response", StringComparison.Ordinal));
    }

    [Fact]
    public void HandleUplink_IncomingCallConnect_QueuesConnectAcknowledge()
    {
        List<string> trace = [];
        LapdmLink link = new(trace.Add);
        EstablishIncomingCall(link);

        LapdmLink.UplinkResult callConfirmedResult = link.HandleUplink(0x80, BuildInformationFrame([0x03, 0x08], sendSequence: 1, receiveSequence: 3));

        byte[] callConfirmedAck = Assert.Single(callConfirmedResult.DownlinkFrames);
        AssertFrame(callConfirmedAck, [0x01, 0x41, 0x01]);

        LapdmLink.UplinkResult alertingResult = link.HandleUplink(0x80, BuildInformationFrame([0x03, 0x01], sendSequence: 2, receiveSequence: 3));

        byte[] alertingAck = Assert.Single(alertingResult.DownlinkFrames);
        AssertFrame(alertingAck, [0x01, 0x61, 0x01]);

        LapdmLink.UplinkResult connectResult = link.HandleUplink(0x80, BuildInformationFrame([0x03, 0x07], sendSequence: 3, receiveSequence: 3));

        Assert.Equal(2, connectResult.DownlinkFrames.Count);
        AssertFrame(connectResult.DownlinkFrames[0], [0x01, 0x81, 0x01]);
        AssertFrame(connectResult.DownlinkFrames[1], [0x03, 0x86, 0x09, 0x83, 0x0F]);
        Assert.Contains(trace, message => message.Contains("DSP CC CONNECT received for incoming call", StringComparison.Ordinal));
    }

    [Fact]
    public void HandleUplink_SabmWithPagingResponseForIncomingSms_EstablishesSapi3AndSendsSegmentedCpData()
    {
        List<string> trace = [];
        LapdmLink link = new(trace.Add, FixedNetworkTime);
        link.QueueIncomingSms("5551234", "hello");

        link.HandleUplink(0x80, BuildSabm(PagingResponse()));
        link.HandleUplink(0x80, BuildReceiveReady(receiveSequence: 1));

        LapdmLink.UplinkResult cipheringCompleteResult = link.HandleUplink(0x80, BuildInformationFrame([0x06, 0x32], receiveSequence: 1));

        Assert.Equal(3, cipheringCompleteResult.DownlinkFrames.Count);
        AssertFrame(cipheringCompleteResult.DownlinkFrames[0], [0x01, 0x21, 0x01]);
        AssertFramePrefix(cipheringCompleteResult.DownlinkFrames[1], [0x03, 0x22, 0x29, 0x05, 0x32, 0x47]);
        AssertFrame(cipheringCompleteResult.DownlinkFrames[2], [0x0F, 0x3F, 0x01]);

        LapdmLink.UplinkResult uaResult = link.HandleUplink(0x80, BuildUaResponse(sapi: 3));

        Assert.Equal(2, uaResult.DownlinkFrames.Count);
        AssertFrame(
            uaResult.DownlinkFrames[0],
            [
                0x0F, 0x00, 0x53,
                0x09, 0x01, 0x21, 0x01, 0x40, 0x06, 0x91, 0x21, 0x43, 0x65, 0x87,
                0x09, 0x00, 0x16, 0x04, 0x07, 0x81, 0x55, 0x15, 0x32,
            ]);
        AssertFrame(
            uaResult.DownlinkFrames[1],
            [
                0x0F, 0x02, 0x41,
                0xF4, 0x00, 0x00, 0x62, 0x70, 0x60, 0x41, 0x35, 0x92, 0x23, 0x05,
                0xE8, 0x32, 0x9B, 0xFD, 0x06,
            ]);
        Assert.Contains(trace, message => message.Contains("DSP SMS MT SAPI3 established", StringComparison.Ordinal));

        LapdmLink.UplinkResult cpDataAckResult = link.HandleUplink(0x80, BuildReceiveReady(receiveSequence: 2, sapi: 3));

        Assert.Empty(cpDataAckResult.DownlinkFrames);

        LapdmLink.UplinkResult cpAckResult = link.HandleUplink(0x80, BuildInformationFrame([0x89, 0x04], sendSequence: 0, receiveSequence: 2, sapi: 3));

        byte[] cpAckReceiveReady = Assert.Single(cpAckResult.DownlinkFrames);
        AssertFrame(cpAckReceiveReady, [0x0D, 0x21, 0x01]);

        LapdmLink.UplinkResult rpAckResult = link.HandleUplink(0x80, BuildInformationFrame([0x89, 0x01, 0x01, 0x02, 0x02, 0x40], sendSequence: 1, receiveSequence: 2, sapi: 3));

        Assert.Equal(3, rpAckResult.DownlinkFrames.Count);
        AssertFrame(rpAckResult.DownlinkFrames[0], [0x0D, 0x41, 0x01]);
        AssertFrame(rpAckResult.DownlinkFrames[1], [0x0F, 0x44, 0x09, 0x09, 0x04]);
        AssertFrame(rpAckResult.DownlinkFrames[2], [0x03, 0x24, 0x0D, 0x06, 0x0D, 0x00]);
        Assert.Contains(trace, message => message.Contains("DSP SMS MT RP-ACK ref=40", StringComparison.Ordinal));
    }

    [Fact]
    public void ExpirePending_DropsStaleModeSettingAcknowledgement()
    {
        List<string> trace = [];
        LapdmLink link = new(trace.Add);
        link.QueueIncomingSms("5551234", "hello");

        link.HandleUplink(0x80, BuildSabm(PagingResponse()), cycles: 0);
        link.HandleUplink(0x80, BuildReceiveReady(receiveSequence: 1), cycles: 1);
        LapdmLink.UplinkResult cipheringCompleteResult =
            link.HandleUplink(0x80, BuildInformationFrame([0x06, 0x32], receiveSequence: 1), cycles: 2);

        Assert.Equal(3, cipheringCompleteResult.DownlinkFrames.Count);
        AssertFramePrefix(cipheringCompleteResult.DownlinkFrames[1], [0x03, 0x22, 0x29, 0x05, 0x32, 0x47]);
        AssertFrame(cipheringCompleteResult.DownlinkFrames[2], [0x0F, 0x3F, 0x01]);

        Assert.True(link.ExpirePending(cycles: 8, timeoutCycles: 5));

        LapdmLink.UplinkResult uaResult = link.HandleUplink(0x80, BuildUaResponse(sapi: 3), cycles: 9);

        Assert.False(uaResult.ReleaseAfterDownlinkFrames);
        Assert.Empty(uaResult.DownlinkFrames);
        Assert.Contains(trace, message => message.Contains("DSP LAPDm pending state timed out", StringComparison.Ordinal));
    }

    [Fact]
    public void HandleUplink_MalformedSabmLength_DoesNotQueueFrames()
    {
        LapdmLink link = new(null);
        byte[] malformed = BuildSabm(LocationUpdatingRequest());
        malformed[2] = 0xFD;

        LapdmLink.UplinkResult result = link.HandleUplink(0x80, malformed);

        Assert.False(result.ReleaseAfterDownlinkFrames);
        Assert.Empty(result.DownlinkFrames);
    }

    [Fact]
    public void HandleUplink_SabmWithBadAddressEa_DoesNotQueueFrames()
    {
        LapdmLink link = new(null);
        byte[] malformed = BuildSabm(LocationUpdatingRequest(), sapi: 0);
        malformed[0] &= 0xFE;

        LapdmLink.UplinkResult result = link.HandleUplink(0x80, malformed);

        Assert.False(result.ReleaseAfterDownlinkFrames);
        Assert.Empty(result.DownlinkFrames);
    }

    [Fact]
    public void HandleUplink_SabmWithLengthMBit_DoesNotQueueFrames()
    {
        LapdmLink link = new(null);
        byte[] malformed = BuildSabm(LocationUpdatingRequest());
        malformed[2] |= 0x02;

        LapdmLink.UplinkResult result = link.HandleUplink(0x80, malformed);

        Assert.False(result.ReleaseAfterDownlinkFrames);
        Assert.Empty(result.DownlinkFrames);
    }

    [Fact]
    public void HandleUplink_UnsupportedControlFrame_DoesNotQueueFrames()
    {
        LapdmLink link = new(null);
        byte[] unsupported = BuildEmptyFrame();
        unsupported[0] = 0x03;
        unsupported[1] = 0x03;
        unsupported[2] = 0x01;

        LapdmLink.UplinkResult result = link.HandleUplink(0x80, unsupported);

        Assert.False(result.ReleaseAfterDownlinkFrames);
        Assert.Empty(result.DownlinkFrames);
    }

    [Fact]
    public void HandleUplink_SupervisoryFrameWithUnexpectedLength_DoesNotQueueFrames()
    {
        LapdmLink link = new(null);
        byte[] unsupported = BuildReceiveReady(receiveSequence: 0);
        unsupported[2] = 0x05;

        LapdmLink.UplinkResult result = link.HandleUplink(0x80, unsupported);

        Assert.False(result.ReleaseAfterDownlinkFrames);
        Assert.Empty(result.DownlinkFrames);
    }

    [Fact]
    public void HandleUplink_SabmWithNonzeroSapi_PreservesSapiInUa()
    {
        LapdmLink link = new(null);

        LapdmLink.UplinkResult result = link.HandleUplink(0x80, BuildSabm([0x06, 0x00], sapi: 3));

        Assert.False(result.ReleaseAfterDownlinkFrames);
        byte[] ua = Assert.Single(result.DownlinkFrames);
        AssertFrame(ua, [0x0D, 0x73, 0x09, 0x06, 0x00]);
    }

    [Fact]
    public void HandleUplink_DiscWithNonzeroSapi_PreservesSapiInUa()
    {
        LapdmLink link = new(null);

        LapdmLink.UplinkResult result = link.HandleUplink(0x80, BuildDisconnect(sapi: 3));

        Assert.True(result.ReleaseAfterDownlinkFrames);
        byte[] ua = Assert.Single(result.DownlinkFrames);
        AssertFrame(ua, [0x0D, 0x73, 0x01]);
    }

    [Fact]
    public void BuildFillFrame_UsesBsToMsFillAddressAndControl()
    {
        byte[] frame = LapdmLink.BuildFillFrame();

        AssertFrame(frame, [0x03, 0x03, 0x01]);
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

    private static byte[] MobileOriginatedCallSetup(string destination)
    {
        byte[] bcd = new byte[(destination.Length + 1) / 2];
        for (int index = 0; index < destination.Length; index++)
        {
            bcd[index / 2] |= (byte)((destination[index] - '0') << ((index & 1) * 4));
        }

        if ((destination.Length & 1) != 0)
        {
            bcd[^1] |= 0xF0;
        }

        return
        [
            0x03,
            0x05,
            0x04, 0x04, 0x60, 0x02, 0x00, 0x81,
            0x5E,
            (byte)(bcd.Length + 1),
            0x81,
            .. bcd,
        ];
    }

    private static byte[] BuildSabm(ReadOnlySpan<byte> information, byte sapi = 0)
    {
        byte[] layer2 = BuildEmptyFrame();
        layer2[0] = (byte)((sapi << 2) | 0x01);
        layer2[1] = 0x3F;
        layer2[2] = (byte)((information.Length << 2) | 0x01);
        information.CopyTo(layer2.AsSpan(3));
        return layer2;
    }

    private static byte[] BuildUaResponse(byte sapi = 0)
    {
        byte[] layer2 = BuildEmptyFrame();
        layer2[0] = (byte)((sapi << 2) | 0x03);
        layer2[1] = 0x73;
        layer2[2] = 0x01;
        return layer2;
    }

    private static byte[] BuildReceiveReady(byte receiveSequence, byte sapi = 0)
    {
        byte[] layer2 = BuildEmptyFrame();
        layer2[0] = (byte)((sapi << 2) | 0x03);
        layer2[1] = (byte)(((receiveSequence & 0x07) << 5) | 0x01);
        layer2[2] = 0x01;
        return layer2;
    }

    private static byte[] BuildInformationFrame(ReadOnlySpan<byte> information, byte sendSequence = 0, byte receiveSequence = 0, byte sapi = 0, bool moreData = false, bool pollFinal = false)
    {
        byte[] layer2 = BuildEmptyFrame();
        layer2[0] = (byte)((sapi << 2) | 0x01);
        layer2[1] = (byte)(((receiveSequence & 0x07) << 5) | ((sendSequence & 0x07) << 1) | (pollFinal ? 0x10 : 0));
        layer2[2] = (byte)((information.Length << 2) | (moreData ? 0x03 : 0x01));
        information.CopyTo(layer2.AsSpan(3));
        return layer2;
    }

    private static byte[] BuildDisconnect(byte sapi = 0)
    {
        byte[] layer2 = BuildEmptyFrame();
        layer2[0] = (byte)((sapi << 2) | 0x01);
        layer2[1] = 0x53;
        layer2[2] = 0x01;
        return layer2;
    }

    private static void EstablishCmService(LapdmLink link, byte serviceType)
    {
        LapdmLink.UplinkResult sabmResult = link.HandleUplink(0x80, BuildSabm(CmServiceRequest(serviceType)));

        Assert.Equal(2, sabmResult.DownlinkFrames.Count);
        AssertFrame(
            sabmResult.DownlinkFrames[0],
            [
                0x01, 0x73, 0x1D,
                0x05, 0x24, serviceType, 0x02, 0x00, 0x01, 0x29,
            ]);
        AssertFrame(
            sabmResult.DownlinkFrames[1],
            [
                0x03, 0x00, 0x0D,
                0x06, 0x35, 0x01,
            ]);

        LapdmLink.UplinkResult cipheringCompleteResult = link.HandleUplink(0x80, BuildInformationFrame([0x06, 0x32], receiveSequence: 1));

        Assert.Equal(2, cipheringCompleteResult.DownlinkFrames.Count);
        AssertFrame(cipheringCompleteResult.DownlinkFrames[0], [0x01, 0x21, 0x01]);
        AssertFramePrefix(cipheringCompleteResult.DownlinkFrames[1], [0x03, 0x22, 0x29, 0x05, 0x32, 0x47]);

        LapdmLink.UplinkResult mmInformationAck = link.HandleUplink(0x80, BuildReceiveReady(receiveSequence: 2));

        Assert.Empty(mmInformationAck.DownlinkFrames);
    }

    private static void EstablishIncomingCall(LapdmLink link)
    {
        link.QueueIncomingCall("5551234");
        link.HandleUplink(0x80, BuildSabm(PagingResponse()));
        link.HandleUplink(0x80, BuildReceiveReady(receiveSequence: 1));

        LapdmLink.UplinkResult cipheringCompleteResult = link.HandleUplink(0x80, BuildInformationFrame([0x06, 0x32], receiveSequence: 1));

        Assert.Equal(3, cipheringCompleteResult.DownlinkFrames.Count);
        AssertFrame(cipheringCompleteResult.DownlinkFrames[0], [0x01, 0x21, 0x01]);
        AssertFramePrefix(cipheringCompleteResult.DownlinkFrames[1], [0x03, 0x22, 0x29, 0x05, 0x32, 0x47]);
        AssertFramePrefix(cipheringCompleteResult.DownlinkFrames[2], [0x03, 0x24, 0x45]);

        LapdmLink.UplinkResult setupAckResult = link.HandleUplink(0x80, BuildReceiveReady(receiveSequence: 3));
        Assert.Empty(setupAckResult.DownlinkFrames);
    }

    private static byte[] BuildEmptyFrame()
    {
        byte[] layer2 = new byte[LapdmLink.FrameLength];
        Array.Fill<byte>(layer2, 0x2B);
        return layer2;
    }

    private static void AssertFrame(byte[] actual, ReadOnlySpan<byte> expectedPrefix)
    {
        Assert.Equal(LapdmLink.FrameLength, actual.Length);

        for (int index = 0; index < expectedPrefix.Length; index++)
        {
            Assert.Equal(expectedPrefix[index], actual[index]);
        }

        for (int index = expectedPrefix.Length; index < actual.Length; index++)
        {
            Assert.Equal(0x2B, actual[index]);
        }
    }

    private static void AssertFramePrefix(byte[] actual, ReadOnlySpan<byte> expectedPrefix)
    {
        Assert.Equal(LapdmLink.FrameLength, actual.Length);

        for (int index = 0; index < expectedPrefix.Length; index++)
        {
            Assert.Equal(expectedPrefix[index], actual[index]);
        }
    }

    private static DateTimeOffset FixedNetworkTime() =>
        new(2026, 7, 6, 14, 53, 29, TimeSpan.FromHours(8));
}
