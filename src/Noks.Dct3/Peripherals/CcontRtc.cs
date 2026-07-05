namespace Noks.Dct3.Peripherals;

public sealed class CcontRtc
{
    // Firmware uses 0x54 as the steady RTC control value, so bit 2 is the effective run bit.
    private const byte RtcEnableBit = 0x04;
    private const byte RtcInterruptMask = 0xF0;
    private const byte SecondInterrupt = 0x10;
    private const byte MinuteInterrupt = 0x20;
    private const byte DayInterrupt = 0x40;
    private const byte AlarmInterrupt = 0x80;
    private byte control;
    private byte interruptPending;
    private byte interruptMask;
    private byte alarmMinute;
    private byte alarmHour;
    private byte calibration;
    private int second;
    private int minute;
    private int hour;
    private int day;

    public CcontRtc(DateTime? initialTime = null)
    {
        DateTime now = initialTime ?? DateTime.Now;
        second = now.Second;
        minute = now.Minute;
        hour = now.Hour;
        day = now.Day;
    }

    public bool InterruptLine => (interruptPending & ~interruptMask & RtcInterruptMask) != 0;

    public CcontRtcState State => new(
        control,
        interruptPending,
        interruptMask,
        second,
        minute,
        hour,
        day);

    public void SetTime(int hour, int minute, int second, int day)
    {
        this.hour = Clamp(hour, 0, 23);
        this.minute = Clamp(minute, 0, 59);
        this.second = Clamp(second, 0, 59);
        this.day = day & 0x3F;
    }

    public void Write(int register, byte value)
    {
        switch (register)
        {
            case 0x6:
                control = value;
                break;
            case 0x7:
                second = Clamp(value & 0x3F, 0, 59);
                break;
            case 0x8:
                minute = Clamp(value & 0x3F, 0, 59);
                break;
            case 0x9:
                hour = Clamp(value & 0x1F, 0, 23);
                break;
            case 0xA:
                day = value & 0x3F;
                break;
            case 0xB:
                alarmMinute = (byte)Clamp(value & 0x3F, 0, 59);
                break;
            case 0xC:
                alarmHour = (byte)((value & 0x80) | Clamp(value & 0x1F, 0, 23));
                break;
            case 0xD:
                calibration = value;
                break;
            case 0xE:
                interruptPending = (byte)(interruptPending & ~(value & RtcInterruptMask));
                break;
            case 0xF:
                interruptMask = value;
                break;
        }
    }

    public byte Read(int register)
    {
        return register switch
        {
            0x6 => control,
            0x7 => (byte)second,
            0x8 => (byte)minute,
            0x9 => (byte)hour,
            0xA => (byte)day,
            0xB => alarmMinute,
            0xC => alarmHour,
            0xD => calibration,
            0xE => (byte)(0x01 | interruptPending),
            0xF => interruptMask,
            _ => 0,
        };
    }

    public void TickSecond()
    {
        if ((control & RtcEnableBit) == 0)
        {
            return;
        }

        interruptPending |= SecondInterrupt;
        second++;

        if (second < 60)
        {
            return;
        }

        second = 0;
        minute++;
        interruptPending |= MinuteInterrupt;

        if (minute >= 60)
        {
            minute = 0;
            hour++;

            if (hour >= 24)
            {
                hour = 0;
                day = (day + 1) & 0x3F;
                interruptPending |= DayInterrupt;
            }
        }

        if (AlarmEnabled && hour == AlarmHour && minute == alarmMinute)
        {
            interruptPending |= AlarmInterrupt;
        }
    }

    private bool AlarmEnabled => (alarmHour & 0x80) != 0;

    private int AlarmHour => alarmHour & 0x1F;

    private static int Clamp(int value, int min, int max) => Math.Min(Math.Max(value, min), max);
}
