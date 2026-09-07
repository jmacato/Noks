using Noks.Waku.Transport.Libp2p.Mplex;
using Noks.Waku.Transport.Libp2p.Protocols;
using Noks.Waku.Transport.Libp2p.Wire;

namespace Noks.Waku.Tests.Transport.Libp2p.Protocols;

public sealed class Libp2pPingTests
{
    [Fact]
    public async Task HandlerEchoesFragmentedRawChallenge()
    {
        List<(long Id, int Type, byte[] Payload)> sent = [];
        MplexStream stream = new((id, type, payload, _) =>
        {
            sent.Add((id, type, payload.ToArray()));
            return ValueTask.CompletedTask;
        }, 3, localIsInitiator: false, "ping");
        byte[] challenge = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        stream.TryWrite(challenge[..0x1f]);
        stream.TryWrite(challenge[0x1f..]);
        stream.Complete();

        await Libp2pPing.HandleAsync(stream, CancellationToken.None);

        (long id, int type, byte[] payload) = Assert.Single(sent, frame => frame.Type == MplexProtocol.MessageReceiver);
        Assert.Equal(3, id);
        Assert.Equal(MplexProtocol.MessageReceiver, type);
        Assert.Equal(challenge, payload);
    }

    [Fact]
    public async Task HandlerEchoesMultipleCoalescedRawChallenges()
    {
        List<byte[]> echoed = [];
        MplexStream stream = new((_, type, payload, _) =>
        {
            if (type == MplexProtocol.MessageReceiver)
                echoed.Add(payload.ToArray());
            return ValueTask.CompletedTask;
        }, 1, localIsInitiator: false, "ping");
        byte[] first = Enumerable.Repeat((byte)0x20, 32).ToArray();
        byte[] second = Enumerable.Repeat((byte)0x1f, 32).ToArray();
        stream.TryWrite([.. first, .. second]);
        stream.Complete();

        await Libp2pPing.HandleAsync(stream, CancellationToken.None);

        Assert.Equal(2, echoed.Count);
        Assert.Equal(first, echoed[0]);
        Assert.Equal(second, echoed[1]);
    }

    [Fact]
    public async Task QueryRejectsMismatchAndCleanEof()
    {
        await Assert.ThrowsAsync<IOException>(() => Libp2pPing.QueryAsync(
            new FakeConnection(stream => stream.TryWrite(new byte[32])), CancellationToken.None));

        await Assert.ThrowsAsync<IOException>(() => Libp2pPing.QueryAsync(
            new FakeConnection(stream => stream.Complete()), CancellationToken.None));
    }

    [Fact]
    public async Task ReadExactlyReturnsNullOnlyForCleanEofBeforeAnyBytes()
    {
        MplexStream empty = NewStream();
        empty.Complete();
        Assert.Null(await empty.ReadExactlyAsync(32, CancellationToken.None));

        MplexStream truncated = NewStream();
        truncated.TryWrite([0x1f]);
        truncated.Complete();
        await Assert.ThrowsAsync<IOException>(() => truncated.ReadExactlyAsync(2, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ReadExactlyDoesNotTreatFailedChannelAsCleanEof()
    {
        MplexStream stream = NewStream();
        stream.Complete(new System.Threading.Channels.ChannelClosedException());
        await Assert.ThrowsAsync<IOException>(() => stream.ReadExactlyAsync(32, CancellationToken.None).AsTask());
    }

    private static MplexStream NewStream() => new(
        (_, _, _, _) => ValueTask.CompletedTask, 0, localIsInitiator: true, "test");

    private sealed class FakeConnection(Action<MplexStream> prepare) : ILibp2pConnection
    {
        public bool IsAlive => true;
        public Task Completion => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<MplexStream> OpenStreamAsync(string protocol, CancellationToken cancellationToken)
        {
            MplexStream stream = NewStream();
            prepare(stream);
            return ValueTask.FromResult(stream);
        }
    }
}
