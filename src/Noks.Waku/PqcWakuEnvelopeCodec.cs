using System.Buffers.Binary;
using System.Security.Cryptography;
using Noks.Cryptography;

namespace Noks.Waku;

/// <summary>
/// This class defines a fully post-quantum envelope for direct messages.
/// ML-KEM-768 establishes a fresh secret for each packet. AES-256-GCM protects
/// the fixed-size plaintext. ML-DSA-65 authenticates the application message. The
/// compact routing fields are SHA-256 fingerprints, not elliptic-curve keys.
/// </summary>
public static class PqcWakuEnvelopeCodec
{
    public const int EnvelopeSize = 16384;
    public const int PlaintextSize = 14336;
    public const int MaximumPayloadSize = 8192;

    private const byte ProtocolVersion = 2;
    private const int ChallengeOffset = 8;
    private const int NonceOffset = ChallengeOffset + PqcRendezvousCrypto.MlKem768CiphertextSize;
    private const int CiphertextOffset = NonceOffset + PqcRendezvousCrypto.AesNonceSize;
    private const int TagOffset = CiphertextOffset + PlaintextSize;
    private const int PaddingOffset = TagOffset + PqcRendezvousCrypto.AesTagSize;
    private const int PaddingSize = EnvelopeSize - PaddingOffset;

    private const int SenderRoutingKeyOffset = 40;
    private const int SenderRoutingKeySize = 33;
    private const int RecipientRoutingKeyOffset = 73;
    private const int RecipientRoutingKeySize = 32;
    private const int PayloadLengthOffset = 105;
    private const int SigningPublicKeyOffset = 107;
    private const int SignatureLengthOffset =
        SigningPublicKeyOffset + PqcRendezvousCrypto.MlDsa65PublicKeySize;
    private const int SignatureOffset = SignatureLengthOffset + 2;
    private const int PayloadOffset =
        SignatureOffset + PqcRendezvousCrypto.MlDsa65SignatureSize;

    private static ReadOnlySpan<byte> EnvelopeMagic => "NQP2"u8;
    private static ReadOnlySpan<byte> MessageMagic => "NQM2"u8;
    private static ReadOnlySpan<byte> KeyDomain => "noks/waku/pqc-envelope/ml-kem-768/aes-256-gcm/v2"u8;
    private static ReadOnlySpan<byte> SignatureDomain => "noks/waku/pqc-message/ml-dsa-65/v2"u8;

