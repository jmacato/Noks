using Noks.Waku.Transport.Libp2p.Wire;

namespace Noks.Waku.Tests.Transport.Libp2p.Wire;

public sealed class Libp2pWireTests
{
    [Theory]
    [InlineData(0ul, "00")]
    [InlineData(1ul, "01")]
    [InlineData(127ul, "7F")]
    [InlineData(128ul, "8001")]
    [InlineData(300ul, "AC02")]
    [InlineData(ulong.MaxValue, "FFFFFFFFFFFFFFFFFF01")]
    public void VarintRoundTrips(ulong value, string expectedHex)
    {
        Span<byte> encoded = stackalloc byte[Libp2pVarint.MaximumEncodedLength];
        int written = Libp2pVarint.Write(encoded, value);

        Assert.Equal(expectedHex, Convert.ToHexString(encoded[..written]));
        Assert.True(Libp2pVarint.TryRead(encoded[..written], out ulong decoded, out int read));
        Assert.Equal(value, decoded);
        Assert.Equal(written, read);
    }

    [Fact]
    public void MultistreamSelectRoundTripsFragmentedInput()
    {
        byte[] encoded = MultistreamSelect.Encode("/noise");
        ByteQueue queue = new();
        queue.Append(encoded.AsSpan(0, 2));
        Assert.False(MultistreamSelect.TryDecode(queue, out _));

        queue.Append(encoded.AsSpan(2));

        Assert.True(MultistreamSelect.TryDecode(queue, out string protocol));
        Assert.Equal("/noise", protocol);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void ProtobufSupportsWakuFieldNumbersAndZigZagTimestamps()
    {
        ProtobufWriter writer = new();
        writer.WriteString(1, "request");
        writer.WriteString(20, "/waku/2/rs/1/3");
        writer.WriteSInt64(10, 1_721_234_567_890_000_000);

        ProtobufReader reader = new(writer.WrittenSpan);
        Assert.True(reader.TryReadTag(out int field, out int wire));
        Assert.Equal((1, 2), (field, wire));
        Assert.Equal("request", reader.ReadString());
        Assert.True(reader.TryReadTag(out field, out wire));
        Assert.Equal((20, 2), (field, wire));
        Assert.Equal("/waku/2/rs/1/3", reader.ReadString());
        Assert.True(reader.TryReadTag(out field, out wire));
        Assert.Equal((10, 0), (field, wire));
        Assert.Equal(1_721_234_567_890_000_000, reader.ReadSInt64());
        Assert.True(reader.End);
    }
}
