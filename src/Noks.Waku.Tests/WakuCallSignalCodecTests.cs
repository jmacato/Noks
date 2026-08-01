using System.Security.Cryptography;

namespace Noks.Waku.Tests;

public sealed class WakuCallSignalCodecTests
{
    [Fact]
    public void LargeSdpCanBeFragmentedAndReassembled()
    {
        var attemptId = Guid.NewGuid();
        var signalId = Guid.NewGuid();
        var original = RandomNumberGenerator.GetBytes(5_000);

        var encoded = WakuCallSignalCodec.EncodeFragments(attemptId, signalId, original);
        var decoded = encoded.Select(item =>
        {
            Assert.True(WakuCallSignalCodec.TryDecode(item, out var fragment));
            return fragment!;
        }).Reverse().ToArray();

        Assert.Equal(3, encoded.Count);
        Assert.All(encoded, item => Assert.True(item.Length <= WakuEnvelopeCodec.MaximumPayloadSize));
        Assert.True(WakuCallSignalCodec.TryReassemble(decoded, out var reassembled));
        Assert.Equal(original, reassembled);
    }

    [Fact]
    public void MissingFragmentCannotBeReassembled()
    {
        var encoded = WakuCallSignalCodec.EncodeFragments(Guid.NewGuid(), Guid.NewGuid(), new byte[5_000]);
        var decoded = encoded.Take(2).Select(item =>
        {
            Assert.True(WakuCallSignalCodec.TryDecode(item, out var fragment));
            return fragment!;
        });

        Assert.False(WakuCallSignalCodec.TryReassemble(decoded, out _));
    }
}
