using Noks.Dct3.Display;
namespace Noks.Dct3.Tests;

public sealed class Pcd8544Tests
{
    [Fact]
    public void CompleteDisplayRamRefresh_RaisesOneFrameEvent()
    {
        Pcd8544 lcd = new();
        int completedFrames = 0;
        lcd.FrameCompleted += () => completedFrames++;

        for (int address = 0; address < Pcd8544.Width * Pcd8544.Height / 8; address++)
        {
            lcd.WriteData((byte)address);
        }

        Assert.Equal(1, completedFrames);
        Assert.Equal(1, lcd.CompletedRefreshes);
        Assert.Equal(Pcd8544.Width * Pcd8544.Height / 8, lcd.DataWrites);
    }

    [Fact]
    public void PartialOrRepeatedWrites_DoNotPublishTornFrame()
    {
        Pcd8544 lcd = new();
        int completedFrames = 0;
        lcd.FrameCompleted += () => completedFrames++;

        for (int repeat = 0; repeat < 4; repeat++)
        {
            lcd.WriteCommand(0x40);
            lcd.WriteCommand(0x80);
            for (int x = 0; x < Pcd8544.Width; x++)
            {
                lcd.WriteData((byte)(repeat + x));
            }
        }

        Assert.Equal(0, completedFrames);

        for (int bank = 1; bank < Pcd8544.Height / 8; bank++)
        {
            lcd.WriteCommand((byte)(0x40 | bank));
            lcd.WriteCommand(0x80);
            for (int x = 0; x < Pcd8544.Width; x++)
            {
                lcd.WriteData((byte)(bank + x));
            }
        }

        Assert.Equal(1, completedFrames);
    }

    [Fact]
    public void NewOriginWrite_DiscardsCoverageFromAnOlderPartialRefresh()
    {
        Pcd8544 lcd = new();
        int completedFrames = 0;
        lcd.FrameCompleted += () => completedFrames++;

        for (int bank = 1; bank < Pcd8544.Height / 8; bank++)
        {
            WriteBank(lcd, bank, (byte)(0x10 + bank));
        }

        Assert.Equal(0, completedFrames);

        WriteBank(lcd, 0, 0x20);

        Assert.Equal(0, completedFrames);

        for (int bank = 1; bank < Pcd8544.Height / 8; bank++)
        {
            WriteBank(lcd, bank, (byte)(0x20 + bank));
        }

        Assert.Equal(1, completedFrames);
    }

    [Fact]
    public void PowerAndDisplayModeChanges_ArePublishedImmediately()
    {
        Pcd8544 lcd = new();
        int stateChanges = 0;
        lcd.DisplayStateChanged += () => stateChanges++;

        lcd.WriteCommand(0x20);
        lcd.WriteCommand(0x20);
        lcd.WriteCommand(0x09);
        lcd.WriteCommand(0x09);

        Assert.False(lcd.PowerDown);
        Assert.Equal(1, lcd.DisplayMode);
        Assert.Equal(2, stateChanges);
    }

    private static void WriteBank(Pcd8544 lcd, int bank, byte value)
    {
        lcd.WriteCommand((byte)(0x40 | bank));
        lcd.WriteCommand(0x80);
        for (int x = 0; x < Pcd8544.Width; x++)
        {
            lcd.WriteData(value);
        }
    }
}
