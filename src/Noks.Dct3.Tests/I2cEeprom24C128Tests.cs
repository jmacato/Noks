using Noks.Dct3.Memory;
namespace Noks.Dct3.Tests;

public sealed class I2cEeprom24C128Tests
{
    [Fact]
    public void ReadByte_UsesTwoByteAddress()
    {
        byte[] image = new byte[I2cEeprom24C128.Size];
        image[0x1234] = 0x5A;
        I2cEeprom24C128 eeprom = new(image);
        I2cBus bus = new(eeprom);

        bus.Start();
        Assert.True(bus.WriteByte(0xA0));
        Assert.True(bus.WriteByte(0x12));
        Assert.True(bus.WriteByte(0x34));
        bus.Start();
        Assert.True(bus.WriteByte(0xA1));

        byte value = bus.ReadByte(ack: false);
        bus.Stop();

        Assert.Equal(0x5A, value);
    }

    [Fact]
    public void WriteByte_StoresDataAndIncrementsVersion()
    {
        I2cEeprom24C128 eeprom = new(ReadOnlySpan<byte>.Empty);
        I2cBus bus = new(eeprom);

        bus.Start();
        Assert.True(bus.WriteByte(0xA0));
        Assert.True(bus.WriteByte(0x00));
        Assert.True(bus.WriteByte(0x20));
        Assert.True(bus.WriteByte(0x3C));
        bus.Stop();

        Assert.Equal(0x3C, eeprom.Data[0x20]);
        Assert.Equal(1, eeprom.PersistenceVersion);
    }

    private sealed class I2cBus
    {
        private readonly I2cEeprom24C128 eeprom;
        private bool scl = true;
        private bool sda = true;

        public I2cBus(I2cEeprom24C128 eeprom)
        {
            this.eeprom = eeprom;
            Observe();
        }

        public void Start()
        {
            SetSda(true);
            SetScl(true);
            SetSda(false);
            SetScl(false);
        }

        public void Stop()
        {
            SetSda(false);
            SetScl(true);
            SetSda(true);
        }

        public bool WriteByte(byte value)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                WriteBit((value & (1 << bit)) != 0);
            }

            SetSda(true);
            SetScl(true);
            bool ack = eeprom.DrivesSdaLow;
            SetScl(false);
            return ack;
        }

        public byte ReadByte(bool ack)
        {
            byte value = 0;
            SetSda(true);

            for (int bit = 7; bit >= 0; bit--)
            {
                SetScl(true);
                if (!eeprom.DrivesSdaLow)
                {
                    value |= (byte)(1 << bit);
                }

                SetScl(false);
            }

            WriteBit(!ack);
            return value;
        }

        private void WriteBit(bool high)
        {
            SetSda(high);
            SetScl(true);
            SetScl(false);
        }

        private void SetScl(bool high)
        {
            scl = high;
            Observe();
        }

        private void SetSda(bool high)
        {
            sda = high;
            Observe();
        }

        private void Observe() => eeprom.Observe(scl, sda);
    }
}
