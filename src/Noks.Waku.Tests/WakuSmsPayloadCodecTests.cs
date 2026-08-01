namespace Noks.Waku.Tests;

public sealed class WakuSmsPayloadCodecTests
{
    [Fact]
    public void RoundTripPreservesUtf8Text()
    {
        byte[] encoded = WakuSmsPayloadCodec.Encode("Hello from Noks 📟");
        Assert.True(WakuSmsPayloadCodec.TryDecode(encoded, out string text));
        Assert.Equal("Hello from Noks 📟", text);
    }

    [Fact]
    public void RejectsMalformedLengthAndUtf8()
    {
        byte[] encoded = WakuSmsPayloadCodec.Encode("hello");
        encoded[7]++;
        Assert.False(WakuSmsPayloadCodec.TryDecode(encoded, out _));

        encoded = WakuSmsPayloadCodec.Encode("a");
        encoded[^1] = 0xFF;
        Assert.False(WakuSmsPayloadCodec.TryDecode(encoded, out _));
    }
}
