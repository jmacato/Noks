using Noks.Dct3.Peripherals;
namespace Noks.Dct3.Tests;

public sealed class CcontAdcInputsTests
{
    [Fact]
    public void NormalBattery_ProvidesPlausibleRssi()
    {
        CcontAdcInputs inputs = CcontAdcInputs.NormalBattery();

        Assert.Equal(0x220, inputs.Rssi);
    }
}
