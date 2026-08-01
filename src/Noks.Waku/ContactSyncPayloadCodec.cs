using System.Buffers.Binary;
using System.Collections.Immutable;

namespace Noks.Waku;

public static class ContactSyncPayloadCodec
{
    private const byte Version = 1;
    private const int HeaderSize = 24;
    private static ReadOnlySpan<byte> Magic => "NCS1"u8;

    public static byte[] Encode(
        Guid transactionId,
        ContactSyncKind kind,
        ReadOnlySpan<byte> encodedContactCard)
    {
        if (transactionId == Guid.Empty)
            throw new ArgumentException("A contact-sync transaction identifier is required.", nameof(transactionId));
        if (kind is not (ContactSyncKind.Offer or ContactSyncKind.Acknowledge))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (encodedContactCard.IsEmpty || encodedContactCard.Length > ushort.MaxValue ||
            (!ContactCardV2Codec.TryDecode(encodedContactCard, out _) &&
             !PqcContactCardCodec.TryDecode(encodedContactCard, out _)))
        {
            throw new ArgumentException("A canonical ContactCardV2 is required.", nameof(encodedContactCard));
        }

        byte[] encoded = new byte[HeaderSize + encodedContactCard.Length];
        Magic.CopyTo(encoded);
        encoded[4] = Version;
        encoded[5] = (byte)kind;
        BinaryPrimitives.WriteUInt16BigEndian(encoded.AsSpan(6, 2), checked((ushort)encodedContactCard.Length));
        transactionId.TryWriteBytes(encoded.AsSpan(8, 16), bigEndian: true, out _);
        encodedContactCard.CopyTo(encoded.AsSpan(HeaderSize));
        return encoded;
    }

    public static bool TryDecode(ReadOnlySpan<byte> encoded, out ContactSyncPayload? payload)
    {
        payload = null;
        if (encoded.Length < HeaderSize + 1 ||
            !encoded[..4].SequenceEqual(Magic) ||
            encoded[4] != Version ||
            encoded[5] is not ((byte)ContactSyncKind.Offer) and not ((byte)ContactSyncKind.Acknowledge))
        {
            return false;
        }

        int cardLength = BinaryPrimitives.ReadUInt16BigEndian(encoded.Slice(6, 2));
        Guid transactionId = new(encoded.Slice(8, 16), bigEndian: true);
        if (transactionId == Guid.Empty || cardLength == 0 || encoded.Length != HeaderSize + cardLength ||
            (!ContactCardV2Codec.TryDecode(encoded[HeaderSize..], out _) &&
             !PqcContactCardCodec.TryDecode(encoded[HeaderSize..], out _)))
        {
            return false;
        }

        payload = new ContactSyncPayload(
            transactionId,
            (ContactSyncKind)encoded[5],
            ImmutableArray.Create(encoded[HeaderSize..].ToArray()));
        return true;
    }
}
