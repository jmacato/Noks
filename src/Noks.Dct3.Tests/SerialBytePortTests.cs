using Noks.Dct3.Peripherals;
namespace Noks.Dct3.Tests;

public sealed class SerialBytePortTests
{
    [Fact]
    public void ControlStatus_WhenEnabledReportsCardReady()
    {
        SerialBytePort port = new(byteCycles: 10)
        {
            Enabled = true,
        };

        Assert.Equal(SerialBytePort.TxReadyStatus | SerialBytePort.CardReadyStatus, port.ControlStatus);
    }

    [Fact]
    public void Tick_LongTransmitRequestsInterruptAtLowWatermark()
    {
        SerialBytePort port = new(byteCycles: 10)
        {
            Enabled = true,
        };
        List<byte> transmitted = [];
        port.ByteTransmitted = (value, _) => transmitted.Add(value);

        for (int value = 0; value < 15; value++)
        {
            port.WriteTx((byte)value, cycles: 0);
        }

        port.Tick(cycles: 100);

        Assert.True(port.ConsumeInterruptRequest());
        Assert.Equal(SerialBytePort.TxCompleteInterrupt, port.InterruptId);
        Assert.Equal(5, port.TxCount);
        Assert.Equal(10, transmitted.Count);
    }
}
