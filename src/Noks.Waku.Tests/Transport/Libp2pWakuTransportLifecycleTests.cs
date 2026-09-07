using System.Collections.Concurrent;
using System.Net.WebSockets;
using Noks.Waku.Transport;
using Noks.Waku.Transport.Libp2p.Discovery;
using Noks.Waku.Transport.Libp2p.Mplex;
using Noks.Waku.Transport.Libp2p.Protocols;
using Noks.Waku.Transport.Libp2p.Wire;
using Noks.Waku.Transport.Protocols;

namespace Noks.Waku.Tests.Transport;

public sealed class Libp2pWakuTransportLifecycleTests
{
    private const string Topic = "/noks/1/test/proto";

    [Fact]
    public async Task DisconnectedSubscriptionReconnectsAndDeliversWithoutAnOutgoingRequest()
    {
        FakeNetwork network = new();
        await using Libp2pWakuTransport transport = network.CreateTransport();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task<WakuTransportMessage> received = ReadOneAsync(transport, timeout.Token);
        await UntilAsync(() => transport.Diagnostics.FilterReady);
        FakeConnection[] original = network.Connections.ToArray();
        network.ConnectGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (FakeConnection connection in original)
            connection.Fail();
        await UntilAsync(() => transport.Diagnostics.PeerCount == 0);
        Assert.False(transport.Diagnostics.FilterReady);
        Assert.False(transport.Diagnostics.LightPushReady);
        network.ConnectGate.SetResult();
        await UntilAsync(() => transport.Diagnostics.FilterReady && network.Connections.Count >= 6);
        FakeConnection replacement = network.Connections.First(connection => connection.IsAlive && connection.Subscriptions > 0);
        await replacement.PushAsync(Topic, [1, 2, 3], timeout.Token);
        WakuTransportMessage message = await received;
        Assert.Equal(Topic, message.ContentTopic);
        Assert.Equal(new byte[] { 1, 2, 3 }, message.Payload.ToArray());
    }

    [Fact]
    public async Task RecheckReplacesDeadPoolAndStoreCanReadAgain()
    {
        FakeNetwork network = new();
        await using Libp2pWakuTransport transport = network.CreateTransport();
        await transport.RefreshDiagnosticsAsync();
        foreach (FakeConnection connection in network.Connections)
            connection.Fail();
        await transport.RefreshDiagnosticsAsync();
        Assert.Equal(3, transport.Diagnostics.PeerCount);
        Assert.Equal(6, network.Connections.Count);
        await QueryAsync(transport, CancellationToken.None);
        Assert.True(transport.Diagnostics.StoreReady);
        Assert.False(transport.Diagnostics.FilterReady);
    }

    [Fact]
    public async Task MissingFilterSubscriptionIsReinstalledOnTheSameConnection()
    {
        FakeNetwork network = new();
        await using Libp2pWakuTransport transport = network.CreateTransport(filterInterval: TimeSpan.FromMilliseconds(30));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task<WakuTransportMessage> received = ReadOneAsync(transport, timeout.Token);
        await UntilAsync(() => transport.Diagnostics.FilterReady);
        FakeConnection subscribed = network.Connections.First(connection => connection.Subscriptions > 0);
        int initial = subscribed.Subscriptions;
        subscribed.SubscriptionLost = true;
        await UntilAsync(() => subscribed.Subscriptions > initial);
        Assert.Equal(3, network.Connections.Count);
        Assert.True(subscribed.FilterPings > 0);
        await subscribed.PushAsync(Topic, [9], timeout.Token);
        Assert.Equal(new byte[] { 9 }, (await received).Payload.ToArray());
    }

    [Fact]
    public async Task DisposedSocketDuringPublishIsEvictedAndRetried()
    {
        FakeNetwork network = new() { FailNextPublish = 1 };
        await using Libp2pWakuTransport transport = network.CreateTransport();
        WakuPublishResult result = await transport.PublishAsync(new(Topic, new byte[] { 1 }, true, 1));
        Assert.True(result.AcceptedByServicePeer);
        Assert.Equal(1, transport.Diagnostics.PublishFailures);
        Assert.Equal(1, transport.Diagnostics.PublishSuccesses);
        Assert.Equal(4, network.Connections.Count);
        Assert.Single(network.Connections, connection => !connection.IsAlive);
    }

