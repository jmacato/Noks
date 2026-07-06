using Noks.Dct3.Peripherals;
namespace Noks.Dct3.Tests;

public sealed class CcontRtcTests
{
    [Fact]
    public void TickSecond_WhenDisabled_DoesNotAdvance()
    {
        CcontRtc rtc = new();
        CcontRtcState initial = rtc.State;

        rtc.TickSecond();

        Assert.Equal(initial, rtc.State);
    }

    [Fact]
    public void TickSecond_WhenEnabled_AdvancesSecondAndRaisesInterrupt()
    {
        CcontRtc rtc = new();
        rtc.Write(0x7, 10);
        rtc.Write(0x8, 20);
        rtc.Write(0x9, 3);
        rtc.Write(0xA, 4);
        rtc.Write(0x6, 0x54);

        rtc.TickSecond();

        CcontRtcState state = rtc.State;
        Assert.Equal(11, state.Second);
        Assert.Equal(20, state.Minute);
        Assert.Equal(3, state.Hour);
        Assert.Equal(4, state.Day);
        Assert.Equal(0x10, state.InterruptPending);
    }

    [Fact]
    public void TickSecond_WhenEnabled_RollsMinuteHourAndDay()
    {
        CcontRtc rtc = new();
        rtc.Write(0x7, 59);
        rtc.Write(0x8, 59);
        rtc.Write(0x9, 23);
        rtc.Write(0xA, 63);
        rtc.Write(0x6, 0x54);

        rtc.TickSecond();

        CcontRtcState state = rtc.State;
        Assert.Equal(0, state.Second);
        Assert.Equal(0, state.Minute);
        Assert.Equal(0, state.Hour);
        Assert.Equal(0, state.Day);
        Assert.Equal(0x70, state.InterruptPending);
    }

    [Fact]
    public void WriteDay_AllowsFirmwareResetToZero()
    {
        CcontRtc rtc = new();

        rtc.Write(0xA, 0);

        Assert.Equal(0, rtc.State.Day);
        Assert.Equal(0, rtc.Read(0xA));
    }

    [Fact]
    public void SetTime_UpdatesClockWithoutChangingControlOrPendingInterrupts()
    {
        CcontRtc rtc = new();
        rtc.Write(0x7, 10);
        rtc.Write(0x8, 20);
        rtc.Write(0x9, 3);
        rtc.Write(0xA, 4);
        rtc.Write(0x6, 0x54);
        rtc.TickSecond();

        rtc.SetTime(hour: 2, minute: 3, second: 4, day: 0);

        CcontRtcState state = rtc.State;
        Assert.Equal(0x54, state.Control);
        Assert.Equal(0x10, state.InterruptPending);
        Assert.Equal(4, state.Second);
        Assert.Equal(3, state.Minute);
        Assert.Equal(2, state.Hour);
        Assert.Equal(0, state.Day);
    }
}
