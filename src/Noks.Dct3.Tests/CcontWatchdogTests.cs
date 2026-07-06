using Noks.Dct3.Peripherals;
namespace Noks.Dct3.Tests;

public sealed class CcontWatchdogTests
{
    [Fact]
    public void TickSecond_WhenWatchdogExpires_ReturnsTrue()
    {
        Ccont ccont = new(CcontAdcInputs.NormalBattery(), trace: null);
        WriteCcont(ccont, 0x5, 0x01);

        bool expired = ccont.TickSecond();

        Assert.True(expired);
        Assert.Equal(0, ccont.WatchdogValue);
        Assert.Equal(1, ccont.WatchdogExpires);
    }

    [Fact]
    public void TickSecond_WhenWatchdogExpirationDisabled_ClampsWithoutExpiring()
    {
        Ccont ccont = new(CcontAdcInputs.NormalBattery(), trace: null)
        {
            WatchdogExpirationEnabled = false,
        };
        WriteCcont(ccont, 0x5, 0x01);

        bool expired = ccont.TickSecond();

        Assert.False(expired);
        Assert.Equal(1, ccont.WatchdogValue);
        Assert.Equal(0, ccont.WatchdogExpires);
    }

    private static void WriteCcont(Ccont ccont, int register, byte value)
    {
        ccont.Write((byte)(register << 3));
        ccont.Write(value);
    }
}
