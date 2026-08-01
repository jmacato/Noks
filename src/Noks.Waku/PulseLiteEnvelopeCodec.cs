using System.Security.Cryptography;
using Noks.Cryptography;

namespace Noks.Waku;

public static class PulseLiteEnvelopeCodec
{
    public const int PacketSize = WakuEnvelopeCodec.EnvelopeSize;

    private const int EphemeralPublicKeyOffset = 0;
    private const int SaltOffset = EphemeralPublicKeyOffset + WakuCrypto.X25519KeySize;
    private const int NonceOffset = SaltOffset + 16;
    private const int HeaderSize = NonceOffset + WakuCrypto.ChaCha20Poly1305NonceSize;
    private const int CiphertextSize = PacketSize - HeaderSize;
    private const int EncryptedPlaintextSize = CiphertextSize - WakuCrypto.ChaCha20Poly1305TagSize;

    private static ReadOnlySpan<byte> KeyInfo => "noks/waku/pulse-lite/v1/chacha20poly1305"u8;

    public static byte[] Encrypt(
        WakuApplicationMessage message,
        ReadOnlySpan<byte> senderIdentityPrivateKey)
    {
        ArgumentNullException.ThrowIfNull(message);

        Span<byte> actualSenderPublicKey = stackalloc byte[WakuCrypto.Secp256k1CompressedPublicKeySize];
        WakuCrypto.GetSecp256k1PublicKey(senderIdentityPrivateKey, actualSenderPublicKey);
        if (!CryptographicOperations.FixedTimeEquals(actualSenderPublicKey, message.SenderIdentityPublicKey.Span))
            throw new ArgumentException("The sender private key does not match the message identity.", nameof(senderIdentityPrivateKey));

        var plaintext = new byte[EncryptedPlaintextSize];
        var ephemeralPrivateKey = new byte[WakuCrypto.X25519KeySize];
        var sharedSecret = new byte[WakuCrypto.X25519KeySize];
        var key = new byte[WakuCrypto.ChaCha20Poly1305KeySize];
        try
        {
            RandomNumberGenerator.Fill(plaintext.AsSpan(WakuEnvelopeCodec.PlaintextSize));
            WakuEnvelopeCodec.EncodeAndSignPlaintext(
                message,
                senderIdentityPrivateKey,
                plaintext.AsSpan(0, WakuEnvelopeCodec.PlaintextSize));

            var packet = new byte[PacketSize];
            WakuCrypto.GenerateX25519PrivateKey(ephemeralPrivateKey);
            WakuCrypto.GetX25519PublicKey(
                ephemeralPrivateKey,
                packet.AsSpan(EphemeralPublicKeyOffset, WakuCrypto.X25519KeySize));
            RandomNumberGenerator.Fill(packet.AsSpan(SaltOffset, 16));
            RandomNumberGenerator.Fill(packet.AsSpan(NonceOffset, WakuCrypto.ChaCha20Poly1305NonceSize));

            if (!WakuCrypto.TryX25519Agreement(ephemeralPrivateKey, message.RecipientMailboxPublicKey.Span, sharedSecret))
                throw new CryptographicException("The recipient mailbox public key is invalid.");
            HkdfSha256.DeriveKey(sharedSecret, packet.AsSpan(SaltOffset, 16), KeyInfo, key);

            var associatedData = BuildAssociatedData(packet);
            try
            {
                WakuCrypto.ChaCha20Poly1305Encrypt(
                    key,
                    packet.AsSpan(NonceOffset, WakuCrypto.ChaCha20Poly1305NonceSize),
                    plaintext,
                    associatedData,
                    packet.AsSpan(HeaderSize, CiphertextSize));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(associatedData);
            }

            return packet;
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
        ReadOnlySpan<byte> packet,
        ReadOnlySpan<byte> recipientMailboxPrivateKey,
        out WakuApplicationMessage? message)
    {
        message = null;
        if (packet.Length != PacketSize)
            return false;

        var recipientPublicKey = new byte[WakuCrypto.X25519KeySize];
        var sharedSecret = new byte[WakuCrypto.X25519KeySize];
        var key = new byte[WakuCrypto.ChaCha20Poly1305KeySize];
        var plaintext = new byte[EncryptedPlaintextSize];
        try
        {
            WakuCrypto.GetX25519PublicKey(recipientMailboxPrivateKey, recipientPublicKey);
            if (!WakuCrypto.TryX25519Agreement(
                    recipientMailboxPrivateKey,
                    packet.Slice(EphemeralPublicKeyOffset, WakuCrypto.X25519KeySize),
                    sharedSecret))
                return false;

            HkdfSha256.DeriveKey(sharedSecret, packet.Slice(SaltOffset, 16), KeyInfo, key);
            var associatedData = BuildAssociatedData(packet);
            try
            {
                if (!WakuCrypto.TryChaCha20Poly1305Decrypt(
                        key,
                        packet.Slice(NonceOffset, WakuCrypto.ChaCha20Poly1305NonceSize),
                        packet.Slice(HeaderSize, CiphertextSize),
                        associatedData,
                        plaintext))
                    return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(associatedData);
            }

            return WakuEnvelopeCodec.TryDecodeAndVerifyPlaintext(
                plaintext.AsSpan(0, WakuEnvelopeCodec.PlaintextSize),
                recipientPublicKey,
                out message);
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

    private static byte[] BuildAssociatedData(ReadOnlySpan<byte> packet)
    {
        return packet[..HeaderSize].ToArray();
    }
}
