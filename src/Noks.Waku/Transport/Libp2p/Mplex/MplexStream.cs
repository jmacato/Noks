using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Noks.Waku.Transport.Libp2p.Wire;

namespace Noks.Waku.Transport.Libp2p.Mplex;

internal sealed class MplexStream
{
    private const int MaximumFrameLength = 1_048_576;
    private readonly Libp2pWebSocketConnection connection;
    private readonly Channel<byte[]> input = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    private readonly ByteQueue bufferedInput = new();
    private readonly bool localIsInitiator;
    private int closed;

    public MplexStream(
        Libp2pWebSocketConnection connection,
        long id,
        bool localIsInitiator,
        string name)
    {
        this.connection = connection;
        Id = id;
        this.localIsInitiator = localIsInitiator;
        Name = name;
    }

    public long Id { get; }

    public string Name { get; }

    public async ValueTask NegotiateOutboundAsync(string protocol, CancellationToken cancellationToken)
    {
        await SendAsync(MultistreamSelect.Encode(MultistreamSelect.Protocol), cancellationToken);
        string multistream = await ReadMultistreamAsync(cancellationToken);
        if (!string.Equals(multistream, MultistreamSelect.Protocol, StringComparison.Ordinal))
            throw new IOException($"Peer rejected {MultistreamSelect.Protocol} with '{multistream}'.");

        await SendAsync(MultistreamSelect.Encode(protocol), cancellationToken);
        string selected = await ReadMultistreamAsync(cancellationToken);
        if (!string.Equals(selected, protocol, StringComparison.Ordinal))
            throw new IOException($"Peer rejected {protocol} with '{selected}'.");
    }

    public async ValueTask<string?> AcceptInboundAsync(
        IReadOnlySet<string> supportedProtocols,
        CancellationToken cancellationToken)
    {
        string multistream = await ReadMultistreamAsync(cancellationToken);
        if (!string.Equals(multistream, MultistreamSelect.Protocol, StringComparison.Ordinal))
            throw new IOException($"Inbound stream did not negotiate {MultistreamSelect.Protocol}.");
        await SendAsync(MultistreamSelect.Encode(MultistreamSelect.Protocol), cancellationToken);

        string requested = await ReadMultistreamAsync(cancellationToken);
        if (!supportedProtocols.Contains(requested))
        {
            await SendAsync(MultistreamSelect.Encode(MultistreamSelect.NotAvailable), cancellationToken);
            return null;
        }

        await SendAsync(MultistreamSelect.Encode(requested), cancellationToken);
        return requested;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        connection.SendMplexAsync(
            Id,
            localIsInitiator ? MplexProtocol.MessageInitiator : MplexProtocol.MessageReceiver,
            payload,
            cancellationToken);

    public ValueTask SendLengthPrefixedAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken) =>
        SendAsync(Libp2pVarint.Prefix(payload.Span), cancellationToken);

    public async ValueTask<byte[]> ReadLengthPrefixedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (Libp2pVarint.TryRead(
                    bufferedInput.Span,
                    out ulong lengthValue,
                    out int prefixLength))
            {
                int length = checked((int)lengthValue);
                if (length > MaximumFrameLength)
                    throw new IOException("Protocol frame exceeds 1 MiB.");
                if (bufferedInput.Count >= prefixLength + length)
                {
                    bufferedInput.Consume(prefixLength);
                    if (!bufferedInput.TryRead(length, out byte[] frame))
                        throw new InvalidOperationException("Mplex input accounting failed.");
                    return frame;
                }
            }

            byte[] next = await ReadInputAsync(cancellationToken);
            bufferedInput.Append(next);
        }
    }

    public async IAsyncEnumerable<byte[]> ReadAllLengthPrefixedAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
            yield return await ReadLengthPrefixedAsync(cancellationToken);
    }

    public async ValueTask CloseWriteAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref closed, 1) != 0)
            return;
        await connection.SendMplexAsync(
            Id,
            localIsInitiator ? MplexProtocol.CloseInitiator : MplexProtocol.CloseReceiver,
            ReadOnlyMemory<byte>.Empty,
            cancellationToken);
    }

    internal bool TryWrite(byte[] payload) => input.Writer.TryWrite(payload);

    internal void Complete(Exception? failure = null)
    {
        if (failure is null)
            input.Writer.TryComplete();
        else
            input.Writer.TryComplete(failure);
    }

    private async ValueTask<string> ReadMultistreamAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (MultistreamSelect.TryDecode(bufferedInput, out string protocol))
                return protocol;
            byte[] next = await ReadInputAsync(cancellationToken);
            bufferedInput.Append(next);
        }
    }

    private async ValueTask<byte[]> ReadInputAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await input.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
