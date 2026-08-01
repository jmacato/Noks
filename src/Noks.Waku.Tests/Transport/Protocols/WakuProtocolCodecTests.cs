using Noks.Waku.Transport.Libp2p.Wire;
using Noks.Waku.Transport.Protocols;

namespace Noks.Waku.Tests.Transport.Protocols;

public sealed class WakuProtocolCodecTests
{
    [Fact]
    public void AutoshardingMatchesRfc51SuffixRule()
    {
        Assert.Equal("/waku/2/rs/1/7", WakuSharding.GetPubsubTopic("/toy-chat/2/huilong/proto"));
        Assert.Equal("/waku/2/rs/1/7", WakuSharding.GetPubsubTopic("/0/toy-chat/2/huilong/proto"));
    }

    [Fact]
    public void LightPushV3UsesCurrentFieldNumbers()
    {
        WakuPublishRequest request = new(
            "/toy-chat/2/huilong/proto",
            "hello"u8.ToArray(),
            Ephemeral: true,
            TimestampUnixMilliseconds: 1_721_234_567_890);

        byte[] encoded = WakuProtocolCodec.EncodeLightPushRequest(request, out string requestId);

        ProtobufReader reader = new(encoded);
        Assert.True(reader.TryReadTag(out int field, out int wire));
        Assert.Equal((1, 2), (field, wire));
        Assert.Equal(requestId, reader.ReadString());
        Assert.True(reader.TryReadTag(out field, out wire));
        Assert.Equal((20, 2), (field, wire));
        Assert.Equal("/waku/2/rs/1/7", reader.ReadString());
        Assert.True(reader.TryReadTag(out field, out wire));
        Assert.Equal((21, 2), (field, wire));

        WakuWireMessage decoded = WakuProtocolCodec.DecodeWakuMessage(reader.ReadBytes());
        Assert.Equal(request.ContentTopic, decoded.ContentTopic);
        Assert.Equal(request.Payload.ToArray(), decoded.Payload);
        Assert.Equal(request.TimestampUnixMilliseconds, decoded.TimestampUnixMilliseconds);
        Assert.True(decoded.Ephemeral);
    }

    [Fact]
    public void DecodesFilterPushEnvelope()
    {
        ProtobufWriter push = new();
        push.WriteMessage(1, message =>
        {
            message.WriteBytes(1, [1, 2, 3]);
            message.WriteString(2, "/toy-chat/2/huilong/proto");
            message.WriteSInt64(10, 1_700_000_000_123_000_000);
        });
        push.WriteString(2, "/waku/2/rs/1/0");

        WakuWireMessage decoded = WakuProtocolCodec.DecodeFilterPush(push.WrittenSpan);

        Assert.Equal(new byte[] { 1, 2, 3 }, decoded.Payload);
        Assert.Equal(1_700_000_000_123, decoded.TimestampUnixMilliseconds);
    }
}