    public static byte[] Encrypt(
        WakuApplicationMessage message,
        PqcRendezvousIdentity sender,
        ReadOnlySpan<byte> recipientMlKemPublicKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(sender);
        if (!PqcRendezvousCrypto.IsValidMlKem768PublicKey(recipientMlKemPublicKey))
            throw new ArgumentException("The recipient ML-KEM-768 public key is invalid.", nameof(recipientMlKemPublicKey));

        byte[] actualSenderRoutingKey =
            PqcContactCardCodec.CreateEnvelopeRoutingKey(sender.SigningPublicKey);
        byte[] actualRecipientRoutingKey =
            PqcContactCardCodec.CreateMailboxRoutingKey(recipientMlKemPublicKey);
        if (!CryptographicOperations.FixedTimeEquals(
                actualSenderRoutingKey,
                message.SenderIdentityPublicKey.Span))
        {
            throw new ArgumentException("The ML-DSA sender does not match the message routing identity.", nameof(sender));
        }
        if (!CryptographicOperations.FixedTimeEquals(
                actualRecipientRoutingKey,
                message.RecipientMailboxPublicKey.Span))
        {
            throw new ArgumentException("The ML-KEM recipient does not match the message routing identity.", nameof(recipientMlKemPublicKey));
        }

        byte[] plaintext = new byte[PlaintextSize];
        PqcKemEncapsulation? encapsulation = null;
        byte[]? key = null;
        try
        {
            EncodeAndSignPlaintext(message, sender, plaintext);
            encapsulation = PqcRendezvousCrypto.EncapsulateMlKem768(recipientMlKemPublicKey);
            key = PqcRendezvousCrypto.DeriveAes256Key(encapsulation.SharedSecret, KeyDomain);

            byte[] envelope = new byte[EnvelopeSize];
            EnvelopeMagic.CopyTo(envelope);
            envelope[4] = ProtocolVersion;
            envelope[5] = (byte)message.DeliveryClass;
            encapsulation.Ciphertext.CopyTo(envelope, ChallengeOffset);
            RandomNumberGenerator.Fill(envelope.AsSpan(NonceOffset, PqcRendezvousCrypto.AesNonceSize));
            RandomNumberGenerator.Fill(envelope.AsSpan(PaddingOffset, PaddingSize));

            byte[] associatedData = BuildAssociatedData(envelope);
            byte[] ciphertext = new byte[PlaintextSize];
            byte[] tag = new byte[PqcRendezvousCrypto.AesTagSize];
            try
            {
                PqcRendezvousCrypto.EncryptAes256Gcm(
                    key,
                    envelope.AsSpan(NonceOffset, PqcRendezvousCrypto.AesNonceSize).ToArray(),
                    plaintext,
                    associatedData,
                    ciphertext,
                    tag);
                ciphertext.CopyTo(envelope, CiphertextOffset);
                tag.CopyTo(envelope, TagOffset);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(associatedData);
                CryptographicOperations.ZeroMemory(ciphertext);
                CryptographicOperations.ZeroMemory(tag);
            }
            return envelope;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualSenderRoutingKey);
            CryptographicOperations.ZeroMemory(actualRecipientRoutingKey);
            CryptographicOperations.ZeroMemory(plaintext);
            if (encapsulation is not null)
                CryptographicOperations.ZeroMemory(encapsulation.SharedSecret);
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
        }
    }

    public static bool TryDecrypt(
        ReadOnlySpan<byte> envelope,
        PqcRendezvousIdentity recipient,
        out WakuApplicationMessage? message)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        message = null;
        if (!HasValidHeader(envelope))
            return false;

        byte[]? sharedSecret = null;
        byte[]? key = null;
        byte[] recipientRoutingKey =
            PqcContactCardCodec.CreateMailboxRoutingKey(recipient.ChallengePublicKey);
        byte[] plaintext = new byte[PlaintextSize];
        try
        {
            if (!PqcRendezvousCrypto.TryDecapsulateMlKem768(
                    recipient,
                    envelope.Slice(ChallengeOffset, PqcRendezvousCrypto.MlKem768CiphertextSize),
                    out sharedSecret))
            {
                return false;
            }
            key = PqcRendezvousCrypto.DeriveAes256Key(sharedSecret, KeyDomain);

            byte[] associatedData = BuildAssociatedData(envelope);
            try
            {
                if (!PqcRendezvousCrypto.TryDecryptAes256Gcm(
                        key,
                        envelope.Slice(NonceOffset, PqcRendezvousCrypto.AesNonceSize).ToArray(),
                        envelope.Slice(CiphertextOffset, PlaintextSize).ToArray(),
                        envelope.Slice(TagOffset, PqcRendezvousCrypto.AesTagSize).ToArray(),
                        associatedData,
                        plaintext))
                {
                    return false;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(associatedData);
            }

            if (!TryDecodeAndVerifyPlaintext(
                    plaintext,
                    recipientRoutingKey,
                    out WakuApplicationMessage? decoded) ||
                decoded is null ||
                (byte)decoded.DeliveryClass != envelope[5])
            {
                return false;
            }

            message = decoded;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            message = null;
            return false;
        }
        finally
        {
            if (sharedSecret is not null)
                CryptographicOperations.ZeroMemory(sharedSecret);
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(recipientRoutingKey);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void EncodeAndSignPlaintext(
        WakuApplicationMessage message,
        PqcRendezvousIdentity sender,
        Span<byte> plaintext)
    {
        if (message.Payload.Length > MaximumPayloadSize)
            throw new ArgumentException("The application payload is too large.", nameof(message));

        MessageMagic.CopyTo(plaintext);
        plaintext[4] = ProtocolVersion;
        plaintext[5] = (byte)message.Kind;
        message.EventId.TryWriteBytes(plaintext.Slice(8, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(
            plaintext.Slice(24, 8),
            message.IssuedAtUnixMilliseconds);
        BinaryPrimitives.WriteInt64BigEndian(
            plaintext.Slice(32, 8),
            message.ExpiresAtUnixMilliseconds);
        message.SenderIdentityPublicKey.Span.CopyTo(
            plaintext.Slice(SenderRoutingKeyOffset, SenderRoutingKeySize));
        message.RecipientMailboxPublicKey.Span.CopyTo(
            plaintext.Slice(RecipientRoutingKeyOffset, RecipientRoutingKeySize));
        BinaryPrimitives.WriteUInt16BigEndian(
            plaintext.Slice(PayloadLengthOffset, 2),
            checked((ushort)message.Payload.Length));
        sender.SigningPublicKey.CopyTo(
            plaintext.Slice(
                SigningPublicKeyOffset,
                PqcRendezvousCrypto.MlDsa65PublicKeySize));

        byte[] signedData = BuildSignedData(
            plaintext[..SignatureLengthOffset],
            message.Payload.Span);
        byte[] signature = PqcRendezvousCrypto.SignMlDsa65(sender, signedData);
        try
        {
            BinaryPrimitives.WriteUInt16BigEndian(
                plaintext.Slice(SignatureLengthOffset, 2),
                checked((ushort)signature.Length));
            signature.CopyTo(plaintext[SignatureOffset..]);
            message.Payload.Span.CopyTo(plaintext[PayloadOffset..]);
            RandomNumberGenerator.Fill(
                plaintext[(PayloadOffset + message.Payload.Length)..]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signedData);
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static bool TryDecodeAndVerifyPlaintext(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> expectedRecipientRoutingKey,
        out WakuApplicationMessage? message)
    {
        message = null;
        if (plaintext.Length != PlaintextSize ||
            !plaintext[..4].SequenceEqual(MessageMagic) ||
            plaintext[4] != ProtocolVersion ||
            plaintext[6] != 0 ||
            plaintext[7] != 0)
        {
            return false;
        }

        var kind = (WakuEventKind)plaintext[5];
        int payloadLength = BinaryPrimitives.ReadUInt16BigEndian(
            plaintext.Slice(PayloadLengthOffset, 2));
        int signatureLength = BinaryPrimitives.ReadUInt16BigEndian(
            plaintext.Slice(SignatureLengthOffset, 2));
        if (!WakuEventPolicy.IsKnown(kind) ||
            payloadLength > MaximumPayloadSize ||
            signatureLength != PqcRendezvousCrypto.MlDsa65SignatureSize ||
            !CryptographicOperations.FixedTimeEquals(
                plaintext.Slice(RecipientRoutingKeyOffset, RecipientRoutingKeySize),
                expectedRecipientRoutingKey))
        {
            return false;
        }

        ReadOnlySpan<byte> signingPublicKey = plaintext.Slice(
            SigningPublicKeyOffset,
            PqcRendezvousCrypto.MlDsa65PublicKeySize);
        byte[] expectedSenderRoutingKey =
            PqcContactCardCodec.CreateEnvelopeRoutingKey(signingPublicKey);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    plaintext.Slice(SenderRoutingKeyOffset, SenderRoutingKeySize),
                    expectedSenderRoutingKey))
            {
                return false;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedSenderRoutingKey);
        }

        ReadOnlySpan<byte> payload = plaintext.Slice(PayloadOffset, payloadLength);
        byte[] signedData = BuildSignedData(plaintext[..SignatureLengthOffset], payload);
        try
        {
            if (!PqcRendezvousCrypto.VerifyMlDsa65(
                    signingPublicKey,
                    signedData,
                    plaintext.Slice(SignatureOffset, signatureLength)))
            {
                return false;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signedData);
        }

        try
        {
            message = new WakuApplicationMessage(
                new Guid(plaintext.Slice(8, 16), bigEndian: true),
                kind,
                BinaryPrimitives.ReadInt64BigEndian(plaintext.Slice(24, 8)),
                BinaryPrimitives.ReadInt64BigEndian(plaintext.Slice(32, 8)),
                plaintext.Slice(SenderRoutingKeyOffset, SenderRoutingKeySize),
                plaintext.Slice(RecipientRoutingKeyOffset, RecipientRoutingKeySize),
                payload);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool HasValidHeader(ReadOnlySpan<byte> envelope) =>
        envelope.Length == EnvelopeSize &&
        envelope[..4].SequenceEqual(EnvelopeMagic) &&
        envelope[4] == ProtocolVersion &&
        envelope[5] <= (byte)WakuDeliveryClass.Realtime &&
        envelope[6] == 0 &&
        envelope[7] == 0;

    private static byte[] BuildAssociatedData(ReadOnlySpan<byte> envelope)
    {
        byte[] associatedData = new byte[CiphertextOffset + PaddingSize];
        envelope[..CiphertextOffset].CopyTo(associatedData);
        envelope.Slice(PaddingOffset, PaddingSize).CopyTo(
            associatedData.AsSpan(CiphertextOffset));
        return associatedData;
    }

    private static byte[] BuildSignedData(
        ReadOnlySpan<byte> header,
        ReadOnlySpan<byte> payload)
    {
        byte[] signedData = new byte[
            SignatureDomain.Length + header.Length + payload.Length];
        SignatureDomain.CopyTo(signedData);
        header.CopyTo(signedData.AsSpan(SignatureDomain.Length));
        payload.CopyTo(signedData.AsSpan(SignatureDomain.Length + header.Length));
        return signedData;
    }
}
