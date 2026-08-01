using Noks.Waku.Transport.Libp2p.Cryptography;

namespace Noks.Waku.Tests.Transport.Libp2p.Cryptography;

public sealed class Libp2pIdentityTests
{
    [Fact]
    public void Secp256k1IdentityAuthenticatesNoiseStaticKey()
    {
        Libp2pIdentity identity = Libp2pIdentity.FromPrivateKey(
            Convert.FromHexString("67E5504410BBC9F6465C3E7A3E234E04B4E38E0B6F0F7AC34F8B3F8A6D4F12A1"));
        byte[] noiseStaticKey = Convert.FromHexString(
            "358072D6365880D1AEEA329AD016484123A6654DC290F13E6E7244F70C311A6D");

        byte[] payload = identity.CreateNoiseHandshakePayload(noiseStaticKey);
        NoiseRemoteIdentity decoded = Libp2pIdentity.DecodeAndVerifyNoiseHandshakePayload(
            payload,
            noiseStaticKey,
            identity.PublicKey);

        Assert.Equal(identity.PublicKey, decoded.PublicKey);
        Assert.Contains("/mplex/6.7.0", decoded.StreamMuxers);
        Assert.StartsWith("16Uiu2", identity.PeerId, StringComparison.Ordinal);
    }

    [Fact]
    public void NoisePayloadRejectsASelectedPeerMismatch()
    {
        Libp2pIdentity identity = Libp2pIdentity.Create();
        Libp2pIdentity other = Libp2pIdentity.Create();
        byte[] noiseStaticKey = new byte[32];
        noiseStaticKey[0] = 1;
        byte[] payload = identity.CreateNoiseHandshakePayload(noiseStaticKey);

        Assert.Throws<System.Security.Cryptography.CryptographicException>(() =>
            Libp2pIdentity.DecodeAndVerifyNoiseHandshakePayload(
                payload,
                noiseStaticKey,
                other.PublicKey));
    }

    [Fact]
    public void NoiseCipherStateUsesLittleEndianTransportNonce()
    {
        byte[] key = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        NoiseCipherState encrypt = new(key);
        NoiseCipherState decrypt = new(key);

        byte[] first = encrypt.Encrypt("aad"u8, "first"u8);
        byte[] second = encrypt.Encrypt("aad"u8, "second"u8);

        Assert.Equal("first"u8.ToArray(), decrypt.Decrypt("aad"u8, first));
        Assert.Equal("second"u8.ToArray(), decrypt.Decrypt("aad"u8, second));
        Assert.NotEqual(first, second);
    }
}
