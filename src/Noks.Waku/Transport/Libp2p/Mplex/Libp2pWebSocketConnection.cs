using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Threading.Channels;
using Noks.Waku.Transport.Libp2p.Cryptography;
using Noks.Waku.Transport.Libp2p.Discovery;
using Noks.Waku.Transport.Libp2p.Wire;

namespace Noks.Waku.Transport.Libp2p.Mplex;

internal sealed class Libp2pWebSocketConnection : IAsyncDisposable
{
    private const int MaximumNoiseFrameLength = 65_535;
    private const int MaximumMplexFrameLength = 1_048_576;
    // This deadline detects a connection that stops without a WebSocket close frame.
    // For example, an abrupt TCP close can leave the socket in CLOSE_WAIT.
    // Without a read deadline, ReceiveAsync can block indefinitely.
    // Then the client cannot detect the failure, reconnect, or subscribe again.
    private static readonly TimeSpan SocketReceiveTimeout = TimeSpan.FromSeconds(90);
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly ByteQueue socketInput = new(16_384);
    private readonly ByteQueue secureInput = new(4096);
    private readonly ByteQueue mplexInput = new(16_384);
    private readonly ConcurrentDictionary<(long Id, bool Initiator), MplexStream> streams = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<MplexStream, CancellationToken, Task> inboundStreamHandler;
    private NoiseSession? noise;
    private Task? receiveTask;
    private long nextStreamId = -1;
    private int disposed;

    private Libp2pWebSocketConnection(
        Func<MplexStream, CancellationToken, Task> inboundStreamHandler)
    {
        this.inboundStreamHandler = inboundStreamHandler;
    }

