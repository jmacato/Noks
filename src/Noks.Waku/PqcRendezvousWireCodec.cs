using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Noks.Cryptography;

namespace Noks.Waku;

/// <summary>
/// This class defines fixed-size public Waku records for the experimental PQC
/// rendezvous. Descriptor records are public and signed. ML-KEM and AES-256-GCM
/// protect request records before this codec frames them.
/// </summary>
public static class PqcRendezvousWireCodec
{
    private const byte Version = 1;
    private const int HeaderSize = 8;
    private const int DescriptorHeaderSize = 44;
    private static ReadOnlySpan<byte> Magic => "NPQ1"u8;

    public static byte[] EncodeDescriptorChunk(PqcRendezvousDescriptorChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.ProtocolVersion != PqcRendezvousCrypto.RendezvousProtocolVersion ||
            chunk.DescriptorHash.Length != 32 || chunk.Index < 0 || chunk.Count is < 1 or > 64 ||
            chunk.Index >= chunk.Count || chunk.Payload.Length == 0 ||
            HeaderSize + DescriptorHeaderSize + chunk.Payload.Length > WakuEnvelopeCodec.EnvelopeSize)
        {
            throw new ArgumentException("The descriptor chunk cannot be represented in a Waku rendezvous record.", nameof(chunk));
        }