    [Fact]
    public async Task StoreRequestTimeoutIsBoundedAndFailedPeersAreRemoved()
    {
        FakeNetwork network = new() { HoldStore = true };
        await using Libp2pWakuTransport transport = network.CreateTransport(operationTimeout: TimeSpan.FromMilliseconds(40));
        IOException failure = await Assert.ThrowsAsync<IOException>(async () =>
            await QueryAsync(transport, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Contains("timed out", failure.Message);
        Assert.Equal(3, network.Connections.Count(connection => !connection.IsAlive));
        Assert.False(transport.Diagnostics.StoreReady);
        Assert.NotNull(transport.Diagnostics.LastError);
    }

    [Fact]
    public async Task CallerCancellationDoesNotEvictHealthyStorePeer()
    {
        FakeNetwork network = new() { HoldStore = true };
        await using Libp2pWakuTransport transport = network.CreateTransport();
        using CancellationTokenSource cancellation = new();
        Task query = QueryAsync(transport, cancellation.Token);
        await network.StoreStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        Assert.Equal(3, transport.Diagnostics.PeerCount);
        Assert.All(network.Connections, connection => Assert.True(connection.IsAlive));
    }

    [Fact]
    public async Task DisposalStopsWaitingSubscriptionAndConnections()
    {
        FakeNetwork network = new();
        Libp2pWakuTransport transport = network.CreateTransport();
        Task<WakuTransportMessage> received = ReadOneAsync(transport, CancellationToken.None);
        await UntilAsync(() => transport.Diagnostics.FilterReady);
        await transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => received);
        Assert.Equal("disposed", transport.Diagnostics.Phase);
        Assert.Equal(0, transport.Diagnostics.PeerCount);
        Assert.All(network.Connections, connection => Assert.False(connection.IsAlive));
    }

    private static async Task<WakuTransportMessage> ReadOneAsync(Libp2pWakuTransport transport, CancellationToken token)
    {
        await foreach (WakuTransportMessage message in transport.SubscribeAsync([Topic], token))
            return message;
        throw new OperationCanceledException();
    }

    private static async Task QueryAsync(Libp2pWakuTransport transport, CancellationToken token)
    {
        await foreach (WakuTransportMessage _ in transport.QueryStoreAsync(new([Topic], 0, 1), token)) { }
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        while (!predicate())
            await Task.Delay(5, timeout.Token);
    }

    private sealed class FakeNetwork
    {
        public ConcurrentBag<FakeConnection> Connections { get; } = [];
        public TaskCompletionSource? ConnectGate;
        public TaskCompletionSource StoreStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldStore;
        public int FailNextPublish;

        public Libp2pWakuTransport CreateTransport(TimeSpan? operationTimeout = null, TimeSpan? filterInterval = null) => new(
            _ => Task.FromResult<IReadOnlyList<WakuPeer>>(Enumerable.Range(0, 3)
                .Select(index => new WakuPeer(new Uri($"wss://peer{index}.invalid/"), $"peer{index}", [], "")).ToArray()),
            async (_, inbound, token) =>
            {
                if (ConnectGate is { } gate)
                    await gate.Task.WaitAsync(token);
                FakeConnection connection = new(this, inbound);
                Connections.Add(connection);
                return connection;
            }, operationTimeout ?? TimeSpan.FromSeconds(2), filterInterval ?? TimeSpan.FromMinutes(1));
    }

    private sealed class FakeConnection(FakeNetwork network, Func<MplexStream, CancellationToken, Task> inbound) : ILibp2pConnection
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsAlive { get; private set; } = true;
        public Task Completion => completion.Task;
        public int Subscriptions;
        public int FilterPings;
        public bool SubscriptionLost;

        public void Fail()
        {
            IsAlive = false;
            completion.TrySetResult();
        }

        public ValueTask DisposeAsync() { Fail(); return ValueTask.CompletedTask; }

        public ValueTask<MplexStream> OpenStreamAsync(string protocol, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!IsAlive)
                throw new WebSocketException("Peer is closed");
            if (protocol == WakuProtocolCodec.LightPush && Interlocked.Exchange(ref network.FailNextPublish, 0) == 1)
                throw new ObjectDisposedException("ClientWebSocket");
            MplexStream? stream = null;
            stream = new((streamId, type, payload, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (type != MplexProtocol.MessageInitiator)
                    return ValueTask.CompletedTask;
                Assert.True(Libp2pVarint.TryRead(payload.Span, out _, out int prefix));
                ProtobufReader reader = new(payload.Span[prefix..]);
                string requestId = "";
                uint requestType = 0;
                while (reader.TryReadTag(out int field, out int wire))
                {
                    if (field == 1 && wire == 2) requestId = reader.ReadString();
                    else if (field == 2 && wire == 0) requestType = reader.ReadUInt32();
                    else reader.Skip(wire);
                }
                uint status = 200;
                if (protocol == WakuProtocolCodec.FilterSubscribe)
                {
                    if (requestType == 1) { Interlocked.Increment(ref Subscriptions); SubscriptionLost = false; }
                    if (requestType == 0) { Interlocked.Increment(ref FilterPings); if (SubscriptionLost) status = 404; }
                }
                if (protocol == WakuProtocolCodec.Store)
                {
                    network.StoreStarted.TrySetResult();
                    if (network.HoldStore) return ValueTask.CompletedTask;
                }
                ProtobufWriter response = new();
                response.WriteString(1, requestId);
                response.WriteUInt32(10, status);
                response.WriteUInt32(12, 1);
                stream!.TryWrite(Libp2pVarint.Prefix(response.ToArray()));
                return ValueTask.CompletedTask;
            }, 1, true, protocol);
            if (protocol == Libp2pIdentify.Protocol)
            {
                ProtobufWriter identified = new();
                foreach (string service in new[] { WakuProtocolCodec.LightPush, WakuProtocolCodec.FilterSubscribe, WakuProtocolCodec.Store })
                    identified.WriteString(3, service);
                stream.TryWrite(Libp2pVarint.Prefix(identified.ToArray()));
            }
            return ValueTask.FromResult(stream);
        }

        public async Task PushAsync(string topic, byte[] payload, CancellationToken token)
        {
            MplexStream stream = new((_, _, _, _) => ValueTask.CompletedTask, 2, false, "push");
            ProtobufWriter message = new();
            message.WriteBytes(1, payload);
            message.WriteString(2, topic);
            ProtobufWriter push = new();
            push.WriteBytes(1, message.ToArray());
            stream.TryWrite([.. MultistreamSelect.Encode(MultistreamSelect.Protocol),
                .. MultistreamSelect.Encode(WakuProtocolCodec.FilterPush), .. Libp2pVarint.Prefix(push.ToArray())]);
            stream.Complete();
            try { await inbound(stream, token); }
            catch (System.Threading.Channels.ChannelClosedException) { }
        }
    }
}
