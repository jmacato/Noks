using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Noks.Cryptography;
using Noks.Waku;
using Noks.AvaloniaApp;
using Noks.Dct3.Radio;
using Noks.Dct3.Sim;
using Noks.AvaloniaApp.Emulation;
using Noks.AvaloniaApp.Messaging;

namespace Noks.Application.Tests;

public sealed class WakuPhoneBridgeTests
{
    [Fact]
    public async Task RequiredPqcModeCannotBeDisabled()
    {
        await using WakuProfileManager profile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuPhoneBridge bridge = new(
            profile,
            new InMemoryWakuHub().CreateTransport(),
            options: WakuPhoneBridgeOptions.Default with
            {
                EnablePostQuantumRendezvous = false,
                RequirePostQuantumRendezvous = true,
                PostQuantumMinimumWorkBits = 1,
            });

        Assert.True(bridge.PostQuantumRendezvousEnabled);
        Assert.True(bridge.PostQuantumRendezvousRequired);
        bridge.SetPostQuantumRendezvousEnabled(false);
        Assert.True(bridge.PostQuantumRendezvousEnabled);
    }

    [Fact]
    public async Task StockFirmwareSmsSubmitGetsRpAckWhilePqcDeliveryRemainsDeferred()
    {
        InMemoryWakuHub hub = new();
        await using WakuProfileManager callerProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuPhoneBridge caller = new(
            callerProfile,
            hub.CreateTransport(),
            options: WakuPhoneBridgeOptions.Default with
            {
                EnablePostQuantumRendezvous = true,
                RequirePostQuantumRendezvous = true,
                PostQuantumMinimumWorkBits = 1,
            });
        BridgeHarness callerHarness = new(caller);
        OutgoingNetworkRequest? submitted = null;
        LapdmLink link = new(
            trace: null,
            outgoingNetworkRequest: request =>
            {
                submitted = request;
                Assert.True(caller.TryEnqueue(request));
            });
        caller.Start();

        EstablishSmsService(link);
        byte[] stockFirmwareCpData =
        [
            // Captured from stock Nokia 3310 v4.18 after composing "A" and
            // sending it to the temporary ID 1234567890123.
            0x39, 0x01, 0x1B, 0x00, 0x01, 0x00, 0x06, 0x91, 0x21, 0x43,
            0x65, 0x87, 0x09, 0x10, 0x11, 0x05, 0x0D, 0x81, 0x21, 0x43,
            0x65, 0x87, 0x09, 0x21, 0xF3, 0x00, 0x00, 0xA7, 0x01, 0x41,
        ];
        link.HandleUplink(
            0x80,
            BuildLapdmInformationFrame(
                stockFirmwareCpData.AsSpan(0, 20),
                sapi: 3,
                moreData: true));
        LapdmLink.UplinkResult submittedResult = link.HandleUplink(
            0x80,
            BuildLapdmInformationFrame(
                stockFirmwareCpData.AsSpan(20),
                sendSequence: 1,
                sapi: 3));

        Assert.Equal(2, submittedResult.DownlinkFrames.Count);
        OutgoingNetworkRequest request = Assert.IsType<OutgoingNetworkRequest>(submitted);
        Assert.Equal(NetworkRequestKind.Sms, request.Kind);
        Assert.Equal("1234567890123", request.NormalizedDestination);
        Assert.Equal("A", request.SmsText);
        await WaitUntilAsync(() =>
            GetPrivateCollectionCount(caller, "deferredPqcOutbound") == 1);
        WakuPhoneCommand queued = await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest &&
            command.RequestId == request.RequestId);
        Assert.Equal(NetworkRequestDecision.Accept, queued.Decision);

        LapdmLink.UplinkResult resolved = link.ResolveNetworkRequest(
            new ResolveNetworkRequest(request.RequestId, queued.Decision));

