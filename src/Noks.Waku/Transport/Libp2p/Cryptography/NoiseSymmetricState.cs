using System.Security.Cryptography;
using Noks.Cryptography;

namespace Noks.Waku.Transport.Libp2p.Cryptography;

internal sealed class NoiseSymmetricState
{
    private byte[] chainingKey;
    private byte[] handshakeHash;
    private NoiseCipherState? cipher;

    public NoiseSymmetricState(string protocolName)
    {
        byte[] name = System.Text.Encoding.ASCII.GetBytes(protocolName);
        handshakeHash = name.Length <= 32 ? new byte[32] : SHA256.HashData(name);
        if (name.Length <= 32)
            name.CopyTo(handshakeHash, 0);
        chainingKey = handshakeHash.ToArray();
    }

    public void MixHash(ReadOnlySpan<byte> data)
    {
        byte[] combined = new byte[handshakeHash.Length + data.Length];
        handshakeHash.CopyTo(combined, 0);
        data.CopyTo(combined.AsSpan(handshakeHash.Length));
        handshakeHash = SHA256.HashData(combined);
    }

    public void MixKey(ReadOnlySpan<byte> inputKeyMaterial)
    {
        (byte[] nextChainingKey, byte[] cipherKey, _) = Hkdf(chainingKey, inputKeyMaterial);
        chainingKey = nextChainingKey;
        cipher = new NoiseCipherState(cipherKey);
        CryptographicOperations.ZeroMemory(cipherKey);
    }

    public byte[] EncryptAndHash(ReadOnlySpan<byte> plaintext)
    {
        byte[] ciphertext = cipher?.Encrypt(handshakeHash, plaintext) ?? plaintext.ToArray();
        MixHash(ciphertext);
        return ciphertext;
    }

    public byte[] DecryptAndHash(ReadOnlySpan<byte> ciphertext)
    {
        byte[] plaintext = cipher?.Decrypt(handshakeHash, ciphertext) ?? ciphertext.ToArray();
        MixHash(ciphertext);
        return plaintext;
    }

    public (NoiseCipherState First, NoiseCipherState Second) Split()
    {
        (byte[] first, byte[] second, _) = Hkdf(chainingKey, []);
        NoiseCipherState firstCipher = new(first);
        NoiseCipherState secondCipher = new(second);
        CryptographicOperations.ZeroMemory(first);
        CryptographicOperations.ZeroMemory(second);
        return (firstCipher, secondCipher);
    }

    private static (byte[] First, byte[] Second, byte[] Third) Hkdf(
        ReadOnlySpan<byte> chainingKey,
        ReadOnlySpan<byte> inputKeyMaterial)
    {
        byte[] temporaryKey = HMACSHA256.HashData(chainingKey, inputKeyMaterial);
        byte[] first = HMACSHA256.HashData(temporaryKey, new byte[] { 1 });

        byte[] secondInput = new byte[first.Length + 1];
        first.CopyTo(secondInput, 0);
        secondInput[^1] = 2;
        byte[] second = HMACSHA256.HashData(temporaryKey, secondInput);

        byte[] thirdInput = new byte[second.Length + 1];
        second.CopyTo(thirdInput, 0);
        thirdInput[^1] = 3;
        byte[] third = HMACSHA256.HashData(temporaryKey, thirdInput);

        CryptographicOperations.ZeroMemory(temporaryKey);
        CryptographicOperations.ZeroMemory(secondInput);
        CryptographicOperations.ZeroMemory(thirdInput);
        return (first, second, third);
    }
}
