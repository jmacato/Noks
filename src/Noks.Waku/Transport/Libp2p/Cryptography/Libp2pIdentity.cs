using System.Security.Cryptography;
using Noks.Cryptography;
using Noks.Waku.Transport.Libp2p.Discovery;
using Noks.Waku.Transport.Libp2p.Wire;

namespace Noks.Waku.Transport.Libp2p.Cryptography;

internal sealed class Libp2pIdentity
{
    private const uint Secp256k1KeyType = 2;
    private static ReadOnlySpan<byte> NoiseSignaturePrefix => "noise-libp2p-static-key:"u8;

    private Libp2pIdentity(byte[] privateKey, byte[] publicKey)
    {
        PrivateKey = privateKey;
        PublicKey = publicKey;
        ProtobufPublicKey = EncodePublicKey(publicKey);

        byte[] identityMultihash = new byte[2 + ProtobufPublicKey.Length];
        identityMultihash[0] = 0;
        identityMultihash[1] = checked((byte)ProtobufPublicKey.Length);
        ProtobufPublicKey.CopyTo(identityMultihash, 2);
        PeerId = Base58Btc.Encode(identityMultihash);
    }

    public byte[] PrivateKey { get; }

    public byte[] PublicKey { get; }

    public byte[] ProtobufPublicKey { get; }

    public string PeerId { get; }

    public static Libp2pIdentity Create()
    {
        byte[] privateKey = new byte[WakuCrypto.Secp256k1PrivateKeySize];
        byte[] publicKey = new byte[WakuCrypto.Secp256k1CompressedPublicKeySize];
        WakuCrypto.GenerateSecp256k1PrivateKey(privateKey);
        WakuCrypto.GetSecp256k1PublicKey(privateKey, publicKey);
        return new Libp2pIdentity(privateKey, publicKey);
    }

    public static Libp2pIdentity FromPrivateKey(ReadOnlySpan<byte> privateKey)
    {
        byte[] ownedPrivateKey = privateKey.ToArray();
        byte[] publicKey = new byte[WakuCrypto.Secp256k1CompressedPublicKeySize];
        WakuCrypto.GetSecp256k1PublicKey(ownedPrivateKey, publicKey);
        return new Libp2pIdentity(ownedPrivateKey, publicKey);
    }

    public byte[] CreateNoiseHandshakePayload(ReadOnlySpan<byte> noiseStaticPublicKey)
    {
        byte[] signatureMessage = new byte[NoiseSignaturePrefix.Length + noiseStaticPublicKey.Length];
        NoiseSignaturePrefix.CopyTo(signatureMessage);
        noiseStaticPublicKey.CopyTo(signatureMessage.AsSpan(NoiseSignaturePrefix.Length));
        byte[] signature;
        try
        {
            signature = WakuCrypto.SignSecp256k1Sha256(PrivateKey, signatureMessage);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signatureMessage);
        }

        ProtobufWriter payload = new();
        payload.WriteBytes(1, ProtobufPublicKey);
        payload.WriteBytes(2, signature);
        payload.WriteMessage(4, extensions => extensions.WriteString(2, MplexProtocol.Protocol));
        return payload.ToArray();
    }

    public static NoiseRemoteIdentity DecodeAndVerifyNoiseHandshakePayload(
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> remoteNoiseStaticPublicKey,
        ReadOnlySpan<byte> expectedIdentityPublicKey)
    {
        byte[]? protobufIdentity = null;
        byte[]? signature = null;
        HashSet<string> streamMuxers = new(StringComparer.Ordinal);
        ProtobufReader reader = new(payload);
        while (reader.TryReadTag(out int field, out int wireType))
        {
            switch (field)
            {
                case 1 when wireType == 2:
                    protobufIdentity = reader.ReadBytes();
                    break;
                case 2 when wireType == 2:
                    signature = reader.ReadBytes();
                    break;
                case 4 when wireType == 2:
                    DecodeExtensions(reader.ReadBytes(), streamMuxers);
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        byte[] publicKey = DecodePublicKey(protobufIdentity ??
            throw new CryptographicException("Noise payload has no identity key."));
        if (!publicKey.AsSpan().SequenceEqual(expectedIdentityPublicKey))
            throw new CryptographicException("Noise identity does not match the selected Waku peer.");
        if (signature is null)
            throw new CryptographicException("Noise payload has no identity signature.");

        byte[] signatureMessage = new byte[NoiseSignaturePrefix.Length + remoteNoiseStaticPublicKey.Length];
        NoiseSignaturePrefix.CopyTo(signatureMessage);
        remoteNoiseStaticPublicKey.CopyTo(signatureMessage.AsSpan(NoiseSignaturePrefix.Length));
        try
        {
            if (!WakuCrypto.VerifySecp256k1Sha256(publicKey, signatureMessage, signature))
                throw new CryptographicException("Noise identity signature is invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signatureMessage);
        }

        return new NoiseRemoteIdentity(publicKey, streamMuxers);
    }

    private static byte[] EncodePublicKey(ReadOnlySpan<byte> publicKey)
    {
        ProtobufWriter writer = new();
        writer.WriteUInt32(1, Secp256k1KeyType);
        writer.WriteBytes(2, publicKey);
        return writer.ToArray();
    }

    private static byte[] DecodePublicKey(byte[] encoded)
    {
        uint? keyType = null;
        byte[]? publicKey = null;
        ProtobufReader reader = new(encoded);
        while (reader.TryReadTag(out int field, out int wireType))
        {
            switch (field)
            {
                case 1 when wireType == 0:
                    keyType = reader.ReadUInt32();
                    break;
                case 2 when wireType == 2:
                    publicKey = reader.ReadBytes();
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        if (keyType != Secp256k1KeyType ||
            publicKey is not { Length: WakuCrypto.Secp256k1CompressedPublicKeySize } ||
            publicKey[0] is not (0x02 or 0x03))
        {
            throw new CryptographicException("Noise peer does not use a valid secp256k1 identity.");
        }

        return publicKey;
    }

    private static void DecodeExtensions(ReadOnlySpan<byte> encoded, HashSet<string> streamMuxers)
    {
        ProtobufReader reader = new(encoded);
        while (reader.TryReadTag(out int field, out int wireType))
        {
            if (field == 2 && wireType == 2)
                streamMuxers.Add(reader.ReadString());
            else
                reader.Skip(wireType);
        }
    }
}
