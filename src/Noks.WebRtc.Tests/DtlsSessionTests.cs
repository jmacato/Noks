using System.Threading.Channels;
using Noks.WebRtc;

namespace Noks.WebRtc.Tests;

public sealed class DtlsSessionTests
{
    [Fact]
    public async Task Dtls_handshake_derives_matching_rfc5764_key_block()
    {
        using var clientIdentity = new DtlsIdentity();
        using var serverIdentity = new DtlsIdentity();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (clientInput, serverInput, clientSend, serverSend) = CreateDatagramPair();

        var client = DtlsSession.HandshakeAsync(clientIdentity, true, serverIdentity.Fingerprint, clientInput, clientSend, cancellation.Token);
        var server = DtlsSession.HandshakeAsync(serverIdentity, false, clientIdentity.Fingerprint, serverInput, serverSend, cancellation.Token);
        using var sessions = new DisposablePair(await client, await server);

        Assert.Equal(sessions.Left.Keys.ClientKey, sessions.Right.Keys.ClientKey);
        Assert.Equal(sessions.Left.Keys.ServerKey, sessions.Right.Keys.ServerKey);
        Assert.Equal(sessions.Left.Keys.ClientSalt, sessions.Right.Keys.ClientSalt);
        Assert.Equal(sessions.Left.Keys.ServerSalt, sessions.Right.Keys.ServerSalt);
        Assert.Equal(16, sessions.Left.Keys.ClientKey.Length);
        Assert.Equal(14, sessions.Left.Keys.ClientSalt.Length);
    }

    [Fact]
    public async Task Dtls_handshake_rejects_wrong_sdp_fingerprint()
    {
        using var clientIdentity = new DtlsIdentity();
        using var serverIdentity = new DtlsIdentity();
        using var otherIdentity = new DtlsIdentity();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (clientInput, serverInput, clientSend, serverSend) = CreateDatagramPair();

        var client = DtlsSession.HandshakeAsync(clientIdentity, true, otherIdentity.Fingerprint, clientInput, clientSend, cancellation.Token);
        var server = DtlsSession.HandshakeAsync(serverIdentity, false, clientIdentity.Fingerprint, serverInput, serverSend, cancellation.Token);
        var results = await Task.WhenAll(Observe(client), Observe(server));

        Assert.Contains(results, static exception => exception is not null);
    }

    [Fact]
    public void Dtls_rejects_absent_or_different_srtp_profile()
    {
        Assert.Throws<InvalidDataException>(() => DtlsSession.ValidateSrtp(false, null, null));
        Assert.Throws<InvalidDataException>(() => DtlsSession.ValidateSrtp(true, [2], []));
        DtlsSession.ValidateSrtp(true, [1], []);
    }

    private static async Task<Exception?> Observe(Task task)
    {
        try { await task; return null; }
        catch (Exception exception) { return exception; }
    }

    private static (ChannelReader<byte[]> ClientInput, ChannelReader<byte[]> ServerInput,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> ClientSend,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> ServerSend) CreateDatagramPair()
    {
        var client = Channel.CreateUnbounded<byte[]>();
        var server = Channel.CreateUnbounded<byte[]>();
        return (client.Reader, server.Reader,
            (datagram, token) => server.Writer.WriteAsync(datagram.ToArray(), token),
            (datagram, token) => client.Writer.WriteAsync(datagram.ToArray(), token));
    }

    private sealed class DisposablePair(DtlsSession left, DtlsSession right) : IDisposable
    {
        public DtlsSession Left { get; } = left;
        public DtlsSession Right { get; } = right;
        public void Dispose() { Right.Dispose(); Left.Dispose(); }
    }
}
