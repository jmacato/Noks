namespace Noks.WebRtc.Tests;

public sealed class SessionDescriptionTests
{
    private const string Base = "v=0\r\n" +
        "m=audio 9 UDP/TLS/RTP/SAVPF 0\r\na=mid:0\r\na=ice-ufrag:abcd\r\na=ice-pwd:abcdefghijklmnopqrstuvwxyz\r\n" +
        "a=fingerprint:sha-256 AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA:AA\r\na=setup:actpass\r\na=rtcp-mux\r\na=rtpmap:0 PCMU/8000\r\n";

    [Fact]
    public void Parses_required_single_muxed_pcmu_description()
    {
        SessionDescription parsed = SessionDescription.Parse(Base + "a=sendrecv\r\n");
        Assert.Equal("abcd", parsed.Ufrag); Assert.Equal(0, parsed.PayloadType); Assert.Equal("sendrecv", parsed.Direction);
    }

    [Theory]
    [InlineData("", "missing mux")]
    [InlineData("a=fingerprint:sha-1 AA\r\n", "wrong fingerprint")]
    [InlineData("a=fingerprint:sha-256 AA:BB\r\n", "short fingerprint")]
    [InlineData("a=ice-pwd:short\r\n", "invalid ICE password")]
    [InlineData("m=video 9 UDP/TLS/RTP/SAVPF 96\r\n", "extra media")]
    public void Rejects_missing_or_unsupported_security_requirements(string append, string _)
    {
        string sdp = append.StartsWith("a=", StringComparison.Ordinal) && append.Contains("ice-pwd", StringComparison.Ordinal)
            ? Base.Replace("a=ice-pwd:abcdefghijklmnopqrstuvwxyz\r\n", append, StringComparison.Ordinal)
            : Base + append;
        if (append.Length == 0) sdp = Base.Replace("a=rtcp-mux\r\n", "", StringComparison.Ordinal);
        Assert.ThrowsAny<Exception>(() => SessionDescription.Parse(sdp));
    }
}
