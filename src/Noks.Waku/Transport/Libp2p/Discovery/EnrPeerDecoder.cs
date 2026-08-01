using System.Text;
using Noks.Waku.Transport.Libp2p.Wire;

namespace Noks.Waku.Transport.Libp2p.Discovery;

internal static class EnrPeerDecoder
{
    private const int Dns4MultiaddrCode = 54;
    private const int Dns6MultiaddrCode = 55;
    private const int TcpMultiaddrCode = 6;
    private const int TlsMultiaddrCode = 448;
    private const int WebSocketMultiaddrCode = 477;
    private const int SecureWebSocketMultiaddrCode = 478;

    public static WakuPeer Decode(string enr)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enr);
        if (!enr.StartsWith("enr:", StringComparison.Ordinal))
            throw new FormatException("ENR must start with 'enr:'.");

        byte[] record = DecodeBase64Url(enr[4..]);
        IReadOnlyList<byte[]> values = Rlp.DecodeFlatList(record);
        if (values.Count < 4 || (values.Count & 1) != 0)
            throw new FormatException("ENR does not contain key/value pairs.");

        Dictionary<string, byte[]> fields = new(StringComparer.Ordinal);
        for (int index = 2; index < values.Count; index += 2)
            fields[Encoding.UTF8.GetString(values[index])] = values[index + 1];

        if (!fields.TryGetValue("id", out byte[]? identityScheme) ||
            !identityScheme.AsSpan().SequenceEqual("v4"u8))
        {
            throw new FormatException("Only ENR v4 identities are supported.");
        }

        if (!fields.TryGetValue("secp256k1", out byte[]? publicKey) ||
            publicKey.Length != 33 ||
            publicKey[0] is not (0x02 or 0x03))
        {
            throw new FormatException("ENR is missing a compressed secp256k1 public key.");
        }

        if (!fields.TryGetValue("multiaddrs", out byte[]? multiaddrs))
            throw new FormatException("ENR does not advertise a browser WebSocket address.");

        Uri? webSocketUri = DecodeFirstWebSocketMultiaddr(multiaddrs);
        if (webSocketUri is null)
            throw new FormatException("ENR does not advertise a secure WebSocket address.");

        byte[] protobufPublicKey = EncodePublicKey(publicKey);
        byte[] identityMultihash = new byte[2 + protobufPublicKey.Length];
        identityMultihash[0] = 0;
        identityMultihash[1] = checked((byte)protobufPublicKey.Length);
        protobufPublicKey.CopyTo(identityMultihash, 2);

        return new WakuPeer(
            webSocketUri,
            Base58Btc.Encode(identityMultihash),
            publicKey,
            enr);
    }

    public static WakuPeer DecodeRlp(ReadOnlySpan<byte> record)
    {
        string encoded = Convert.ToBase64String(record)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return Decode($"enr:{encoded}");
    }

    private static Uri? DecodeFirstWebSocketMultiaddr(ReadOnlySpan<byte> encoded)
    {
        int offset = 0;
        while (offset < encoded.Length)
        {
            if (encoded.Length - offset < 2)
                throw new FormatException("Truncated ENR multiaddr length.");

            int length = (encoded[offset] << 8) | encoded[offset + 1];
            offset += 2;
            if (length > encoded.Length - offset)
                throw new FormatException("Truncated ENR multiaddr.");

            if (TryDecodeWebSocketMultiaddr(encoded.Slice(offset, length), out Uri? uri))
                return uri;
            offset += length;
        }

        return null;
    }

    private static bool TryDecodeWebSocketMultiaddr(ReadOnlySpan<byte> encoded, out Uri? uri)
    {
        uri = null;
        int offset = 0;
        string? host = null;
        int? port = null;
        bool tls = false;
        bool webSocket = false;

        while (offset < encoded.Length)
        {
            if (!Libp2pVarint.TryRead(encoded[offset..], out ulong codeValue, out int codeLength))
                throw new FormatException("Truncated multiaddr protocol code.");
            offset += codeLength;
            int code = checked((int)codeValue);

            switch (code)
            {
                case Dns4MultiaddrCode:
                case Dns6MultiaddrCode:
                    if (!Libp2pVarint.TryRead(encoded[offset..], out ulong hostLengthValue, out int hostPrefixLength))
                        throw new FormatException("Truncated multiaddr DNS length.");
                    offset += hostPrefixLength;
                    int hostLength = checked((int)hostLengthValue);
                    if (hostLength > encoded.Length - offset)
                        throw new FormatException("Truncated multiaddr DNS name.");
                    host = Encoding.UTF8.GetString(encoded.Slice(offset, hostLength));
                    offset += hostLength;
                    break;
                case TcpMultiaddrCode:
                    if (encoded.Length - offset < 2)
                        throw new FormatException("Truncated multiaddr TCP port.");
                    port = (encoded[offset] << 8) | encoded[offset + 1];
                    offset += 2;
                    break;
                case TlsMultiaddrCode:
                    tls = true;
                    break;
                case WebSocketMultiaddrCode:
                    webSocket = true;
                    break;
                case SecureWebSocketMultiaddrCode:
                    tls = true;
                    webSocket = true;
                    break;
                default:
                    return false;
            }
        }

        if (!webSocket || !tls || string.IsNullOrWhiteSpace(host) || port is null)
            return false;

        uri = new UriBuilder("wss", host, port.Value, "/").Uri;
        return true;
    }

    private static byte[] EncodePublicKey(ReadOnlySpan<byte> publicKey)
    {
        ProtobufWriter writer = new();
        writer.WriteUInt32(1, 2);
        writer.WriteBytes(2, publicKey);
        return writer.ToArray();
    }

    private static byte[] DecodeBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}
