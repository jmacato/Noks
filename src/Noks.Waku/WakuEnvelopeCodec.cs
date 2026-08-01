using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Noks.Cryptography;

namespace Noks.Waku;

public static class WakuEnvelopeCodec
{
    public const int EnvelopeSize = 2304;
    public const int PlaintextSize = 2048;
    public const int MaximumPayloadSize = 1868;

    private const byte ProtocolVersion = 1;
    private const int EnvelopeHeaderSize = 68;
    private const int EnvelopeCiphertextSize = PlaintextSize + WakuCrypto.ChaCha20Poly1305TagSize;
    private const int EnvelopePaddingOffset = EnvelopeHeaderSize + EnvelopeCiphertextSize;
    private const int EnvelopePaddingSize = EnvelopeSize - EnvelopePaddingOffset;
    private const int PlaintextHeaderSize = 107;
    private const int SignatureLengthOffset = 107;
    private const int SignatureOffset = 108;
    private const int SignatureFieldSize = 72;
    private const int PayloadOffset = SignatureOffset + SignatureFieldSize;

    private static ReadOnlySpan<byte> EnvelopeMagic => "NWE1"u8;
    private static ReadOnlySpan<byte> MessageMagic => "NWM1"u8;
    private static ReadOnlySpan<byte> KeyInfo => "noks/waku/envelope/v1/chacha20poly1305"u8;

    public static byte[] Encrypt(
        WakuApplicationMessage message,
        ReadOnlySpan<byte> senderIdentityPrivateKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Payload.Length > MaximumPayloadSize)
            throw new ArgumentException($"Payload cannot exceed {MaximumPayloadSize} bytes.", nameof(message));

        Span<byte> actualSenderPublicKey = stackalloc byte[WakuCrypto.Secp256k1CompressedPublicKeySize];
        WakuCrypto.GetSecp256k1PublicKey(senderIdentityPrivateKey, actualSenderPublicKey);
        if (!CryptographicOperations.FixedTimeEquals(actualSenderPublicKey, message.SenderIdentityPublicKey.Span))
            throw new ArgumentException("The sender private key does not match the message identity.", nameof(senderIdentityPrivateKey));

