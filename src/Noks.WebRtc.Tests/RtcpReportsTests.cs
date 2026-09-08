namespace Noks.WebRtc.Tests;

public sealed class RtcpReportsTests
{
    [Fact]
    public void Truncated_sdes_is_rejected()
    {
        var bytes = RtcpReports.CreateSenderReport(7, 9, 11, 13, DateTimeOffset.UtcNow, "name");
        Assert.False(RtcpReports.TryParse(bytes[..^1], out _));
    }

    [Fact]
    public void Bye_source_is_parsed_and_malformed_reason_rejected()
    {
        var sr = RtcpReports.CreateSenderReport(7, 9, 11, 13, DateTimeOffset.UtcNow, "name");
        var bye = new byte[] { 0x81, 203, 0, 2, 0, 0, 0, 42, 1, (byte)'x', 0, 0 };
        var compound = sr.Concat(bye).ToArray();
        Assert.True(RtcpReports.TryParse(compound, out var source)); Assert.Equal(42u, source);
        compound[^4] = 5; Assert.False(RtcpReports.TryParse(compound, out _));
    }
}
