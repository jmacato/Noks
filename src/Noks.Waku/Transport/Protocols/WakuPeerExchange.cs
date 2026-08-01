using Noks.Waku.Transport.Libp2p.Discovery;
using Noks.Waku.Transport.Libp2p.Mplex;
using Noks.Waku.Transport.Libp2p.Wire;

namespace Noks.Waku.Transport.Protocols;

internal static class WakuPeerExchange
{
    public const string Protocol = "/vac/waku/peer-exchange/2.0.0-alpha1";

    public static async Task<IReadOnlyList<WakuPeer>> QueryAsync(
        Libp2pWebSocketConnection connection,
        int peerCount,
        CancellationToken cancellationToken)
    {
        MplexStream stream = await connection.OpenStreamAsync(Protocol, cancellationToken);
        try
        {
            ProtobufWriter request = new();
            request.WriteMessage(1, query => query.WriteUInt64(1, checked((ulong)peerCount)));
            await stream.SendLengthPrefixedAsync(request.WrittenSpan.ToArray(), cancellationToken);
            byte[] response = await stream.ReadLengthPrefixedAsync(cancellationToken);
            return DecodeResponse(response);
        }
        finally
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

    private static IReadOnlyList<WakuPeer> DecodeResponse(ReadOnlySpan<byte> encoded)
    {
        byte[]? response = null;
        ProtobufReader rpc = new(encoded);
        while (rpc.TryReadTag(out int field, out int wireType))
        {
            if (field == 2 && wireType == 2)
                response = rpc.ReadBytes();
            else
                rpc.Skip(wireType);
        }

        if (response is null)
            throw new FormatException("Waku Peer Exchange returned no response.");

        List<WakuPeer> peers = [];
        ProtobufReader responseReader = new(response);
        while (responseReader.TryReadTag(out int field, out int wireType))
        {
            if (field != 1 || wireType != 2)
            {
                responseReader.Skip(wireType);
                continue;
            }

            byte[] peerInfo = responseReader.ReadBytes();
            ProtobufReader peerReader = new(peerInfo);
            while (peerReader.TryReadTag(out int peerField, out int peerWireType))
            {
                if (peerField == 1 && peerWireType == 2)
                {
                    try
                    {
                        peers.Add(EnrPeerDecoder.DecodeRlp(peerReader.ReadBytes()));
                    }
                    catch (FormatException)
                    {
                    }
                }
                else
                {
                    peerReader.Skip(peerWireType);
                }
            }
        }

        return peers
            .DistinctBy(peer => peer.PeerId, StringComparer.Ordinal)
            .ToArray();
    }
}
