using Noks.Waku.Transport.Libp2p.Cryptography;
using Noks.Waku.Transport.Libp2p.Mplex;
using Noks.Waku.Transport.Libp2p.Wire;

namespace Noks.Waku.Transport.Libp2p.Protocols;

internal static class Libp2pIdentify
{
    public const string Protocol = "/ipfs/id/1.0.0";
    public const string PushProtocol = "/ipfs/id/push/1.0.0";
    public const string PingProtocol = "/ipfs/ping/1.0.0";

    public static async Task<Libp2pIdentifyResponse> QueryAsync(
        Libp2pWebSocketConnection connection,
        CancellationToken cancellationToken)
    {
        MplexStream stream = await connection.OpenStreamAsync(Protocol, cancellationToken);
        try
        {
            return Decode(await stream.ReadLengthPrefixedAsync(cancellationToken));
        }
        finally
        {
            await CloseIgnoringFailureAsync(stream);
        }
    }

    public static byte[] Encode(
        Libp2pIdentity identity,
        IReadOnlyCollection<string> protocols)
    {
        ProtobufWriter response = new();
        response.WriteBytes(1, identity.ProtobufPublicKey);
        foreach (string protocol in protocols)
            response.WriteString(3, protocol);
        response.WriteString(5, "ipfs/0.1.0");
        response.WriteString(6, "noks-dotnet/1.0");
        return response.ToArray();
    }

    public static Libp2pIdentifyResponse Decode(ReadOnlySpan<byte> encoded)
    {
        byte[]? publicKey = null;
        string? protocolVersion = null;
        string? agentVersion = null;
        List<string> protocols = [];
        ProtobufReader reader = new(encoded);
        while (reader.TryReadTag(out int field, out int wireType))
        {
            switch (field)
            {
                case 1 when wireType == 2:
                    publicKey = reader.ReadBytes();
                    break;
                case 3 when wireType == 2:
                    protocols.Add(reader.ReadString());
                    break;
                case 5 when wireType == 2:
                    protocolVersion = reader.ReadString();
                    break;
                case 6 when wireType == 2:
                    agentVersion = reader.ReadString();
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return new Libp2pIdentifyResponse(
            publicKey,
            protocols.ToHashSet(StringComparer.Ordinal),
            protocolVersion,
            agentVersion);
    }

    private static async Task CloseIgnoringFailureAsync(MplexStream stream)
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