        byte[] rpAck = Assert.Single(resolved.DownlinkFrames);
        AssertLapdmFrame(
            rpAck,
            [0x0F, 0x42, 0x15, 0xB9, 0x01, 0x02, 0x03, 0x01]);
        Assert.DoesNotContain(hub.PublishedRequests, publish =>
            publish.Payload.Span.Length >= 6 &&
            publish.Payload.Span[..4].SequenceEqual("NPQ1"u8) &&
            publish.Payload.Span[5] == (byte)PqcRendezvousWireKind.Request);
    }

    [Fact]
    public async Task PqcRendezvousCompletesFromStoreWhenPeerWasOfflineAtSendTime()
    {
        InMemoryWakuHub hub = new();
        await using WakuProfileManager callerProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuProfileManager receiverProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        WakuPhoneBridgeOptions pqcOptions = WakuPhoneBridgeOptions.Default with
        {
            EnablePostQuantumRendezvous = true,
            RequirePostQuantumRendezvous = true,
            PostQuantumMinimumWorkBits = 1,
        };

        await using (WakuPhoneBridge descriptorPublisher =
            new(receiverProfile, hub.CreateTransport(), options: pqcOptions))
        {
            descriptorPublisher.Start();
            await WaitUntilAsync(() => hub.PublishedRequests.Any(request =>
                request.Payload.Span.Length >= 6 &&
                request.Payload.Span[..4].SequenceEqual("NPQ1"u8) &&
                request.Payload.Span[5] == (byte)PqcRendezvousWireKind.DescriptorChunk));
        }

        await using WakuPhoneBridge caller =
            new(callerProfile, hub.CreateTransport(), options: pqcOptions);
        BridgeHarness callerHarness = new(caller);
        caller.Start();
        await WaitUntilAsync(() => GetPrivateCollectionCount(caller, "pqcDescriptors") != 0);

        Guid requestId = Guid.NewGuid();
        caller.TryEnqueue(new OutgoingNetworkRequest(
            requestId,
            NetworkRequestKind.Sms,
            receiverProfile.Profile.PhoneNumber,
            "stored PQC request reached an offline peer"));
        await WaitUntilAsync(() => hub.PublishedRequests.Any(request =>
            request.Payload.Span.Length >= 6 &&
            request.Payload.Span[..4].SequenceEqual("NPQ1"u8) &&
            request.Payload.Span[5] == (byte)PqcRendezvousWireKind.Request));
        Assert.Equal(0, GetPrivateCollectionCount(caller, "deferredPqcOutbound"));
        WakuPhoneCommand queued = await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest &&
            command.RequestId == requestId);
        Assert.Equal(NetworkRequestDecision.Accept, queued.Decision);

        await using WakuPhoneBridge receiver =
            new(receiverProfile, hub.CreateTransport(), options: pqcOptions);
        BridgeHarness receiverHarness = new(receiver);
        receiver.Start();

        WakuPhoneCommand callerCard = await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSmartMessage);
        Assert.Equal(callerProfile.Profile.PhoneNumber, callerCard.Address);
        receiver.TryEnqueue(PhonebookWrite(
            1,
            EmptyRecord(),
            SimPhonebookCodec.Encode("Caller", callerProfile.Profile.PhoneNumber)));

        WakuPhoneCommand incoming = await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSms &&
            command.Text == "stored PQC request reached an offline peer");
        Assert.Equal(callerProfile.Profile.PhoneNumber, incoming.Address);
        Assert.False(callerHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest &&
            command.RequestId == requestId, out _));
        Assert.Null(GetPrivateFieldValue(callerProfile.Profile, "keys"));
        Assert.Null(GetPrivateFieldValue(receiverProfile.Profile, "keys"));
    }

    [Fact]
    public async Task PqcAsyncRendezvousPairsThenDeliversOverPqcDirectEnvelope()
    {
        InMemoryWakuHub hub = new();
        await using WakuProfileManager callerProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuProfileManager receiverProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        WakuPhoneBridgeOptions pqcOptions = WakuPhoneBridgeOptions.Default with
        {
            EnablePostQuantumRendezvous = true,
            PostQuantumMinimumWorkBits = 1,
        };
        await using WakuPhoneBridge caller =
            new(callerProfile, hub.CreateTransport(), options: pqcOptions);
        await using WakuPhoneBridge receiver =
            new(receiverProfile, hub.CreateTransport(), options: pqcOptions);
        BridgeHarness callerHarness = new(caller);
        BridgeHarness receiverHarness = new(receiver);

        caller.Start();
        await WaitUntilAsync(() => hub.SubscriptionCount == 1);

        Guid requestId = Guid.NewGuid();
        caller.TryEnqueue(new OutgoingNetworkRequest(
            requestId,
            NetworkRequestKind.Sms,
            receiverProfile.Profile.PhoneNumber,
            "async PQC rendezvous reached the phone"));
        await WaitUntilAsync(() =>
            GetPrivateCollectionCount(caller, "deferredPqcOutbound") == 1);
        WakuPhoneCommand queued = await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest &&
            command.RequestId == requestId);
        Assert.Equal(NetworkRequestDecision.Accept, queued.Decision);

        receiver.Start();
        await WaitUntilAsync(() => hub.SubscriptionCount == 2);
        await WaitUntilAsync(() =>
            GetPrivateCollectionCount(caller, "pqcDescriptors") != 0 &&
            GetPrivateCollectionCount(receiver, "pqcDescriptors") != 0);

        WakuPhoneCommand callerCard = await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSmartMessage);
        Assert.Equal(callerProfile.Profile.PhoneNumber, callerCard.Address);
        receiver.TryEnqueue(PhonebookWrite(
            1,
            EmptyRecord(),
            SimPhonebookCodec.Encode("Caller", callerProfile.Profile.PhoneNumber)));

        WakuPhoneCommand incoming = await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSms &&
            command.Text == "async PQC rendezvous reached the phone");
        Assert.Equal(callerProfile.Profile.PhoneNumber, incoming.Address);
        Assert.False(callerHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest &&
            command.RequestId == requestId, out _));

        Assert.Contains(hub.PublishedRequests, request =>
            request.Payload.Length == PqcWakuEnvelopeCodec.EnvelopeSize &&
            request.Payload.Span[..4].SequenceEqual("NPQ1"u8) &&
            request.Payload.Span[5] == (byte)PqcRendezvousWireKind.Request &&
            !request.Ephemeral);
        Assert.Contains(hub.PublishedRequests, request =>
            request.Payload.Length == PqcWakuEnvelopeCodec.EnvelopeSize &&
            request.Payload.Span[..4].SequenceEqual("NQP2"u8) &&
            !request.Ephemeral);
        Assert.DoesNotContain(hub.PublishedRequests, request =>
            request.Payload.Span.Length >= 4 &&
            request.Payload.Span[..4].SequenceEqual("NWE1"u8));
        Assert.Null(GetPrivateFieldValue(callerProfile.Profile, "keys"));
        Assert.Null(GetPrivateFieldValue(receiverProfile.Profile, "keys"));
    }

    [Fact]
    public async Task PqcModeUsesMlKemAesForEstablishedPacketsAndRejectsLegacyEnvelope()
    {
        InMemoryWakuHub hub = new();
        await using WakuProfileManager callerProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuProfileManager receiverProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        WakuProfileContact receiverContact = CreatePqcContact(receiverProfile, now);
        WakuProfileContact callerContact = CreatePqcContact(callerProfile, now);
        await callerProfile.UpsertContactAsync(receiverContact, receiverProfile.Profile.PhoneNumber);
        await receiverProfile.UpsertContactAsync(callerContact, callerProfile.Profile.PhoneNumber);

        IWakuTransport callerTransport = hub.CreateTransport();
        WakuPhoneBridgeOptions pqcOptions = WakuPhoneBridgeOptions.Default with
        {
            EnablePostQuantumRendezvous = true,
            PostQuantumMinimumWorkBits = 1,
        };
        await using WakuPhoneBridge caller = new(callerProfile, callerTransport, options: pqcOptions);
        await using WakuPhoneBridge receiver = new(receiverProfile, hub.CreateTransport(), options: pqcOptions);
        BridgeHarness callerHarness = new(caller);
        BridgeHarness receiverHarness = new(receiver);
        caller.Start();
        receiver.Start();
        await WaitUntilAsync(() => hub.SubscriptionCount == 2);

        Guid requestId = Guid.NewGuid();
        caller.TryEnqueue(new OutgoingNetworkRequest(
            requestId,
            NetworkRequestKind.Sms,
            receiverProfile.Profile.PhoneNumber,
            "established packet is post-quantum"));
        WakuPhoneCommand accepted = await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest &&
            command.RequestId == requestId);
        Assert.Equal(NetworkRequestDecision.Accept, accepted.Decision);
        WakuPhoneCommand incoming = await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSms &&
            command.Text == "established packet is post-quantum");
        Assert.Equal(callerProfile.Profile.PhoneNumber, incoming.Address);

        Assert.Contains(hub.PublishedRequests, request =>
            request.Payload.Length == PqcWakuEnvelopeCodec.EnvelopeSize &&
            request.Payload.Span[..4].SequenceEqual("NQP2"u8));
        Assert.DoesNotContain(hub.PublishedRequests, request =>
            request.Payload.Span.Length >= 4 &&
            request.Payload.Span[..4].SequenceEqual("NWE1"u8));

        byte[] callerEntropy = NoksRecoveryPhrase.Decode(callerProfile.Profile.CreateRecoveryPhrase());
        using WakuProfileKeys callerKeys = WakuProfileKeys.Create(callerEntropy);
        WakuApplicationMessage legacyMessage = new(
            Guid.NewGuid(),
            WakuEventKind.Sms,
            now.ToUnixTimeMilliseconds(),
            now.AddMinutes(10).ToUnixTimeMilliseconds(),
            callerKeys.EnvelopePublicKey.Span,
            receiverContact.MailboxPublicKey.AsSpan(),
            WakuSmsPayloadCodec.Encode("legacy X25519 packet must be ignored"));
        byte[] legacyEnvelope = WakuEnvelopeCodec.Encrypt(
            legacyMessage,
            callerKeys.EnvelopePrivateKey.Span);
        await callerTransport.PublishAsync(
            WakuPublishRequestFactory.Create(legacyMessage, legacyEnvelope, now));
        await Task.Delay(100);
        Assert.False(receiverHarness.TryTake(
            command => command.Kind == WakuPhoneCommandKind.QueueIncomingSms &&
                       command.Text == "legacy X25519 packet must be ignored",
            out _));
    }

    [Theory]
    [InlineData(NetworkRequestKind.Call)]
    [InlineData(NetworkRequestKind.Sms)]
    public async Task FullBridgeIngressRejectsSecondNetworkRequestExactlyOnce(NetworkRequestKind requestKind)
    {
        InMemoryWakuHub hub = new();
        await using WakuProfileManager profile = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuPhoneBridge bridge = new(
            profile,
            hub.CreateTransport(),
            options: WakuPhoneBridgeOptions.Default with { MaximumQueuedWork = 1 });
        using PhoneEmulator emulator = new(new byte[0x20_0000]);
        OutgoingNetworkRequest first = new(
            Guid.NewGuid(),
            NetworkRequestKind.Call,
            "1234567890123",
            "");
        OutgoingNetworkRequest second = new(
            Guid.NewGuid(),
            requestKind,
            "3214567890123",
            requestKind == NetworkRequestKind.Sms ? "hello" : "");
        System.Collections.Concurrent.ConcurrentQueue<OutgoingNetworkRequest> requests =
            GetPrivateField<System.Collections.Concurrent.ConcurrentQueue<OutgoingNetworkRequest>>(
                emulator,
                "outgoingNetworkRequests");
        requests.Enqueue(first);
        requests.Enqueue(second);

        WakuBridgeIngress.DrainNetworkRequests(emulator, bridge);
        WakuBridgeIngress.DrainNetworkRequests(emulator, bridge);

        Assert.Empty(requests);
        System.Collections.Concurrent.ConcurrentQueue<ResolveNetworkRequest> resolutions =
            GetPrivateField<System.Collections.Concurrent.ConcurrentQueue<ResolveNetworkRequest>>(
                emulator,
                "networkResolutionChanges");
        ResolveNetworkRequest rejected = Assert.Single(resolutions);
        Assert.Equal(second.RequestId, rejected.RequestId);
        Assert.Equal(NetworkRequestDecision.Reject, rejected.Decision);
    }

    [Fact]
    public async Task EmulatorPublishesSameBatchAdnMutationBeforeOutgoingRequest()
    {
        const string destination = "1234567890123";
        SimCard sim = new(null);
        List<SimMutation> mutations = [];
        sim.MutationCommitted += mutations.Add;
        SendSimApdu(sim, 0xA0, 0xA4, 0x00, 0x00, 0x02, 0x7F, 0x10);
        SendSimApdu(sim, 0xA0, 0xA4, 0x00, 0x00, 0x02, 0x6F, 0x3A);
        byte[] contact = SimPhonebookCodec.Encode("Receiver", destination);
        Assert.Equal(
            [0xDC, 0x90, 0x00],
            SendSimApdu(
                sim,
                [0xA0, 0xDC, 0x01, 0x04, SimPhonebookCodec.RecordLength, .. contact]));
        SimMutation mutation = Assert.Single(mutations);
        Assert.Equal(SimMutationOrigin.Firmware, mutation.Origin);

        InMemoryWakuHub hub = new();
        await using WakuProfileManager profile = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuPhoneBridge bridge = new(profile, hub.CreateTransport());
        using PhoneEmulator emulator = new(new byte[0x20_0000]);
        Guid requestId = Guid.NewGuid();
        OutgoingNetworkRequest request = new(
            requestId,
            NetworkRequestKind.Call,
            destination,
            "");
        GetPrivateField<System.Collections.Concurrent.ConcurrentQueue<SimMutation>>(
            emulator,
            "simMutations").Enqueue(mutation);
        GetPrivateField<System.Collections.Concurrent.ConcurrentQueue<OutgoingNetworkRequest>>(
            emulator,
            "outgoingNetworkRequests").Enqueue(request);
        SetPrivateField(emulator, "simMutationNotificationPending", 1);
        SetPrivateField(emulator, "networkNotificationPending", 1);

        List<string> notificationOrder = [];
        emulator.SimMutationAvailable += source =>
        {
            notificationOrder.Add("sim");
            while (source.TryDequeueSimMutation(out SimMutation? item) && item is not null)
                Assert.True(bridge.TryEnqueue(item));
        };
        emulator.NetworkRequestAvailable += source =>
        {
            notificationOrder.Add("network");
            while (source.TryDequeueOutgoingNetworkRequest(out OutgoingNetworkRequest? item) && item is not null)
                Assert.True(bridge.TryEnqueue(item));
        };

        InvokePrivate(emulator, "PublishPendingBridgeNotifications");
        Assert.Equal(["sim", "network"], notificationOrder);
        bridge.Start();

        await hub.WaitForPublishAsync();
    }

    [Fact]
    public async Task UnsavedDirectCallPairsThenRoutesBothDirectionsByStableIdentity()
    {
        InMemoryWakuHub hub = new();
        MemoryStore callerStore = new();
        MemoryStore receiverStore = new();
        await using WakuProfileManager callerProfile = await WakuProfileManager.LoadOrCreateAsync(callerStore);
        await using WakuProfileManager receiverProfile = await WakuProfileManager.LoadOrCreateAsync(receiverStore);
        await callerProfile.UpdateUserNameAsync("beacon-ab12");
        await receiverProfile.UpdateUserNameAsync("river-cd34");
        await using WakuPhoneBridge caller = new(callerProfile, hub.CreateTransport());
        await using WakuPhoneBridge receiver = new(receiverProfile, hub.CreateTransport());
        BridgeHarness callerHarness = new(caller);
        BridgeHarness receiverHarness = new(receiver);
        caller.Start();
        receiver.Start();
        await WaitUntilAsync(() => hub.SubscriptionCount == 2);

        string receiverNumber = receiverProfile.Profile.PhoneNumber;
        Guid callId = Guid.NewGuid();
        caller.TryEnqueue(new OutgoingNetworkRequest(callId, NetworkRequestKind.Call, receiverNumber, ""));

        WakuPhoneCommand resumed = await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest && command.RequestId == callId);
        Assert.Equal(NetworkRequestDecision.Accept, resumed.Decision);
        WakuPhoneCommand calleeMedia = await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.BeginCallMedia && command.RequestId == callId);
        WakuPhoneCommand callerMedia = await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.BeginCallMedia && command.RequestId == callId);
        Assert.False(calleeMedia.IsCaller);
        Assert.True(callerMedia.IsCaller);
        Assert.False(receiverHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingCall && command.RequestId == callId, out _));
        Assert.False(callerHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.ActivateCallMedia && command.RequestId == callId, out _));
        Assert.False(receiverHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.ActivateCallMedia && command.RequestId == callId, out _));
        Assert.False(callerHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.ConnectNetworkCall && command.RequestId == callId, out _));

        receiver.TryEnqueue(WakuCallMediaEvent.State(callId, WakuCallMediaEventKind.Connected));
        WakuPhoneCommand incomingCall = await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingCall && command.RequestId == callId);
        Assert.Equal(callerProfile.Profile.PhoneNumber, incomingCall.Address);
        Assert.False(receiverHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSmartMessage, out _));

        caller.TryEnqueue(WakuCallMediaEvent.State(callId, WakuCallMediaEventKind.Connected));
        Assert.False(callerHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.ConnectNetworkCall && command.RequestId == callId, out _));

        receiver.TryEnqueue(new CallTransition(
            callId,
            CallDirection.Incoming,
            CallTransitionKind.Answer,
            callerProfile.Profile.PhoneNumber));
        await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ActivateCallMedia && command.RequestId == callId);
        await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ConnectNetworkCall && command.RequestId == callId);
        Assert.False(callerHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.ActivateCallMedia && command.RequestId == callId, out _));
        WakuPhoneCommand card = await receiverHarness.WaitForAsync(
            command => command.Kind == WakuPhoneCommandKind.QueueIncomingSmartMessage);
        Assert.Equal(NokiaBusinessCardVCard.DestinationPort, card.DestinationPort);
        Assert.Equal(callerProfile.Profile.PhoneNumber, card.Address);
        Assert.Contains(
            "\r\nN:beacon-ab12\r\n",
            Encoding.ASCII.GetString(card.Payload.AsSpan()),
            StringComparison.Ordinal);
        WakuPhoneCommand returnCard = await callerHarness.WaitForAsync(
            command => command.Kind == WakuPhoneCommandKind.QueueIncomingSmartMessage);
        Assert.Equal(NokiaBusinessCardVCard.DestinationPort, returnCard.DestinationPort);
        Assert.Equal(receiverNumber, returnCard.Address);
        Assert.Contains(
            "\r\nN:river-cd34\r\n",
            Encoding.ASCII.GetString(returnCard.Payload.AsSpan()),
            StringComparison.Ordinal);
        Assert.Empty(callerProfile.Profile.Contacts);
        Assert.Empty(receiverProfile.Profile.Contacts);
        await using (WakuProfileManager reloadedCaller =
                     await WakuProfileManager.LoadOrCreateAsync(callerStore))
        await using (WakuProfileManager reloadedReceiver =
                     await WakuProfileManager.LoadOrCreateAsync(receiverStore))
        {
            Assert.Empty(reloadedCaller.Profile.Contacts);
            Assert.Empty(reloadedReceiver.Profile.Contacts);
        }

        receiver.TryEnqueue(PhonebookWrite(
            1,
            EmptyRecord(),
            SimPhonebookCodec.Encode("Locally Edited", callerProfile.Profile.PhoneNumber)));
        caller.TryEnqueue(PhonebookWrite(
            1,
            EmptyRecord(),
            SimPhonebookCodec.Encode("Receiver", receiverNumber)));

        await WaitUntilAsync(() =>
            callerProfile.Profile.FindContactByLocalNumber(receiverNumber) is not null &&
            receiverProfile.Profile.FindContactByLocalNumber(callerProfile.Profile.PhoneNumber) is not null);
        Assert.NotNull(callerProfile.Profile.FindContactByLocalNumber(receiverNumber));
        Assert.NotNull(receiverProfile.Profile.FindContactByLocalNumber(callerProfile.Profile.PhoneNumber));

        caller.TryEnqueue(new CallTransition(
            callId,
            CallDirection.Outgoing,
            CallTransitionKind.Connect,
            receiverNumber));
        await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ActivateCallMedia && command.RequestId == callId);

        byte[] offer = Enumerable.Range(0, 5_000).Select(index => (byte)(index * 31)).ToArray();
        caller.TryEnqueue(WakuCallMediaEvent.Signal(
            callId,
            WakuCallMediaEventKind.SdpOffer,
            offer));
        WakuPhoneCommand receivedOffer = await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ApplyCallMediaSignal &&
            command.RequestId == callId &&
            command.EventKind == WakuEventKind.SdpOffer);
        Assert.Equal(offer, receivedOffer.Payload);

        byte[] answer = Encoding.UTF8.GetBytes("{\"type\":\"answer\",\"sdp\":\"v=0\\r\\n\"}");
        receiver.TryEnqueue(WakuCallMediaEvent.Signal(
            callId,
            WakuCallMediaEventKind.SdpAnswer,
            answer));
        WakuPhoneCommand receivedAnswer = await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ApplyCallMediaSignal &&
            command.RequestId == callId &&
            command.EventKind == WakuEventKind.SdpAnswer);
        Assert.Equal(answer, receivedAnswer.Payload);

        byte[] receiverOffer = Encoding.UTF8.GetBytes("{\"type\":\"offer\",\"sdp\":\"v=0\\r\\n\"}");
        receiver.TryEnqueue(WakuCallMediaEvent.Signal(
            callId,
            WakuCallMediaEventKind.SdpOffer,
            receiverOffer));
        WakuPhoneCommand receivedReceiverOffer = await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ApplyCallMediaSignal &&
            command.RequestId == callId &&
            command.EventKind == WakuEventKind.SdpOffer);
        Assert.Equal(receiverOffer, receivedReceiverOffer.Payload);

        byte[] callerAnswer = Encoding.UTF8.GetBytes("{\"type\":\"answer\",\"sdp\":\"v=0\\r\\n\"}");
        caller.TryEnqueue(WakuCallMediaEvent.Signal(
            callId,
            WakuCallMediaEventKind.SdpAnswer,
            callerAnswer));
        WakuPhoneCommand receivedCallerAnswer = await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ApplyCallMediaSignal &&
            command.RequestId == callId &&
            command.EventKind == WakuEventKind.SdpAnswer);
        Assert.Equal(callerAnswer, receivedCallerAnswer.Payload);

        byte[] candidate = Encoding.UTF8.GetBytes("{\"candidate\":\"candidate:1 1 UDP 1 192.0.2.1 9 typ host\"}");
        caller.TryEnqueue(WakuCallMediaEvent.Signal(
            callId,
            WakuCallMediaEventKind.IceCandidate,
            candidate));
        WakuPhoneCommand receivedCandidate = await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ApplyCallMediaSignal &&
            command.RequestId == callId &&
            command.EventKind == WakuEventKind.IceCandidate);
        Assert.Equal(candidate, receivedCandidate.Payload);

        receiver.TryEnqueue(new CallTransition(
            callId,
            CallDirection.Incoming,
            CallTransitionKind.Hangup,
            callerProfile.Profile.PhoneNumber));
        await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.EndCallMedia && command.RequestId == callId);
        await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.EndCallMedia && command.RequestId == callId);
        await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.TerminateNetworkCall && command.RequestId == callId);
        Assert.False(receiverHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.TerminateNetworkCall && command.RequestId == callId, out _));

        Guid smsId = Guid.NewGuid();
        caller.TryEnqueue(new OutgoingNetworkRequest(
            smsId,
            NetworkRequestKind.Sms,
            receiverNumber,
            "Pairing survived the temporary-number route."));
        WakuPhoneCommand smsAccepted = await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest && command.RequestId == smsId);
        Assert.Equal(NetworkRequestDecision.Accept, smsAccepted.Decision);
        WakuPhoneCommand incomingSms = await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSms);
        Assert.Equal(callerProfile.Profile.PhoneNumber, incomingSms.Address);
        Assert.Equal("Pairing survived the temporary-number route.", incomingSms.Text);
        Assert.False(receiverHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSmartMessage, out _));

        Guid reverseSmsId = Guid.NewGuid();
        receiver.TryEnqueue(new OutgoingNetworkRequest(
            reverseSmsId,
            NetworkRequestKind.Sms,
            callerProfile.Profile.PhoneNumber,
            "Both phones saved the exchanged cards."));
        WakuPhoneCommand reverseSmsAccepted = await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest && command.RequestId == reverseSmsId);
        Assert.Equal(NetworkRequestDecision.Accept, reverseSmsAccepted.Decision);
        WakuPhoneCommand reverseIncomingSms = await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSms);
        Assert.Equal(receiverNumber, reverseIncomingSms.Address);
        Assert.Equal("Both phones saved the exchanged cards.", reverseIncomingSms.Text);

        Guid reverseCallId = Guid.NewGuid();
        receiver.TryEnqueue(new OutgoingNetworkRequest(
            reverseCallId,
            NetworkRequestKind.Call,
            callerProfile.Profile.PhoneNumber,
            ""));
        WakuPhoneCommand reverseCallAccepted = await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest && command.RequestId == reverseCallId);
        Assert.Equal(NetworkRequestDecision.Accept, reverseCallAccepted.Decision);
        await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.BeginCallMedia && command.RequestId == reverseCallId);
        await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.BeginCallMedia && command.RequestId == reverseCallId);
        receiver.TryEnqueue(WakuCallMediaEvent.State(reverseCallId, WakuCallMediaEventKind.Connected));
        caller.TryEnqueue(WakuCallMediaEvent.State(reverseCallId, WakuCallMediaEventKind.Connected));
        WakuPhoneCommand reverseIncomingCall = await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingCall && command.RequestId == reverseCallId);
        Assert.Equal(receiverNumber, reverseIncomingCall.Address);
    }

    [Fact]
    public async Task KnownPeerDisplaysIncomingCallOnlyAfterMediaWithoutSendingCardFirst()
    {
        InMemoryWakuHub hub = new();
        await using WakuProfileManager callerProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuProfileManager receiverProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await callerProfile.UpsertContactAsync(
            CreateContact(receiverProfile, now),
            receiverProfile.Profile.PhoneNumber);
        await receiverProfile.UpsertContactAsync(
            CreateContact(callerProfile, now),
            callerProfile.Profile.PhoneNumber);
        await using WakuPhoneBridge caller = new(callerProfile, hub.CreateTransport());
        await using WakuPhoneBridge receiver = new(receiverProfile, hub.CreateTransport());
        BridgeHarness callerHarness = new(caller);
        BridgeHarness receiverHarness = new(receiver);
        caller.Start();
        receiver.Start();
        await WaitUntilAsync(() => hub.SubscriptionCount == 2);
        Guid callId = Guid.NewGuid();

        caller.TryEnqueue(PhonebookWrite(
            1,
            EmptyRecord(),
            SimPhonebookCodec.Encode("Receiver", receiverProfile.Profile.PhoneNumber)));
        caller.TryEnqueue(new OutgoingNetworkRequest(
            callId,
            NetworkRequestKind.Call,
            receiverProfile.Profile.PhoneNumber,
            ""));

        WakuPhoneCommand first = await receiverHarness.WaitForNextAsync();
        Assert.Equal(WakuPhoneCommandKind.BeginCallMedia, first.Kind);
        Assert.Equal(callId, first.RequestId);
        Assert.False(first.IsCaller);
        await Task.Delay(500);
        Assert.False(receiverHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingCall, out _));
        Assert.False(receiverHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSmartMessage, out _));

        receiver.TryEnqueue(WakuCallMediaEvent.State(callId, WakuCallMediaEventKind.Connected));
        WakuPhoneCommand incoming = await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingCall && command.RequestId == callId);
        Assert.Equal(callerProfile.Profile.PhoneNumber, incoming.Address);

        receiver.TryEnqueue(new CallTransition(
            callId,
            CallDirection.Incoming,
            CallTransitionKind.Answer,
            callerProfile.Profile.PhoneNumber));
        WakuPhoneCommand receiverCard = await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSmartMessage);
        Assert.Equal(callerProfile.Profile.PhoneNumber, receiverCard.Address);
        Assert.False(callerHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSmartMessage, out _));
    }

    [Fact]
    public async Task DroppingProvisionalCallDeclinesPairingWithoutSendingBusinessCard()
    {
        InMemoryWakuHub hub = new();
        await using WakuProfileManager callerProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuProfileManager receiverProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuPhoneBridge caller = new(callerProfile, hub.CreateTransport());
        await using WakuPhoneBridge receiver = new(receiverProfile, hub.CreateTransport());
        BridgeHarness callerHarness = new(caller);
        BridgeHarness receiverHarness = new(receiver);
        caller.Start();
        receiver.Start();
        await WaitUntilAsync(() => hub.SubscriptionCount == 2);
        Guid callId = Guid.NewGuid();

        caller.TryEnqueue(new OutgoingNetworkRequest(
            callId,
            NetworkRequestKind.Call,
            receiverProfile.Profile.PhoneNumber,
            ""));
        WakuPhoneCommand accepted = await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest && command.RequestId == callId);
        Assert.Equal(NetworkRequestDecision.Accept, accepted.Decision);
        await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.BeginCallMedia && command.RequestId == callId);
        receiver.TryEnqueue(WakuCallMediaEvent.State(callId, WakuCallMediaEventKind.Connected));
        WakuPhoneCommand incoming = await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingCall && command.RequestId == callId);
        Assert.Equal(callerProfile.Profile.PhoneNumber, incoming.Address);

        receiver.TryEnqueue(new CallTransition(
            callId,
            CallDirection.Incoming,
            CallTransitionKind.Reject,
            callerProfile.Profile.PhoneNumber));

        await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.TerminateNetworkCall && command.RequestId == callId);
        Assert.False(receiverHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSmartMessage, out _));
        Assert.Empty(callerProfile.Profile.Contacts);
        Assert.Empty(receiverProfile.Profile.Contacts);
    }

    [Fact]
    public async Task ReceiverHangupDuringApprovedCallSetupTerminatesCaller()
    {
        InMemoryWakuHub hub = new();
        await using WakuProfileManager callerProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuProfileManager receiverProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuPhoneBridge caller = new(callerProfile, hub.CreateTransport());
        await using WakuPhoneBridge receiver = new(receiverProfile, hub.CreateTransport());
        BridgeHarness callerHarness = new(caller);
        BridgeHarness receiverHarness = new(receiver);
        caller.Start();
        receiver.Start();
        await WaitUntilAsync(() => hub.SubscriptionCount == 2);
        Guid callId = Guid.NewGuid();

        caller.TryEnqueue(new OutgoingNetworkRequest(
            callId,
            NetworkRequestKind.Call,
            receiverProfile.Profile.PhoneNumber,
            ""));
        await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest && command.RequestId == callId);
        await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.BeginCallMedia && command.RequestId == callId);
        receiver.TryEnqueue(WakuCallMediaEvent.State(callId, WakuCallMediaEventKind.Connected));
        await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingCall && command.RequestId == callId);

        receiver.TryEnqueue(new CallTransition(
            callId,
            CallDirection.Incoming,
            CallTransitionKind.Answer,
            callerProfile.Profile.PhoneNumber));
        receiver.TryEnqueue(new CallTransition(
            callId,
            CallDirection.Incoming,
            CallTransitionKind.Hangup,
            callerProfile.Profile.PhoneNumber));

        await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.TerminateNetworkCall && command.RequestId == callId);
        Assert.False(receiverHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingCall && command.RequestId == callId, out _));
    }

    [Fact]
    public async Task UnsavedFirstSmsStillUsesBusinessCardSaveConsent()
    {
        InMemoryWakuHub hub = new();
        await using WakuProfileManager callerProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuProfileManager receiverProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuPhoneBridge caller = new(callerProfile, hub.CreateTransport());
        await using WakuPhoneBridge receiver = new(receiverProfile, hub.CreateTransport());
        BridgeHarness callerHarness = new(caller);
        BridgeHarness receiverHarness = new(receiver);
        caller.Start();
        receiver.Start();
        await WaitUntilAsync(() => hub.SubscriptionCount == 2);
        Guid smsId = Guid.NewGuid();

        caller.TryEnqueue(new OutgoingNetworkRequest(
            smsId,
            NetworkRequestKind.Sms,
            receiverProfile.Profile.PhoneNumber,
            "hello before pairing"));
        WakuPhoneCommand card = await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSmartMessage);
        Assert.Equal(callerProfile.Profile.PhoneNumber, card.Address);
        Assert.False(receiverHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSms, out _));

        receiver.TryEnqueue(PhonebookWrite(
            1,
            EmptyRecord(),
            SimPhonebookCodec.Encode("Caller", callerProfile.Profile.PhoneNumber)));

        WakuPhoneCommand accepted = await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest && command.RequestId == smsId);
        Assert.Equal(NetworkRequestDecision.Accept, accepted.Decision);
        WakuPhoneCommand incoming = await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSms);
        Assert.Equal(callerProfile.Profile.PhoneNumber, incoming.Address);
        Assert.Equal("hello before pairing", incoming.Text);
        WakuPhoneCommand returnCard = await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSmartMessage);
        Assert.Equal(receiverProfile.Profile.PhoneNumber, returnCard.Address);
        Assert.Equal(NokiaBusinessCardVCard.DestinationPort, returnCard.DestinationPort);
    }

    [Fact]
    public async Task ApprovedPeerDoesNotSendDuplicateCardWhenDialerAlreadySavedNumber()
    {
        InMemoryWakuHub hub = new();
        await using WakuProfileManager callerProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuProfileManager receiverProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuPhoneBridge caller = new(callerProfile, hub.CreateTransport());
        await using WakuPhoneBridge receiver = new(receiverProfile, hub.CreateTransport());
        BridgeHarness callerHarness = new(caller);
        BridgeHarness receiverHarness = new(receiver);
        caller.Start();
        receiver.Start();
        await WaitUntilAsync(() => hub.SubscriptionCount == 2);

        caller.TryEnqueue(PhonebookWrite(
            1,
            EmptyRecord(),
            SimPhonebookCodec.Encode("Already Saved", receiverProfile.Profile.PhoneNumber)));
        Guid callId = Guid.NewGuid();
        caller.TryEnqueue(new OutgoingNetworkRequest(
            callId,
            NetworkRequestKind.Call,
            receiverProfile.Profile.PhoneNumber,
            ""));
        await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.BeginCallMedia && command.RequestId == callId);
        receiver.TryEnqueue(WakuCallMediaEvent.State(callId, WakuCallMediaEventKind.Connected));
        await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingCall && command.RequestId == callId);

        receiver.TryEnqueue(new CallTransition(
            callId,
            CallDirection.Incoming,
            CallTransitionKind.Answer,
            callerProfile.Profile.PhoneNumber));
        await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSmartMessage);
        WakuPhoneCommand accepted = await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest && command.RequestId == callId);

        Assert.Equal(NetworkRequestDecision.Accept, accepted.Decision);
        Assert.False(callerHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSmartMessage, out _));
    }

    [Fact]
    public async Task NonCanonicalDestinationIsRejectedWithoutPublishing()
    {
        InMemoryWakuHub hub = new();
        await using WakuProfileManager profile = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuPhoneBridge bridge = new(profile, hub.CreateTransport());
        BridgeHarness harness = new(bridge);
        bridge.Start();
        Guid requestId = Guid.NewGuid();

        bridge.TryEnqueue(new OutgoingNetworkRequest(
            requestId,
            NetworkRequestKind.Sms,
            "12345",
            "hello"));

        WakuPhoneCommand rejected = await harness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest && command.RequestId == requestId);
        Assert.Equal(NetworkRequestDecision.Reject, rejected.Decision);
    }

    [Fact]
    public async Task FullReceiverAllowsTemporaryCallWithoutInjectingCardFirst()
    {
        InMemoryWakuHub hub = new();
        await using WakuProfileManager callerProfile = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuProfileManager receiverProfile = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuPhoneBridge caller = new(callerProfile, hub.CreateTransport());
        await using WakuPhoneBridge receiver = new(receiverProfile, hub.CreateTransport());
        BridgeHarness callerHarness = new(caller);
        BridgeHarness receiverHarness = new(receiver);
        caller.Start();
        receiver.Start();
        await WaitUntilAsync(() => hub.SubscriptionCount == 2);

        receiver.TryEnqueue(FullPhonebookMutation());
        string receiverNumber = receiverProfile.Profile.PhoneNumber;
        caller.TryEnqueue(PhonebookWrite(1, EmptyRecord(), SimPhonebookCodec.Encode("Receiver", receiverNumber)));
        Guid requestId = Guid.NewGuid();
        caller.TryEnqueue(new OutgoingNetworkRequest(requestId, NetworkRequestKind.Call, receiverNumber, ""));

        WakuPhoneCommand accepted = await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest && command.RequestId == requestId);
        Assert.Equal(NetworkRequestDecision.Accept, accepted.Decision);
        await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.BeginCallMedia && command.RequestId == requestId);
        receiver.TryEnqueue(WakuCallMediaEvent.State(requestId, WakuCallMediaEventKind.Connected));
        WakuPhoneCommand incoming = await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingCall && command.RequestId == requestId);
        Assert.Equal(callerProfile.Profile.PhoneNumber, incoming.Address);
        Assert.False(receiverHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSmartMessage, out _));
        Assert.Empty(callerProfile.Profile.Contacts);
        Assert.Empty(receiverProfile.Profile.Contacts);

        receiver.TryEnqueue(new CallTransition(
            requestId,
            CallDirection.Incoming,
            CallTransitionKind.Reject,
            callerProfile.Profile.PhoneNumber));
        await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.TerminateNetworkCall && command.RequestId == requestId);
    }

    [Fact]
    public async Task MediaSetupTimeoutNeverDisplaysIncomingCallOrBusinessCard()
    {
        ManualTimeProvider time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000));
        InMemoryWakuHub hub = new();
        await using WakuProfileManager callerProfile = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuProfileManager receiverProfile = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        WakuPhoneBridgeOptions options = WakuPhoneBridgeOptions.Default with
        {
            CallMediaSetupTimeout = TimeSpan.FromSeconds(8),
        };
        await using WakuPhoneBridge caller = new(callerProfile, hub.CreateTransport(), time, options);
        await using WakuPhoneBridge receiver = new(receiverProfile, hub.CreateTransport(), time, options);
        BridgeHarness callerHarness = new(caller);
        BridgeHarness receiverHarness = new(receiver);
        caller.Start();
        receiver.Start();
        await WaitUntilAsync(() => hub.SubscriptionCount == 2);
        Guid callId = Guid.NewGuid();

        caller.TryEnqueue(new OutgoingNetworkRequest(
            callId,
            NetworkRequestKind.Call,
            receiverProfile.Profile.PhoneNumber,
            ""));
        await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.BeginCallMedia && command.RequestId == callId);
        await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.BeginCallMedia && command.RequestId == callId);
        Assert.False(receiverHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingCall, out _));

        time.Advance(TimeSpan.FromSeconds(8));

        await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.EndCallMedia && command.RequestId == callId);
        await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.TerminateNetworkCall && command.RequestId == callId);
        Assert.False(receiverHarness.TryTake(command =>
            command.Kind is WakuPhoneCommandKind.QueueIncomingCall or
                WakuPhoneCommandKind.QueueIncomingSmartMessage, out _));
        Assert.Empty(callerProfile.Profile.Contacts);
        Assert.Empty(receiverProfile.Profile.Contacts);
    }

    [Fact]
    public async Task FirmwareConnectAcknowledgementCompletesCallSynchronization()
    {
        ManualTimeProvider time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000));
        InMemoryWakuHub hub = new();
        await using WakuProfileManager callerProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuProfileManager receiverProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await callerProfile.UpsertContactAsync(
            CreateContact(receiverProfile, time.GetUtcNow()),
            receiverProfile.Profile.PhoneNumber);
        await receiverProfile.UpsertContactAsync(
            CreateContact(callerProfile, time.GetUtcNow()),
            callerProfile.Profile.PhoneNumber);
        WakuPhoneBridgeOptions options = WakuPhoneBridgeOptions.Default with
        {
            CallMediaSetupTimeout = TimeSpan.FromSeconds(5),
        };
        await using WakuPhoneBridge caller = new(callerProfile, hub.CreateTransport(), time, options);
        await using WakuPhoneBridge receiver = new(receiverProfile, hub.CreateTransport(), time, options);
        BridgeHarness callerHarness = new(caller);
        BridgeHarness receiverHarness = new(receiver);
        caller.Start();
        receiver.Start();
        await WaitUntilAsync(() => hub.SubscriptionCount == 2);
        Guid callId = Guid.NewGuid();

        caller.TryEnqueue(new OutgoingNetworkRequest(
            callId,
            NetworkRequestKind.Call,
            receiverProfile.Profile.PhoneNumber,
            ""));
        await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.BeginCallMedia && command.RequestId == callId);
        await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.BeginCallMedia && command.RequestId == callId);
        receiver.TryEnqueue(WakuCallMediaEvent.State(callId, WakuCallMediaEventKind.Connected));
        await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingCall && command.RequestId == callId);
        receiver.TryEnqueue(new CallTransition(
            callId,
            CallDirection.Incoming,
            CallTransitionKind.Answer,
            callerProfile.Profile.PhoneNumber));
        await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ActivateCallMedia && command.RequestId == callId);

        Assert.False(callerHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.ConnectNetworkCall && command.RequestId == callId, out _));
        caller.TryEnqueue(WakuCallMediaEvent.State(callId, WakuCallMediaEventKind.Connected));
        await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ConnectNetworkCall && command.RequestId == callId);
        caller.TryEnqueue(new CallTransition(
            callId,
            CallDirection.Outgoing,
            CallTransitionKind.Connect,
            receiverProfile.Profile.PhoneNumber));
        await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ActivateCallMedia && command.RequestId == callId);
        await Task.Delay(100);

        time.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(100);
        Assert.False(callerHarness.TryTake(command =>
            command.Kind is WakuPhoneCommandKind.EndCallMedia or
                WakuPhoneCommandKind.TerminateNetworkCall, out _));
        Assert.False(receiverHarness.TryTake(command =>
            command.Kind is WakuPhoneCommandKind.EndCallMedia or
                WakuPhoneCommandKind.TerminateNetworkCall, out _));
    }

    [Fact]
    public async Task MissingCallerFirmwareConnectTimesOutBothSides()
    {
        ManualTimeProvider time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000));
        InMemoryWakuHub hub = new();
        await using WakuProfileManager callerProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuProfileManager receiverProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await callerProfile.UpsertContactAsync(
            CreateContact(receiverProfile, time.GetUtcNow()),
            receiverProfile.Profile.PhoneNumber);
        await receiverProfile.UpsertContactAsync(
            CreateContact(callerProfile, time.GetUtcNow()),
            callerProfile.Profile.PhoneNumber);
        WakuPhoneBridgeOptions options = WakuPhoneBridgeOptions.Default with
        {
            CallMediaSetupTimeout = TimeSpan.FromSeconds(5),
        };
        await using WakuPhoneBridge caller = new(callerProfile, hub.CreateTransport(), time, options);
        await using WakuPhoneBridge receiver = new(receiverProfile, hub.CreateTransport(), time, options);
        BridgeHarness callerHarness = new(caller);
        BridgeHarness receiverHarness = new(receiver);
        caller.Start();
        receiver.Start();
        await WaitUntilAsync(() => hub.SubscriptionCount == 2);
        Guid callId = Guid.NewGuid();

        caller.TryEnqueue(new OutgoingNetworkRequest(
            callId,
            NetworkRequestKind.Call,
            receiverProfile.Profile.PhoneNumber,
            ""));
        await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.BeginCallMedia && command.RequestId == callId);
        await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.BeginCallMedia && command.RequestId == callId);
        caller.TryEnqueue(WakuCallMediaEvent.State(callId, WakuCallMediaEventKind.Connected));
        receiver.TryEnqueue(WakuCallMediaEvent.State(callId, WakuCallMediaEventKind.Connected));
        await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingCall && command.RequestId == callId);
        receiver.TryEnqueue(new CallTransition(
            callId,
            CallDirection.Incoming,
            CallTransitionKind.Answer,
            callerProfile.Profile.PhoneNumber));
        await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ActivateCallMedia && command.RequestId == callId);
        await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ConnectNetworkCall && command.RequestId == callId);

        // Do not report CallTransitionKind.Connect from the caller firmware.
        // The callee must not remain connected while the caller keeps its
        // authentic ALERTING/ringback state forever.
        time.Advance(TimeSpan.FromSeconds(5));
        await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.TerminateNetworkCall && command.RequestId == callId);
        await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.TerminateNetworkCall && command.RequestId == callId);
    }

    [Fact]
    public async Task FirmwareCancellationStopsPreflightMediaImmediately()
    {
        InMemoryWakuHub hub = new();
        await using WakuProfileManager callerProfile = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuProfileManager receiverProfile = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await callerProfile.UpsertContactAsync(
            CreateContact(receiverProfile, now),
            receiverProfile.Profile.PhoneNumber);
        await receiverProfile.UpsertContactAsync(
            CreateContact(callerProfile, now),
            callerProfile.Profile.PhoneNumber);
        await using WakuPhoneBridge caller = new(callerProfile, hub.CreateTransport());
        await using WakuPhoneBridge receiver = new(receiverProfile, hub.CreateTransport());
        BridgeHarness callerHarness = new(caller);
        BridgeHarness receiverHarness = new(receiver);
        caller.Start();
        receiver.Start();
        await WaitUntilAsync(() => hub.SubscriptionCount == 2);
        Guid callId = Guid.NewGuid();

        caller.TryEnqueue(new OutgoingNetworkRequest(
            callId,
            NetworkRequestKind.Call,
            receiverProfile.Profile.PhoneNumber,
            ""));
        await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.BeginCallMedia && command.RequestId == callId);
        await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.BeginCallMedia && command.RequestId == callId);

        caller.TryEnqueue(new CallTransition(
            callId,
            CallDirection.Outgoing,
            CallTransitionKind.Reject,
            receiverProfile.Profile.PhoneNumber));

        await callerHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.EndCallMedia && command.RequestId == callId);
        await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.EndCallMedia && command.RequestId == callId);
        Assert.False(receiverHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingCall, out _));
    }

    [Fact]
    public async Task UnansweredPairingExpiresThroughNativeTimeoutExactlyOnce()
    {
        ManualTimeProvider time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000));
        InMemoryWakuHub hub = new();
        await using WakuProfileManager profile = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuPhoneBridge bridge = new(profile, hub.CreateTransport(), time);
        BridgeHarness harness = new(bridge);
        bridge.Start();
        const string unreachable = "1234567890123";
        bridge.TryEnqueue(PhonebookWrite(1, EmptyRecord(), SimPhonebookCodec.Encode("Offline", unreachable)));
        Guid requestId = Guid.NewGuid();
        bridge.TryEnqueue(new OutgoingNetworkRequest(requestId, NetworkRequestKind.Sms, unreachable, "hello"));
        await hub.WaitForPublishAsync();

        time.Advance(TimeSpan.FromMinutes(2));
        WakuPhoneCommand timedOut = await harness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest && command.RequestId == requestId);
        Assert.Equal(NetworkRequestDecision.Timeout, timedOut.Decision);
        time.Advance(TimeSpan.FromMinutes(2));
        await Task.Delay(20);
        Assert.False(harness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest && command.RequestId == requestId, out _));
    }

    [Fact]
    public async Task IdleLiveSubscriptionRollsAtEpochBoundaryWithoutPollingOrOverlap()
    {
        const long epoch = 12_345;
        const long offsetMilliseconds = 1_234;
        DateTimeOffset initial = DateTimeOffset.FromUnixTimeMilliseconds(
            epoch * WakuTopicProfile.EpochDurationMilliseconds + offsetMilliseconds);
        ManualTimeProvider time = new(initial);
        RolloverRecordingTransport transport = new();
        await using WakuProfileManager profile = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuPhoneBridge bridge = new(profile, transport, time);

        bridge.Start();

        string[] first = await transport.WaitForSubscriptionAsync();
        Assert.Equal(WakuTopicProfile.GetLiveCoverTopics(initial), first);
        await Task.Delay(20);
        Assert.False(transport.TryTakeSubscription(out _));

        time.Advance(TimeSpan.FromMilliseconds(
            WakuTopicProfile.EpochDurationMilliseconds - offsetMilliseconds - 1));
        await Task.Delay(20);
        Assert.False(transport.TryTakeSubscription(out _));

        time.Advance(TimeSpan.FromMilliseconds(1));
        string[] second = await transport.WaitForSubscriptionAsync();
        Assert.Equal(WakuTopicProfile.GetLiveCoverTopics(time.GetUtcNow()), second);
        Assert.Equal(1, transport.MaximumConcurrentSubscriptions);

        await Task.Delay(20);
        Assert.False(transport.TryTakeSubscription(out _));
    }

    [Fact]
    public async Task CompletedLiveSubscriptionIsRetried()
    {
        CompletingSubscriptionTransport transport = new();
        await using WakuProfileManager profile = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuPhoneBridge bridge = new(profile, transport);

        bridge.Start();

        Assert.Equal(1, await transport.WaitForSubscriptionAsync());
        Assert.Equal(2, await transport.WaitForSubscriptionAsync());
    }

    [Fact]
    public async Task RealtimeCallEnvelopeIsRepeatedWithTheSameIdempotentPacket()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
        ManualTimeProvider time = new(now);
        RolloverRecordingTransport transport = new();
        await using WakuProfileManager callerProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuProfileManager receiverProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await callerProfile.UpsertContactAsync(
            CreateContact(receiverProfile, now),
            receiverProfile.Profile.PhoneNumber);
        await using WakuPhoneBridge bridge = new(callerProfile, transport, time);
        BridgeHarness harness = new(bridge);
        bridge.Start();
        await transport.WaitForSubscriptionAsync();

        Guid callId = Guid.NewGuid();
        bridge.TryEnqueue(new OutgoingNetworkRequest(
            callId,
            NetworkRequestKind.Call,
            receiverProfile.Profile.PhoneNumber,
            ""));

        WakuPublishRequest first = await transport.WaitForPublishAsync();
        WakuPhoneCommand accepted = await harness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest && command.RequestId == callId);
        Assert.Equal(NetworkRequestDecision.Accept, accepted.Decision);

        time.Advance(TimeSpan.FromMilliseconds(350));
        WakuPublishRequest second = await transport.WaitForPublishAsync();
        time.Advance(TimeSpan.FromMilliseconds(700));
        WakuPublishRequest third = await transport.WaitForPublishAsync();

        Assert.True(first.Ephemeral);
        Assert.Equal(first.ContentTopic, second.ContentTopic);
        Assert.Equal(first.ContentTopic, third.ContentTopic);
        Assert.Equal(first.TimestampUnixMilliseconds, second.TimestampUnixMilliseconds);
        Assert.Equal(first.TimestampUnixMilliseconds, third.TimestampUnixMilliseconds);
        Assert.True(first.Payload.Span.SequenceEqual(second.Payload.Span));
        Assert.True(first.Payload.Span.SequenceEqual(third.Payload.Span));

        time.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(20);
        Assert.False(transport.TryTakePublish(out _));
    }

    [Fact]
    public async Task RendezvousPublishedAfterEpochRolloverReachesRenewedSubscription()
    {
        const long epoch = 23_456;
        DateTimeOffset initial = DateTimeOffset.FromUnixTimeMilliseconds(
            (epoch + 1) * WakuTopicProfile.EpochDurationMilliseconds - 1);
        ManualTimeProvider time = new(initial);
        InMemoryWakuHub hub = new();
        await using WakuProfileManager callerProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuProfileManager receiverProfile =
            await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuPhoneBridge caller = new(callerProfile, hub.CreateTransport(), time);
        await using WakuPhoneBridge receiver = new(receiverProfile, hub.CreateTransport(), time);
        BridgeHarness receiverHarness = new(receiver);
        caller.Start();
        receiver.Start();
        await WaitUntilAsync(() => hub.SubscriptionCount == 2);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(() => hub.SubscriptionCount == 4);

        string receiverNumber = receiverProfile.Profile.PhoneNumber;
        Assert.True(caller.TryEnqueue(PhonebookWrite(
            1,
            EmptyRecord(),
            SimPhonebookCodec.Encode("Receiver", receiverNumber))));
        Guid callId = Guid.NewGuid();
        Assert.True(caller.TryEnqueue(new OutgoingNetworkRequest(
            callId,
            NetworkRequestKind.Call,
            receiverNumber,
            "")));

        await receiverHarness.WaitForAsync(
            command => command.Kind == WakuPhoneCommandKind.BeginCallMedia && command.RequestId == callId);
        receiver.TryEnqueue(WakuCallMediaEvent.State(callId, WakuCallMediaEventKind.Connected));
        WakuPhoneCommand incoming = await receiverHarness.WaitForAsync(
            command => command.Kind == WakuPhoneCommandKind.QueueIncomingCall);
        Assert.Equal(callerProfile.Profile.PhoneNumber, incoming.Address);
    }

    [Fact]
    public async Task RestoredPhonebookSnapshotAllowsPairingRouteAfterRefresh()
    {
        InMemoryWakuHub hub = new();
        await using WakuProfileManager profile = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuPhoneBridge bridge = new(profile, hub.CreateTransport());
        BridgeHarness harness = new(bridge);
        bridge.Start();
        const string destination = "1234567890123";
        byte[] file = Enumerable.Repeat(
            (byte)0xFF,
            SimCard.AdnRecordCount * SimPhonebookCodec.RecordLength).ToArray();
        SimPhonebookCodec.Encode("Receiver", destination).CopyTo(file, 0);
        bridge.TryEnqueue(new SimMutation(
            0x7F10,
            0x6F3A,
            0,
            new byte[file.Length],
            file,
            SimMutationOrigin.PersistenceRestore));
        Guid requestId = Guid.NewGuid();

        bridge.TryEnqueue(new OutgoingNetworkRequest(
            requestId,
            NetworkRequestKind.Call,
            destination,
            ""));

        await hub.WaitForPublishAsync();
        Assert.False(harness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest &&
            command.RequestId == requestId, out _));
    }

    [Fact]
    public async Task EditingNumberDetachesFinalBindingWhileNameOnlyWritePreservesIt()
    {
        MemoryStore store = new();
        await using WakuProfileManager profile = await WakuProfileManager.LoadOrCreateAsync(store);
        WakuProfileContact contact = CreateContact("1234567890123");
        await profile.UpsertContactAsync(contact, "1234567890123");
        InMemoryWakuHub hub = new();
        await using WakuPhoneBridge bridge = new(profile, hub.CreateTransport());
        bridge.Start();
        byte[] oldRecord = SimPhonebookCodec.Encode("Original", "1234567890123");
        bridge.TryEnqueue(PhonebookWrite(1, EmptyRecord(), oldRecord));
        bridge.TryEnqueue(PhonebookWrite(
            1,
            oldRecord,
            SimPhonebookCodec.Encode("Renamed", "1234567890123")));
        await WaitUntilAsync(() => profile.Profile.FindContactByLocalNumber("1234567890123") is not null);
        Assert.NotNull(profile.Profile.FindContactByLocalNumber("1234567890123"));

        bridge.TryEnqueue(PhonebookWrite(
            1,
            SimPhonebookCodec.Encode("Renamed", "1234567890123"),
            SimPhonebookCodec.Encode("Renamed", "9876543210987")));
        await WaitUntilAsync(() => profile.Profile.FindContactByLocalNumber("1234567890123") is null);
        Assert.Empty(profile.Profile.Contacts);
    }

    [Fact]
    public async Task ContactSyncRetriesMissingCardWithoutImmediateDuplicates()
    {
        ManualTimeProvider time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000));
        InMemoryWakuHub hub = new();
        await using WakuProfileManager callerProfile = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuProfileManager receiverProfile = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        WakuProfileContact callerContact = CreateContact(callerProfile, time.GetUtcNow());
        WakuProfileContact receiverContact = CreateContact(receiverProfile, time.GetUtcNow());
        await callerProfile.UpsertContactAsync(receiverContact, receiverContact.CurrentNumber);
        await receiverProfile.UpsertContactAsync(callerContact, callerContact.CurrentNumber);
        await using WakuPhoneBridge caller = new(callerProfile, hub.CreateTransport(), time);
        await using WakuPhoneBridge receiver = new(receiverProfile, hub.CreateTransport(), time);
        BridgeHarness receiverHarness = new(receiver);
        caller.Start();
        receiver.Start();
        await WaitUntilAsync(() => hub.SubscriptionCount == 2);

        byte[] savedReceiver = SimPhonebookCodec.Encode("Receiver", receiverContact.CurrentNumber);
        caller.TryEnqueue(PhonebookWrite(1, EmptyRecord(), savedReceiver));
        await WaitUntilAsync(() => GetPrivateCollectionCount(caller, "deferredContactSyncOffers") != 0);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => GetPrivateCollectionCount(caller, "deferredContactSyncOffers") == 0);
        WakuPhoneCommand firstCard = await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSmartMessage);
        Assert.Equal(callerContact.CurrentNumber, firstCard.Address);

        caller.TryEnqueue(PhonebookWrite(
            1,
            savedReceiver,
            SimPhonebookCodec.Encode("Renamed", receiverContact.CurrentNumber)));
        await WaitUntilAsync(() => GetPrivateCollectionCount(caller, "deferredContactSyncOffers") != 0);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => GetPrivateCollectionCount(caller, "deferredContactSyncOffers") == 0);
        await Task.Delay(50);
        Assert.False(receiverHarness.TryTake(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSmartMessage, out _));

        time.Advance(TimeSpan.FromSeconds(31));
        caller.TryEnqueue(PhonebookWrite(
            1,
            SimPhonebookCodec.Encode("Renamed", receiverContact.CurrentNumber),
            SimPhonebookCodec.Encode("Receiver", receiverContact.CurrentNumber)));
        await WaitUntilAsync(() => GetPrivateCollectionCount(caller, "deferredContactSyncOffers") != 0);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => GetPrivateCollectionCount(caller, "deferredContactSyncOffers") == 0);
        WakuPhoneCommand retriedCard = await receiverHarness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.QueueIncomingSmartMessage);
        Assert.Equal(callerContact.CurrentNumber, retriedCard.Address);
        Assert.Single(receiverProfile.Profile.Contacts);
        Assert.Single(receiverProfile.Profile.NumberBindings);
    }

    [Fact]
    public async Task RestoreRejectsPendingRouteAndUpdatesManagedOwnNumber()
    {
        InMemoryWakuHub hub = new();
        await using WakuProfileManager profile = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuProfileManager recoverySource = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using WakuPhoneBridge bridge = new(profile, hub.CreateTransport());
        BridgeHarness harness = new(bridge);
        bridge.Start();

        const string unreachable = "1234567890123";
        bridge.TryEnqueue(PhonebookWrite(1, EmptyRecord(), SimPhonebookCodec.Encode("Offline", unreachable)));
        Guid requestId = Guid.NewGuid();
        bridge.TryEnqueue(new OutgoingNetworkRequest(requestId, NetworkRequestKind.Call, unreachable, ""));
        await hub.WaitForPublishAsync();

        string previousNumber = profile.Profile.PhoneNumber;
        await profile.RestoreAsync(recoverySource.Profile.CreateRecoveryPhrase());

        WakuPhoneCommand rejected = await harness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest && command.RequestId == requestId);
        Assert.Equal(NetworkRequestDecision.Reject, rejected.Decision);
        WakuPhoneCommand update = await harness.WaitForAsync(command =>
            command.Kind == WakuPhoneCommandKind.SetManagedOwnNumber);
        Assert.Equal(profile.Profile.PhoneNumber, update.Address);
        Assert.NotEqual(previousNumber, update.Address);
    }

    private static WakuProfileContact CreateContact(string number)
    {
        byte[] entropy = Enumerable.Repeat((byte)0xA5, Noks.Cryptography.NoksRecoveryPhrase.EntropySize).ToArray();
        using Noks.Cryptography.WakuProfileKeys keys = Noks.Cryptography.WakuProfileKeys.Create(entropy);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return WakuProfileContact.FromValidatedCard(ContactCardV2Codec.CreateSigned(
            keys,
            "clear-forest-zz99",
            number,
            now,
            now.AddMinutes(1)));
    }

    private static WakuProfileContact CreateContact(WakuProfileManager profile, DateTimeOffset now)
    {
        byte[] entropy = NoksRecoveryPhrase.Decode(profile.Profile.CreateRecoveryPhrase());
        using WakuProfileKeys keys = WakuProfileKeys.Create(entropy);
        return WakuProfileContact.FromValidatedCard(ContactCardV2Codec.CreateSigned(
            keys,
            profile.Profile.UserName,
            profile.Profile.PhoneNumber,
            now,
            now.AddMinutes(2)));
    }

    private static WakuProfileContact CreatePqcContact(
        WakuProfileManager profile,
        DateTimeOffset now)
    {
        byte[] entropy = NoksRecoveryPhrase.Decode(profile.Profile.CreateRecoveryPhrase());
        PqcRendezvousIdentity pqcIdentity = PqcRendezvousCrypto.CreateIdentity(entropy);
        return WakuProfileContact.FromValidatedPqcCard(PqcContactCardCodec.CreateSigned(
            pqcIdentity,
            profile.Profile.UserName,
            profile.Profile.PhoneNumber,
            now,
            now.AddMinutes(2)));
    }

    private static SimMutation PhonebookWrite(int record, byte[] oldValue, byte[] newValue) =>
        new(0x7F10, 0x6F3A, record, oldValue, newValue, SimMutationOrigin.Firmware);

    private static byte[] SendSimApdu(SimCard sim, params byte[] bytes)
    {
        List<byte> responseBytes = [];
        foreach (byte value in bytes)
        {
            if (sim.Transmit(value) is { } response)
                responseBytes.AddRange(response.Data);
        }
        return responseBytes.ToArray();
    }

    private static T GetPrivateField<T>(object target, string name) where T : class =>
        Assert.IsType<T>(target.GetType()
            .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(target));

    private static object? GetPrivateFieldValue(object target, string name) =>
        target.GetType()
            .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(target);

    private static void SetPrivateField(object target, string name, object value) =>
        target.GetType()
            .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(target, value);

    private static int GetPrivateCollectionCount(object target, string name)
    {
        object collection = target.GetType()
            .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(target)!;
        return Assert.IsType<int>(collection.GetType().GetProperty("Count")!.GetValue(collection));
    }

    private static void InvokePrivate(object target, string name) =>
        target.GetType()
            .GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(target, null);

    private static SimMutation FullPhonebookMutation()
    {
        byte[] file = Enumerable.Repeat(
            (byte)0xFF,
            SimCard.AdnRecordCount * SimPhonebookCodec.RecordLength).ToArray();
        for (int record = 1; record <= SimCard.OrdinaryAdnRecordCount; record++)
        {
            SimPhonebookCodec.Encode($"C{record}", record.ToString("D13")).CopyTo(
                file,
                (record - 1) * SimPhonebookCodec.RecordLength);
        }
        return new SimMutation(
            0x7F10,
            0x6F3A,
            0,
            new byte[file.Length],
            file,
            SimMutationOrigin.PersistenceRestore);
    }

    private static byte[] EmptyRecord() =>
        Enumerable.Repeat((byte)0xFF, SimPhonebookCodec.RecordLength).ToArray();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }

    private static void EstablishSmsService(LapdmLink link)
    {
        link.HandleUplink(
            0x80,
            BuildLapdmSabm([0x05, 0x24, 0x04, 0x02, 0x00, 0x01, 0x29]));
        link.HandleUplink(
            0x80,
            BuildLapdmInformationFrame([0x06, 0x32], receiveSequence: 1));
        link.HandleUplink(0x80, BuildLapdmReceiveReady(receiveSequence: 2));
        link.HandleUplink(0x80, BuildLapdmSabm([], sapi: 3));
    }

    private static byte[] BuildLapdmSabm(ReadOnlySpan<byte> information, byte sapi = 0)
    {
        byte[] frame = BuildEmptyLapdmFrame();
        frame[0] = (byte)((sapi << 2) | 0x01);
        frame[1] = 0x3F;
        frame[2] = (byte)((information.Length << 2) | 0x01);
        information.CopyTo(frame.AsSpan(3));
        return frame;
    }

    private static byte[] BuildLapdmInformationFrame(
        ReadOnlySpan<byte> information,
        byte sendSequence = 0,
        byte receiveSequence = 0,
        byte sapi = 0,
        bool moreData = false)
    {
        byte[] frame = BuildEmptyLapdmFrame();
        frame[0] = (byte)((sapi << 2) | 0x01);
        frame[1] = (byte)(
            ((receiveSequence & 0x07) << 5) |
            ((sendSequence & 0x07) << 1));
        frame[2] = (byte)((information.Length << 2) | (moreData ? 0x03 : 0x01));
        information.CopyTo(frame.AsSpan(3));
        return frame;
    }

    private static byte[] BuildLapdmReceiveReady(byte receiveSequence, byte sapi = 0)
    {
        byte[] frame = BuildEmptyLapdmFrame();
        frame[0] = (byte)((sapi << 2) | 0x03);
        frame[1] = (byte)(((receiveSequence & 0x07) << 5) | 0x01);
        frame[2] = 0x01;
        return frame;
    }

    private static byte[] BuildEmptyLapdmFrame()
    {
        byte[] frame = new byte[LapdmLink.FrameLength];
        Array.Fill(frame, (byte)0x2B);
        return frame;
    }

    private static void AssertLapdmFrame(byte[] actual, ReadOnlySpan<byte> expectedPrefix)
    {
        Assert.Equal(LapdmLink.FrameLength, actual.Length);
        for (int index = 0; index < expectedPrefix.Length; index++)
            Assert.Equal(expectedPrefix[index], actual[index]);
        for (int index = expectedPrefix.Length; index < actual.Length; index++)
            Assert.Equal(0x2B, actual[index]);
    }

    private sealed class BridgeHarness
    {
        private readonly Channel<WakuPhoneCommand> observed = Channel.CreateUnbounded<WakuPhoneCommand>();
        private readonly List<WakuPhoneCommand> retained = [];

        public BridgeHarness(WakuPhoneBridge bridge)
        {
            bridge.CommandAvailable += Drain;
            void Drain(WakuPhoneBridge source)
            {
                while (source.TryDequeueCommand(out WakuPhoneCommand? command) && command is not null)
                    observed.Writer.TryWrite(command);
            }
        }

        public async Task<WakuPhoneCommand> WaitForAsync(Func<WakuPhoneCommand, bool> predicate)
        {
            int retainedIndex = retained.FindIndex(command => predicate(command));
            if (retainedIndex >= 0)
            {
                WakuPhoneCommand value = retained[retainedIndex];
                retained.RemoveAt(retainedIndex);
                return value;
            }
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            while (await observed.Reader.WaitToReadAsync(timeout.Token))
            {
                while (observed.Reader.TryRead(out WakuPhoneCommand? command))
                {
                    if (predicate(command))
                        return command;
                    retained.Add(command);
                }
            }
            throw new TimeoutException();
        }

        public async Task<WakuPhoneCommand> WaitForNextAsync()
        {
            if (retained.Count != 0)
            {
                WakuPhoneCommand retainedCommand = retained[0];
                retained.RemoveAt(0);
                return retainedCommand;
            }

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            return await observed.Reader.ReadAsync(timeout.Token);
        }

        public bool TryTake(Func<WakuPhoneCommand, bool> predicate, out WakuPhoneCommand? command)
        {
            while (observed.Reader.TryRead(out WakuPhoneCommand? value))
                retained.Add(value);
            int index = retained.FindIndex(value => predicate(value));
            if (index < 0)
            {
                command = null;
                return false;
            }
            command = retained[index];
            retained.RemoveAt(index);
            return true;
        }
    }

    private sealed class MemoryStore : IWakuProfileStore
    {
        private string? value;

        public ValueTask<string?> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(value);
        }

        public ValueTask SaveAsync(string next, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            value = next;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryWakuHub
    {
        private readonly object sync = new();
        private readonly List<Transport> transports = [];
        private readonly List<WakuTransportMessage> store = [];
        private readonly Channel<bool> publishes = Channel.CreateUnbounded<bool>();
        private readonly List<WakuPublishRequest> publishedRequests = [];
        private int subscriptionCount;

        public int SubscriptionCount => Volatile.Read(ref subscriptionCount);

        public WakuPublishRequest[] PublishedRequests
        {
            get
            {
                lock (sync)
                    return publishedRequests.ToArray();
            }
        }

        public IWakuTransport CreateTransport()
        {
            Transport transport = new(this);
            lock (sync)
                transports.Add(transport);
            return transport;
        }

        public async Task WaitForPublishAsync()
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            await publishes.Reader.ReadAsync(timeout.Token);
        }

        private WakuPublishResult Publish(WakuPublishRequest request)
        {
            WakuTransportMessage message = new(
                request.ContentTopic,
                request.Payload,
                request.TimestampUnixMilliseconds,
                WakuMessageSource.LiveFilter);
            Transport[] targets;
            lock (sync)
            {
                publishedRequests.Add(request);
                if (!request.Ephemeral)
                    store.Add(message with { Source = WakuMessageSource.Store });
                targets = transports.Where(transport => transport.Accepts(request.ContentTopic)).ToArray();
            }
            foreach (Transport target in targets)
                target.Deliver(message);
            publishes.Writer.TryWrite(true);
            return new WakuPublishResult(1);
        }

        private WakuTransportMessage[] Query(WakuStoreQuery query)
        {
            lock (sync)
            {
                return store.Where(message =>
                        query.ContentTopics.Contains(message.ContentTopic, StringComparer.Ordinal) &&
                        message.TimestampUnixMilliseconds >= query.StartUnixMilliseconds &&
                        message.TimestampUnixMilliseconds <= query.EndUnixMilliseconds)
                    .ToArray();
            }
        }

        private void RecordSubscription() => Interlocked.Increment(ref subscriptionCount);

        private sealed class Transport : IWakuTransport
        {
            private readonly InMemoryWakuHub hub;
            private readonly Channel<WakuTransportMessage> inbox = Channel.CreateUnbounded<WakuTransportMessage>();
            private HashSet<string> topics = new(StringComparer.Ordinal);

            public Transport(InMemoryWakuHub hub)
            {
                this.hub = hub;
            }

            public ValueTask<WakuPublishResult> PublishAsync(
                WakuPublishRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(hub.Publish(request));
            }

            public async IAsyncEnumerable<WakuTransportMessage> SubscribeAsync(
                IReadOnlyList<string> contentTopics,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                topics = contentTopics.ToHashSet(StringComparer.Ordinal);
                hub.RecordSubscription();
                await foreach (WakuTransportMessage message in inbox.Reader.ReadAllAsync(cancellationToken))
                    yield return message;
            }

            public async IAsyncEnumerable<WakuTransportMessage> QueryStoreAsync(
                WakuStoreQuery query,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                foreach (WakuTransportMessage message in hub.Query(query))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return message;
                    await Task.Yield();
                }
            }

            public bool Accepts(string topic) => topics.Contains(topic);

            public void Deliver(WakuTransportMessage message) => inbox.Writer.TryWrite(message);
        }
    }

    private sealed class RolloverRecordingTransport : IWakuTransport
    {
        private readonly Channel<string[]> subscriptions = Channel.CreateUnbounded<string[]>();
        private readonly Channel<WakuPublishRequest> publishes = Channel.CreateUnbounded<WakuPublishRequest>();
        private int activeSubscriptions;
        private int maximumConcurrentSubscriptions;

        public int MaximumConcurrentSubscriptions => Volatile.Read(ref maximumConcurrentSubscriptions);

        public ValueTask<WakuPublishResult> PublishAsync(
            WakuPublishRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            publishes.Writer.TryWrite(request);
            return ValueTask.FromResult(new WakuPublishResult(1));
        }

        public async IAsyncEnumerable<WakuTransportMessage> SubscribeAsync(
            IReadOnlyList<string> contentTopics,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            int active = Interlocked.Increment(ref activeSubscriptions);
            UpdateMaximum(active);
            subscriptions.Writer.TryWrite(contentTopics.ToArray());
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref activeSubscriptions);
            }
            yield break;
        }

        public async IAsyncEnumerable<WakuTransportMessage> QueryStoreAsync(
            WakuStoreQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield break;
        }

        public async Task<string[]> WaitForSubscriptionAsync()
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            return await subscriptions.Reader.ReadAsync(timeout.Token);
        }

        public bool TryTakeSubscription(out string[]? topics) => subscriptions.Reader.TryRead(out topics);

        public async Task<WakuPublishRequest> WaitForPublishAsync()
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            return await publishes.Reader.ReadAsync(timeout.Token);
        }

        public bool TryTakePublish(out WakuPublishRequest request) => publishes.Reader.TryRead(out request);

        private void UpdateMaximum(int value)
        {
            int observed = Volatile.Read(ref maximumConcurrentSubscriptions);
            while (value > observed)
            {
                int previous = Interlocked.CompareExchange(
                    ref maximumConcurrentSubscriptions,
                    value,
                    observed);
                if (previous == observed)
                    return;
                observed = previous;
            }
        }
    }

    private sealed class CompletingSubscriptionTransport : IWakuTransport
    {
        private readonly Channel<int> subscriptions = Channel.CreateUnbounded<int>();
        private int subscriptionCount;

        public ValueTask<WakuPublishResult> PublishAsync(
            WakuPublishRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new WakuPublishResult(1));

        public async IAsyncEnumerable<WakuTransportMessage> SubscribeAsync(
            IReadOnlyList<string> contentTopics,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            int subscription = Interlocked.Increment(ref subscriptionCount);
            subscriptions.Writer.TryWrite(subscription);
            if (subscription == 1)
                yield break;

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public async IAsyncEnumerable<WakuTransportMessage> QueryStoreAsync(
            WakuStoreQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield break;
        }

        public async Task<int> WaitForSubscriptionAsync()
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            return await subscriptions.Reader.ReadAsync(timeout.Token);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object sync = new();
        private readonly List<ManualTimer> timers = [];
        private DateTimeOffset now;

        public ManualTimeProvider(DateTimeOffset now)
        {
            this.now = now;
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (sync)
                return now;
        }

        public override long GetTimestamp() => GetUtcNow().UtcTicks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new(this, callback, state);
            lock (sync)
                timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            ManualTimer[] snapshot;
            lock (sync)
            {
                now += amount;
                snapshot = timers.ToArray();
            }
            foreach (ManualTimer timer in snapshot)
                timer.FireIfDue(now);
        }

        private void Remove(ManualTimer timer)
        {
            lock (sync)
                timers.Remove(timer);
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider owner;
            private readonly TimerCallback callback;
            private readonly object? state;
            private DateTimeOffset? dueAt;
            private TimeSpan period;
            private bool disposed;

            public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
            {
                this.owner = owner;
                this.callback = callback;
                this.state = state;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (disposed)
                    return false;
                this.period = period;
                dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;
                return true;
            }

            public void FireIfDue(DateTimeOffset instant)
            {
                if (disposed || dueAt is null || dueAt > instant)
                    return;
                dueAt = period == Timeout.InfiniteTimeSpan ? null : instant + period;
                callback(state);
            }

            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;
                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
