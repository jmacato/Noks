using Noks.Waku.Transport.Libp2p.Wire;
using Noks.Waku.Transport.Protocols;

namespace Noks.Waku.Tests.Transport.Protocols;

public sealed class WakuPeerExchangeTests
{
    [Fact]
    public void PeerExchangeQueryUsesRequestedPeerCount()
    {
        ProtobufWriter rpc = new();
        rpc.WriteMessage(1, query => query.WriteUInt64(1, 60));

        ProtobufReader reader = new(rpc.WrittenSpan);
        Assert.True(reader.TryReadTag(out int field, out int wire));
        Assert.Equal((1, 2), (field, wire));
        ProtobufReader queryReader = new(reader.ReadBytes());
        Assert.True(queryReader.TryReadTag(out field, out wire));
        Assert.Equal((1, 0), (field, wire));
        Assert.Equal(60ul, queryReader.ReadUInt64());
    }
}
