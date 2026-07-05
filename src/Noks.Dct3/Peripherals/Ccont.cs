using Noks.Dct3.Core;
namespace Noks.Dct3.Peripherals;

public sealed class Ccont
{
    private const byte ChargerInterrupt = 0x08;
    private const ushort MinimumBatteryVoltage = 0x240;
    private const ushort FullBatteryVoltage = 0x330;
    private const int ChargeVoltageStep = 0x200;
    private const int DrainVoltageStepSeconds = 120;
    private readonly byte[] regs = new byte[16];
    private readonly CcontAdcInputs adcInputs;
    private readonly CcontRtc rtc;
    private readonly IDct3Trace? trace;
    private bool dataPhase;
    private byte cmd;
    private byte watchdog;
    private int watchdogArmReloads;
    private int watchdogKicks;
    private int watchdogDisables;
    private int watchdogExpires;
    private byte lastWatchdogCommand;
    private byte interruptPending;
    private bool chargerWasPresent;
    private int chargeAccumulator;
    private int drainAccumulator;

    public Ccont(CcontAdcInputs adcInputs, IDct3Trace? trace, DateTime? rtcStart = null)
    {
        this.adcInputs = adcInputs;
        this.trace = trace;
        rtc = new CcontRtc(rtcStart);
    }

    public Action? InterruptRequested { get; set; }

    public Func<int, ushort, ushort>? AdcInputFilter { get; set; }

    public bool PowerOffRequested { get; private set; }

    public string LastPowerOffReason { get; private set; } = "";

    public byte WatchdogValue => watchdog;

    public int WatchdogArmReloads => watchdogArmReloads;

    public int WatchdogKicks => watchdogKicks;

    public int WatchdogDisables => watchdogDisables;

    public int WatchdogExpires => watchdogExpires;

    public byte LastWatchdogCommand => lastWatchdogCommand;

    public CcontRtcState RtcState => rtc.State;

    public CcontEnergyState EnergyState => new(
        AdcControl: regs[0],
        ChargerPwm: regs[1],
        LatchedAdc: (ushort)(regs[2] | ((regs[3] & 0x03) << 8)),
        SelectedAdcChannel: SelectedAdcChannel,
        SelectedAdcInput: adcInputs.Get(SelectedAdcChannel),
        AccessoryDetect: adcInputs.AccessoryDetect,
        Rssi: adcInputs.Rssi,
        BatteryVoltage: adcInputs.BatteryVoltage,
        BatteryType: adcInputs.BatteryType,
        BatteryTemperature: adcInputs.BatteryTemperature,
        ChargerVoltage: adcInputs.ChargerVoltage,
        VcxoTemperature: adcInputs.VcxoTemperature,
        ChargingCurrent: adcInputs.ChargingCurrent,
        AutomaticChargingCurrent: adcInputs.AutomaticChargingCurrent,
        ChargerPresent: ChargerPresent,
        ChargerPwmEnabled: ChargerPwmEnabled,
        InterruptPending: interruptPending,
        Watchdog: watchdog,
        LastWatchdogCommand: lastWatchdogCommand);

    public bool WatchdogExpirationEnabled { get; set; } = true;

    public bool ChargerPresent => adcInputs.ChargerVoltage != 0;

    public bool ChargerPwmEnabled => (regs[0] & 0x80) != 0;

    public byte ChargerPwm => regs[1];

    public void AdcInputChanged(int channel)
    {
        int selectedChannel = SelectedAdcChannel;

        if (channel == selectedChannel)
        {
            LatchAdcResult(channel);
        }

        if (channel == 5)
        {
            UpdateChargerSense();
            if (UpdateAutomaticChargingCurrent() && selectedChannel == 7)
            {
                LatchAdcResult(7);
            }
        }

        SignalInterruptIfNeeded();
    }

    public void AdcInputsChanged()
    {
        UpdateChargerSense();
        UpdateAutomaticChargingCurrent();
        LatchAdcResult(SelectedAdcChannel);
        SignalInterruptIfNeeded();
    }

    public void SetRtcTime(int hour, int minute, int second, int day)
    {
        rtc.SetTime(hour, minute, second, day);
        SignalInterruptIfNeeded();
    }

    public void Reset()
    {
        Array.Clear(regs);
        dataPhase = false;
        cmd = 0;
        watchdog = 0;
        interruptPending = 0;
        chargerWasPresent = ChargerPresent;
        PowerOffRequested = false;
        LastPowerOffReason = "";
        chargeAccumulator = 0;
        drainAccumulator = 0;
        adcInputs.UseAutomaticChargingCurrent();
        UpdateAutomaticChargingCurrent();
    }

    public void BeginTransaction()
    {
        dataPhase = false;
    }

