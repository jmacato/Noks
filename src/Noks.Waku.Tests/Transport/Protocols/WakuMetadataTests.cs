using Noks.Waku.Transport.Libp2p.Wire;
using Noks.Waku.Transport.Protocols;

namespace Noks.Waku.Tests.Transport.Protocols;

public sealed class WakuMetadataTests
{
    [Fact]
    public void LightClientMetadataUsesClusterOneAndNoRelayShards()
    {
        WakuMetadataResponse decoded = WakuMetadata.Decode(WakuMetadata.EncodeLightClient());

        Assert.Equal(1u, decoded.ClusterId);
        Assert.Empty(decoded.Shards);
    }

    [Fact]
    public void DecodesPackedRelayShards()
    {
        ProtobufWriter encoded = new();
        encoded.WriteUInt32(1, 1);
        encoded.WriteBytes(3, [0, 1, 7]);

        WakuMetadataResponse decoded = WakuMetadata.Decode(encoded.WrittenSpan);

        Assert.Equal(new uint[] { 0, 1, 7 }, decoded.Shards);
    }
}