    public static async Task<Libp2pWebSocketConnection> ConnectAsync(
        WakuPeer peer,
        Libp2pIdentity identity,
        Func<MplexStream, CancellationToken, Task> inboundStreamHandler,
        CancellationToken cancellationToken)
    {
        Libp2pWebSocketConnection connection = new(inboundStreamHandler);
        try
        {
            try
            {
                // Browser/WASM's ClientWebSocket is backed by the native browser WebSocket
                // object, which does not expose ping/pong control and throws here.
                connection.socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            }
            catch (PlatformNotSupportedException)
            {
            }

            await connection.socket.ConnectAsync(peer.WebSocketUri, cancellationToken);
            await connection.NegotiateSecurityAsync(peer, identity, cancellationToken);
            connection.receiveTask = connection.ReceiveMplexAsync(connection.lifetime.Token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask<MplexStream> OpenStreamAsync(
        string protocol,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        long id = Interlocked.Increment(ref nextStreamId);
        string name = id.ToString(CultureInfo.InvariantCulture);
        MplexStream stream = new(this, id, localIsInitiator: true, name);
        if (!streams.TryAdd((id, true), stream))
            throw new InvalidOperationException("Unable to reserve an Mplex stream id.");

        try
        {
            await SendMplexAsync(
                id,
                MplexProtocol.NewStream,
                System.Text.Encoding.UTF8.GetBytes(name),
                cancellationToken);
            await stream.NegotiateOutboundAsync(protocol, cancellationToken);
            return stream;
        }
        catch
        {
            streams.TryRemove((id, true), out _);
            stream.Complete();
            throw;
        }
    }

    public async ValueTask<bool> SupportsProtocolAsync(
        string protocol,
        CancellationToken cancellationToken)
    {
        MplexStream? stream = null;
        try
        {
            stream = await OpenStreamAsync(protocol, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is IOException or ChannelClosedException)
        {
            return false;
        }
        finally
        {
            if (stream is not null)
            {
                try
                {
                    await stream.CloseWriteAsync(CancellationToken.None);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        lifetime.Cancel();
        foreach (MplexStream stream in streams.Values)
            stream.Complete();

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "disposed",
                    CancellationToken.None);
            }
            catch (WebSocketException)
            {
            }
        }

        if (receiveTask is not null)
        {
            try
            {
                await receiveTask;
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or WebSocketException or IOException)
            {
            }
        }

        socket.Dispose();
        sendLock.Dispose();
        lifetime.Dispose();
    }

    internal async ValueTask SendMplexAsync(
        long streamId,
        int messageType,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (streamId < 0 || streamId > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(streamId));
        if (payload.Length > MaximumMplexFrameLength)
            throw new ArgumentException("Mplex payload exceeds 1 MiB.", nameof(payload));

        ulong header = checked(((ulong)streamId << 3) | (uint)messageType);
        int headerLength = Libp2pVarint.GetEncodedLength(header);
        int payloadLengthLength = Libp2pVarint.GetEncodedLength((ulong)payload.Length);
        byte[] frame = new byte[headerLength + payloadLengthLength + payload.Length];
        Libp2pVarint.Write(frame, header);
        Libp2pVarint.Write(frame.AsSpan(headerLength), (ulong)payload.Length);
        payload.Span.CopyTo(frame.AsSpan(headerLength + payloadLengthLength));
        await SendEncryptedAsync(frame, cancellationToken);
    }

    private async Task NegotiateSecurityAsync(
        WakuPeer peer,
        Libp2pIdentity identity,
        CancellationToken cancellationToken)
    {
        await SendRawAsync(MultistreamSelect.Encode(MultistreamSelect.Protocol), cancellationToken);
        string multistream = await ReadRawMultistreamAsync(cancellationToken);
        if (!string.Equals(multistream, MultistreamSelect.Protocol, StringComparison.Ordinal))
            throw new IOException($"Waku peer rejected {MultistreamSelect.Protocol}.");

        await SendRawAsync(MultistreamSelect.Encode(NoiseSession.Protocol), cancellationToken);
        string selectedSecurity = await ReadRawMultistreamAsync(cancellationToken);
        if (!string.Equals(selectedSecurity, NoiseSession.Protocol, StringComparison.Ordinal))
            throw new IOException($"Waku peer rejected {NoiseSession.Protocol}.");

        noise = new NoiseSession(identity, peer.IdentityPublicKey);
        await SendNoiseHandshakeFrameAsync(noise.WriteMessageA(), cancellationToken);
        noise.ReadMessageB(await ReadNoiseFrameAsync(cancellationToken));
        await SendNoiseHandshakeFrameAsync(noise.WriteMessageC(), cancellationToken);

        await SendEncryptedAsync(
            MultistreamSelect.Encode(MultistreamSelect.Protocol),
            cancellationToken);
        string encryptedMultistream = await ReadEncryptedMultistreamAsync(cancellationToken);
        if (!string.Equals(encryptedMultistream, MultistreamSelect.Protocol, StringComparison.Ordinal))
            throw new IOException($"Waku peer rejected encrypted {MultistreamSelect.Protocol}.");

        await SendEncryptedAsync(
            MultistreamSelect.Encode(MplexProtocol.Protocol),
            cancellationToken);
        string selectedMuxer = await ReadEncryptedMultistreamAsync(cancellationToken);
        if (!string.Equals(selectedMuxer, MplexProtocol.Protocol, StringComparison.Ordinal))
            throw new IOException($"Waku peer rejected {MplexProtocol.Protocol}.");
    }

    private async Task ReceiveMplexAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] plaintext = await ReadEncryptedFrameAsync(cancellationToken);
                mplexInput.Append(plaintext);
                while (TryReadMplexFrame(out long id, out int type, out byte[] payload))
                    HandleMplexFrame(id, type, payload);
            }
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or WebSocketException or IOException or
                FormatException or System.Security.Cryptography.CryptographicException)
        {
            failure = exception;
        }
        finally
        {
            foreach (MplexStream stream in streams.Values)
                stream.Complete(failure);
        }
    }

    private bool TryReadMplexFrame(out long id, out int type, out byte[] payload)
    {
        id = 0;
        type = 0;
        payload = [];
        if (!Libp2pVarint.TryRead(mplexInput.Span, out ulong header, out int headerLength))
            return false;
        if (!Libp2pVarint.TryRead(
                mplexInput.Span[headerLength..],
                out ulong payloadLengthValue,
                out int payloadPrefixLength))
        {
            return false;
        }

        int payloadLength = checked((int)payloadLengthValue);
        if (payloadLength > MaximumMplexFrameLength)
            throw new IOException("Mplex frame exceeds 1 MiB.");
        int frameLength = checked(headerLength + payloadPrefixLength + payloadLength);
        if (mplexInput.Count < frameLength)
            return false;

        id = checked((long)(header >> 3));
        type = (int)(header & 7);
        mplexInput.Consume(headerLength + payloadPrefixLength);
        if (!mplexInput.TryRead(payloadLength, out payload))
            throw new InvalidOperationException("Mplex frame accounting failed.");
        return true;
    }

