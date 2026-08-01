using Noks.Waku.Transport.Libp2p.Cryptography;
using Noks.Waku.Transport.Libp2p.Protocols;

namespace Noks.Waku.Tests.Transport.Libp2p.Protocols;

public sealed class Libp2pIdentifyTests
{
    [Fact]
    public void EncodesIdentityAndAdvertisedProtocols()
    {
        Libp2pIdentity identity = Libp2pIdentity.FromPrivateKey(
            Convert.FromHexString("67E5504410BBC9F6465C3E7A3E234E04B4E38E0B6F0F7AC34F8B3F8A6D4F12A1"));

        byte[] encoded = Libp2pIdentify.Encode(
            identity,
            [Libp2pIdentify.Protocol, "/vac/waku/metadata/1.0.0"]);
        Libp2pIdentifyResponse decoded = Libp2pIdentify.Decode(encoded);

        Assert.Equal(identity.ProtobufPublicKey, decoded.PublicKey);
        Assert.Contains(Libp2pIdentify.Protocol, decoded.Protocols);
        Assert.Contains("/vac/waku/metadata/1.0.0", decoded.Protocols);
        Assert.Equal("ipfs/0.1.0", decoded.ProtocolVersion);
        Assert.Equal("noks-dotnet/1.0", decoded.AgentVersion);
    }
}
