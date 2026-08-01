using System.Security.Cryptography;
using Noks.Cryptography;

namespace Noks.Waku.Transport.Libp2p.Cryptography;

internal sealed class NoiseSession
{
    public const string Protocol = "/noise";
    private const string HandshakeProtocol = "Noise_XX_25519_ChaChaPoly_SHA256";

    private readonly Libp2pIdentity identity;
    private readonly byte[] expectedRemoteIdentityPublicKey;
    private readonly byte[] localStaticPrivateKey = new byte[WakuCrypto.X25519KeySize];
    private readonly byte[] localStaticPublicKey = new byte[WakuCrypto.X25519KeySize];
    private readonly NoiseSymmetricState symmetric = new(HandshakeProtocol);
    private byte[]? localEphemeralPrivateKey;
    private byte[]? remoteEphemeralPublicKey;
    private byte[]? remoteStaticPublicKey;
    private NoiseCipherState? encryptCipher;
    private NoiseCipherState? decryptCipher;

    public IReadOnlySet<string> RemoteStreamMuxers { get; private set; } =
        new HashSet<string>(StringComparer.Ordinal);

    public NoiseSession(Libp2pIdentity identity, ReadOnlySpan<byte> expectedRemoteIdentityPublicKey)
    {
        this.identity = identity;
        this.expectedRemoteIdentityPublicKey = expectedRemoteIdentityPublicKey.ToArray();
        WakuCrypto.GenerateX25519PrivateKey(localStaticPrivateKey);
        WakuCrypto.GetX25519PublicKey(localStaticPrivateKey, localStaticPublicKey);
        symmetric.MixHash([]);
    }

    public byte[] WriteMessageA()
    {
        if (localEphemeralPrivateKey is not null)
            throw new InvalidOperationException("The Noise handshake is already active.");

        localEphemeralPrivateKey = new byte[WakuCrypto.X25519KeySize];
        byte[] localEphemeralPublicKey = new byte[WakuCrypto.X25519KeySize];
        WakuCrypto.GenerateX25519PrivateKey(localEphemeralPrivateKey);
        WakuCrypto.GetX25519PublicKey(localEphemeralPrivateKey, localEphemeralPublicKey);
        symmetric.MixHash(localEphemeralPublicKey);
        _ = symmetric.EncryptAndHash([]);
        return localEphemeralPublicKey;
    }

    public void ReadMessageB(ReadOnlySpan<byte> message)
    {
        if (localEphemeralPrivateKey is null)
            throw new InvalidOperationException("Noise message A is missing.");
        if (remoteEphemeralPublicKey is not null)
            throw new InvalidOperationException("Noise message B was already read.");
        if (message.Length < 32 + 48 + 16)
            throw new CryptographicException("Noise message B is too short.");

        remoteEphemeralPublicKey = message[..32].ToArray();
        symmetric.MixHash(remoteEphemeralPublicKey);
        MixDh(localEphemeralPrivateKey, remoteEphemeralPublicKey);

        remoteStaticPublicKey = symmetric.DecryptAndHash(message.Slice(32, 48));
        if (remoteStaticPublicKey.Length != WakuCrypto.X25519KeySize)
            throw new CryptographicException("Noise peer returned an invalid static key.");
        MixDh(localEphemeralPrivateKey, remoteStaticPublicKey);

        byte[] payload = symmetric.DecryptAndHash(message[(32 + 48)..]);
        NoiseRemoteIdentity remoteIdentity = Libp2pIdentity.DecodeAndVerifyNoiseHandshakePayload(
            payload,
            remoteStaticPublicKey,
            expectedRemoteIdentityPublicKey);
        RemoteStreamMuxers = remoteIdentity.StreamMuxers;
    }

    public byte[] WriteMessageC()
    {
        if (remoteEphemeralPublicKey is null || remoteStaticPublicKey is null)
            throw new InvalidOperationException("Noise message B was not read.");
        if (encryptCipher is not null)
            throw new InvalidOperationException("Noise handshake is already complete.");

        byte[] encryptedStaticKey = symmetric.EncryptAndHash(localStaticPublicKey);
        MixDh(localStaticPrivateKey, remoteEphemeralPublicKey);
        byte[] payload = identity.CreateNoiseHandshakePayload(localStaticPublicKey);
        byte[] encryptedPayload = symmetric.EncryptAndHash(payload);

        byte[] result = new byte[encryptedStaticKey.Length + encryptedPayload.Length];
        encryptedStaticKey.CopyTo(result, 0);
        encryptedPayload.CopyTo(result, encryptedStaticKey.Length);

        (NoiseCipherState first, NoiseCipherState second) = symmetric.Split();
        encryptCipher = first;
        decryptCipher = second;
        return result;
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext) =>
        (encryptCipher ?? throw new InvalidOperationException("Noise handshake is incomplete."))
        .Encrypt([], plaintext);

    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext) =>
        (decryptCipher ?? throw new InvalidOperationException("Noise handshake is incomplete."))
        .Decrypt([], ciphertext);

    private void MixDh(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey)
    {
        Span<byte> sharedSecret = stackalloc byte[WakuCrypto.X25519KeySize];
        if (!WakuCrypto.TryX25519Agreement(privateKey, publicKey, sharedSecret))
            throw new CryptographicException("Noise X25519 agreement failed.");
        try
        {
            symmetric.MixKey(sharedSecret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }
}