    private void HandleMplexFrame(long id, int type, byte[] payload)
    {
        if (type == MplexProtocol.NewStream)
        {
            string name = System.Text.Encoding.UTF8.GetString(payload);
            MplexStream stream = new(this, id, localIsInitiator: false, name);
            if (!streams.TryAdd((id, false), stream))
                return;
            _ = HandleInboundStreamAsync(stream);
            return;
        }

        bool localIsInitiator = (type & 1) != 0;
        if (!streams.TryGetValue((id, localIsInitiator), out MplexStream? target))
            return;

        switch (type)
        {
            case MplexProtocol.MessageReceiver:
            case MplexProtocol.MessageInitiator:
                if (!target.TryWrite(payload))
                    target.Complete(new IOException("Mplex stream input overflowed."));
                break;
            case MplexProtocol.CloseReceiver:
            case MplexProtocol.CloseInitiator:
                target.Complete();
                streams.TryRemove((id, localIsInitiator), out _);
                break;
            case MplexProtocol.ResetReceiver:
            case MplexProtocol.ResetInitiator:
                target.Complete(new IOException("Waku peer reset the Mplex stream."));
                streams.TryRemove((id, localIsInitiator), out _);
                break;
        }
    }

    private async Task HandleInboundStreamAsync(MplexStream stream)
    {
        try
        {
            await inboundStreamHandler(stream, lifetime.Token);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or IOException or FormatException)
        {
            stream.Complete(exception);
        }
        finally
        {
            streams.TryRemove((stream.Id, false), out _);
        }
    }

    private async ValueTask SendEncryptedAsync(
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken)
    {
        if (plaintext.Length > MaximumNoiseFrameLength - 16)
            throw new ArgumentException("Noise plaintext is too large.", nameof(plaintext));

        await sendLock.WaitAsync(cancellationToken);
        try
        {
            byte[] ciphertext = (noise ??
                throw new InvalidOperationException("Noise negotiation is incomplete.")).Encrypt(plaintext.Span);
            byte[] frame = new byte[2 + ciphertext.Length];
            BinaryPrimitives.WriteUInt16BigEndian(frame, checked((ushort)ciphertext.Length));
            ciphertext.CopyTo(frame, 2);
            await socket.SendAsync(frame, WebSocketMessageType.Binary, true, cancellationToken);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private async Task<byte[]> ReadEncryptedFrameAsync(CancellationToken cancellationToken)
    {
        byte[] ciphertext = await ReadNoiseFrameAsync(cancellationToken);
        return (noise ?? throw new InvalidOperationException("Noise negotiation is incomplete."))
            .Decrypt(ciphertext);
    }

    private async Task<string> ReadEncryptedMultistreamAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (MultistreamSelect.TryDecode(secureInput, out string protocol))
                return protocol;
            secureInput.Append(await ReadEncryptedFrameAsync(cancellationToken));
        }
    }

    private async ValueTask SendNoiseHandshakeFrameAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length > MaximumNoiseFrameLength)
            throw new ArgumentException("Noise handshake frame is too large.", nameof(payload));
        byte[] frame = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, checked((ushort)payload.Length));
        payload.Span.CopyTo(frame.AsSpan(2));
        await SendRawAsync(frame, cancellationToken);
    }

    private async Task<byte[]> ReadNoiseFrameAsync(CancellationToken cancellationToken)
    {
        await EnsureSocketInputAsync(2, cancellationToken);
        int length = BinaryPrimitives.ReadUInt16BigEndian(socketInput.Span);
        await EnsureSocketInputAsync(2 + length, cancellationToken);
        socketInput.Consume(2);
        if (!socketInput.TryRead(length, out byte[] payload))
            throw new InvalidOperationException("Noise frame accounting failed.");
        return payload;
    }

    private async Task<string> ReadRawMultistreamAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (MultistreamSelect.TryDecode(socketInput, out string protocol))
                return protocol;
            await ReceiveSocketChunkAsync(cancellationToken);
        }
    }

    private async Task EnsureSocketInputAsync(int length, CancellationToken cancellationToken)
    {
        while (socketInput.Count < length)
            await ReceiveSocketChunkAsync(cancellationToken);
    }

    private async Task ReceiveSocketChunkAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16_384];
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SocketReceiveTimeout);
        ValueWebSocketReceiveResult result;
        try
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebSocketException("Waku peer stopped responding (receive timed out).");
        }
        if (result.MessageType == WebSocketMessageType.Close)
        {
            throw new WebSocketException(
                $"Waku peer closed the WebSocket ({socket.CloseStatus}: {socket.CloseStatusDescription}).");
        }
        if (result.MessageType != WebSocketMessageType.Binary)
            throw new WebSocketException("Waku peer sent a non-binary WebSocket frame.");
        if (result.Count == 0)
            throw new WebSocketException("Waku peer sent an empty WebSocket frame.");
        socketInput.Append(buffer.AsSpan(0, result.Count));
    }

    private async ValueTask SendRawAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Binary, true, cancellationToken);
        }
        finally
        {
            sendLock.Release();
        }
    }
}
