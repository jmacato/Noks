using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using Noks.Cryptography;
using Noks.Waku;
using Noks.Dct3.Core;
using Noks.Dct3.Radio;
using Noks.Dct3.Sim;

namespace Noks.Application;

public sealed class WakuPhoneBridge : IAsyncDisposable
{
    private static readonly TimeSpan ContactCardRepeatDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ContactSyncCoalesceDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RealtimeRepeatDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan CallAcceptRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SubscriptionRetryDelay = TimeSpan.FromSeconds(5);
    private const int RealtimePublishAttempts = 3;
    private readonly WakuProfileManager profiles;
    private readonly IWakuTransport transport;
    private readonly TimeProvider timeProvider;
    private readonly WakuPhoneBridgeOptions options;
    private int postQuantumRendezvousEnabled;
    private readonly PhonebookContactIndex phonebook = new();
    private WakuReplayGuard replayGuard = new();
    private readonly Channel<BridgeWork> work;
    private readonly ConcurrentQueue<WakuPhoneCommand> commands = new();
    private readonly ConcurrentDictionary<long, Task> realtimeRepeatTasks = [];
    private readonly Dictionary<Guid, PendingOutboundRoute> pendingOutbound = [];
    private readonly Dictionary<Guid, DeferredPqcOutboundRoute> deferredPqcOutbound = [];
    private readonly Dictionary<Guid, PendingInboundRoute> pendingInbound = [];
    private readonly Dictionary<Guid, PendingContactSync> pendingContactSync = [];
    private readonly Dictionary<string, DeferredContactSyncOffer> deferredContactSyncOffers =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, TemporaryContact> temporaryContacts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> lastQueuedContactCards = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ActiveCall> activeCalls = [];
    private readonly Dictionary<Guid, List<WakuApplicationMessage>> earlyCallMessages = [];
    private readonly Dictionary<string, PqcRendezvousDescriptor> pqcDescriptors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PqcRendezvousDescriptor> ownPqcDescriptors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PqcDescriptorAssembly> pqcDescriptorAssemblies = new(StringComparer.Ordinal);
    private readonly HashSet<string> pqcReceivedEventIds = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource cancellation = new();
    private readonly ITimer deadlineTimer;
    private Task? eventLoopTask;
    private Task? subscriptionTask;
    private Task? storeTask;
    private CancellationTokenSource? subscriptionCancellation;
    private long subscribedEpoch = -1;
    private int started;
    private int disposed;
    private long nextRealtimeRepeatId;
    private WakuPhoneBridgeStatus status = WakuPhoneBridgeStatus.Offline;
    private WakuProfile observedProfile;
    private string observedPhoneNumber;
    private PqcRendezvousDescriptor? currentPqcDescriptor;
    private static readonly bool DiagnosticsEnabled =
        Environment.GetEnvironmentVariable("NOKS_WAKU_DIAGNOSTICS") == "1";

    private static void LogDiagnostic(string message)
    {
        if (DiagnosticsEnabled)
            Console.WriteLine($"Noks rendezvous: {message}");
    }

    public WakuPhoneBridge(
        WakuProfileManager profiles,
        IWakuTransport transport,
        TimeProvider? timeProvider = null,
        WakuPhoneBridgeOptions? options = null)
    {
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.options = options ?? WakuPhoneBridgeOptions.Default;
        postQuantumRendezvousEnabled =
            this.options.EnablePostQuantumRendezvous || this.options.RequirePostQuantumRendezvous ? 1 : 0;
        observedProfile = profiles.Profile;
        observedPhoneNumber = observedProfile.PhoneNumber;
        if (this.options.PairingLifetime <= TimeSpan.Zero ||
            this.options.PairingLifetime > ContactCardV2Codec.MaximumCardLifetime ||
            this.options.SmsLifetime <= TimeSpan.Zero ||
            this.options.SmsLifetime > WakuEventPolicy.MaximumDurableLifetime ||
            this.options.StoreWindow <= TimeSpan.Zero ||
            this.options.CallMediaSetupTimeout <= TimeSpan.Zero ||
            this.options.CallMediaSetupTimeout > WakuEventPolicy.MaximumRealtimeLifetime ||
            this.options.MaximumPendingRoutes <= 0 ||
            this.options.MaximumQueuedWork <= 0 ||
            this.options.MaximumQueuedCommands <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        if (this.options.PostQuantumMinimumWorkBits is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(options));
        work = Channel.CreateBounded<BridgeWork>(new BoundedChannelOptions(this.options.MaximumQueuedWork)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        deadlineTimer = this.timeProvider.CreateTimer(
            _ => work.Writer.TryWrite(BridgeWork.Deadline()),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        profiles.ProfileChanged += OnProfileChanged;
        if (transport is IWakuTransportAvailability availability)
            availability.AvailabilityChanged += OnTransportAvailabilityChanged;
    }

    public WakuPhoneBridgeStatus Status => status;

    public bool PostQuantumRendezvousEnabled => Volatile.Read(ref postQuantumRendezvousEnabled) != 0;

    public bool PostQuantumRendezvousRequired => options.RequirePostQuantumRendezvous;

    public void SetPostQuantumRendezvousEnabled(bool enabled)
    {
        if (!enabled && options.RequirePostQuantumRendezvous)
            return;
        Volatile.Write(ref postQuantumRendezvousEnabled, enabled ? 1 : 0);
        work.Writer.TryWrite(BridgeWork.Deadline());
    }

    public event Action<WakuPhoneBridge>? CommandAvailable;

    public event Action<WakuPhoneBridge>? StatusChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref started, 1) != 0)
            return;
        SetStatus(WakuPhoneBridgeStatus.Connecting);
        eventLoopTask = RunEventLoopAsync(cancellation.Token);
        work.Writer.TryWrite(BridgeWork.Deadline());
        storeTask = RunStoreQueryAsync(cancellation.Token);
    }

    public bool TryEnqueue(OutgoingNetworkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return work.Writer.TryWrite(BridgeWork.Outgoing(request));
    }

    public bool TryEnqueue(SimMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return work.Writer.TryWrite(BridgeWork.Sim(mutation));
    }

    public bool TryEnqueue(CallTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        return work.Writer.TryWrite(BridgeWork.Call(transition));
    }

    public bool TryEnqueue(WakuCallMediaEvent mediaEvent)
    {
        ArgumentNullException.ThrowIfNull(mediaEvent);
        return work.Writer.TryWrite(BridgeWork.Media(mediaEvent));
    }

