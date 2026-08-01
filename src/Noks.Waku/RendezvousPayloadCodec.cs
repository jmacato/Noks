using System.Buffers.Binary;
using System.Collections.Immutable;

namespace Noks.Waku;

public static class RendezvousPayloadCodec
{
    private const byte Version = 1;
    private const int RequestHeaderSize = 39;
    private const int ResponseHeaderSize = 24;
    private const int CorrelationSize = 24;
    private static ReadOnlySpan<byte> RequestMagic => "NRQ1"u8;
    private static ReadOnlySpan<byte> ResponseMagic => "NRA1"u8;
    private static ReadOnlySpan<byte> CorrelationMagic => "NRC1"u8;

    public static byte[] EncodeRequest(
        Guid rendezvousId,
        RendezvousRouteKind routeKind,
        string targetNumber,
        ReadOnlySpan<byte> encodedContactCard)
    {
        RequireRendezvousId(rendezvousId);
        if (routeKind is not (RendezvousRouteKind.Call or RendezvousRouteKind.Sms))
            throw new ArgumentOutOfRangeException(nameof(routeKind));
        if (!NoksTemporaryNumber.IsCanonical(targetNumber))
            throw new FormatException($"A rendezvous target must contain exactly {NoksTemporaryNumber.DigitCount} digits.");
        RequireCard(encodedContactCard);

        byte[] encoded = new byte[RequestHeaderSize + encodedContactCard.Length];
        RequestMagic.CopyTo(encoded);
        encoded[4] = Version;
        encoded[5] = (byte)routeKind;
        BinaryPrimitives.WriteUInt16BigEndian(encoded.AsSpan(6, 2), checked((ushort)encodedContactCard.Length));
        rendezvousId.TryWriteBytes(encoded.AsSpan(8, 16), bigEndian: true, out _);
        for (int index = 0; index < NoksTemporaryNumber.DigitCount; index++)
            encoded[24 + index] = checked((byte)targetNumber[index]);
        encodedContactCard.CopyTo(encoded.AsSpan(RequestHeaderSize));
        return encoded;
    }

    public static bool TryDecodeRequest(ReadOnlySpan<byte> encoded, out RendezvousRequestPayload? payload)
    {
        payload = null;
        if (encoded.Length < RequestHeaderSize + 1 ||
            !encoded[..4].SequenceEqual(RequestMagic) ||
            encoded[4] != Version ||
            encoded[5] is not ((byte)RendezvousRouteKind.Call) and not ((byte)RendezvousRouteKind.Sms))
        {
            return false;
        }

        int cardLength = BinaryPrimitives.ReadUInt16BigEndian(encoded.Slice(6, 2));
        if (cardLength == 0 || encoded.Length != RequestHeaderSize + cardLength)
            return false;
        Guid id = new(encoded.Slice(8, 16), bigEndian: true);
        if (id == Guid.Empty)
            return false;
        Span<char> target = stackalloc char[NoksTemporaryNumber.DigitCount];
        for (int index = 0; index < target.Length; index++)
        {
            byte value = encoded[24 + index];
            if (value is < (byte)'0' or > (byte)'9')
                return false;
            target[index] = (char)value;
        }

        payload = new RendezvousRequestPayload(
            id,
            (RendezvousRouteKind)encoded[5],
            new string(target),
            ImmutableArray.Create(encoded[RequestHeaderSize..].ToArray()));
        return true;
    }

    public static byte[] EncodeCardResponse(Guid rendezvousId, ReadOnlySpan<byte> encodedContactCard)
    {
        RequireRendezvousId(rendezvousId);
        RequireCard(encodedContactCard);
        byte[] encoded = new byte[ResponseHeaderSize + encodedContactCard.Length];
        ResponseMagic.CopyTo(encoded);
        encoded[4] = Version;
        BinaryPrimitives.WriteUInt16BigEndian(encoded.AsSpan(6, 2), checked((ushort)encodedContactCard.Length));
        rendezvousId.TryWriteBytes(encoded.AsSpan(8, 16), bigEndian: true, out _);
        encodedContactCard.CopyTo(encoded.AsSpan(ResponseHeaderSize));
        return encoded;
    }

    public static bool TryDecodeCardResponse(ReadOnlySpan<byte> encoded, out RendezvousCardResponsePayload? payload)
    {
        payload = null;
        if (encoded.Length < ResponseHeaderSize + 1 ||
            !encoded[..4].SequenceEqual(ResponseMagic) ||
            encoded[4] != Version || encoded[5] != 0)
        {
            return false;
        }
        int cardLength = BinaryPrimitives.ReadUInt16BigEndian(encoded.Slice(6, 2));
        Guid id = new(encoded.Slice(8, 16), bigEndian: true);
        if (cardLength == 0 || encoded.Length != ResponseHeaderSize + cardLength || id == Guid.Empty)
            return false;
        payload = new RendezvousCardResponsePayload(
            id,
            ImmutableArray.Create(encoded[ResponseHeaderSize..].ToArray()));
        return true;
    }

    public static byte[] EncodeCorrelation(Guid rendezvousId)
    {
        RequireRendezvousId(rendezvousId);
        byte[] encoded = new byte[CorrelationSize];
        CorrelationMagic.CopyTo(encoded);
        encoded[4] = Version;
        rendezvousId.TryWriteBytes(encoded.AsSpan(8, 16), bigEndian: true, out _);
        return encoded;
    }

    public static bool TryDecodeCorrelation(ReadOnlySpan<byte> encoded, out Guid rendezvousId)
    {
        rendezvousId = Guid.Empty;
        if (encoded.Length != CorrelationSize || !encoded[..4].SequenceEqual(CorrelationMagic) ||
            encoded[4] != Version || encoded[5] != 0 || encoded[6] != 0 || encoded[7] != 0)
        {
            return false;
        }
        rendezvousId = new Guid(encoded.Slice(8, 16), bigEndian: true);
        return rendezvousId != Guid.Empty;
    }

    private static void RequireRendezvousId(Guid rendezvousId)
    {
        if (rendezvousId == Guid.Empty)
            throw new ArgumentException("A rendezvous identifier is required.", nameof(rendezvousId));
    }

    private static void RequireCard(ReadOnlySpan<byte> encodedContactCard)
    {
        if (encodedContactCard.IsEmpty || encodedContactCard.Length > ushort.MaxValue ||
            (!ContactCardV2Codec.TryDecode(encodedContactCard, out _) &&
             !PqcContactCardCodec.TryDecode(encodedContactCard, out _)))
        {
            throw new ArgumentException("A canonical contact card is required.", nameof(encodedContactCard));
        }
    }
}
