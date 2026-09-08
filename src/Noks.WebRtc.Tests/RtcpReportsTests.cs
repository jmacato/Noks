using System.Buffers.Binary;

namespace Noks.WebRtc.Tests;

public sealed class RtcpReportsTests
{
    [Fact]
    public void Sender_report_is_compound_with_cname()
    {
        var bytes = RtcpReports.CreateSenderReport(7, 9, 11, 13, DateTimeOffset.UnixEpoch, "node@example");
        Assert.True(RtcpReports.TryParse(bytes, out var bye)); Assert.Null(bye);
        Assert.Equal(200, bytes[1]); Assert.Equal(202, bytes[29]);
    }

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