        byte[] body = new byte[DescriptorHeaderSize + chunk.Payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(body, chunk.ProtocolVersion);
        chunk.DescriptorHash.CopyTo(body, 4);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(36, 2), checked((ushort)chunk.Index));
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(38, 2), checked((ushort)chunk.Count));
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(40, 4), chunk.Payload.Length);
        chunk.Payload.CopyTo(body, DescriptorHeaderSize);
        return Encode(
            PqcRendezvousWireKind.DescriptorChunk,
            body,
            WakuEnvelopeCodec.EnvelopeSize);
    }

    public static byte[] EncodeRequest(PqcRendezvousRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!NoksTemporaryNumber.IsCanonical(request.TemporaryId) || request.DescriptorId.Length != 16 ||
            request.AesNonce.Length != PqcRendezvousCrypto.AesNonceSize ||
            request.AuthenticationTag.Length != PqcRendezvousCrypto.AesTagSize ||
            request.Challenge.Length == 0 || request.Ciphertext.Length == 0)
        {
            throw new ArgumentException("The PQC request is malformed.", nameof(request));
        }

        byte[] topic = Encoding.UTF8.GetBytes(request.ContentTopic);
        if (topic.Length == 0 || topic.Length > ushort.MaxValue || request.Challenge.Length > ushort.MaxValue ||
            request.Ciphertext.Length > ushort.MaxValue)
        {
            throw new ArgumentException("The PQC request contains an oversized field.", nameof(request));
        }

        int size = NoksTemporaryNumber.DigitCount + 16 + 8 + 8 + 2 + topic.Length +
            2 + request.Challenge.Length + PqcRendezvousCrypto.AesNonceSize + 2 + request.Ciphertext.Length +
            PqcRendezvousCrypto.AesTagSize + 8;
        byte[] body = new byte[size];
        int offset = 0;
        for (int index = 0; index < NoksTemporaryNumber.DigitCount; index++) body[offset++] = (byte)request.TemporaryId[index];
        request.DescriptorId.CopyTo(body, offset); offset += 16;
        BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(offset, 8), request.DescriptorSequence); offset += 8;
        BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(offset, 8), request.DescriptorExpiresAtUnixMilliseconds); offset += 8;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(offset, 2), checked((ushort)topic.Length)); offset += 2;
        topic.CopyTo(body, offset); offset += topic.Length;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(offset, 2), checked((ushort)request.Challenge.Length)); offset += 2;
        request.Challenge.CopyTo(body, offset); offset += request.Challenge.Length;
        request.AesNonce.CopyTo(body, offset); offset += request.AesNonce.Length;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(offset, 2), checked((ushort)request.Ciphertext.Length)); offset += 2;
        request.Ciphertext.CopyTo(body, offset); offset += request.Ciphertext.Length;
        request.AuthenticationTag.CopyTo(body, offset); offset += request.AuthenticationTag.Length;
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(offset, 8), request.ProofOfWorkNonce);
        return Encode(
            PqcRendezvousWireKind.Request,
            body,
            PqcWakuEnvelopeCodec.EnvelopeSize);
    }

    public static bool TryDecode(ReadOnlySpan<byte> packet, out PqcRendezvousWireRecord? record)
    {
        record = null;
        if (packet.Length is not (WakuEnvelopeCodec.EnvelopeSize or PqcWakuEnvelopeCodec.EnvelopeSize) ||
            !packet[..4].SequenceEqual(Magic) ||
            packet[4] != Version)
            return false;
        int length = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2));
        if (length <= 0 || HeaderSize + length > packet.Length)
            return false;
        ReadOnlySpan<byte> body = packet.Slice(HeaderSize, length);
        return packet[5] switch
        {
            (byte)PqcRendezvousWireKind.DescriptorChunk
                when packet.Length == WakuEnvelopeCodec.EnvelopeSize =>
                TryDecodeDescriptorChunk(body, out record),
            (byte)PqcRendezvousWireKind.Request
                when packet.Length == PqcWakuEnvelopeCodec.EnvelopeSize =>
                TryDecodeRequest(body, out record),
            _ => false,
        };
    }

    private static byte[] Encode(
        PqcRendezvousWireKind kind,
        ReadOnlySpan<byte> body,
        int packetSize)
    {
        if (HeaderSize + body.Length > packetSize || body.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(body));
        byte[] packet = new byte[packetSize];
        Magic.CopyTo(packet);
        packet[4] = Version;
        packet[5] = (byte)kind;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), checked((ushort)body.Length));
        body.CopyTo(packet.AsSpan(HeaderSize));
        RandomNumberGenerator.Fill(packet.AsSpan(HeaderSize + body.Length));
        return packet;
    }

    private static bool TryDecodeDescriptorChunk(ReadOnlySpan<byte> body, out PqcRendezvousWireRecord? record)
    {
        record = null;
        if (body.Length < DescriptorHeaderSize || BinaryPrimitives.ReadInt32BigEndian(body) != PqcRendezvousCrypto.RendezvousProtocolVersion)
            return false;
        int index = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(36, 2));
        int count = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(38, 2));
        int payloadLength = BinaryPrimitives.ReadInt32BigEndian(body.Slice(40, 4));
        if (count is < 1 or > 64 || index >= count || payloadLength <= 0 || body.Length != DescriptorHeaderSize + payloadLength)
            return false;
        record = new PqcRendezvousDescriptorChunkRecord(new(
            PqcRendezvousCrypto.RendezvousProtocolVersion, body.Slice(4, 32).ToArray(), index, count,
            body[DescriptorHeaderSize..].ToArray()));
        return true;
    }

    private static bool TryDecodeRequest(ReadOnlySpan<byte> body, out PqcRendezvousWireRecord? record)
    {
        record = null;
        int minimum = NoksTemporaryNumber.DigitCount + 16 + 8 + 8 + 2 + 2 + PqcRendezvousCrypto.AesNonceSize +
            2 + PqcRendezvousCrypto.AesTagSize + 8;
        if (body.Length < minimum) return false;
        int offset = 0;
        string temporaryId = Encoding.ASCII.GetString(body[..NoksTemporaryNumber.DigitCount]); offset += NoksTemporaryNumber.DigitCount;
        if (!NoksTemporaryNumber.IsCanonical(temporaryId)) return false;
        byte[] descriptorId = body.Slice(offset, 16).ToArray(); offset += 16;
        long sequence = BinaryPrimitives.ReadInt64BigEndian(body.Slice(offset, 8)); offset += 8;
        long expires = BinaryPrimitives.ReadInt64BigEndian(body.Slice(offset, 8)); offset += 8;
        if (sequence <= 0 || expires <= 0 || !TryReadBytes(body, ref offset, out byte[] topicBytes) ||
            !TryReadBytes(body, ref offset, out byte[] challenge) || offset + PqcRendezvousCrypto.AesNonceSize > body.Length)
            return false;
        byte[] nonce = body.Slice(offset, PqcRendezvousCrypto.AesNonceSize).ToArray(); offset += nonce.Length;
        if (!TryReadBytes(body, ref offset, out byte[] ciphertext) ||
            offset + PqcRendezvousCrypto.AesTagSize + 8 != body.Length)
            return false;
        byte[] tag = body.Slice(offset, PqcRendezvousCrypto.AesTagSize).ToArray(); offset += tag.Length;
        ulong proof = BinaryPrimitives.ReadUInt64BigEndian(body.Slice(offset, 8));
        string topic;
        try { topic = Encoding.UTF8.GetString(topicBytes); }
        catch (ArgumentException) { return false; }
        if (string.IsNullOrWhiteSpace(topic) || challenge.Length == 0 || ciphertext.Length == 0) return false;
        record = new PqcRendezvousRequestRecord(new(temporaryId, descriptorId, sequence, expires, topic, challenge, nonce, ciphertext, tag, proof));
        return true;
    }

    private static bool TryReadBytes(ReadOnlySpan<byte> value, ref int offset, out byte[] result)
    {
        result = [];
        if (offset + 2 > value.Length) return false;
        int length = BinaryPrimitives.ReadUInt16BigEndian(value.Slice(offset, 2)); offset += 2;
        if (length <= 0 || offset + length > value.Length) return false;
        result = value.Slice(offset, length).ToArray(); offset += length;
        return true;
    }
}
