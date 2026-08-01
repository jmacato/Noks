using System.Security.Cryptography;
using System.Text;
using Noks.Cryptography;

namespace Noks.Waku;

public static class NumberRendezvousEnvelopeCodec
{
    public const int PacketSize = WakuEnvelopeCodec.EnvelopeSize;

    private const int CoverPrefixOffset = 0;
    private const int CoverPrefixSize = 32;
    private const int SaltOffset = CoverPrefixOffset + CoverPrefixSize;
    private const int SaltSize = 16;
    private const int NonceOffset = SaltOffset + SaltSize;
    private const int HeaderSize = NonceOffset + WakuCrypto.ChaCha20Poly1305NonceSize;
    private const int CiphertextSize = PacketSize - HeaderSize;
    private const int PlaintextSize = CiphertextSize - WakuCrypto.ChaCha20Poly1305TagSize;
    private static ReadOnlySpan<byte> KeyInfo => "noks/waku/number-rendezvous/v1/chacha20poly1305"u8;

    public static byte[] Encrypt(
        WakuApplicationMessage message,
        ReadOnlySpan<byte> senderEnvelopePrivateKey,
        string targetNumber)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireTargetNumber(targetNumber);
        if (message.Kind != WakuEventKind.RendezvousRequest)
            throw new ArgumentException("Only rendezvous requests can use number encryption.", nameof(message));

        Span<byte> actualSenderPublicKey = stackalloc byte[WakuCrypto.Secp256k1CompressedPublicKeySize];
        WakuCrypto.GetSecp256k1PublicKey(senderEnvelopePrivateKey, actualSenderPublicKey);
        if (!CryptographicOperations.FixedTimeEquals(actualSenderPublicKey, message.SenderIdentityPublicKey.Span))
            throw new ArgumentException("The sender private key does not match the message identity.", nameof(senderEnvelopePrivateKey));

        byte[] packet = new byte[PacketSize];
        byte[] plaintext = new byte[PlaintextSize];
        Span<byte> keyMaterial = stackalloc byte[32];
        Span<byte> key = stackalloc byte[WakuCrypto.ChaCha20Poly1305KeySize];
        try
        {
            WakuEnvelopeCodec.EncodeAndSignPlaintext(
                message,
                senderEnvelopePrivateKey,
                plaintext.AsSpan(0, WakuEnvelopeCodec.PlaintextSize));
            RandomNumberGenerator.Fill(plaintext.AsSpan(WakuEnvelopeCodec.PlaintextSize));
            RandomNumberGenerator.Fill(packet.AsSpan(CoverPrefixOffset, CoverPrefixSize));
            // This matches the shape of an encoded X25519 public key. It does not use seed-derived material.
            packet[CoverPrefixOffset + CoverPrefixSize - 1] &= 0x7F;
            RandomNumberGenerator.Fill(packet.AsSpan(SaltOffset, SaltSize));
            RandomNumberGenerator.Fill(packet.AsSpan(NonceOffset, WakuCrypto.ChaCha20Poly1305NonceSize));
            DeriveKeyMaterial(targetNumber, keyMaterial);
            HkdfSha256.DeriveKey(keyMaterial, packet.AsSpan(SaltOffset, SaltSize), KeyInfo, key);
            WakuCrypto.ChaCha20Poly1305Encrypt(
                key,
                packet.AsSpan(NonceOffset, WakuCrypto.ChaCha20Poly1305NonceSize),
                plaintext,
                packet.AsSpan(0, HeaderSize),
                packet.AsSpan(HeaderSize, CiphertextSize));
            return packet;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualSenderPublicKey);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(keyMaterial);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static bool TryDecrypt(
        ReadOnlySpan<byte> packet,
        string targetNumber,
        out WakuApplicationMessage? message)
    {
        message = null;
        if (packet.Length != PacketSize || !NoksTemporaryNumber.IsCanonical(targetNumber))
            return false;

        byte[] plaintext = new byte[PlaintextSize];
        Span<byte> keyMaterial = stackalloc byte[32];
        Span<byte> key = stackalloc byte[WakuCrypto.ChaCha20Poly1305KeySize];
        try
        {
            DeriveKeyMaterial(targetNumber, keyMaterial);
            HkdfSha256.DeriveKey(keyMaterial, packet.Slice(SaltOffset, SaltSize), KeyInfo, key);
            if (!WakuCrypto.TryChaCha20Poly1305Decrypt(
                    key,
                    packet.Slice(NonceOffset, WakuCrypto.ChaCha20Poly1305NonceSize),
                    packet.Slice(HeaderSize, CiphertextSize),
                    packet[..HeaderSize],
                    plaintext))
            {
                return false;
            }

            if (!WakuEnvelopeCodec.TryDecodeAndVerifyPlaintext(
                    plaintext.AsSpan(0, WakuEnvelopeCodec.PlaintextSize),
                    expectedRecipientPublicKey: plaintext.AsSpan(73, WakuCrypto.X25519KeySize),
                    out WakuApplicationMessage? decoded) ||
                decoded is null ||
                decoded.Kind != WakuEventKind.RendezvousRequest)
            {
                return false;
            }

            message = decoded;
            return true;
        }
        catch (ArgumentException)
        {
            message = null;
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(keyMaterial);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void DeriveKeyMaterial(string targetNumber, Span<byte> destination)
    {
        Span<byte> number = stackalloc byte[NoksTemporaryNumber.DigitCount];
        Encoding.ASCII.GetBytes(targetNumber, number);
        SHA256.HashData(number, destination);
        CryptographicOperations.ZeroMemory(number);
    }

    private static void RequireTargetNumber(string targetNumber)
    {
        if (!NoksTemporaryNumber.IsCanonical(targetNumber))
            throw new FormatException($"A rendezvous number must contain exactly {NoksTemporaryNumber.DigitCount} digits.");
    }
}
