using System.Text;

namespace Noks.Application.Tests;

public sealed class NokiaBusinessCardVCardTests
{
    [Fact]
    public void LongRemoteNameUsesEfAdnProjectionAndOfficialVCard21Shape()
    {
        byte[] payload = NokiaBusinessCardVCard.Encode(
            "bright-beacon-ab12",
            "1234567890123");

        Assert.Equal(0x23F4, NokiaBusinessCardVCard.DestinationPort);
        Assert.Equal(
            "BEGIN:VCARD\r\nVERSION:2.1\r\nN:bright-beacon...\r\n" +
            "TEL;PREF:1234567890123\r\nEND:VCARD\r\n",
            Encoding.ASCII.GetString(payload));
        Assert.True(payload.Length <= NokiaBusinessCardVCard.MaximumSinglePartPayloadBytes);
    }

    [Fact]
    public void EditedLongNameUsesPrefixAndTrailingDots()
    {
        string encoded = Encoding.ASCII.GetString(NokiaBusinessCardVCard.Encode(
            "abcdefghijklmno-1234",
            "1234567890123"));
        Assert.Contains("\r\nN:abcdefghijklm...\r\n", encoded, StringComparison.Ordinal);
    }
}
