using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Noks.Waku.Transport.Libp2p.Cryptography;
using Noks.Waku.Transport.Libp2p.Discovery;
using Noks.Waku.Transport.Libp2p.Mplex;
using Noks.Waku.Transport.Libp2p.Protocols;
using Noks.Waku.Transport.Protocols;

namespace Noks.Waku.Transport;

public sealed class Libp2pWakuTransport :
    IWakuTransport,
    IWakuTransportAvailability,
    IWakuTransportDiagnostics,
    IAsyncDisposable
{
    private const int MaximumStorePages = 10;

    // These values match the js-waku PeerManager, LightPush, and Filter defaults.
    // Filter uses redundant subscriptions across two peers. LightPush retries across no more than three peers.
    // The pool keeps at least that many active connections.
    private const int TargetPoolSize = 3;
    private const int FilterRedundancy = 2;
    private const int MaxLightPushAttempts = 3;

    private static readonly IReadOnlySet<string> InboundProtocols = new HashSet<string>(
        [
            Libp2pIdentify.Protocol,
            Libp2pIdentify.PushProtocol,
            Libp2pIdentify.PingProtocol,
            WakuMetadata.Protocol,
            WakuProtocolCodec.FilterPush,
        ],
        StringComparer.Ordinal);
    private static readonly IReadOnlyCollection<string> AdvertisedProtocols =
    [
        Libp2pIdentify.PushProtocol,
        Libp2pIdentify.Protocol,
        Libp2pIdentify.PingProtocol,
        WakuMetadata.Protocol,
        WakuProtocolCodec.FilterPush,
    ];

    private readonly SemaphoreSlim connectionLock = new(1, 1);
    private readonly Channel<WakuTransportMessage> messages = Channel.CreateBounded<WakuTransportMessage>(
        new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly Libp2pIdentity identity = Libp2pIdentity.Create();
    private readonly Func<CancellationToken, Task<IReadOnlyList<WakuPeer>>> discover;
    private readonly Func<WakuPeer, Func<MplexStream, CancellationToken, Task>, CancellationToken, Task<ILibp2pConnection>> connect;
    private readonly TimeSpan operationTimeout;
    private readonly TimeSpan filterKeepAliveInterval;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim subscriptionLock = new(1, 1);
    private readonly SemaphoreSlim subscriptionChanged = new(0, 1);
    private readonly ConcurrentDictionary<ILibp2pConnection, byte> filterPeers = new();
    private readonly ConcurrentDictionary<ILibp2pConnection, byte> storePeers = new();
    private readonly object diagnosticsLock = new();
    private readonly List<WakuTransportDiagnosticEvent> recentEvents = [];
    private readonly ConcurrentDictionary<string, PooledPeer> pool = new(StringComparer.Ordinal);
    private WakuTransportDiagnostics diagnostics = WakuTransportDiagnostics.Empty with
    {
        Mode = "public-libp2p",
    };
    private int nextPublishIndex;
    private int disposed;

    public Libp2pWakuTransport()
    {
        discover = new WakuDnsDiscovery().DiscoverAsync;
        connect = async (peer, handler, token) =>
            await Libp2pWebSocketConnection.ConnectAsync(peer, identity, handler, token);
        operationTimeout = TimeSpan.FromSeconds(20);
        filterKeepAliveInterval = TimeSpan.FromMinutes(1);
    }

    internal Libp2pWakuTransport(
        Func<CancellationToken, Task<IReadOnlyList<WakuPeer>>> discover,
        Func<WakuPeer, Func<MplexStream, CancellationToken, Task>, CancellationToken, Task<ILibp2pConnection>> connect,
        TimeSpan? operationTimeout = null,
        TimeSpan? filterKeepAliveInterval = null)
    {
        this.discover = discover;
        this.connect = connect;
        this.operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(20);
        this.filterKeepAliveInterval = filterKeepAliveInterval ?? TimeSpan.FromMinutes(1);
    }

    public event Action<bool>? AvailabilityChanged;

    public event Action<WakuTransportDiagnostics>? DiagnosticsChanged;

    public WakuTransportDiagnostics Diagnostics => Volatile.Read(ref diagnostics);

    public string DiagnosticsReport => System.Text.Json.JsonSerializer.Serialize(
        diagnostics,
        WakuJsonContext.Default.WakuTransportDiagnostics);

    public async ValueTask<WakuPublishResult> PublishAsync(
        WakuPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        UpdateDiagnostics(
            "TX",
            "lightpush-attempt",
            request.ContentTopic,
            value => value with { PublishAttempts = value.PublishAttempts + 1 });

        using CancellationTokenSource operation = LinkOperation(cancellationToken);
        Exception? lastFailure = null;
        for (int attempt = 0; attempt < MaxLightPushAttempts; attempt++)
        {
            ILibp2pConnection? connection = null;
            try
            {
                connection = await SelectPublishConnectionAsync(operation.Token);
                byte[] encoded = WakuProtocolCodec.EncodeLightPushRequest(request, out string requestId);
                WakuStatusResponse response = WakuProtocolCodec.DecodeLightPushResponse(
                    await RequestAsync(connection, WakuProtocolCodec.LightPush, encoded, operation.Token));
                ValidateResponse(requestId, response, "Light Push");
                int acceptedPeerCount = checked((int)(response.RelayPeerCount ?? 1));
                if (acceptedPeerCount == 0)
                    acceptedPeerCount = 1;
                UpdateDiagnostics("TX", "lightpush-accepted",
                    $"{request.ContentTopic}; relayPeers={acceptedPeerCount}",
                    value => value with { PublishSuccesses = value.PublishSuccesses + 1 });
                return new WakuPublishResult(acceptedPeerCount);
            }
            catch (Exception exception) when (IsTransportFailure(exception))
            {
                operation.Token.ThrowIfCancellationRequested();
                lastFailure = exception;
                UpdateDiagnostics("ERROR", "lightpush", exception.Message,
                    value => value with
                    {
                        PublishFailures = value.PublishFailures + 1,
                        LastError = exception.Message,
                    });
                if (connection is not null)
                    await InvalidateConnectionAsync(connection);
            }
        }
        throw lastFailure ?? new IOException("Light Push failed after retrying available peers.");
    }

    public async IAsyncEnumerable<WakuTransportMessage> SubscribeAsync(
        IReadOnlyList<string> contentTopics,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contentTopics);
        if (contentTopics.Count == 0)
            yield break;

        using CancellationTokenSource subscription = LinkOperation(cancellationToken);
        await subscriptionLock.WaitAsync(subscription.Token);
        Task maintenance = MaintainSubscriptionAsync(contentTopics, subscription.Token);
        try
        {
            while (true)
            {
                Task<bool> readable = messages.Reader.WaitToReadAsync(subscription.Token).AsTask();
                if (await Task.WhenAny(readable, maintenance) == maintenance)
                {
                    await maintenance;
                    yield break;
                }
                if (!await readable)
                    yield break;
                while (messages.Reader.TryRead(out WakuTransportMessage message))
                    yield return message;
            }
        }
        finally
        {
            subscription.Cancel();
            try { await maintenance; }
            catch (OperationCanceledException) when (subscription.IsCancellationRequested) { }
            finally
            {
                ILibp2pConnection[] previous = filterPeers.Keys.ToArray();
                filterPeers.Clear();
                SetConnectedDiagnostics();
                try
                {
                    await Task.WhenAll(previous.Select(UnsubscribeAsync));
                }
                finally { subscriptionLock.Release(); }
            }
        }
    }

    private async Task MaintainSubscriptionAsync(IReadOnlyList<string> topics, CancellationToken token)
    {
        IReadOnlyDictionary<string, string[]> groups = WakuSharding.GroupByPubsubTopic(topics);
        while (!token.IsCancellationRequested)
        {
            bool retry = false;
            try
            {
                IReadOnlyList<PooledPeer> peers = await EnsurePoolAsync(token);
                foreach (PooledPeer peer in peers)
                {
                    bool subscribed = filterPeers.ContainsKey(peer.Connection);
                    if (!subscribed && filterPeers.Count >= FilterRedundancy)
                        continue;
                    try
                    {
                        if (subscribed)
                        {
                            byte[] ping = WakuProtocolCodec.EncodeFilterControlRequest(0, out string pingId);
                            WakuStatusResponse response = WakuProtocolCodec.DecodeFilterSubscribeResponse(
                                await RequestAsync(peer.Connection, WakuProtocolCodec.FilterSubscribe, ping, token));
                            if (!string.Equals(pingId, response.RequestId, StringComparison.Ordinal))
                                throw new IOException("Filter ping response id did not match the request.");
                            if (response.StatusCode == 404)
                                subscribed = false;
                            else
                                ValidateResponse(pingId, response, "Filter ping");
                        }
                        if (!subscribed)
                        {
                            foreach ((string pubsubTopic, string[] contentTopics) in groups)
                            {
                                byte[] request = WakuProtocolCodec.EncodeFilterSubscribeRequest(
                                    pubsubTopic, contentTopics, out string requestId);
                                WakuStatusResponse response = WakuProtocolCodec.DecodeFilterSubscribeResponse(
                                    await RequestAsync(peer.Connection, WakuProtocolCodec.FilterSubscribe, request, token));
                                ValidateResponse(requestId, response, "Filter subscribe");
                            }
                            if (!peer.Connection.IsAlive)
                                throw new IOException("Filter peer disconnected during subscription.");
                            filterPeers[peer.Connection] = 0;
                            UpdateDiagnostics("STATE", "filter-subscribed",
                                $"topics={topics.Count}; shards={groups.Count}; peer={peer.Peer.PeerId}",
                                value => value with { TopicCount = topics.Distinct(StringComparer.Ordinal).Count() });
                        }
                    }
                    catch (Exception exception) when (IsTransportFailure(exception))
                    {
                        token.ThrowIfCancellationRequested();
                        retry = true;
                        UpdateDiagnostics("ERROR", "filter-subscribe", exception.Message,
                            value => value with { LastError = exception.Message });
                        await InvalidateConnectionAsync(peer.Connection);
                    }
                }
                SetConnectedDiagnostics();
            }
            catch (Exception exception) when (IsTransportFailure(exception))
            {
                token.ThrowIfCancellationRequested();
                retry = true;
                UpdateDiagnostics("ERROR", "filter-reconnect", exception.Message,
                    value => value with { LastError = exception.Message });
                SetConnectedDiagnostics();
            }
            // A dropped peer wakes this wait immediately; otherwise verify subscription health every minute.
            if (retry)
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            else
                await subscriptionChanged.WaitAsync(
                    filterPeers.Count < FilterRedundancy ? TimeSpan.FromSeconds(5) : filterKeepAliveInterval, token);
        }
    }

    private async Task UnsubscribeAsync(ILibp2pConnection connection)
    {
        if (!connection.IsAlive || lifetime.IsCancellationRequested)
            return;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        try
        {
            byte[] request = WakuProtocolCodec.EncodeFilterControlRequest(3, out _);
            _ = await RequestAsync(connection, WakuProtocolCodec.FilterSubscribe, request, timeout.Token);
        }
        catch (Exception exception) when (IsTransportFailure(exception)) { }
    }

    public async IAsyncEnumerable<WakuTransportMessage> QueryStoreAsync(
        WakuStoreQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query.ContentTopics);
        if (query.ContentTopics.Count == 0)
            yield break;

        UpdateDiagnostics(
            "TX",
            "store-query",
            $"topics={query.ContentTopics.Count}",
            value => value with { StoreQueries = value.StoreQueries + 1 });

        using CancellationTokenSource operation = LinkOperation(cancellationToken);
        IReadOnlyDictionary<string, string[]> groups = WakuSharding.GroupByPubsubTopic(query.ContentTopics);
        foreach ((string pubsubTopic, string[] topics) in groups)
        {
            byte[] cursor = [];
            for (int page = 0; page < MaximumStorePages; page++)
            {
                WakuStoreResponse response = await QueryStorePageAsync(query, pubsubTopic, topics, cursor, operation.Token);
                foreach (WakuWireMessage message in response.Messages)
                {
                    UpdateDiagnostics("RX", "store-message", message.ContentTopic,
                        value => value with { StoreRecords = value.StoreRecords + 1 });
                    yield return ToTransportMessage(message, WakuMessageSource.Store);
                }
                if (response.Messages.Count == 0 ||
                    response.PaginationCursor is not { Length: > 0 } nextCursor ||
                    nextCursor.AsSpan().SequenceEqual(cursor))
                    break;
                cursor = nextCursor;
            }
        }
        UpdateDiagnostics("STATE", "store-ready", $"shards={groups.Count}", value => value);
    }

    private async Task<WakuStoreResponse> QueryStorePageAsync(
        WakuStoreQuery query, string pubsubTopic, string[] topics, byte[] cursor, CancellationToken token)
    {
        Exception? lastFailure = null;
        for (int attempt = 0; attempt < TargetPoolSize; attempt++)
        {
            ILibp2pConnection? connection = null;
            try
            {
                IReadOnlyList<PooledPeer> peers = await EnsurePoolAsync(token);
                connection = peers[attempt % peers.Count].Connection;
                byte[] request = WakuProtocolCodec.EncodeStoreQueryRequest(
                    pubsubTopic, topics, query.StartUnixMilliseconds, query.EndUnixMilliseconds, cursor, out string requestId);
                WakuStoreResponse response = WakuProtocolCodec.DecodeStoreQueryResponse(
                    await RequestAsync(connection, WakuProtocolCodec.Store, request, token));
                ValidateStoreResponse(requestId, response);
                storePeers[connection] = 0;
                SetConnectedDiagnostics();
                return response;
            }
            catch (Exception exception) when (IsTransportFailure(exception))
            {
                token.ThrowIfCancellationRequested();
                lastFailure = exception;
                UpdateDiagnostics("ERROR", "store-query", exception.Message,
                    value => value with { LastError = exception.Message });
                if (connection is not null)
                    await InvalidateConnectionAsync(connection);
            }
        }
        throw lastFailure ?? new IOException("Store query failed on all available peers.");
    }

    private async Task<byte[]> RequestAsync(
        ILibp2pConnection connection, string protocol, byte[] request, CancellationToken token)
    {
        using CancellationTokenSource deadline = LinkOperation(token);
        deadline.CancelAfter(operationTimeout);
        MplexStream? stream = null;
        try
        {
            stream = await connection.OpenStreamAsync(protocol, deadline.Token);
            await stream.SendLengthPrefixedAsync(request, deadline.Token);
            return await stream.ReadLengthPrefixedAsync(deadline.Token);
        }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            throw new IOException($"{protocol} request timed out after {operationTimeout.TotalSeconds:g} seconds.", exception);
        }
        finally
        {
            if (stream is not null)
            {
                using CancellationTokenSource closeTimeout = new(TimeSpan.FromSeconds(2));
                await IgnoreFailureAsync(stream.CloseWriteAsync(closeTimeout.Token));
            }
        }
    }

    private CancellationTokenSource LinkOperation(CancellationToken token) =>
        CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);

    public async ValueTask RefreshDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await EnsurePoolAsync(cancellationToken);
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            UpdateDiagnostics(
                "ERROR",
                "diagnose",
                exception.Message,
                value => value with { Phase = "error", LastError = exception.Message });
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        lifetime.Cancel();
        messages.Writer.TryComplete();
        await connectionLock.WaitAsync();
        PooledPeer[] snapshot;
        try
        {
            snapshot = pool.Values.ToArray();
            pool.Clear();
            filterPeers.Clear();
            storePeers.Clear();
            SetConnectedDiagnostics();
        }
        finally { connectionLock.Release(); }
        foreach (PooledPeer pooled in snapshot)
            await pooled.Connection.DisposeAsync();
        await Task.WhenAll(snapshot.Select(peer => peer.Monitor));
    }

    private async ValueTask<ILibp2pConnection> SelectPublishConnectionAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PooledPeer> peers = await EnsurePoolAsync(cancellationToken);
        int index = (Interlocked.Increment(ref nextPublishIndex) & int.MaxValue) % peers.Count;
        return peers[index].Connection;
    }

    private async ValueTask<IReadOnlyList<PooledPeer>> EnsurePoolAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using CancellationTokenSource operation = LinkOperation(cancellationToken);
        operation.CancelAfter(operationTimeout * TargetPoolSize);
        cancellationToken = operation.Token;
        await connectionLock.WaitAsync(cancellationToken);
        try
        {
            foreach (PooledPeer dead in pool.Values.Where(peer => !peer.Connection.IsAlive).ToArray())
            {
                RemovePeer(dead.Connection);
                await dead.Connection.DisposeAsync();
            }
            if (pool.Count >= TargetPoolSize)
                return pool.Values.ToArray();

            UpdateDiagnostics(
                "STATE",
                "discovering",
                "official Waku DNS trees",
                value => value with { Phase = pool.Count > 0 ? value.Phase : "discovering", LastError = null });
            using CancellationTokenSource discoveryTimeout = LinkOperation(cancellationToken);
            discoveryTimeout.CancelAfter(operationTimeout);
            IReadOnlyList<WakuPeer> bootstrapPeers = await discover(discoveryTimeout.Token);
            if (bootstrapPeers.Count == 0 && pool.Count == 0)
                throw new IOException("Waku DNS discovery returned no browser peers.");

            Exception? lastFailure = null;
            Dictionary<string, WakuPeer> pendingPeers = bootstrapPeers
                .Where(peer => !pool.ContainsKey(peer.PeerId))
                .ToDictionary(peer => peer.PeerId, StringComparer.Ordinal);
            HashSet<string> attemptedPeerIds = new(StringComparer.Ordinal);

            while (pool.Count < TargetPoolSize &&
                   pendingPeers.Values.FirstOrDefault(
                       peer => !attemptedPeerIds.Contains(peer.PeerId)) is { } peer)
            {
                attemptedPeerIds.Add(peer.PeerId);
                using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(operationTimeout);
                ILibp2pConnection? candidate = null;
                try
                {
                    UpdateDiagnostics(
                        "STATE",
                        "identifying-peer",
                        $"{peer.PeerId} {peer.WebSocketUri}",
                        value => value with { Phase = pool.Count > 0 ? value.Phase : "connecting" });
                    candidate = await connect(peer, HandleInboundStreamAsync, attempt.Token);

                    Libp2pIdentifyResponse identified = await Libp2pIdentify.QueryAsync(
                        candidate,
                        attempt.Token);
                    UpdateDiagnostics(
                        "RX",
                        "identify",
                        $"{peer.PeerId}; {string.Join(',', identified.Protocols.Order())}",
                        value => value);

                    if (SupportsWakuServices(identified.Protocols))
                    {
                        if (!candidate.IsAlive)
                            throw new IOException("Peer disconnected during identification.");
                        PooledPeer pooled = new(peer, candidate);
                        pool[peer.PeerId] = pooled;
                        pooled.Monitor = MonitorConnectionAsync(candidate);
                        candidate = null;
                        SetConnectedDiagnostics();
                        continue;
                    }

                    if (identified.Protocols.Contains(WakuPeerExchange.Protocol))
                    {
                        IReadOnlyList<WakuPeer> exchanged = await WakuPeerExchange.QueryAsync(
                            candidate,
                            60,
                            attempt.Token);
                        foreach (WakuPeer servicePeer in exchanged)
                        {
                            if (!pool.ContainsKey(servicePeer.PeerId))
                                pendingPeers.TryAdd(servicePeer.PeerId, servicePeer);
                        }
                    }

                    UpdateDiagnostics(
                        "STATE",
                        "service-mismatch",
                        $"{peer.PeerId}; peerExchange={identified.Protocols.Contains(WakuPeerExchange.Protocol)}",
                        value => value);
                }
                catch (Exception exception) when (IsTransportFailure(exception))
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw;
                    lastFailure = exception;
                    UpdateDiagnostics(
                        "ERROR",
                        "service-dial",
                        $"{peer.WebSocketUri}: {exception.Message}",
                        value => value with { LastError = pool.Count > 0 ? value.LastError : exception.Message });
                }
                finally
                {
                    if (candidate is not null)
                        await candidate.DisposeAsync();
                }
            }

            if (pool.Count == 0)
            {
                string failureMessage =
                    $"Unable to identify a WSS Waku peer with Light Push, Filter, and Store. " +
                    $"Last failure: {lastFailure?.Message}";
                UpdateDiagnostics(
                    "ERROR",
                    "connect",
                    failureMessage,
                    value => value with { Phase = "error", LastError = failureMessage });
                throw new IOException(failureMessage, lastFailure);
            }

            return pool.Values.ToArray();
        }
        finally
        {
            connectionLock.Release();
        }
    }

    private static bool SupportsWakuServices(IReadOnlySet<string> protocols) =>
        protocols.Contains(WakuProtocolCodec.LightPush) &&
        protocols.Contains(WakuProtocolCodec.FilterSubscribe) &&
        protocols.Contains(WakuProtocolCodec.Store);

    private async Task HandleInboundStreamAsync(
        MplexStream stream,
        CancellationToken cancellationToken)
    {
        string? protocol = await stream.AcceptInboundAsync(InboundProtocols, cancellationToken);
        switch (protocol)
        {
            case Libp2pIdentify.Protocol:
                await stream.SendLengthPrefixedAsync(
                    Libp2pIdentify.Encode(identity, AdvertisedProtocols),
                    cancellationToken);
                await stream.CloseWriteAsync(cancellationToken);
                break;
            case Libp2pIdentify.PushProtocol:
                _ = Libp2pIdentify.Decode(
                    await stream.ReadLengthPrefixedAsync(cancellationToken));
                await stream.CloseWriteAsync(cancellationToken);
                break;
            case Libp2pIdentify.PingProtocol:
                await Libp2pPing.HandleAsync(stream, cancellationToken);
                break;
            case WakuMetadata.Protocol:
                _ = WakuMetadata.Decode(
                    await stream.ReadLengthPrefixedAsync(cancellationToken));
                await stream.SendLengthPrefixedAsync(
                    WakuMetadata.EncodeLightClient(),
                    cancellationToken);
                await stream.CloseWriteAsync(cancellationToken);
                break;
            case WakuProtocolCodec.FilterPush:
                await HandleFilterPushAsync(stream, cancellationToken);
                break;
            default:
                await stream.CloseWriteAsync(cancellationToken);
                break;
        }
    }

    private async Task HandleFilterPushAsync(
        MplexStream stream,
        CancellationToken cancellationToken)
    {
        await foreach (byte[] frame in stream.ReadAllLengthPrefixedAsync(cancellationToken))
        {
            WakuWireMessage incoming = WakuProtocolCodec.DecodeFilterPush(frame);
            await messages.Writer.WriteAsync(
                ToTransportMessage(incoming, WakuMessageSource.LiveFilter),
                cancellationToken);
            UpdateDiagnostics(
                "RX",
                "filter-message",
                incoming.ContentTopic,
                value => value with
                {
                    LiveMessages = value.LiveMessages + 1,

                });
        }
    }

    private async Task MonitorConnectionAsync(ILibp2pConnection connection)
    {
        try { await connection.Completion; }
        catch (Exception exception)
        {
            if (!lifetime.IsCancellationRequested)
                UpdateDiagnostics("ERROR", "connection-lost", exception.Message,
                    value => value with { LastError = exception.Message });
        }
        await InvalidateConnectionAsync(connection);
    }

    private async ValueTask InvalidateConnectionAsync(ILibp2pConnection failed)
    {
        RemovePeer(failed);
        await failed.DisposeAsync();
    }

    // Remove only this connection: discovery may already have installed a replacement for the same peer.
    private void RemovePeer(ILibp2pConnection failed)
    {
        foreach (KeyValuePair<string, PooledPeer> entry in pool)
        {
            if (ReferenceEquals(entry.Value.Connection, failed))
            {
                pool.TryRemove(new KeyValuePair<string, PooledPeer>(entry.Key, entry.Value));
                break;
            }
        }
        filterPeers.TryRemove(failed, out _);
        storePeers.TryRemove(failed, out _);
        SetConnectedDiagnostics();
        try { subscriptionChanged.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void SetConnectedDiagnostics()
    {
        lock (diagnosticsLock)
        {
            PooledPeer[] connected = pool.Values.Where(peer => peer.Connection.IsAlive).ToArray();
            bool filterReady = connected.Any(peer => filterPeers.ContainsKey(peer.Connection));
            bool storeReady = connected.Any(peer => storePeers.ContainsKey(peer.Connection));
            bool wasAvailable = diagnostics.LightPushReady && diagnostics.FilterReady;
            UpdateDiagnostics("STATE", connected.Length > 0 ? "connected" : "disconnected",
                $"peers={connected.Length}", value => value with
                {
                    Phase = Volatile.Read(ref disposed) != 0 ? "disposed" : connected.Length > 0 ? "ready" : "disconnected",
                    PeerCount = connected.Length,
                    LightPushReady = connected.Length > 0,
                    FilterReady = filterReady,
                    StoreReady = storeReady,
                    LastError = filterReady ? null : value.LastError,
                    Peers = connected.Select(pooled => new WakuTransportPeerDiagnostic(
                        pooled.Peer.PeerId, pooled.Peer.WebSocketUri.AbsoluteUri, ["LightPush", "Filter", "Store"]))
                        .ToArray(),
                });
            bool available = connected.Length > 0 && filterReady;
            if (wasAvailable != available)
                AvailabilityChanged?.Invoke(available);
        }
    }

    private void UpdateDiagnostics(
        string direction,
        string eventName,
        string details,
        Func<WakuTransportDiagnostics, WakuTransportDiagnostics> update)
    {
        WakuTransportDiagnostics next;
        lock (diagnosticsLock)
        {
            recentEvents.Add(new WakuTransportDiagnosticEvent(
                DateTimeOffset.UtcNow,
                direction,
                eventName,
                details));
            if (recentEvents.Count > 120)
                recentEvents.RemoveRange(0, recentEvents.Count - 120);
            next = update(diagnostics) with
            {
                LastEvent = $"{direction} {eventName}",
                RecentEvents = recentEvents.ToArray(),
            };
            diagnostics = next;
        }

        DiagnosticsChanged?.Invoke(next);
    }

    private static void ValidateResponse(
        string requestId,
        WakuStatusResponse response,
        string protocol)
    {
        if (!string.Equals(requestId, response.RequestId, StringComparison.Ordinal))
            throw new IOException($"{protocol} response id did not match the request.");
        if (response.StatusCode is < 200 or >= 300)
        {
            throw new IOException(
                $"{protocol} rejected the request with {response.StatusCode}: " +
                (response.StatusDescription ?? "no description"));
        }
    }

    private static void ValidateStoreResponse(string requestId, WakuStoreResponse response)
    {
        if (!string.Equals(requestId, response.RequestId, StringComparison.Ordinal))
            throw new IOException("Store response id did not match the request.");
        if (response.StatusCode is < 200 or >= 300)
        {
            throw new IOException(
                $"Store rejected the query with {response.StatusCode}: " +
                (response.StatusDescription ?? "no description"));
        }
    }

    private static WakuTransportMessage ToTransportMessage(
        WakuWireMessage message,
        WakuMessageSource source) =>
        new(
            message.ContentTopic,
            message.Payload,
            message.TimestampUnixMilliseconds,
            source);

    private static bool IsTransportFailure(Exception exception) =>
        exception is IOException or WebSocketException or OperationCanceledException or
            FormatException or System.Security.Cryptography.CryptographicException or
            ChannelClosedException or PlatformNotSupportedException or ObjectDisposedException;

    private static async ValueTask IgnoreFailureAsync(ValueTask operation)
    {
        try
        {
            await operation;
        }
        catch (Exception)
        {
        }
    }

    private sealed record PooledPeer(WakuPeer Peer, ILibp2pConnection Connection)
    {
        public Task Monitor { get; set; } = Task.CompletedTask;
    }
}