    public void Write(byte value)
    {
        if (!dataPhase)
        {
            cmd = value;
        }
        else
        {
            int addr = (cmd >> 3) & 0x0F;

            switch (addr)
            {
                case 0x0:
                    regs[addr] = value;
                    int channel = (value >> 4) & 0x07;
                    bool chargingCurrentChanged = UpdateAutomaticChargingCurrent();
                    ushort result = LatchAdcResult(channel);
                    if (channel == 5)
                    {
                        UpdateChargerSense();
                    }
                    else if (chargingCurrentChanged && channel == 7)
                    {
                        result = LatchAdcResult(channel);
                    }

                    SignalInterruptIfNeeded();
                    trace?.Event($"CCONT adc ch{channel} en={(value >> 3) & 1} -> {result:X3}");
                    break;

                case 0x1:
                    regs[addr] = value;
                    if (UpdateAutomaticChargingCurrent() && SelectedAdcChannel == 7)
                    {
                        LatchAdcResult(7);
                    }

                    break;

                case 0x5:
                    lastWatchdogCommand = value;
                    if (value == 0x20)
                    {
                        regs[addr] = value;
                        watchdog = value;
                        watchdogArmReloads++;
                    }
                    else if (value == 0x31)
                    {
                        watchdog = value;
                        watchdogKicks++;
                    }
                    else if (value == 0x3F)
                    {
                        watchdog = 0;
                        watchdogDisables++;
                    }
                    else if (value == 0)
                    {
                        PowerOffRequested = true;
                        LastPowerOffReason = "firmware wrote CCONT power register 0x05 value 0x00";
                        trace?.Event($"CCONT power-off {EnergyState}");
                    }
                    else
                    {
                        regs[addr] = value;
                        watchdog = value;
                        watchdogArmReloads++;
                    }

                    break;

                case 0xE:
                    regs[addr] = value;
                    interruptPending = (byte)(interruptPending & ~(value & ChargerInterrupt));
                    rtc.Write(addr, value);
                    SignalInterruptIfNeeded();
                    break;

                case >= 0x6 and <= 0xF:
                    regs[addr] = value;
                    rtc.Write(addr, value);
                    SignalInterruptIfNeeded();
                    break;

                default:
                    regs[addr] = value;
                    break;
            }

            trace?.CcontWrite(addr, value);
        }

        dataPhase = !dataPhase;
    }

    public byte Read()
    {
        int addr = (cmd >> 3) & 0x0F;
        byte data = addr switch
        {
            0x3 => (byte)(0xB0 | (regs[addr] & 0x03)),
            0xE => (byte)(rtc.Read(addr) | interruptPending),
            >= 0x6 and <= 0xF => rtc.Read(addr),
            _ => regs[addr],
        };

        dataPhase = !dataPhase;
        trace?.CcontRead(addr, data);
        return data;
    }

    public bool TickSecond()
    {
        rtc.TickSecond();
        AdvanceBatteryModel();
        SignalInterruptIfNeeded();

        if (watchdog == 0)
        {
            return false;
        }

        watchdog--;
        if (watchdog != 0)
        {
            return false;
        }

        if (!WatchdogExpirationEnabled)
        {
            watchdog = 1;
            return false;
        }

        watchdogExpires++;
        return true;
    }

    private ushort LatchAdcResult(int channel)
    {
        ushort result = adcInputs.Get(channel);
        if (AdcInputFilter is { } filter)
        {
            result = Math.Min(filter(channel, result), CcontAdcInputs.MaximumAdcValue);
        }

        regs[2] = (byte)result;
        regs[3] = (byte)((result >> 8) & 0x03);
        return result;
    }

    private int SelectedAdcChannel => (regs[0] >> 4) & 0x07;

    private void SignalInterruptIfNeeded()
    {
        if (rtc.InterruptLine || (interruptPending & ~rtc.Read(0xF) & ChargerInterrupt) != 0)
        {
            InterruptRequested?.Invoke();
        }
    }

    private void UpdateChargerSense()
    {
        bool present = adcInputs.ChargerVoltage != 0;

        if (present == chargerWasPresent)
        {
            return;
        }

        chargerWasPresent = present;
        interruptPending |= ChargerInterrupt;
        trace?.Event(present ? "CCONT charger present" : "CCONT charger removed");
    }

    private void AdvanceBatteryModel()
    {
        bool currentChanged = UpdateAutomaticChargingCurrent();
        bool voltageChanged = false;

        if (ChargerPresent && adcInputs.ChargingCurrent != 0)
        {
            drainAccumulator = 0;
            chargeAccumulator += adcInputs.ChargingCurrent;

            while (chargeAccumulator >= ChargeVoltageStep && adcInputs.BatteryVoltage < FullBatteryVoltage)
            {
                chargeAccumulator -= ChargeVoltageStep;
                adcInputs.BatteryVoltage++;
                voltageChanged = true;
            }
        }
        else
        {
            chargeAccumulator = 0;

            if (adcInputs.BatteryVoltage > MinimumBatteryVoltage && ++drainAccumulator >= DrainVoltageStepSeconds)
            {
                drainAccumulator = 0;
                adcInputs.BatteryVoltage--;
                voltageChanged = true;
            }
        }

        int selectedChannel = SelectedAdcChannel;
        if ((voltageChanged && selectedChannel == 2) || (currentChanged && selectedChannel == 7))
        {
            LatchAdcResult(selectedChannel);
        }
    }

    private bool UpdateAutomaticChargingCurrent()
    {
        ushort current = CalculateChargingCurrent();
        bool changed = adcInputs.SetAutomaticChargingCurrent(current);

        if (changed)
        {
            trace?.Event($"CCONT charging current {current:X3}");
        }

        return changed;
    }

    private ushort CalculateChargingCurrent()
    {
        if (!ChargerPresent || !ChargerPwmEnabled || ChargerPwm == 0)
        {
            return 0;
        }

        int current = adcInputs.ChargerVoltage * ChargerPwm / byte.MaxValue;
        return (ushort)Math.Min(current, CcontAdcInputs.MaximumAdcValue);
    }
}
