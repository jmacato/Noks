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
    private readonly WakuDnsDiscovery discovery = new();
    private readonly object diagnosticsLock = new();
    private readonly List<WakuTransportDiagnosticEvent> recentEvents = [];
    private readonly Dictionary<string, PooledPeer> pool = new(StringComparer.Ordinal);
    private WakuTransportDiagnostics diagnostics = WakuTransportDiagnostics.Empty with
    {
        Mode = "public-libp2p",
    };
    private int nextPublishIndex;
    private int disposed;

    public event Action<bool>? AvailabilityChanged;

    public event Action<WakuTransportDiagnostics>? DiagnosticsChanged;

    public WakuTransportDiagnostics Diagnostics => diagnostics;

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

        Exception? lastFailure = null;
        for (int attempt = 0; attempt < MaxLightPushAttempts; attempt++)
        {
            Libp2pWebSocketConnection activeConnection = await SelectPublishConnectionAsync(cancellationToken);
            MplexStream? stream = null;
            try
            {
                stream = await activeConnection.OpenStreamAsync(
                    WakuProtocolCodec.LightPush,
                    cancellationToken);
                byte[] encoded = WakuProtocolCodec.EncodeLightPushRequest(request, out string requestId);
                await stream.SendLengthPrefixedAsync(encoded, cancellationToken);
                WakuStatusResponse response = WakuProtocolCodec.DecodeLightPushResponse(
                    await stream.ReadLengthPrefixedAsync(cancellationToken));
                ValidateResponse(requestId, response, "Light Push");

                int acceptedPeerCount = checked((int)(response.RelayPeerCount ?? 1));
                if (acceptedPeerCount == 0)
                    acceptedPeerCount = 1;
                UpdateDiagnostics(
                    "TX",
                    "lightpush-accepted",
                    $"{request.ContentTopic}; relayPeers={acceptedPeerCount}",
                    value => value with
                    {
                        PublishSuccesses = value.PublishSuccesses + 1,
                        LightPushReady = true,
                    });
                AvailabilityChanged?.Invoke(true);
                return new WakuPublishResult(acceptedPeerCount);
            }
            catch (Exception exception) when (IsTransportFailure(exception))
            {
                lastFailure = exception;
                UpdateDiagnostics(
                    "ERROR",
                    "lightpush",
                    exception.Message,
                    value => value with
                    {
                        Phase = pool.Count > 0 ? value.Phase : "error",
                        PublishFailures = value.PublishFailures + 1,
                        LastError = exception.Message,
                    });
                await InvalidateConnectionAsync(activeConnection);
            }
            finally
            {
                if (stream is not null)
                    await IgnoreFailureAsync(stream.CloseWriteAsync(CancellationToken.None));
            }
        }

        AvailabilityChanged?.Invoke(false);
        throw lastFailure ?? new IOException("Light Push failed after retrying available peers.");
    }

    public async IAsyncEnumerable<WakuTransportMessage> SubscribeAsync(
        IReadOnlyList<string> contentTopics,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contentTopics);
        if (contentTopics.Count == 0)
            yield break;

        IReadOnlyDictionary<string, string[]> groups = WakuSharding.GroupByPubsubTopic(contentTopics);
        IReadOnlyList<PooledPeer> peers = await EnsurePoolAsync(cancellationToken);
        IReadOnlyList<PooledPeer> selected = peers.Take(FilterRedundancy).ToArray();

        int subscribedPeerCount = 0;
        Exception? lastFailure = null;
        foreach (PooledPeer pooled in selected)
        {
            try
            {
                foreach ((string pubsubTopic, string[] topics) in groups)
                {
                    MplexStream stream = await pooled.Connection.OpenStreamAsync(
                        WakuProtocolCodec.FilterSubscribe,
                        cancellationToken);
                    byte[] request = WakuProtocolCodec.EncodeFilterSubscribeRequest(
                        pubsubTopic,
                        topics,
                        out string requestId);
                    await stream.SendLengthPrefixedAsync(request, cancellationToken);
                    WakuStatusResponse response = WakuProtocolCodec.DecodeFilterSubscribeResponse(
                        await stream.ReadLengthPrefixedAsync(cancellationToken));
                    ValidateResponse(requestId, response, "Filter subscribe");
                }

                subscribedPeerCount++;
            }
            catch (Exception exception) when (IsTransportFailure(exception))
            {
                lastFailure = exception;
                UpdateDiagnostics(
                    "ERROR",
                    "filter-subscribe",
                    exception.Message,
                    value => value with { LastError = exception.Message });
                await InvalidateConnectionAsync(pooled.Connection);
            }
        }

        if (subscribedPeerCount == 0)
        {
            AvailabilityChanged?.Invoke(false);
            throw lastFailure ?? new IOException("Filter subscribe failed on all available peers.");
        }

        UpdateDiagnostics(
            "STATE",
            "filter-subscribed",
            $"topics={contentTopics.Count}; shards={groups.Count}; peers={subscribedPeerCount}",
            value => value with
            {
                FilterReady = true,
                TopicCount = contentTopics.Distinct(StringComparer.Ordinal).Count(),
            });
        AvailabilityChanged?.Invoke(true);

        await foreach (WakuTransportMessage message in messages.Reader.ReadAllAsync(cancellationToken))
            yield return message;
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

        IReadOnlyDictionary<string, string[]> groups = WakuSharding.GroupByPubsubTopic(query.ContentTopics);
        IReadOnlyList<PooledPeer> peers = await EnsurePoolAsync(cancellationToken);
        Libp2pWebSocketConnection activeConnection = peers[0].Connection;
        foreach ((string pubsubTopic, string[] topics) in groups)
        {
            byte[] cursor = [];
            for (int page = 0; page < MaximumStorePages; page++)
            {
                MplexStream? stream = null;
                try
                {
                    stream = await activeConnection.OpenStreamAsync(
                        WakuProtocolCodec.Store,
                        cancellationToken);
                    byte[] request = WakuProtocolCodec.EncodeStoreQueryRequest(
                        pubsubTopic,
                        topics,
                        query.StartUnixMilliseconds,
                        query.EndUnixMilliseconds,
                        cursor,
                        out string requestId);
                    await stream.SendLengthPrefixedAsync(request, cancellationToken);
                    WakuStoreResponse response = WakuProtocolCodec.DecodeStoreQueryResponse(
                        await stream.ReadLengthPrefixedAsync(cancellationToken));
                    ValidateStoreResponse(requestId, response);

                    foreach (WakuWireMessage message in response.Messages)
                    {
                        UpdateDiagnostics(
                            "RX",
                            "store-message",
                            message.ContentTopic,
                            value => value with
                            {
                                StoreRecords = value.StoreRecords + 1,
                                StoreReady = true,
                            });
                        yield return ToTransportMessage(message, WakuMessageSource.Store);
                    }

                    if (response.Messages.Count == 0 ||
                        response.PaginationCursor is not { Length: > 0 } nextCursor ||
                        nextCursor.AsSpan().SequenceEqual(cursor))
                    {
                        break;
                    }

                    cursor = nextCursor;
                }
                finally
                {
                    if (stream is not null)
                        await IgnoreFailureAsync(stream.CloseWriteAsync(CancellationToken.None));
                }
            }
        }

        UpdateDiagnostics(
            "STATE",
            "store-ready",
            $"shards={groups.Count}",
            value => value with { StoreReady = true });
    }

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
        messages.Writer.TryComplete();
        PooledPeer[] snapshot = pool.Values.ToArray();
        pool.Clear();
        foreach (PooledPeer pooled in snapshot)
            await pooled.Connection.DisposeAsync();
        connectionLock.Dispose();
    }

    private async ValueTask<Libp2pWebSocketConnection> SelectPublishConnectionAsync(
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
        await connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (pool.Count >= TargetPoolSize)
                return pool.Values.ToArray();

            UpdateDiagnostics(
                "STATE",
                "discovering",
                "official Waku DNS trees",
                value => value with { Phase = pool.Count > 0 ? value.Phase : "discovering", LastError = null });
            IReadOnlyList<WakuPeer> bootstrapPeers = await discovery.DiscoverAsync(cancellationToken);
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
                attempt.CancelAfter(TimeSpan.FromSeconds(20));
                Libp2pWebSocketConnection? candidate = null;
                try
                {
                    UpdateDiagnostics(
                        "STATE",
                        "identifying-peer",
                        $"{peer.PeerId} {peer.WebSocketUri}",
                        value => value with { Phase = pool.Count > 0 ? value.Phase : "connecting" });
                    candidate = await Libp2pWebSocketConnection.ConnectAsync(
                        peer,
                        identity,
                        HandleInboundStreamAsync,
                        attempt.Token);

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
                        pool[peer.PeerId] = new PooledPeer(peer, candidate);
                        candidate = null;
                        SetConnectedDiagnostics();
                        AvailabilityChanged?.Invoke(true);
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
                await HandlePingAsync(stream, cancellationToken);
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
                    FilterReady = true,
                });
        }
    }

    private static async Task HandlePingAsync(
        MplexStream stream,
        CancellationToken cancellationToken)
    {
        await foreach (byte[] frame in stream.ReadAllLengthPrefixedAsync(cancellationToken))
        {
            if (frame.Length != 32)
                throw new FormatException("libp2p Ping payload must be 32 bytes.");
            await stream.SendLengthPrefixedAsync(frame, cancellationToken);
        }
    }

    private async ValueTask InvalidateConnectionAsync(Libp2pWebSocketConnection failed)
    {
        string? removedPeerId = null;
        await connectionLock.WaitAsync();
        try
        {
            foreach (KeyValuePair<string, PooledPeer> entry in pool)
            {
                if (ReferenceEquals(entry.Value.Connection, failed))
                {
                    removedPeerId = entry.Key;
                    break;
                }
            }

            if (removedPeerId is not null)
                pool.Remove(removedPeerId);
        }
        finally
        {
            connectionLock.Release();
        }

        if (removedPeerId is not null)
            SetConnectedDiagnostics();
        await failed.DisposeAsync();
    }

    private void SetConnectedDiagnostics()
    {
        UpdateDiagnostics(
            "STATE",
            pool.Count > 0 ? "connected" : "disconnected",
            $"peers={pool.Count}",
            value => value with
            {
                Phase = pool.Count > 0 ? "ready" : value.Phase,
                PeerCount = pool.Count,
                LightPushReady = pool.Count > 0,
                LastError = pool.Count > 0 ? null : value.LastError,
                Peers = pool.Values
                    .Select(pooled => new WakuTransportPeerDiagnostic(
                        pooled.Peer.PeerId,
                        pooled.Peer.WebSocketUri.AbsoluteUri,
                        ["LightPush", "Filter", "Store"]))
                    .ToArray(),
            });
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
            ChannelClosedException or PlatformNotSupportedException;

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

    private sealed record PooledPeer(WakuPeer Peer, Libp2pWebSocketConnection Connection);
}