        var plaintext = new byte[PlaintextSize];
        var ephemeralPrivateKey = new byte[WakuCrypto.X25519KeySize];
        var sharedSecret = new byte[WakuCrypto.X25519KeySize];
        var key = new byte[WakuCrypto.ChaCha20Poly1305KeySize];
        try
        {
            EncodeAndSignPlaintext(message, senderIdentityPrivateKey, plaintext);

            var envelope = new byte[EnvelopeSize];
            EnvelopeMagic.CopyTo(envelope);
            envelope[4] = ProtocolVersion;
            envelope[5] = (byte)message.DeliveryClass;

            WakuCrypto.GenerateX25519PrivateKey(ephemeralPrivateKey);
            WakuCrypto.GetX25519PublicKey(ephemeralPrivateKey, envelope.AsSpan(8, WakuCrypto.X25519KeySize));
            RandomNumberGenerator.Fill(envelope.AsSpan(40, 16));
            RandomNumberGenerator.Fill(envelope.AsSpan(56, WakuCrypto.ChaCha20Poly1305NonceSize));
            RandomNumberGenerator.Fill(envelope.AsSpan(EnvelopePaddingOffset, EnvelopePaddingSize));

            if (!WakuCrypto.TryX25519Agreement(ephemeralPrivateKey, message.RecipientMailboxPublicKey.Span, sharedSecret))
                throw new CryptographicException("The recipient mailbox public key is invalid.");
            HkdfSha256.DeriveKey(sharedSecret, envelope.AsSpan(40, 16), KeyInfo, key);

            var associatedData = BuildAssociatedData(envelope);
            try
            {
                WakuCrypto.ChaCha20Poly1305Encrypt(
                    key,
                    envelope.AsSpan(56, WakuCrypto.ChaCha20Poly1305NonceSize),
                    plaintext,
                    associatedData,
                    envelope.AsSpan(EnvelopeHeaderSize, EnvelopeCiphertextSize));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(associatedData);
            }

            return envelope;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualSenderPublicKey);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ephemeralPrivateKey);
            CryptographicOperations.ZeroMemory(sharedSecret);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static bool TryDecrypt(
        ReadOnlySpan<byte> envelope,
        ReadOnlySpan<byte> recipientMailboxPrivateKey,
        out WakuApplicationMessage? message)
    {
        message = null;
        if (!HasValidEnvelopeHeader(envelope))
            return false;

        var recipientPublicKey = new byte[WakuCrypto.X25519KeySize];
        var sharedSecret = new byte[WakuCrypto.X25519KeySize];
        var key = new byte[WakuCrypto.ChaCha20Poly1305KeySize];
        var plaintext = new byte[PlaintextSize];
        try
        {
            WakuCrypto.GetX25519PublicKey(recipientMailboxPrivateKey, recipientPublicKey);
            if (!WakuCrypto.TryX25519Agreement(
                    recipientMailboxPrivateKey,
                    envelope.Slice(8, WakuCrypto.X25519KeySize),
                    sharedSecret))
                return false;

            HkdfSha256.DeriveKey(sharedSecret, envelope.Slice(40, 16), KeyInfo, key);
            var associatedData = BuildAssociatedData(envelope);
            try
            {
                if (!WakuCrypto.TryChaCha20Poly1305Decrypt(
                        key,
                        envelope.Slice(56, WakuCrypto.ChaCha20Poly1305NonceSize),
                        envelope.Slice(EnvelopeHeaderSize, EnvelopeCiphertextSize),
                        associatedData,
                        plaintext))
                    return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(associatedData);
            }

            if (!TryDecodeAndVerifyPlaintext(plaintext, recipientPublicKey, out var decodedMessage) || decodedMessage is null)
                return false;
            if ((byte)decodedMessage.DeliveryClass != envelope[5])
            {
                return false;
            }

            message = decodedMessage;
            return true;
        }
        catch (ArgumentException)
        {
            message = null;
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recipientPublicKey);
            CryptographicOperations.ZeroMemory(sharedSecret);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static void EncodeAndSignPlaintext(
        WakuApplicationMessage message,
        ReadOnlySpan<byte> senderIdentityPrivateKey,
        Span<byte> plaintext)
    {
        MessageMagic.CopyTo(plaintext);
        plaintext[4] = ProtocolVersion;
        plaintext[5] = (byte)message.Kind;
        message.EventId.TryWriteBytes(plaintext.Slice(8, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(plaintext.Slice(24, 8), message.IssuedAtUnixMilliseconds);
        BinaryPrimitives.WriteInt64BigEndian(plaintext.Slice(32, 8), message.ExpiresAtUnixMilliseconds);
        message.SenderIdentityPublicKey.Span.CopyTo(plaintext.Slice(40, WakuCrypto.Secp256k1CompressedPublicKeySize));
        message.RecipientMailboxPublicKey.Span.CopyTo(plaintext.Slice(73, WakuCrypto.X25519KeySize));
        BinaryPrimitives.WriteUInt16BigEndian(plaintext.Slice(105, 2), checked((ushort)message.Payload.Length));

        var signedData = BuildSignedData(plaintext[..PlaintextHeaderSize], message.Payload.Span);
        byte[] signature;
        try
        {
            signature = WakuCrypto.SignSecp256k1Sha256(senderIdentityPrivateKey, signedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signedData);
        }

        if (signature.Length > SignatureFieldSize)
            throw new CryptographicException("The secp256k1 signature does not fit the envelope signature field.");
        plaintext[SignatureLengthOffset] = checked((byte)signature.Length);
        signature.CopyTo(plaintext[SignatureOffset..]);
        CryptographicOperations.ZeroMemory(signature);
        message.Payload.Span.CopyTo(plaintext[PayloadOffset..]);
        RandomNumberGenerator.Fill(plaintext[(PayloadOffset + message.Payload.Length)..]);
    }

    internal static bool TryDecodeAndVerifyPlaintext(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> expectedRecipientPublicKey,
        out WakuApplicationMessage? message)
    {
        message = null;
        if (!plaintext[..4].SequenceEqual(MessageMagic) || plaintext[4] != ProtocolVersion ||
            plaintext[6] != 0 || plaintext[7] != 0)
            return false;

        var kind = (WakuEventKind)plaintext[5];
        if (!WakuEventPolicy.IsKnown(kind))
            return false;

        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(plaintext.Slice(105, 2));
        var signatureLength = plaintext[SignatureLengthOffset];
        if (payloadLength > MaximumPayloadSize || signatureLength == 0 || signatureLength > SignatureFieldSize)
            return false;
        if (!plaintext.Slice(SignatureOffset + signatureLength, SignatureFieldSize - signatureLength).IsEmpty &&
            ContainsNonZero(plaintext.Slice(SignatureOffset + signatureLength, SignatureFieldSize - signatureLength)))
            return false;

        var recipientPublicKey = plaintext.Slice(73, WakuCrypto.X25519KeySize);
        if (!CryptographicOperations.FixedTimeEquals(recipientPublicKey, expectedRecipientPublicKey))
            return false;

        var payload = plaintext.Slice(PayloadOffset, payloadLength);
        var signedData = BuildSignedData(plaintext[..PlaintextHeaderSize], payload);
        try
        {
            if (!WakuCrypto.VerifySecp256k1Sha256(
                    plaintext.Slice(40, WakuCrypto.Secp256k1CompressedPublicKeySize),
                    signedData,
                    plaintext.Slice(SignatureOffset, signatureLength)))
                return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signedData);
        }

        var eventId = new Guid(plaintext.Slice(8, 16), bigEndian: true);
        var issuedAt = BinaryPrimitives.ReadInt64BigEndian(plaintext.Slice(24, 8));
        var expiresAt = BinaryPrimitives.ReadInt64BigEndian(plaintext.Slice(32, 8));
        try
        {
            message = new WakuApplicationMessage(
                eventId,
                kind,
                issuedAt,
                expiresAt,
                plaintext.Slice(40, WakuCrypto.Secp256k1CompressedPublicKeySize),
                recipientPublicKey,
                payload);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool HasValidEnvelopeHeader(ReadOnlySpan<byte> envelope) =>
        envelope.Length == EnvelopeSize &&
        envelope[..4].SequenceEqual(EnvelopeMagic) &&
        envelope[4] == ProtocolVersion &&
        envelope[5] <= (byte)WakuDeliveryClass.Realtime &&
        envelope[6] == 0 &&
        envelope[7] == 0;

    private static byte[] BuildAssociatedData(ReadOnlySpan<byte> envelope)
    {
        var associatedData = new byte[EnvelopeHeaderSize + EnvelopePaddingSize];
        envelope[..EnvelopeHeaderSize].CopyTo(associatedData);
        envelope.Slice(EnvelopePaddingOffset, EnvelopePaddingSize).CopyTo(associatedData.AsSpan(EnvelopeHeaderSize));
        return associatedData;
    }

    private static byte[] BuildSignedData(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
    {
        var domain = "noks/waku/message/v1"u8;
        var signedData = new byte[domain.Length + header.Length + payload.Length];
        domain.CopyTo(signedData);
        header.CopyTo(signedData.AsSpan(domain.Length));
        payload.CopyTo(signedData.AsSpan(domain.Length + header.Length));
        return signedData;
    }

    private static bool ContainsNonZero(ReadOnlySpan<byte> value)
    {
        byte aggregate = 0;
        foreach (var item in value)
            aggregate |= item;
        return aggregate != 0;
    }
}