    public bool TryDequeueCommand(out WakuPhoneCommand? command) => commands.TryDequeue(out command);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        cancellation.Cancel();
        profiles.ProfileChanged -= OnProfileChanged;
        if (transport is IWakuTransportAvailability availability)
            availability.AvailabilityChanged -= OnTransportAvailabilityChanged;
        work.Writer.TryComplete();
        deadlineTimer.Dispose();
        await AwaitStoppedAsync(eventLoopTask);
        await AwaitStoppedAsync(subscriptionTask);
        await AwaitStoppedAsync(storeTask);
        await AwaitStoppedAsync(Task.WhenAll(realtimeRepeatTasks.Values.ToArray()));
        subscriptionCancellation?.Dispose();
        cancellation.Dispose();
        SetStatus(WakuPhoneBridgeStatus.Offline);
    }

    private async Task RunEventLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (BridgeWork item in work.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await ProcessWorkAsync(item, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    SetStatus(WakuPhoneBridgeStatus.Offline);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunSubscriptionAsync(
        IReadOnlyList<string> topics,
        CancellationToken cancellationToken)
    {
        // The Filter subscription can stop before the epoch changes.
        // A closed relay WebSocket or peer churn can cause this failure.
        // EnsureSubscriptionEpochAsync subscribes again only after an epoch change.
        // Without this retry loop, one transient disconnect stops all live delivery for hours.
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (WakuTransportMessage message in transport.SubscribeAsync(topics, cancellationToken))
                {
                    if (!work.Writer.TryWrite(BridgeWork.Transport(message)))
                    {
                        LogDiagnostic($"work queue full, dropped live message topic={message.ContentTopic}");
                        SetStatus(WakuPhoneBridgeStatus.Offline);
                    }
                }

                // A Filter peer can also close its stream cleanly. That is not a
                // successful, terminal subscription. Reconnect to keep the live
                // route available, the same as after an exception.
                LogDiagnostic("filter subscription completed, retrying");
                SetStatus(WakuPhoneBridgeStatus.Offline);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogDiagnostic($"filter subscription dropped, retrying: {exception.Message}");
                SetStatus(WakuPhoneBridgeStatus.Offline);
            }

            try
            {
                await Task.Delay(SubscriptionRetryDelay, timeProvider, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void RequestStoreRefresh()
    {
        if (storeTask is null || storeTask.IsCompleted)
            storeTask = RunStoreQueryAsync(cancellation.Token);
    }

    private async Task RunStoreQueryAsync(CancellationToken cancellationToken)
    {
        try
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            WakuStoreQuery query = new(
                WakuTopicProfile.GetStoreCoverTopics(now, options.StoreWindow),
                (now - options.StoreWindow).ToUnixTimeMilliseconds(),
                now.AddMinutes(1).ToUnixTimeMilliseconds());
            await foreach (WakuTransportMessage message in transport.QueryStoreAsync(query, cancellationToken))
            {
                if (!work.Writer.TryWrite(BridgeWork.Transport(message)))
                {
                    LogDiagnostic($"work queue full, dropped store message topic={message.ContentTopic}");
                    SetStatus(WakuPhoneBridgeStatus.Offline);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            SetStatus(WakuPhoneBridgeStatus.Offline);
        }
    }

    private async ValueTask ProcessWorkAsync(BridgeWork item, CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        await EnsureSubscriptionEpochAsync(now, cancellationToken);
        if (PostQuantumRendezvousEnabled)
            await EnsurePqcDescriptorAsync(now, cancellationToken);
        ExpirePendingRoutes(now);
        await ExpireCallMediaSetupsAsync(now, cancellationToken);
        await MaintainCallSynchronizationAsync(now, cancellationToken);
        switch (item.Kind)
        {
            case BridgeWorkKind.OutgoingRequest:
                await ProcessOutgoingRequestAsync(item.NetworkRequest!, cancellationToken);
                break;
            case BridgeWorkKind.SimMutation:
                await ProcessSimMutationAsync(item.SimMutation!, cancellationToken);
                break;
            case BridgeWorkKind.CallTransition:
                await ProcessCallTransitionAsync(item.CallTransition!, cancellationToken);
                break;
            case BridgeWorkKind.CallMediaEvent:
                await ProcessCallMediaEventAsync(item.CallMediaEvent!, cancellationToken);
                break;
            case BridgeWorkKind.TransportMessage:
                await ProcessTransportMessageAsync(item.TransportMessage, cancellationToken);
                break;
            case BridgeWorkKind.Deadline:
                break;
            case BridgeWorkKind.ProfileChanged:
                ProcessProfileChanged();
                break;
            case BridgeWorkKind.TransportAvailability:
                SetStatus(item.TransportAvailable
                    ? WakuPhoneBridgeStatus.Online
                    : WakuPhoneBridgeStatus.Offline);
                if (item.TransportAvailable && PostQuantumRendezvousEnabled)
                    RequestStoreRefresh();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(item));
        }
        if (PostQuantumRendezvousEnabled)
            await FlushDeferredPqcOutboundAsync(timeProvider.GetUtcNow(), cancellationToken);
        await FlushDeferredContactSyncOffersAsync(timeProvider.GetUtcNow(), cancellationToken);
        ScheduleDeadlineTimer();
    }

    private async ValueTask EnsureSubscriptionEpochAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        long epoch = WakuTopicProfile.GetEpoch(now);
        if (epoch == subscribedEpoch)
            return;

        CancellationTokenSource? previousCancellation = subscriptionCancellation;
        previousCancellation?.Cancel();
        await AwaitStoppedAsync(subscriptionTask);
        previousCancellation?.Dispose();
        cancellationToken.ThrowIfCancellationRequested();

        CancellationTokenSource nextCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        subscriptionCancellation = nextCancellation;
        subscribedEpoch = epoch;
        subscriptionTask = RunSubscriptionAsync(
            WakuTopicProfile.GetLiveCoverTopics(now),
            nextCancellation.Token);
    }

    private async ValueTask ProcessOutgoingRequestAsync(
        OutgoingNetworkRequest request,
        CancellationToken cancellationToken)
    {
        WakuProfile profile = profiles.Profile;
        if (string.Equals(request.NormalizedDestination, profile.PhoneNumber, StringComparison.Ordinal) ||
            !NoksTemporaryNumber.IsCanonical(request.NormalizedDestination) ||
            (request.Kind == NetworkRequestKind.Call &&
                (activeCalls.Count != 0 ||
                 pendingInbound.Values.Any(value => value.RouteKind == RendezvousRouteKind.Call))))
        {
            if (request.Kind == NetworkRequestKind.Call &&
                profile.FindContactByLocalNumber(request.NormalizedDestination) is { } blockedContact)
            {
                deferredContactSyncOffers.Remove(blockedContact.StableContactId);
            }
            EnqueueCommand(WakuPhoneCommand.Resolve(request.RequestId, NetworkRequestDecision.Reject));
            return;
        }

        WakuProfileContact? contact = profile.FindContactByLocalNumber(request.NormalizedDestination);
        if (PostQuantumRendezvousEnabled &&
            contact is not null &&
            (!contact.HasPqcIdentity ||
             !PqcRendezvousCrypto.IsValidMlDsa65PublicKey(contact.PqcSigningPublicKey.AsSpan()) ||
             !PqcRendezvousCrypto.IsValidMlKem768PublicKey(contact.PqcMailboxPublicKey.AsSpan())))
        {
            // Profiles saved before the PQC contact-card extension re-pair over
            // the asynchronous rendezvous instead of silently falling back to
            // X25519 for an established packet.
            contact = null;
        }
        if (contact is not null)
        {
            if (request.Kind == NetworkRequestKind.Sms)
            {
                QueueContactCardIfMissing(contact);
                await PublishContactSyncOfferAsync(contact, cancellationToken);
            }
            bool routed = await PublishRoutedRequestAsync(request, contact, cancellationToken);
            if (routed && request.Kind == NetworkRequestKind.Call)
            {
                activeCalls[request.RequestId] = ActiveCall.CreateOutgoing(
                    request.RequestId,
                    contact,
                    timeProvider.GetUtcNow() + options.CallMediaSetupTimeout);
                EnqueueCommand(WakuPhoneCommand.BeginMedia(request.RequestId, isCaller: true));
            }
            else if (!routed && request.Kind == NetworkRequestKind.Call)
            {
                deferredContactSyncOffers.Remove(contact.StableContactId);
            }
            EnqueueCommand(WakuPhoneCommand.Resolve(
                request.RequestId,
                routed ? NetworkRequestDecision.Accept : NetworkRequestDecision.Reject));
            return;
        }

        if (pendingOutbound.Count + deferredPqcOutbound.Count >= options.MaximumPendingRoutes ||
            pendingOutbound.Values.Any(pending =>
                string.Equals(pending.Request.NormalizedDestination, request.NormalizedDestination, StringComparison.Ordinal)) ||
            deferredPqcOutbound.Values.Any(pending =>
                string.Equals(pending.Request.NormalizedDestination, request.NormalizedDestination, StringComparison.Ordinal)))
        {
            EnqueueCommand(WakuPhoneCommand.Resolve(request.RequestId, NetworkRequestDecision.Reject));
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset expiresAt = now +
            (PostQuantumRendezvousEnabled && request.Kind == NetworkRequestKind.Sms
                ? options.SmsLifetime
                : options.PairingLifetime);
        byte[] encodedCard = CreateOwnEncodedContactCard(now, expiresAt);
        Guid rendezvousId = request.Kind == NetworkRequestKind.Call
            ? request.RequestId
            : Guid.NewGuid();
        byte[] payload = RendezvousPayloadCodec.EncodeRequest(
            rendezvousId,
            request.Kind == NetworkRequestKind.Call ? RendezvousRouteKind.Call : RendezvousRouteKind.Sms,
            request.NormalizedDestination,
            encodedCard);
        if (PostQuantumRendezvousEnabled)
        {
            var deferred = new DeferredPqcOutboundRoute(
                rendezvousId,
                request,
                payload,
                expiresAt,
                now);
            bool haveDescriptor = pqcDescriptors.TryGetValue(
                request.NormalizedDestination,
                out PqcRendezvousDescriptor? descriptor);
            LogDiagnostic(
                $"outgoing {request.Kind} to={request.NormalizedDestination}: " +
                $"haveDescriptor={haveDescriptor} verified={haveDescriptor && PqcRendezvousCrypto.VerifyDescriptor(descriptor!, now)} " +
                $"knownDescriptors={string.Join(',', pqcDescriptors.Keys)}");
            if (haveDescriptor &&
                PqcRendezvousCrypto.VerifyDescriptor(descriptor!, now) &&
                await TryPublishPqcRendezvousAsync(deferred, descriptor!, now, cancellationToken))
            {
                var pending = new PendingOutboundRoute(request, expiresAt);
                pendingOutbound[rendezvousId] = pending;
                AcceptQueuedSms(pending);
                return;
            }

            LogDiagnostic($"outgoing {request.Kind} to={request.NormalizedDestination}: deferred, will retry in 5s");
            deferred.ScheduleRetry(now + TimeSpan.FromSeconds(5));
            deferredPqcOutbound[rendezvousId] = deferred;
            AcceptQueuedSms(deferred);
            RequestStoreRefresh();
            return;
        }
        byte[] randomRecipient = RandomNumberGenerator.GetBytes(WakuCrypto.X25519KeySize);
        try
        {
            WakuApplicationMessage message = new(
                Guid.NewGuid(),
                WakuEventKind.RendezvousRequest,
                now.ToUnixTimeMilliseconds(),
                expiresAt.ToUnixTimeMilliseconds(),
                profile.Keys.EnvelopePublicKey.Span,
                randomRecipient,
                payload);
            byte[] packet = NumberRendezvousEnvelopeCodec.Encrypt(
                message,
                profile.Keys.EnvelopePrivateKey.Span,
                request.NormalizedDestination);
            WakuPublishRequest publish = new(
                WakuTopicProfile.GetTopic(
                    RandomNumberGenerator.GetInt32(WakuTopicProfile.BucketCount),
                    WakuTopicProfile.GetEpoch(now)),
                packet,
                Ephemeral: true,
                now.ToUnixTimeMilliseconds());
            if (!await TryPublishAsync(publish, cancellationToken))
            {
                EnqueueCommand(WakuPhoneCommand.Resolve(request.RequestId, NetworkRequestDecision.Reject));
                return;
            }
            ScheduleRealtimeRepeats(publish);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(randomRecipient);
        }

        pendingOutbound[rendezvousId] = new PendingOutboundRoute(request, expiresAt);
    }

    private void OnProfileChanged(WakuProfile _) => work.Writer.TryWrite(BridgeWork.Profile());

    private void OnTransportAvailabilityChanged(bool available) =>
        work.Writer.TryWrite(BridgeWork.Availability(available));

    private void ProcessProfileChanged()
    {
        WakuProfile current = profiles.Profile;
        bool replaced = !ReferenceEquals(observedProfile, current);
        bool phoneNumberChanged = !string.Equals(observedPhoneNumber, current.PhoneNumber, StringComparison.Ordinal);
        observedProfile = current;
        observedPhoneNumber = current.PhoneNumber;
        if (replaced)
        {
            foreach (PendingOutboundRoute pending in pendingOutbound.Values)
            {
                ResolveNetworkRequest(pending, NetworkRequestDecision.Reject);
            }
            pendingOutbound.Clear();
            foreach (DeferredPqcOutboundRoute deferred in deferredPqcOutbound.Values)
            {
                ResolveNetworkRequest(deferred, NetworkRequestDecision.Reject);
            }
            deferredPqcOutbound.Clear();
            pendingInbound.Clear();
            pendingContactSync.Clear();
            deferredContactSyncOffers.Clear();
            temporaryContacts.Clear();
            lastQueuedContactCards.Clear();
            earlyCallMessages.Clear();
            foreach (Guid attemptId in activeCalls.Keys.ToArray())
                EndActiveCall(attemptId, terminateNetworkCall: true);
            activeCalls.Clear();
            replayGuard = new WakuReplayGuard();
            currentPqcDescriptor = null;
            pqcDescriptors.Clear();
            ownPqcDescriptors.Clear();
            pqcDescriptorAssemblies.Clear();
            pqcReceivedEventIds.Clear();
        }
        if (phoneNumberChanged)
            EnqueueCommand(WakuPhoneCommand.ManagedOwnNumber(current.PhoneNumber));
    }

    private async ValueTask ProcessSimMutationAsync(SimMutation mutation, CancellationToken cancellationToken)
    {
        PhonebookIndexUpdate update = phonebook.Apply(mutation);
        if (mutation.Origin == SimMutationOrigin.PersistenceRestore)
            return;

        foreach (string removedNumber in update.RemovedNumbers)
        {
            lastQueuedContactCards.Remove(removedNumber);
        }

        WakuProfileContact? temporaryContact = null;
        if (mutation.Origin == SimMutationOrigin.Firmware && update.WrittenNumber is not null)
        {
            lastQueuedContactCards.Remove(update.WrittenNumber);
            TemporaryContact? temporary = temporaryContacts.Values
                .Where(value => string.Equals(
                    value.Contact.CurrentNumber,
                    update.WrittenNumber,
                    StringComparison.Ordinal))
                .OrderByDescending(value => value.ExpiresAt)
                .FirstOrDefault();
            temporaryContact = temporary?.Contact ??
                activeCalls.Values
                    .Select(value => value.Contact)
                    .FirstOrDefault(value => string.Equals(
                        value.CurrentNumber,
                        update.WrittenNumber,
                        StringComparison.Ordinal)) ??
                pendingInbound.Values
                    .Select(value => value.Contact)
                    .FirstOrDefault(value => string.Equals(
                        value.CurrentNumber,
                        update.WrittenNumber,
                        StringComparison.Ordinal));
        }

        await profiles.ApplyCoherentSimMutationAsync(
            mutation,
            mutation.Origin == SimMutationOrigin.Firmware ? update.RemovedNumbers : [],
            temporaryContact,
            temporaryContact is null ? null : update.WrittenNumber,
            cancellationToken);

        if (temporaryContact is not null)
        {
            temporaryContacts.Remove(temporaryContact.StableContactId);
        }
        if (mutation.Origin != SimMutationOrigin.Firmware || update.WrittenNumber is null)
            return;
        PendingInboundRoute? pending = pendingInbound.Values
            .Where(value => !value.AcceptSent &&
                string.Equals(value.Contact.CurrentNumber, update.WrittenNumber, StringComparison.Ordinal))
            .OrderBy(value => value.ExpiresAt)
            .FirstOrDefault();
        if (pending is not null)
        {
            await PublishRendezvousAcceptAsync(pending, cancellationToken);
            return;
        }

        WakuProfileContact? contact = profiles.Profile.FindContactByLocalNumber(update.WrittenNumber);
        if (contact is not null)
            ScheduleContactSyncOffer(contact);
    }

    private async ValueTask<bool> PublishRendezvousAcceptAsync(
        PendingInboundRoute pending,
        CancellationToken cancellationToken)
    {
        byte[] ownCard = CreateOwnEncodedContactCard(
            timeProvider.GetUtcNow(),
            pending.ExpiresAt);
        byte[] response = RendezvousPayloadCodec.EncodeCardResponse(
            pending.RendezvousId,
            ownCard);
        bool sent = await PublishEnvelopeAsync(
            WakuEventKind.RendezvousAccept,
            pending.Contact,
            response,
            pending.ExpiresAt,
            cancellationToken);
        if (sent)
            pending.AcceptSent = true;
        return sent;
    }

    private ValueTask<bool> PublishRendezvousReadyAsync(
        PendingInboundRoute pending,
        CancellationToken cancellationToken)
    {
        byte[] ownCard = CreateOwnEncodedContactCard(
            timeProvider.GetUtcNow(),
            pending.ExpiresAt);
        return PublishEnvelopeAsync(
            WakuEventKind.RendezvousReady,
            pending.Contact,
            RendezvousPayloadCodec.EncodeCardResponse(
                pending.RendezvousId,
                ownCard),
            pending.ExpiresAt,
            cancellationToken);
    }

    private bool QueueContactCardIfMissing(WakuProfileContact contact)
    {
        if (phonebook.ContainsNumber(contact.CurrentNumber))
        {
            lastQueuedContactCards.Remove(contact.CurrentNumber);
            return false;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (lastQueuedContactCards.TryGetValue(contact.CurrentNumber, out DateTimeOffset lastQueued) &&
            now - lastQueued < ContactCardRepeatDelay)
        {
            return false;
        }
        lastQueuedContactCards[contact.CurrentNumber] = now;
        EnqueueCommand(WakuPhoneCommand.SmartMessage(
            contact.CurrentNumber,
            NokiaBusinessCardVCard.DestinationPort,
            NokiaBusinessCardVCard.Encode(
                contact.UserName,
                contact.CurrentNumber)));
        return true;
    }

    private void RememberTemporaryContact(WakuProfileContact contact, DateTimeOffset expiresAt)
    {
        if (profiles.Profile.FindContactByStableId(contact.StableContactId) is not null)
        {
            temporaryContacts.Remove(contact.StableContactId);
            return;
        }

        if (!temporaryContacts.TryGetValue(contact.StableContactId, out TemporaryContact? existing) ||
            expiresAt > existing.ExpiresAt)
        {
            temporaryContacts[contact.StableContactId] = new TemporaryContact(contact, expiresAt);
        }
    }

    private async ValueTask ProcessCallTransitionAsync(
        CallTransition transition,
        CancellationToken cancellationToken)
    {
        if (!activeCalls.TryGetValue(transition.RequestId, out ActiveCall? call))
            return;
        if (call.Incoming != (transition.Direction == CallDirection.Incoming))
            return;
        WakuProfileContact contact = call.Contact;

        if (transition.Direction == CallDirection.Outgoing &&
            transition.Kind == CallTransitionKind.Connect)
        {
            if (!call.TryMarkLocalFirmwareConnected())
                return;
            EnqueueCommand(WakuPhoneCommand.ActivateMedia(transition.RequestId));
            // WebRTC readiness does not complete CONNECT.
            // After the caller firmware consumes GSM CONNECT and emits its call transition, report CONNECT.
            // The callee uses this report as the acknowledgement for its consent packet.
            await PublishCallControlAsync(
                transition.RequestId,
                contact,
                WakuEventKind.CallConnected,
                cancellationToken);
            return;
        }

        WakuEventKind? kind = transition.Kind switch
        {
            CallTransitionKind.Answer when call.Incoming && call.Session.AcceptIncoming() =>
                WakuEventKind.CallAccept,
            CallTransitionKind.Connect when call.Incoming => null,
            CallTransitionKind.Reject when call.Incoming && call.Session.RejectIncoming() =>
                WakuEventKind.CallReject,
            CallTransitionKind.Reject when call.Session.EndLocally(transition.RequestId) =>
                WakuEventKind.CallHangup,
            CallTransitionKind.Hangup when call.Session.EndLocally(transition.RequestId) =>
                WakuEventKind.CallHangup,
            _ => null,
        };
        if (kind is null)
            return;

        bool sent;
        if (kind == WakuEventKind.CallAccept &&
            pendingInbound.TryGetValue(transition.RequestId, out PendingInboundRoute? pendingCall) &&
            pendingCall.RouteKind == RendezvousRouteKind.Call)
        {
            sent = await PublishRendezvousAcceptAsync(pendingCall, cancellationToken);
            if (sent)
            {
                pendingCall.CallAnswered = true;
                RememberTemporaryContact(pendingCall.Contact, pendingCall.ExpiresAt);
            }
        }
        else
        {
            sent = true;
        }

        if (sent)
            sent = await PublishCallControlAsync(
                transition.RequestId,
                contact,
                kind.Value,
                cancellationToken);
        if (sent && kind == WakuEventKind.CallAccept)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            call.BeginRemoteFirmwareSynchronization(
                now + CallAcceptRetryDelay,
                now + options.CallMediaSetupTimeout);
            QueueContactCardIfMissing(contact);
            EnqueueCommand(WakuPhoneCommand.ActivateMedia(transition.RequestId));
        }
        if (kind is WakuEventKind.CallReject or WakuEventKind.CallHangup)
        {
            pendingInbound.Remove(transition.RequestId);
            EndActiveCall(transition.RequestId, terminateNetworkCall: false);
        }
        else if (!sent)
        {
            await FailActiveCallAsync(transition.RequestId, call, contact, cancellationToken);
        }
    }

    private async ValueTask ProcessCallMediaEventAsync(
        WakuCallMediaEvent mediaEvent,
        CancellationToken cancellationToken)
    {
        if (mediaEvent.AttemptId == Guid.Empty ||
            !activeCalls.TryGetValue(mediaEvent.AttemptId, out ActiveCall? call))
        {
            return;
        }

        WakuProfileContact contact = call.Contact;

        if (mediaEvent.Kind == WakuCallMediaEventKind.Connected)
        {
            if (!call.Session.MarkWebRtcConnected(mediaEvent.AttemptId))
                return;
            call.MarkMediaReady();
            TryConnectOutgoingFirmware(mediaEvent.AttemptId, call);
            if (call.Incoming && call.TryMarkIncomingDisplayed())
            {
                if (await PublishCallControlAsync(
                        mediaEvent.AttemptId,
                        contact,
                        WakuEventKind.CallRinging,
                        cancellationToken))
                {
                    EnqueueCommand(WakuPhoneCommand.IncomingCall(
                        mediaEvent.AttemptId,
                        call.IncomingAddress));
                }
                else
                {
                    await FailActiveCallAsync(mediaEvent.AttemptId, call, contact, cancellationToken);
                }
            }
            return;
        }
        if (mediaEvent.Kind == WakuCallMediaEventKind.Failed)
        {
            if (call.Session.FailLocally(mediaEvent.AttemptId))
            {
                await PublishCallControlAsync(
                    mediaEvent.AttemptId,
                    contact,
                    WakuEventKind.CallFailed,
                    cancellationToken);
                EndActiveCall(mediaEvent.AttemptId, terminateNetworkCall: true);
            }
            return;
        }

        WakuEventKind eventKind = mediaEvent.Kind switch
        {
            WakuCallMediaEventKind.SdpOffer => WakuEventKind.SdpOffer,
            WakuCallMediaEventKind.SdpAnswer => WakuEventKind.SdpAnswer,
            WakuCallMediaEventKind.IceCandidate => WakuEventKind.IceCandidate,
            _ => throw new ArgumentOutOfRangeException(nameof(mediaEvent)),
        };
        bool stateAllowed = call.Session.State is
            WakuCallState.OutgoingRinging or
            WakuCallState.IncomingRinging or
            WakuCallState.Negotiating or
            WakuCallState.Connected;
        if (!stateAllowed ||
            mediaEvent.Payload.IsDefault ||
            mediaEvent.Payload.Length == 0 ||
            mediaEvent.Payload.Length > WakuCallSignalCodec.MaximumSignalSize)
        {
            return;
        }

        Guid signalId = Guid.NewGuid();
        IReadOnlyList<byte[]> fragments = WakuCallSignalCodec.EncodeFragments(
            mediaEvent.AttemptId,
            signalId,
            mediaEvent.Payload.AsSpan());
        foreach (byte[] fragment in fragments)
        {
            if (!await PublishEnvelopeAsync(
                    eventKind,
                    contact,
                    fragment,
                    timeProvider.GetUtcNow() + options.PairingLifetime,
                    cancellationToken))
            {
                await FailActiveCallAsync(mediaEvent.AttemptId, call, contact, cancellationToken);
                return;
            }
        }
    }

    private async ValueTask ProcessTransportMessageAsync(
        WakuTransportMessage transportMessage,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        WakuProfile profile = profiles.Profile;
        if (PostQuantumRendezvousEnabled)
        {
            if (PqcRendezvousWireCodec.TryDecode(
                    transportMessage.Payload.Span,
                    out PqcRendezvousWireRecord? pqcRecord) &&
                pqcRecord is not null)
            {
                await ProcessPqcRecordAsync(pqcRecord, transportMessage.ContentTopic, profile, now, cancellationToken);
                return;
            }
            if (PqcWakuEnvelopeCodec.TryDecrypt(
                    transportMessage.Payload.Span,
                    profile.GetPqcRendezvousIdentity(),
                    out WakuApplicationMessage? pqcDirect) &&
                pqcDirect is not null &&
                replayGuard.TryAccept(pqcDirect, now) &&
                await profiles.TryRememberIncomingEventAsync(pqcDirect, now, cancellationToken))
            {
                await ProcessDirectMessageAsync(pqcDirect, cancellationToken);
            }
            return;
        }
        if (WakuEnvelopeCodec.TryDecrypt(
                transportMessage.Payload.Span,
                profile.Keys.MailboxPrivateKey.Span,
                out WakuApplicationMessage? direct) &&
            direct is not null)
        {
            if (replayGuard.TryAccept(direct, now) &&
                await profiles.TryRememberIncomingEventAsync(direct, now, cancellationToken))
            {
                await ProcessDirectMessageAsync(direct, cancellationToken);
            }
            return;
        }
        if (NumberRendezvousEnvelopeCodec.TryDecrypt(
                transportMessage.Payload.Span,
                profile.PhoneNumber,
                out WakuApplicationMessage? rendezvous) &&
            rendezvous is not null && replayGuard.TryAccept(rendezvous, now) &&
            await profiles.TryRememberIncomingEventAsync(rendezvous, now, cancellationToken))
        {
            await ProcessRendezvousRequestAsync(rendezvous, cancellationToken);
        }
    }

    private async ValueTask EnsurePqcDescriptorAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (currentPqcDescriptor is not null &&
            currentPqcDescriptor.ExpiresAtUnixMilliseconds > now.AddMinutes(5).ToUnixTimeMilliseconds())
        {
            return;
        }

        DateTimeOffset expiresAt = now + options.StoreWindow;
        PqcRendezvousDescriptor descriptor = PqcRendezvousCrypto.CreateDescriptor(
            profiles.Profile.GetPqcRendezvousIdentity(),
            profiles.Profile.PhoneNumber,
            Math.Max(1, WakuTopicProfile.GetEpoch(now) + 1),
            expiresAt,
            options.PostQuantumMinimumWorkBits);
        LogDiagnostic(
            $"creating own pqc descriptor descriptorId={Convert.ToHexString(descriptor.DescriptorId)} " +
            $"expiresAt={descriptor.ExpiresAtUnixMilliseconds} sequence={descriptor.Sequence} " +
            $"previous={(currentPqcDescriptor is null ? "none" : Convert.ToHexString(currentPqcDescriptor.DescriptorId))}");
        PqcRendezvousDescriptorChunk[] chunks = PqcRendezvousCrypto.CreateDescriptorChunks(descriptor);
        foreach (PqcRendezvousDescriptorChunk chunk in chunks)
        {
            WakuPublishRequest publish = new(
                WakuTopicProfile.GetTopic(RandomNumberGenerator.GetInt32(WakuTopicProfile.BucketCount), WakuTopicProfile.GetEpoch(now)),
                PqcRendezvousWireCodec.EncodeDescriptorChunk(chunk),
                Ephemeral: false,
                now.ToUnixTimeMilliseconds());
            if (!await TryPublishAsync(publish, cancellationToken))
                return;
        }
        currentPqcDescriptor = descriptor;
        ownPqcDescriptors[Convert.ToHexString(descriptor.DescriptorId)] = descriptor;
        pqcDescriptors[descriptor.TemporaryId] = descriptor;
    }

    private async ValueTask FlushDeferredPqcOutboundAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        bool refreshStore = false;
        foreach (Guid rendezvousId in deferredPqcOutbound.Keys.ToArray())
        {
            DeferredPqcOutboundRoute deferred = deferredPqcOutbound[rendezvousId];
            if (deferred.ExpiresAt <= now || deferred.NextAttemptAt > now)
                continue;

            if (!pqcDescriptors.TryGetValue(
                    deferred.Request.NormalizedDestination,
                    out PqcRendezvousDescriptor? descriptor) ||
                !PqcRendezvousCrypto.VerifyDescriptor(descriptor, now))
            {
                deferred.ScheduleRetry(now + TimeSpan.FromSeconds(10));
                refreshStore = true;
                continue;
            }

            if (await TryPublishPqcRendezvousAsync(
                    deferred,
                    descriptor,
                    now,
                    cancellationToken))
            {
                deferredPqcOutbound.Remove(rendezvousId);
                pendingOutbound[rendezvousId] = new PendingOutboundRoute(
                    deferred.Request,
                    deferred.ExpiresAt,
                    deferred.NetworkRequestResolved);
                continue;
            }

            deferred.ScheduleRetry(now + TimeSpan.FromSeconds(5));
        }

        if (refreshStore)
            RequestStoreRefresh();
    }

    private async ValueTask<bool> TryPublishPqcRendezvousAsync(
        DeferredPqcOutboundRoute deferred,
        PqcRendezvousDescriptor descriptor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            string topic = WakuTopicProfile.GetTopic(
                RandomNumberGenerator.GetInt32(WakuTopicProfile.BucketCount),
                WakuTopicProfile.GetEpoch(now));
            PqcRendezvousOutbound outbound = PqcRendezvousCrypto.CreateRequest(
                descriptor,
                topic,
                deferred.Payload);
            WakuPublishRequest publish = new(
                topic,
                PqcRendezvousWireCodec.EncodeRequest(outbound.Request),
                Ephemeral: false,
                now.ToUnixTimeMilliseconds());
            LogDiagnostic(
                $"publishing pqc request descriptorId={Convert.ToHexString(descriptor.DescriptorId)} " +
                $"expiresAt={descriptor.ExpiresAtUnixMilliseconds} sequence={descriptor.Sequence} topic={topic}");
            return await TryPublishAsync(publish, cancellationToken);
        }
        catch (Exception exception) when (
            exception is ArgumentException or CryptographicException or OverflowException)
        {
            return false;
        }
    }

    private async ValueTask ProcessPqcRecordAsync(
        PqcRendezvousWireRecord record,
        string contentTopic,
        WakuProfile profile,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (record is PqcRendezvousDescriptorChunkRecord descriptorChunk)
        {
            string hash = Convert.ToHexString(descriptorChunk.Chunk.DescriptorHash);
            if (!pqcDescriptorAssemblies.TryGetValue(hash, out PqcDescriptorAssembly? assembly))
            {
                if (pqcDescriptorAssemblies.Count >= 64)
                    return;
                assembly = new PqcDescriptorAssembly(descriptorChunk.Chunk.Count);
                pqcDescriptorAssemblies.Add(hash, assembly);
            }
            if (!assembly.TryAdd(descriptorChunk.Chunk))
                return;
            if (assembly.IsComplete && PqcRendezvousCrypto.TryReassembleDescriptor(assembly.Chunks, now, out var descriptor))
            {
                pqcDescriptorAssemblies.Remove(hash);
                pqcDescriptors[descriptor.TemporaryId] = descriptor;
                PqcRendezvousIdentity ownIdentity = profile.GetPqcRendezvousIdentity();
                if (string.Equals(
                        descriptor.TemporaryId,
                        profile.PhoneNumber,
                        StringComparison.Ordinal) &&
                    CryptographicOperations.FixedTimeEquals(
                        descriptor.SigningPublicKey,
                        ownIdentity.SigningPublicKey))
                {
                    ownPqcDescriptors[Convert.ToHexString(descriptor.DescriptorId)] = descriptor;
                }
                foreach (DeferredPqcOutboundRoute deferred in deferredPqcOutbound.Values
                             .Where(value => string.Equals(
                                 value.Request.NormalizedDestination,
                                 descriptor.TemporaryId,
                                 StringComparison.Ordinal)))
                {
                    deferred.ScheduleRetry(now);
                }
            }
            return;
        }

        if (record is not PqcRendezvousRequestRecord requestRecord)
        {
            LogDiagnostic("pqc record: not a request record, ignoring");
            return;
        }
        if (!string.Equals(requestRecord.Request.ContentTopic, contentTopic, StringComparison.Ordinal))
        {
            LogDiagnostic(
                $"pqc request dropped: content topic mismatch embedded={requestRecord.Request.ContentTopic} " +
                $"delivered={contentTopic}");
            return;
        }

        string descriptorKey = Convert.ToHexString(requestRecord.Request.DescriptorId);
        if (!ownPqcDescriptors.TryGetValue(
                descriptorKey,
                out PqcRendezvousDescriptor? requestDescriptor))
        {
            LogDiagnostic(
                $"pqc request dropped: unknown descriptorId={descriptorKey} " +
                $"known={string.Join(',', ownPqcDescriptors.Keys)}");
            return;
        }
        PqcRendezvousReceiveResult received = PqcRendezvousCrypto.TryReceive(
            profile.GetPqcRendezvousIdentity(),
            requestDescriptor,
            requestRecord.Request,
            pqcReceivedEventIds,
            now);
        if (!received.IsAccepted)
        {
            LogDiagnostic($"pqc request dropped: TryReceive rejected: {received.Reason}");
            return;
        }
        if (!RendezvousPayloadCodec.TryDecodeRequest(received.Plaintext, out RendezvousRequestPayload? payload) ||
            payload is null)
        {
            LogDiagnostic("pqc request dropped: inner RendezvousRequestPayload decode failed");
            return;
        }
        if (!PqcContactCardCodec.TryValidate(
                payload.ContactCard.AsSpan(),
                now,
                out PqcContactCard? card) ||
            card is null)
        {
            LogDiagnostic("pqc request dropped: sender contact card failed validation");
            return;
        }

        byte[] recipientRoutingKey = PqcContactCardCodec.CreateMailboxRoutingKey(
            profile.GetPqcRendezvousIdentity().ChallengePublicKey);
        WakuApplicationMessage message = new(
            Guid.NewGuid(),
            WakuEventKind.RendezvousRequest,
            now.ToUnixTimeMilliseconds(),
            card.ExpiresAtUnixMilliseconds,
            card.EnvelopeRoutingKey.Span,
            recipientRoutingKey,
            received.Plaintext);
        CryptographicOperations.ZeroMemory(recipientRoutingKey);
        await ProcessRendezvousRequestAsync(message, cancellationToken);
    }

    private async ValueTask ProcessRendezvousRequestAsync(
        WakuApplicationMessage message,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (!RendezvousPayloadCodec.TryDecodeRequest(message.Payload.Span, out RendezvousRequestPayload? request) ||
            request is null)
        {
            LogDiagnostic("rendezvous request dropped: outer payload decode failed");
            return;
        }
        if (!string.Equals(request.TargetNumber, profiles.Profile.PhoneNumber, StringComparison.Ordinal))
        {
            LogDiagnostic(
                $"rendezvous request dropped: target={request.TargetNumber} != own={profiles.Profile.PhoneNumber}");
            return;
        }
        if (!TryValidateEncodedContactCard(
                request.ContactCard.AsSpan(),
                now,
                message.SenderIdentityPublicKey.Span,
                out WakuProfileContact? contact) ||
            contact is null)
        {
            LogDiagnostic("rendezvous request dropped: sender contact card failed validation");
            return;
        }
        if (string.Equals(
                contact.StableContactId,
                PostQuantumRendezvousEnabled
                    ? profiles.Profile.PqcStableContactId
                    : profiles.Profile.StableContactId,
                StringComparison.Ordinal))
        {
            LogDiagnostic("rendezvous request dropped: sender is self");
            return;
        }

        bool callRouteUnavailable = request.RouteKind == RendezvousRouteKind.Call &&
            (activeCalls.Count != 0 ||
             pendingInbound.Values.Any(value => value.RouteKind == RendezvousRouteKind.Call));
        if ((request.RouteKind != RendezvousRouteKind.Call && phonebook.IsFull) ||
            pendingInbound.Count >= options.MaximumPendingRoutes ||
            callRouteUnavailable)
        {
            LogDiagnostic(
                $"rendezvous request declined: phonebookFull={phonebook.IsFull} " +
                $"pendingInbound={pendingInbound.Count} callRouteUnavailable={callRouteUnavailable}");
            await PublishEnvelopeAsync(
                WakuEventKind.RendezvousDecline,
                contact,
                RendezvousPayloadCodec.EncodeCorrelation(request.RendezvousId),
                DateTimeOffset.FromUnixTimeMilliseconds(message.ExpiresAtUnixMilliseconds),
                cancellationToken);
            return;
        }
        if (pendingInbound.ContainsKey(request.RendezvousId) ||
            pendingInbound.Values.Any(value => value.Contact.MatchesEnvelopeKey(message.SenderIdentityPublicKey.Span)))
        {
            LogDiagnostic("rendezvous request dropped: duplicate rendezvousId or contact already pending");
            return;
        }
        LogDiagnostic($"rendezvous request accepted: routeKind={request.RouteKind} from={contact.CurrentNumber}");

        DateTimeOffset expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(message.ExpiresAtUnixMilliseconds);
        PendingInboundRoute pending = new(
            request.RendezvousId,
            request.RouteKind,
            contact,
            expiresAt);
        pendingInbound[request.RendezvousId] = pending;
        if (request.RouteKind == RendezvousRouteKind.Call)
        {
            if (!await PublishRendezvousReadyAsync(pending, cancellationToken))
                pendingInbound.Remove(request.RendezvousId);
        }
        else
            QueueContactCardIfMissing(pending.Contact);
    }

    private async ValueTask ProcessDirectMessageAsync(
        WakuApplicationMessage message,
        CancellationToken cancellationToken)
    {
        switch (message.Kind)
        {
            case WakuEventKind.RendezvousAccept:
                await ProcessRendezvousAcceptAsync(message, cancellationToken);
                return;
            case WakuEventKind.RendezvousDecline:
                ProcessRendezvousDecline(message);
                return;
            case WakuEventKind.RendezvousConfirm:
                await ProcessRendezvousConfirmAsync(message, cancellationToken);
                return;
            case WakuEventKind.RendezvousReady:
                await ProcessRendezvousReadyAsync(message, cancellationToken);
                return;
            case WakuEventKind.ContactUpdate:
                await ProcessContactSyncAsync(message, cancellationToken);
                return;
            case WakuEventKind.Sms:
            case WakuEventKind.CallInvite:
            case WakuEventKind.CallRinging:
            case WakuEventKind.CallAccept:
            case WakuEventKind.CallReject:
            case WakuEventKind.CallHangup:
            case WakuEventKind.CallFailed:
            case WakuEventKind.CallConnected:
            case WakuEventKind.SdpOffer:
            case WakuEventKind.SdpAnswer:
            case WakuEventKind.IceCandidate:
                await ProcessRoutedMessageAsync(message, cancellationToken);
                return;
            default:
                return;
        }
    }

    private async ValueTask ProcessRendezvousReadyAsync(
        WakuApplicationMessage message,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (!RendezvousPayloadCodec.TryDecodeCardResponse(
                message.Payload.Span,
                out RendezvousCardResponsePayload? response) || response is null)
        {
            LogDiagnostic("rendezvous ready dropped: outer payload decode failed");
            return;
        }
        if (!pendingOutbound.TryGetValue(response.RendezvousId, out PendingOutboundRoute? pending))
        {
            LogDiagnostic($"rendezvous ready dropped: no pending outbound route for id={response.RendezvousId}");
            return;
        }
        if (pending.Request.Kind != NetworkRequestKind.Call || pending.RouteReady)
        {
            LogDiagnostic(
                $"rendezvous ready dropped: kind={pending.Request.Kind} routeReady={pending.RouteReady}");
            return;
        }
        if (!TryValidateEncodedContactCard(
                response.ContactCard.AsSpan(),
                now,
                message.SenderIdentityPublicKey.Span,
                out WakuProfileContact? contact) ||
            contact is null)
        {
            LogDiagnostic("rendezvous ready dropped: sender contact card failed validation");
            return;
        }
        if (!string.Equals(
                contact.CurrentNumber,
                pending.Request.NormalizedDestination,
                StringComparison.Ordinal))
        {
            LogDiagnostic(
                $"rendezvous ready dropped: contact number={contact.CurrentNumber} != " +
                $"destination={pending.Request.NormalizedDestination}");
            return;
        }
        LogDiagnostic($"rendezvous ready accepted: from={contact.CurrentNumber}, publishing CallInvite");

        RememberTemporaryContact(contact, pending.ExpiresAt);
        ActiveCall call = ActiveCall.CreateOutgoing(
            pending.Request.RequestId,
            contact,
            now + options.CallMediaSetupTimeout);
        activeCalls[pending.Request.RequestId] = call;
        pending.RouteReady = true;
        bool routed = await PublishRoutedRequestAsync(pending.Request, contact, cancellationToken);
        LogDiagnostic($"CallInvite publish routed={routed}");
        if (!routed)
        {
            pendingOutbound.Remove(response.RendezvousId);
            EndActiveCall(pending.Request.RequestId, terminateNetworkCall: false);
            ResolveNetworkRequest(pending, NetworkRequestDecision.Reject);
            return;
        }

        EnqueueCommand(WakuPhoneCommand.BeginMedia(pending.Request.RequestId, isCaller: true));
        ResolveNetworkRequest(pending, NetworkRequestDecision.Accept);
    }

    private async ValueTask ProcessRendezvousAcceptAsync(
        WakuApplicationMessage message,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (!RendezvousPayloadCodec.TryDecodeCardResponse(
            message.Payload.Span,
            out RendezvousCardResponsePayload? response) || response is null ||
            !pendingOutbound.TryGetValue(response.RendezvousId, out PendingOutboundRoute? pending) ||
            !TryValidateEncodedContactCard(
                response.ContactCard.AsSpan(),
                now,
                message.SenderIdentityPublicKey.Span,
                out WakuProfileContact? contact) ||
            contact is null ||
            !string.Equals(
                contact.CurrentNumber,
                pending.Request.NormalizedDestination,
                StringComparison.Ordinal))
        {
            return;
        }
        pendingOutbound.Remove(response.RendezvousId);

        bool confirmed = await PublishEnvelopeAsync(
            WakuEventKind.RendezvousConfirm,
            contact,
            RendezvousPayloadCodec.EncodeCorrelation(response.RendezvousId),
            pending.ExpiresAt,
            cancellationToken);
        if (!confirmed)
        {
            ResolveNetworkRequest(pending, NetworkRequestDecision.Reject);
            return;
        }

        if (pending.Request.Kind == NetworkRequestKind.Call)
            RememberTemporaryContact(contact, pending.ExpiresAt);
        else
            await profiles.UpsertContactAsync(contact, pending.Request.NormalizedDestination, cancellationToken);
        QueueContactCardIfMissing(contact);
        if (pending.RouteReady &&
            activeCalls.TryGetValue(pending.Request.RequestId, out ActiveCall? active) &&
            string.Equals(active.StableContactId, contact.StableContactId, StringComparison.Ordinal))
        {
            return;
        }
        bool routed = await PublishRoutedRequestAsync(pending.Request, contact, cancellationToken);
        if (routed && pending.Request.Kind == NetworkRequestKind.Call)
        {
            ActiveCall call = ActiveCall.CreateOutgoing(
                pending.Request.RequestId,
                contact,
                now + options.CallMediaSetupTimeout);
            call.MarkRemoteAnswered();
            activeCalls[pending.Request.RequestId] = call;
            EnqueueCommand(WakuPhoneCommand.BeginMedia(pending.Request.RequestId, isCaller: true));
        }
        ResolveNetworkRequest(
            pending,
            routed ? NetworkRequestDecision.Accept : NetworkRequestDecision.Reject);
    }

    private void ProcessRendezvousDecline(WakuApplicationMessage message)
    {
        if (!RendezvousPayloadCodec.TryDecodeCorrelation(message.Payload.Span, out Guid rendezvousId) ||
            !pendingOutbound.Remove(rendezvousId, out PendingOutboundRoute? pending))
        {
            return;
        }
        ResolveNetworkRequest(pending, NetworkRequestDecision.Reject);
    }

    private async ValueTask ProcessRendezvousConfirmAsync(
        WakuApplicationMessage message,
        CancellationToken cancellationToken)
    {
        if (!RendezvousPayloadCodec.TryDecodeCorrelation(message.Payload.Span, out Guid rendezvousId) ||
            !pendingInbound.TryGetValue(rendezvousId, out PendingInboundRoute? pending) ||
            !pending.AcceptSent ||
            !pending.Contact.MatchesEnvelopeKey(message.SenderIdentityPublicKey.Span))
        {
            return;
        }
        pending.Confirmed = true;
        bool durableConsent = pending.RouteKind != RendezvousRouteKind.Call ||
            phonebook.ContainsNumber(pending.Contact.CurrentNumber);
        if (durableConsent)
        {
            await profiles.UpsertContactAsync(
                pending.Contact,
                pending.Contact.CurrentNumber,
                cancellationToken);
        }
        else
        {
            RememberTemporaryContact(pending.Contact, pending.ExpiresAt);
        }
        if (pending.RouteKind != RendezvousRouteKind.Call)
            pendingInbound.Remove(rendezvousId);
        if (durableConsent)
            await PublishContactSyncOfferAsync(pending.Contact, cancellationToken);
        if (pending.DeferredMessage is { } deferred)
        {
            pending.DeferredMessage = null;
            await ProcessRoutedMessageAsync(deferred, cancellationToken);
        }
    }

    private async ValueTask<bool> PublishContactSyncOfferAsync(
        WakuProfileContact contact,
        CancellationToken cancellationToken)
    {
        foreach (Guid existingId in pendingContactSync
                     .Where(pair => string.Equals(
                         pair.Value.StableContactId,
                         contact.StableContactId,
                         StringComparison.Ordinal))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            pendingContactSync.Remove(existingId);
        }
        if (pendingContactSync.Count >= options.MaximumPendingRoutes)
            return false;

        Guid transactionId = Guid.NewGuid();
        DateTimeOffset expiresAt = timeProvider.GetUtcNow() + options.PairingLifetime;
        pendingContactSync[transactionId] = new PendingContactSync(contact.StableContactId, expiresAt);
        bool sent = await PublishContactSyncAsync(
            contact,
            transactionId,
            ContactSyncKind.Offer,
            expiresAt,
            cancellationToken);
        if (!sent)
            pendingContactSync.Remove(transactionId);
        return sent;
    }

    private void ScheduleContactSyncOffer(WakuProfileContact contact)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        deferredContactSyncOffers[contact.StableContactId] = new DeferredContactSyncOffer(
            contact,
            now + ContactSyncCoalesceDelay,
            now + options.PairingLifetime);
    }

    private async ValueTask FlushDeferredContactSyncOffersAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (string stableContactId in deferredContactSyncOffers.Keys.ToArray())
        {
            DeferredContactSyncOffer offer = deferredContactSyncOffers[stableContactId];
            if (offer.ExpiresAt <= now)
            {
                deferredContactSyncOffers.Remove(stableContactId);
                continue;
            }
            if (offer.NotBefore > now)
                continue;

            ActiveCall? outgoingCall = activeCalls.Values.FirstOrDefault(value =>
                !value.Incoming &&
                string.Equals(value.StableContactId, stableContactId, StringComparison.Ordinal));
            if (outgoingCall is not null && !outgoingCall.RemoteAnswered)
            {
                offer.NotBefore = now + ContactSyncCoalesceDelay;
                continue;
            }

            deferredContactSyncOffers.Remove(stableContactId);
            await PublishContactSyncOfferAsync(offer.Contact, cancellationToken);
        }
    }

    private ValueTask<bool> PublishContactSyncAsync(
        WakuProfileContact contact,
        Guid transactionId,
        ContactSyncKind kind,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        byte[] ownCard = CreateOwnEncodedContactCard(
            timeProvider.GetUtcNow(),
            expiresAt);
        byte[] payload = ContactSyncPayloadCodec.Encode(
            transactionId,
            kind,
            ownCard);
        return PublishEnvelopeAsync(
            WakuEventKind.ContactUpdate,
            contact,
            payload,
            expiresAt,
            cancellationToken);
    }

    private async ValueTask ProcessContactSyncAsync(
        WakuApplicationMessage message,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (!ContactSyncPayloadCodec.TryDecode(message.Payload.Span, out ContactSyncPayload? sync) ||
            sync is null)
        {
            return;
        }

        WakuProfileContact? known = profiles.Profile.FindContactByEnvelopeKey(
            message.SenderIdentityPublicKey.Span);
        if (known is null ||
            !TryValidateEncodedContactCard(
                sync.ContactCard.AsSpan(),
                now,
                message.SenderIdentityPublicKey.Span,
                out WakuProfileContact? updated) ||
            updated is null ||
            !string.Equals(updated.StableContactId, known.StableContactId, StringComparison.Ordinal))
        {
            return;
        }

        if (sync.Kind == ContactSyncKind.Acknowledge)
        {
            if (!pendingContactSync.TryGetValue(sync.TransactionId, out PendingContactSync? pending) ||
                !string.Equals(pending.StableContactId, known.StableContactId, StringComparison.Ordinal))
            {
                return;
            }
            pendingContactSync.Remove(sync.TransactionId);
        }

        await profiles.UpsertContactAsync(updated, updated.CurrentNumber, cancellationToken);
        QueueContactCardIfMissing(updated);

        if (sync.Kind == ContactSyncKind.Offer)
        {
            await PublishContactSyncAsync(
                updated,
                sync.TransactionId,
                ContactSyncKind.Acknowledge,
                DateTimeOffset.FromUnixTimeMilliseconds(message.ExpiresAtUnixMilliseconds),
                cancellationToken);
        }
    }

    private async ValueTask ProcessRoutedMessageAsync(
        WakuApplicationMessage message,
        CancellationToken cancellationToken)
    {
        WakuProfileContact? durableContact = profiles.Profile.FindContactByEnvelopeKey(
            message.SenderIdentityPublicKey.Span);
        PendingInboundRoute? provisional = pendingInbound.Values.FirstOrDefault(value =>
            value.Contact.MatchesEnvelopeKey(message.SenderIdentityPublicKey.Span));
        ActiveCall? temporaryCall = activeCalls.Values.FirstOrDefault(value =>
            value.Contact.MatchesEnvelopeKey(message.SenderIdentityPublicKey.Span));
        if (durableContact is null && provisional is not null && !provisional.Confirmed &&
            provisional.DeferredMessage is null &&
            message.Kind == WakuEventKind.Sms)
        {
            provisional.DeferredMessage = message;
            return;
        }
        WakuProfileContact? contact = durableContact ?? provisional?.Contact ?? temporaryCall?.Contact;
        if (contact is null)
        {
            LogDiagnostic(
                $"routed message dropped: unknown sender kind={message.Kind} " +
                $"durable={durableContact is not null} provisional={provisional is not null} " +
                $"temporaryCall={temporaryCall is not null}");
            return;
        }
        LogDiagnostic($"routed message received: kind={message.Kind} from={contact.CurrentNumber}");
        if (message.Kind == WakuEventKind.Sms)
        {
            if (durableContact is null)
                return;
            QueueContactCardIfMissing(contact);
            string? localNumber = profiles.Profile.FindLocalNumberForStableId(contact.StableContactId);
            if (localNumber is null)
                return;
            if (WakuSmsPayloadCodec.TryDecode(message.Payload.Span, out string text))
            {
                EnqueueCommand(WakuPhoneCommand.IncomingSms(
                    localNumber,
                    text,
                    message.IssuedAtUnixMilliseconds));
            }
            return;
        }

        if (!WakuCallSignalCodec.TryDecode(message.Payload.Span, out WakuCallSignalFragment? fragment) ||
            fragment is null)
        {
            return;
        }
        if (message.Kind == WakuEventKind.CallInvite)
        {
            if (fragment.ChunkCount != 1 || !fragment.Data.IsEmpty)
                return;
            if (activeCalls.Count != 0)
            {
                await PublishCallControlAsync(
                    fragment.AttemptId,
                    contact,
                    WakuEventKind.CallReject,
                    cancellationToken);
                return;
            }
            string? localNumber = profiles.Profile.FindLocalNumberForStableId(contact.StableContactId) ??
                (provisional?.RouteKind == RendezvousRouteKind.Call
                    ? provisional.Contact.CurrentNumber
                    : null);
            if (localNumber is null)
            {
                LogDiagnostic(
                    $"CallInvite dropped: no local number for contact, provisional={provisional is not null} " +
                    $"provisionalRouteKind={provisional?.RouteKind}");
                return;
            }
            LogDiagnostic($"CallInvite accepted from={contact.CurrentNumber}, ringing");
            activeCalls[fragment.AttemptId] = ActiveCall.CreateIncoming(
                fragment.AttemptId,
                fragment.SignalId,
                contact,
                localNumber,
                timeProvider.GetUtcNow() + options.CallMediaSetupTimeout);
            EnqueueCommand(WakuPhoneCommand.BeginMedia(fragment.AttemptId, isCaller: false));
            if (earlyCallMessages.Remove(
                    fragment.AttemptId,
                    out List<WakuApplicationMessage>? earlyMessages))
            {
                foreach (WakuApplicationMessage earlyMessage in earlyMessages)
                    await ProcessRoutedMessageAsync(earlyMessage, cancellationToken);
            }
            return;
        }

        if (!activeCalls.TryGetValue(fragment.AttemptId, out ActiveCall? call))
        {
            BufferEarlyCallMessage(fragment.AttemptId, message);
            return;
        }
        if (!string.Equals(call.StableContactId, contact.StableContactId, StringComparison.Ordinal))
            return;

        if (message.Kind is
            WakuEventKind.CallRinging or
            WakuEventKind.CallAccept or
            WakuEventKind.CallReject or
            WakuEventKind.CallHangup or
            WakuEventKind.CallFailed or
            WakuEventKind.CallConnected)
        {
            if (fragment.ChunkCount != 1 || !fragment.Data.IsEmpty)
            {
                return;
            }

            WakuCallSignalResult signalResult = call.Session.ApplyRemote(new WakuCallSignal(
                fragment.SignalId,
                fragment.AttemptId,
                message.Kind));
            if (signalResult != WakuCallSignalResult.Applied)
            {
                // A fresh retry of an already-applied consent packet asks the
                // caller to repeat its firmware CONNECT acknowledgement. This
                // recovers when Waku delivered consent but lost every copy of
                // the acknowledgement, without reconnecting GSM twice.
                if (message.Kind == WakuEventKind.CallAccept &&
                    !call.Incoming &&
                    call.RemoteFirmwareAnswered &&
                    call.LocalFirmwareConnected)
                {
                    await PublishCallControlAsync(
                        fragment.AttemptId,
                        contact,
                        WakuEventKind.CallConnected,
                        cancellationToken);
                }
                return;
            }

            if (message.Kind == WakuEventKind.CallAccept)
            {
                call.MarkRemoteAnswered();
                call.MarkRemoteFirmwareAnswered();
                QueueContactCardIfMissing(contact);
                TryConnectOutgoingFirmware(fragment.AttemptId, call);
            }
            else if (message.Kind == WakuEventKind.CallConnected)
            {
                if (call.Incoming)
                    call.MarkRemoteFirmwareConnected();
            }
            else if (message.Kind is
                WakuEventKind.CallReject or
                WakuEventKind.CallHangup or
                WakuEventKind.CallFailed)
            {
                EndActiveCall(fragment.AttemptId, terminateNetworkCall: true);
            }
            return;
        }

        bool isMediaSignal = message.Kind is
            WakuEventKind.SdpOffer or
            WakuEventKind.SdpAnswer or
            WakuEventKind.IceCandidate;
        if (!isMediaSignal ||
            !call.TryAcceptFragment(message.Kind, fragment, out byte[]? signal) ||
            signal is null ||
            call.Session.ApplyRemote(new WakuCallSignal(
                fragment.SignalId,
                fragment.AttemptId,
                message.Kind)) != WakuCallSignalResult.Applied)
        {
            return;
        }

        EnqueueCommand(WakuPhoneCommand.ApplyMediaSignal(
            fragment.AttemptId,
            message.Kind,
            signal));
    }

    private void TryConnectOutgoingFirmware(Guid attemptId, ActiveCall call)
    {
        if (call.Incoming ||
            call.LocalFirmwareConnected ||
            !call.RemoteFirmwareAnswered ||
            !call.Session.IsWebRtcConnected)
        {
            return;
        }

        // Dct3Machine/GsmNetwork treats CONNECT idempotently. Reissuing this
        // command on a later consent retry is intentional until the firmware
        // confirms it consumed CONNECT through CallTransitionKind.Connect.
        EnqueueCommand(WakuPhoneCommand.ConnectCall(attemptId));
    }

    private ValueTask<bool> PublishCallControlAsync(
        Guid attemptId,
        WakuProfileContact contact,
        WakuEventKind kind,
        CancellationToken cancellationToken,
        bool scheduleRealtimeRepeats = true)
    {
        byte[] payload = WakuCallSignalCodec.EncodeFragments(attemptId, Guid.NewGuid(), [])[0];
        return PublishEnvelopeAsync(
            kind,
            contact,
            payload,
            timeProvider.GetUtcNow() + options.PairingLifetime,
            cancellationToken,
            scheduleRealtimeRepeats);
    }

    private async ValueTask FailActiveCallAsync(
        Guid attemptId,
        ActiveCall call,
        WakuProfileContact contact,
        CancellationToken cancellationToken)
    {
        if (!call.Session.FailLocally(attemptId))
            return;
        await PublishCallControlAsync(
            attemptId,
            contact,
            WakuEventKind.CallFailed,
            cancellationToken);
        EndActiveCall(attemptId, terminateNetworkCall: true);
    }

    private void EndActiveCall(Guid attemptId, bool terminateNetworkCall)
    {
        if (!activeCalls.Remove(attemptId, out ActiveCall? call))
            return;
        pendingOutbound.Remove(attemptId);
        pendingInbound.Remove(attemptId);
        earlyCallMessages.Remove(attemptId);
        if (!call.Incoming && !call.RemoteAnswered)
            deferredContactSyncOffers.Remove(call.StableContactId);
        EnqueueCommand(WakuPhoneCommand.EndMedia(attemptId));
        if (terminateNetworkCall)
            EnqueueCommand(WakuPhoneCommand.TerminateCall(attemptId));
    }

    private void BufferEarlyCallMessage(Guid attemptId, WakuApplicationMessage message)
    {
        const int maximumBufferedAttempts = 16;
        const int maximumMessagesPerAttempt = 32;
        if (!earlyCallMessages.TryGetValue(attemptId, out List<WakuApplicationMessage>? buffered))
        {
            if (earlyCallMessages.Count >= maximumBufferedAttempts)
                return;
            buffered = [];
            earlyCallMessages.Add(attemptId, buffered);
        }
        if (buffered.Count < maximumMessagesPerAttempt)
            buffered.Add(message);
    }

    private async ValueTask<bool> PublishRoutedRequestAsync(
        OutgoingNetworkRequest request,
        WakuProfileContact contact,
        CancellationToken cancellationToken)
    {
        DateTimeOffset expiresAt;
        byte[] payload;
        WakuEventKind kind;
        if (request.Kind == NetworkRequestKind.Sms)
        {
            kind = WakuEventKind.Sms;
            expiresAt = timeProvider.GetUtcNow() + options.SmsLifetime;
            payload = WakuSmsPayloadCodec.Encode(request.SmsText);
        }
        else
        {
            kind = WakuEventKind.CallInvite;
            expiresAt = timeProvider.GetUtcNow() + options.PairingLifetime;
            payload = WakuCallSignalCodec.EncodeFragments(request.RequestId, Guid.NewGuid(), [])[0];
        }
        return await PublishEnvelopeAsync(
            kind,
            contact,
            payload,
            expiresAt,
            cancellationToken);
    }

    private async ValueTask<bool> PublishEnvelopeAsync(
        WakuEventKind kind,
        WakuProfileContact recipient,
        ReadOnlyMemory<byte> payload,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken,
        bool scheduleRealtimeRepeats = true)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (expiresAt <= now)
            return false;
        WakuProfile profile = profiles.Profile;
        byte[] senderRoutingKey = PostQuantumRendezvousEnabled
            ? PqcContactCardCodec.CreateEnvelopeRoutingKey(
                profile.GetPqcRendezvousIdentity().SigningPublicKey)
            : profile.Keys.EnvelopePublicKey.ToArray();
        WakuApplicationMessage message;
        try
        {
            message = new WakuApplicationMessage(
                Guid.NewGuid(),
                kind,
                now.ToUnixTimeMilliseconds(),
                expiresAt.ToUnixTimeMilliseconds(),
                senderRoutingKey,
                recipient.MailboxPublicKey.AsSpan(),
                payload.Span);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(senderRoutingKey);
        }
        byte[] encrypted;
        if (PostQuantumRendezvousEnabled)
        {
            if (!recipient.HasPqcIdentity)
                return false;
            encrypted = PqcWakuEnvelopeCodec.Encrypt(
                message,
                profile.GetPqcRendezvousIdentity(),
                recipient.PqcMailboxPublicKey.AsSpan());
        }
        else
        {
            encrypted = WakuEnvelopeCodec.Encrypt(message, profile.Keys.EnvelopePrivateKey.Span);
        }
        WakuPublishRequest request = WakuPublishRequestFactory.Create(message, encrypted, now);
        bool published = await TryPublishAsync(request, cancellationToken);
        if (published && scheduleRealtimeRepeats && ShouldRepeatRealtimeEnvelope(kind))
            ScheduleRealtimeRepeats(request);
        return published;
    }

    private void ScheduleRealtimeRepeats(WakuPublishRequest request)
    {
        long repeatId = Interlocked.Increment(ref nextRealtimeRepeatId);
        realtimeRepeatTasks.TryAdd(
            repeatId,
            RepeatRealtimeAsync(repeatId, request, cancellation.Token));
    }

    private async Task RepeatRealtimeAsync(
        long repeatId,
        WakuPublishRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            for (int attempt = 1; attempt < RealtimePublishAttempts; attempt++)
            {
                await Task.Delay(RealtimeRepeatDelay * attempt, timeProvider, cancellationToken);
                await transport.PublishAsync(request, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
        }
        finally
        {
            realtimeRepeatTasks.TryRemove(repeatId, out _);
        }
    }

    private static bool ShouldRepeatRealtimeEnvelope(WakuEventKind kind) => kind is
        WakuEventKind.RendezvousAccept or
        WakuEventKind.RendezvousDecline or
        WakuEventKind.RendezvousConfirm or
        WakuEventKind.RendezvousReady or
        WakuEventKind.CallInvite or
        WakuEventKind.CallRinging or
        WakuEventKind.CallAccept or
        WakuEventKind.CallReject or
        WakuEventKind.CallHangup or
        WakuEventKind.CallFailed or
        WakuEventKind.CallConnected or
        WakuEventKind.SdpOffer or
        WakuEventKind.SdpAnswer;

    private async ValueTask<bool> TryPublishAsync(
        WakuPublishRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            WakuPublishResult result = await transport.PublishAsync(request, cancellationToken);
            if (result.AcceptedByServicePeer)
            {
                SetStatus(WakuPhoneBridgeStatus.Online);
                return true;
            }
            LogDiagnostic($"publish not accepted by any service peer topic={request.ContentTopic}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogDiagnostic($"publish failed topic={request.ContentTopic}: {exception.Message}");
        }
        SetStatus(WakuPhoneBridgeStatus.Offline);
        return false;
    }

    private void ExpirePendingRoutes(DateTimeOffset now)
    {
        long nowMilliseconds = now.ToUnixTimeMilliseconds();
        foreach (string key in ownPqcDescriptors
                     .Where(pair => pair.Value.ExpiresAtUnixMilliseconds <= nowMilliseconds)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            ownPqcDescriptors.Remove(key);
        }
        foreach (string temporaryId in pqcDescriptors
                     .Where(pair => pair.Value.ExpiresAtUnixMilliseconds <= nowMilliseconds)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            pqcDescriptors.Remove(temporaryId);
        }
        foreach (Guid id in deferredPqcOutbound
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            DeferredPqcOutboundRoute deferred = deferredPqcOutbound[id];
            deferredPqcOutbound.Remove(id);
            ResolveNetworkRequest(deferred, NetworkRequestDecision.Timeout);
        }
        foreach (Guid id in pendingOutbound
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            PendingOutboundRoute pending = pendingOutbound[id];
            pendingOutbound.Remove(id);
            if (!pending.RouteReady)
            {
                ResolveNetworkRequest(pending, NetworkRequestDecision.Timeout);
            }
        }
        foreach (Guid id in pendingInbound
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            PendingInboundRoute pending = pendingInbound[id];
            pendingInbound.Remove(id);
            if (pending.RouteKind == RendezvousRouteKind.Call && !pending.CallEnded)
                EnqueueCommand(WakuPhoneCommand.TerminateCall(id));
        }
        foreach (Guid id in pendingContactSync
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            pendingContactSync.Remove(id);
        }
        foreach (string stableContactId in deferredContactSyncOffers
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            deferredContactSyncOffers.Remove(stableContactId);
        }
        foreach (string stableContactId in temporaryContacts
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            temporaryContacts.Remove(stableContactId);
        }
    }

    private async ValueTask ExpireCallMediaSetupsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (Guid attemptId in activeCalls
                     .Where(pair => pair.Value.MediaSetupDeadline is { } deadline && deadline <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            if (activeCalls.TryGetValue(attemptId, out ActiveCall? call))
                await FailActiveCallAsync(attemptId, call, call.Contact, cancellationToken);
        }
    }

    private async ValueTask MaintainCallSynchronizationAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (Guid attemptId in activeCalls
                     .Where(pair => pair.Value.FirmwareSynchronizationDeadline is not null)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            if (!activeCalls.TryGetValue(attemptId, out ActiveCall? call) ||
                call.FirmwareSynchronizationDeadline is not { } deadline)
            {
                continue;
            }

            if (deadline <= now)
            {
                await FailActiveCallAsync(attemptId, call, call.Contact, cancellationToken);
                continue;
            }

            if (call.NextCallAcceptRetryAt is not { } retryAt || retryAt > now)
                continue;

            // Use a fresh event for this probe so a caller that already
            // applied the original consent can answer again with
            // CallConnected. Each individual event remains idempotent.
            await PublishCallControlAsync(
                attemptId,
                call.Contact,
                WakuEventKind.CallAccept,
                cancellationToken,
                scheduleRealtimeRepeats: false);
            call.ScheduleCallAcceptRetry(now + CallAcceptRetryDelay);
        }
    }

    private void ScheduleDeadlineTimer()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        long nextEpoch = checked(WakuTopicProfile.GetEpoch(now) + 1);
        DateTimeOffset topicRollover = DateTimeOffset.FromUnixTimeMilliseconds(
            checked(nextEpoch * WakuTopicProfile.EpochDurationMilliseconds));
        DateTimeOffset? routeDeadline = pendingOutbound.Values.Select(value => (DateTimeOffset?)value.ExpiresAt)
            .Concat(deferredPqcOutbound.Values.Select(value =>
                (DateTimeOffset?)(value.NextAttemptAt < value.ExpiresAt
                    ? value.NextAttemptAt
                    : value.ExpiresAt)))
            .Concat(pendingInbound.Values.Select(value => (DateTimeOffset?)value.ExpiresAt))
            .Concat(pendingContactSync.Values.Select(value => (DateTimeOffset?)value.ExpiresAt))
            .Concat(deferredContactSyncOffers.Values.Select(value =>
                (DateTimeOffset?)(value.NotBefore < value.ExpiresAt ? value.NotBefore : value.ExpiresAt)))
            .Concat(temporaryContacts.Values.Select(value => (DateTimeOffset?)value.ExpiresAt))
            .Concat(activeCalls.Values.Select(value => value.MediaSetupDeadline))
            .Concat(activeCalls.Values.Select(value => value.NextCallAcceptRetryAt))
            .Concat(activeCalls.Values.Select(value => value.FirmwareSynchronizationDeadline))
            .Min();
        DateTimeOffset deadline = routeDeadline is not null && routeDeadline < topicRollover
            ? routeDeadline.Value
            : topicRollover;
        TimeSpan delay = deadline - now;
        deadlineTimer.Change(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private void EnqueueCommand(WakuPhoneCommand command)
    {
        commands.Enqueue(command);
        while (commands.Count > options.MaximumQueuedCommands)
            commands.TryDequeue(out _);
        CommandAvailable?.Invoke(this);
    }

    private void SetStatus(WakuPhoneBridgeStatus value)
    {
        if (status == value)
            return;
        status = value;
        StatusChanged?.Invoke(this);
    }

    private static bool HasUsableMailboxKey(ReadOnlySpan<byte> publicKey)
    {
        Span<byte> privateKey = stackalloc byte[WakuCrypto.X25519KeySize];
        Span<byte> sharedSecret = stackalloc byte[WakuCrypto.X25519KeySize];
        try
        {
            WakuCrypto.GenerateX25519PrivateKey(privateKey);
            return WakuCrypto.TryX25519Agreement(privateKey, publicKey, sharedSecret);
        }
        catch (ArgumentException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    private byte[] CreateOwnEncodedContactCard(
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        WakuProfile profile = profiles.Profile;
        if (PostQuantumRendezvousEnabled)
        {
            return PqcContactCardCodec.Encode(PqcContactCardCodec.CreateSigned(
                profile.GetPqcRendezvousIdentity(),
                profile.UserName,
                profile.PhoneNumber,
                issuedAt,
                expiresAt));
        }

        return ContactCardV2Codec.Encode(ContactCardV2Codec.CreateSigned(
            profile.Keys,
            profile.UserName,
            profile.PhoneNumber,
            issuedAt,
            expiresAt));
    }

    private bool TryValidateEncodedContactCard(
        ReadOnlySpan<byte> encoded,
        DateTimeOffset now,
        ReadOnlySpan<byte> expectedEnvelopeRoutingKey,
        out WakuProfileContact? contact)
    {
        contact = null;
        if (PostQuantumRendezvousEnabled)
        {
            if (!PqcContactCardCodec.TryValidate(
                    encoded,
                    now,
                    out PqcContactCard? pqcCard,
                    expectedEnvelopeRoutingKey) ||
                pqcCard is null)
            {
                return false;
            }
            contact = WakuProfileContact.FromValidatedPqcCard(pqcCard);
            return true;
        }

        if (!ContactCardV2Codec.TryValidate(
                encoded,
                now,
                WakuProfileKeys.KeyGeneration,
                out ContactCardV2? classicCard,
                out _,
                expectedEnvelopePublicKey: expectedEnvelopeRoutingKey) ||
            classicCard is null ||
            !HasUsableMailboxKey(classicCard.MailboxPublicKey.Span))
        {
            return false;
        }
        contact = WakuProfileContact.FromValidatedCard(classicCard);
        return true;
    }

    private static async ValueTask AwaitStoppedAsync(Task? task)
    {
        if (task is null)
            return;
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private enum BridgeWorkKind
    {
        OutgoingRequest,
        SimMutation,
        CallTransition,
        CallMediaEvent,
        TransportMessage,
        Deadline,
        ProfileChanged,
        TransportAvailability,
    }

    private sealed record BridgeWork(
        BridgeWorkKind Kind,
        OutgoingNetworkRequest? NetworkRequest = null,
        SimMutation? SimMutation = null,
        CallTransition? CallTransition = null,
        WakuCallMediaEvent? CallMediaEvent = null,
        WakuTransportMessage TransportMessage = default,
        bool TransportAvailable = false)
    {
        public static BridgeWork Outgoing(OutgoingNetworkRequest value) =>
            new(BridgeWorkKind.OutgoingRequest, NetworkRequest: value);

        public static BridgeWork Sim(SimMutation value) =>
            new(BridgeWorkKind.SimMutation, SimMutation: value);

        public static BridgeWork Call(CallTransition value) =>
            new(BridgeWorkKind.CallTransition, CallTransition: value);

        public static BridgeWork Media(WakuCallMediaEvent value) =>
            new(BridgeWorkKind.CallMediaEvent, CallMediaEvent: value);

        public static BridgeWork Transport(WakuTransportMessage value) =>
            new(BridgeWorkKind.TransportMessage, TransportMessage: value);

        public static BridgeWork Deadline() => new(BridgeWorkKind.Deadline);

        public static BridgeWork Profile() => new(BridgeWorkKind.ProfileChanged);

        public static BridgeWork Availability(bool available) =>
            new(BridgeWorkKind.TransportAvailability, TransportAvailable: available);
    }

    private void AcceptQueuedSms(PendingOutboundRoute pending)
    {
        // GSM RP-ACK acknowledges that the asynchronous relay accepted the submission.
        // It is not a peer-delivery receipt. A wait for pairing leaves the handset SMS transaction open for hours.
        if (pending.Request.Kind == NetworkRequestKind.Sms)
            ResolveNetworkRequest(pending, NetworkRequestDecision.Accept);
    }

    private void AcceptQueuedSms(DeferredPqcOutboundRoute deferred)
    {
        if (deferred.Request.Kind == NetworkRequestKind.Sms)
            ResolveNetworkRequest(deferred, NetworkRequestDecision.Accept);
    }

    private void ResolveNetworkRequest(
        PendingOutboundRoute pending,
        NetworkRequestDecision decision)
    {
        if (pending.NetworkRequestResolved)
            return;
        pending.NetworkRequestResolved = true;
        EnqueueCommand(WakuPhoneCommand.Resolve(pending.Request.RequestId, decision));
    }

    private void ResolveNetworkRequest(
        DeferredPqcOutboundRoute deferred,
        NetworkRequestDecision decision)
    {
        if (deferred.NetworkRequestResolved)
            return;
        deferred.NetworkRequestResolved = true;
        EnqueueCommand(WakuPhoneCommand.Resolve(deferred.Request.RequestId, decision));
    }

    private sealed class PendingOutboundRoute
    {
        public PendingOutboundRoute(
            OutgoingNetworkRequest request,
            DateTimeOffset expiresAt,
            bool networkRequestResolved = false)
        {
            Request = request;
            ExpiresAt = expiresAt;
            NetworkRequestResolved = networkRequestResolved;
        }

        public OutgoingNetworkRequest Request { get; }

        public DateTimeOffset ExpiresAt { get; }

        public bool NetworkRequestResolved { get; set; }

        public bool RouteReady { get; set; }
    }

    private sealed class DeferredPqcOutboundRoute
    {
        public DeferredPqcOutboundRoute(
            Guid rendezvousId,
            OutgoingNetworkRequest request,
            ReadOnlySpan<byte> payload,
            DateTimeOffset expiresAt,
            DateTimeOffset nextAttemptAt)
        {
            RendezvousId = rendezvousId;
            Request = request;
            Payload = payload.ToArray();
            ExpiresAt = expiresAt;
            NextAttemptAt = nextAttemptAt;
        }

        public Guid RendezvousId { get; }

        public OutgoingNetworkRequest Request { get; }

        public byte[] Payload { get; }

        public DateTimeOffset ExpiresAt { get; }

        public bool NetworkRequestResolved { get; set; }

        public DateTimeOffset NextAttemptAt { get; private set; }

        public void ScheduleRetry(DateTimeOffset nextAttemptAt) =>
            NextAttemptAt = nextAttemptAt;
    }

    private sealed record PendingContactSync(string StableContactId, DateTimeOffset ExpiresAt);

    private sealed class PqcDescriptorAssembly
    {
        private readonly Dictionary<int, PqcRendezvousDescriptorChunk> chunks = [];
        private readonly int count;

        public PqcDescriptorAssembly(int count) => this.count = count;

        public bool IsComplete => chunks.Count == count;

        public IEnumerable<PqcRendezvousDescriptorChunk> Chunks => chunks.Values;

        public bool TryAdd(PqcRendezvousDescriptorChunk chunk) =>
            chunk.Count == count && chunks.TryAdd(chunk.Index, chunk);
    }

    private sealed class DeferredContactSyncOffer
    {
        public DeferredContactSyncOffer(
            WakuProfileContact contact,
            DateTimeOffset notBefore,
            DateTimeOffset expiresAt)
        {
            Contact = contact;
            NotBefore = notBefore;
            ExpiresAt = expiresAt;
        }

        public WakuProfileContact Contact { get; }

        public DateTimeOffset NotBefore { get; set; }

        public DateTimeOffset ExpiresAt { get; }
    }

    private sealed record TemporaryContact(WakuProfileContact Contact, DateTimeOffset ExpiresAt);

    private sealed class PendingInboundRoute
    {
        public PendingInboundRoute(
            Guid rendezvousId,
            RendezvousRouteKind routeKind,
            WakuProfileContact contact,
            DateTimeOffset expiresAt)
        {
            RendezvousId = rendezvousId;
            RouteKind = routeKind;
            Contact = contact;
            ExpiresAt = expiresAt;
        }

        public Guid RendezvousId { get; }

        public RendezvousRouteKind RouteKind { get; }

        public WakuProfileContact Contact { get; }

        public DateTimeOffset ExpiresAt { get; }

        public bool AcceptSent { get; set; }

        public bool CallAnswered { get; set; }

        public bool CallEnded { get; set; }

        public bool Confirmed { get; set; }

        public WakuApplicationMessage? DeferredMessage { get; set; }
    }

    private sealed class ActiveCall
    {
        private const int MaximumPendingSignals = 8;
        private readonly Dictionary<(WakuEventKind Kind, Guid SignalId), PendingSignal> pendingSignals = [];

        private ActiveCall(
            WakuProfileContact contact,
            bool incoming,
            string incomingAddress,
            DateTimeOffset mediaSetupDeadline)
        {
            Contact = contact;
            Incoming = incoming;
            IncomingAddress = incomingAddress;
            MediaSetupDeadline = mediaSetupDeadline;
        }

        public WakuProfileContact Contact { get; }

        public string StableContactId => Contact.StableContactId;

        public bool Incoming { get; }

        public string IncomingAddress { get; }

        public DateTimeOffset? MediaSetupDeadline { get; private set; }

        public bool IncomingDisplayed { get; private set; }

        public bool RemoteAnswered { get; private set; }

        public bool RemoteFirmwareAnswered { get; private set; }

        public bool LocalFirmwareConnected { get; private set; }

        public DateTimeOffset? NextCallAcceptRetryAt { get; private set; }

        public DateTimeOffset? FirmwareSynchronizationDeadline { get; private set; }

        public WakuCallSession Session { get; } = new();

        public void MarkRemoteAnswered() => RemoteAnswered = true;

        public void MarkRemoteFirmwareAnswered() => RemoteFirmwareAnswered = true;

        public void MarkMediaReady() => MediaSetupDeadline = null;

        public bool TryMarkLocalFirmwareConnected()
        {
            if (Incoming || LocalFirmwareConnected || !RemoteFirmwareAnswered || !Session.IsWebRtcConnected)
                return false;
            LocalFirmwareConnected = true;
            return true;
        }

        public void BeginRemoteFirmwareSynchronization(DateTimeOffset retryAt, DateTimeOffset deadline)
        {
            if (!Incoming)
                return;
            NextCallAcceptRetryAt = retryAt;
            FirmwareSynchronizationDeadline = deadline;
        }

        public void ScheduleCallAcceptRetry(DateTimeOffset retryAt)
        {
            if (FirmwareSynchronizationDeadline is not null)
                NextCallAcceptRetryAt = retryAt;
        }

        public void MarkRemoteFirmwareConnected()
        {
            NextCallAcceptRetryAt = null;
            FirmwareSynchronizationDeadline = null;
        }

        public bool TryMarkIncomingDisplayed()
        {
            if (!Incoming || IncomingDisplayed)
                return false;
            IncomingDisplayed = true;
            return true;
        }

        public static ActiveCall CreateOutgoing(
            Guid attemptId,
            WakuProfileContact contact,
            DateTimeOffset mediaSetupDeadline)
        {
            ActiveCall call = new(contact, incoming: false, "", mediaSetupDeadline);
            call.Session.BeginOutgoing(attemptId);
            return call;
        }

        public static ActiveCall CreateIncoming(
            Guid attemptId,
            Guid signalId,
            WakuProfileContact contact,
            string incomingAddress,
            DateTimeOffset mediaSetupDeadline)
        {
            ActiveCall call = new(contact, incoming: true, incomingAddress, mediaSetupDeadline);
            if (call.Session.ApplyRemote(new WakuCallSignal(
                    signalId,
                    attemptId,
                    WakuEventKind.CallInvite)) != WakuCallSignalResult.Applied)
            {
                throw new InvalidOperationException("The incoming call session did not accept its invitation.");
            }
            return call;
        }

        public bool TryAcceptFragment(
            WakuEventKind kind,
            WakuCallSignalFragment fragment,
            out byte[]? signal)
        {
            signal = null;
            var key = (kind, fragment.SignalId);
            if (!pendingSignals.TryGetValue(key, out PendingSignal? pending))
            {
                if (pendingSignals.Count >= MaximumPendingSignals)
                    return false;
                pending = new PendingSignal(fragment);
                pendingSignals.Add(key, pending);
            }
            if (!pending.TryAdd(fragment))
                return false;
            if (!pending.IsComplete)
                return true;

            pendingSignals.Remove(key);
            return WakuCallSignalCodec.TryReassemble(pending.Fragments, out signal);
        }
    }

    private sealed class PendingSignal
    {
        private readonly WakuCallSignalFragment first;
        private readonly Dictionary<ushort, WakuCallSignalFragment> fragments = [];

        public PendingSignal(WakuCallSignalFragment first)
        {
            this.first = first;
        }

        public bool IsComplete => fragments.Count == first.ChunkCount;

        public IEnumerable<WakuCallSignalFragment> Fragments => fragments.Values;

        public bool TryAdd(WakuCallSignalFragment fragment)
        {
            if (fragment.AttemptId != first.AttemptId ||
                fragment.SignalId != first.SignalId ||
                fragment.ChunkCount != first.ChunkCount ||
                fragment.TotalLength != first.TotalLength)
            {
                return false;
            }
            return fragments.TryAdd(fragment.ChunkIndex, fragment);
        }
    }
}
