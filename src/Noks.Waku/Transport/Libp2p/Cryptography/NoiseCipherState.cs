using System.Buffers.Binary;
using System.Security.Cryptography;
using Noks.Cryptography;

namespace Noks.Waku.Transport.Libp2p.Cryptography;

internal sealed class NoiseCipherState
{
    private readonly byte[] key;
    private ulong nonce;

    public NoiseCipherState(ReadOnlySpan<byte> key)
    {
        if (key.Length != WakuCrypto.ChaCha20Poly1305KeySize)
            throw new ArgumentException("Noise cipher key must be 32 bytes.", nameof(key));
        this.key = key.ToArray();
    }

    public byte[] Encrypt(ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> plaintext)
    {
        if (nonce == ulong.MaxValue)
            throw new CryptographicException("Noise cipher nonce is exhausted.");

        Span<byte> encodedNonce = stackalloc byte[WakuCrypto.ChaCha20Poly1305NonceSize];
        BinaryPrimitives.WriteUInt64LittleEndian(encodedNonce[4..], nonce);
        byte[] ciphertext = new byte[plaintext.Length + WakuCrypto.ChaCha20Poly1305TagSize];
        WakuCrypto.ChaCha20Poly1305Encrypt(key, encodedNonce, plaintext, associatedData, ciphertext);
        nonce++;
        return ciphertext;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> ciphertext)
    {
        if (nonce == ulong.MaxValue)
            throw new CryptographicException("Noise cipher nonce is exhausted.");
        if (ciphertext.Length < WakuCrypto.ChaCha20Poly1305TagSize)
            throw new CryptographicException("Noise ciphertext is too short.");

        Span<byte> encodedNonce = stackalloc byte[WakuCrypto.ChaCha20Poly1305NonceSize];
        BinaryPrimitives.WriteUInt64LittleEndian(encodedNonce[4..], nonce);
        byte[] plaintext = new byte[ciphertext.Length - WakuCrypto.ChaCha20Poly1305TagSize];
        if (!WakuCrypto.TryChaCha20Poly1305Decrypt(key, encodedNonce, ciphertext, associatedData, plaintext))
            throw new CryptographicException("Noise ciphertext authentication failed.");
        nonce++;
        return plaintext;
    }
}
