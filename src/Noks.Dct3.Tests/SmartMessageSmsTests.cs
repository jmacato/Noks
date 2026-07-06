using Noks.Dct3.Messaging;
namespace Noks.Dct3.Tests;

public sealed class SmartMessageSmsTests
{
    [Fact]
    public void Split_UsesNokiaLongMessagePayloadCapacityAndSharedReference()
    {
        byte[] payload = Enumerable.Range(0, 300).Select(value => (byte)value).ToArray();

        SmartMessagePart[] parts = SmartMessageSms.Split(payload, reference: 0x7A);

        Assert.Equal(3, parts.Length);
        Assert.Equal([128, 128, 44], parts.Select(part => part.Payload.Length).ToArray());
        for (int index = 0; index < parts.Length; index++)
        {
            Assert.Equal(0x7A, parts[index].Concatenation.Reference);
            Assert.Equal(3, parts[index].Concatenation.PartCount);
            Assert.Equal(index + 1, parts[index].Concatenation.PartNumber);
        }

        Assert.Equal(payload, parts.SelectMany(part => part.Payload).ToArray());
    }

    [Theory]
    [InlineData(133, 1)]
    [InlineData(134, 2)]
    [InlineData(256, 2)]
    [InlineData(257, 3)]
    [InlineData(384, 3)]
    public void GetPartCount_UsesShortAndLongUdhCapacities(int payloadLength, int expectedParts)
    {
        Assert.Equal(expectedParts, SmartMessageSms.GetPartCount(payloadLength));
    }

    [Fact]
    public void GetPartCount_RejectsPayloadBeyondNokiaThreePartLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SmartMessageSms.GetPartCount(SmartMessageSms.MaximumConcatenatedPayloadLength + 1));
    }
}
