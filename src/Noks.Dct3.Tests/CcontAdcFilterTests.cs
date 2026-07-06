using Noks.Dct3.Peripherals;
namespace Noks.Dct3.Tests;

public sealed class CcontAdcFilterTests
{
    [Fact]
    public void Write_WhenAdcFilterAdjustsSelectedChannel_LatchesFilteredValue()
    {
        CcontAdcInputs inputs = CcontAdcInputs.NormalBattery();
        Ccont ccont = new(inputs, trace: null)
        {
            AdcInputFilter = (channel, value) => channel == 1 ? (ushort)0x180 : value,
        };

        WriteCcont(ccont, register: 0, value: 0x18);

        Assert.Equal(0x80, ReadCcont(ccont, register: 2));
        Assert.Equal(0x01, ReadCcont(ccont, register: 3) & 0x03);
    }

    private static void WriteCcont(Ccont ccont, int register, byte value)
    {
        ccont.Write((byte)(register << 3));
        ccont.Write(value);
    }

    private static byte ReadCcont(Ccont ccont, int register)
    {
        ccont.Write((byte)(register << 3));
        return ccont.Read();
    }
}
