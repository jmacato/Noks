using Noks.Dct3.Messaging;
namespace Noks.Dct3.Peripherals;

public readonly record struct CcontEnergyState(
    byte AdcControl,
    byte ChargerPwm,
    ushort LatchedAdc,
    int SelectedAdcChannel,
    ushort SelectedAdcInput,
    ushort AccessoryDetect,
    ushort Rssi,
    ushort BatteryVoltage,
    ushort BatteryType,
    ushort BatteryTemperature,
    ushort ChargerVoltage,
    ushort VcxoTemperature,
    ushort ChargingCurrent,
    bool AutomaticChargingCurrent,
    bool ChargerPresent,
    bool ChargerPwmEnabled,
    byte InterruptPending,
    byte Watchdog,
    byte LastWatchdogCommand)
{
    public bool AdcEnabled => (AdcControl & 0x08) != 0;

    public override string ToString() =>
        $"energy adc=ch{SelectedAdcChannel}/{(AdcEnabled ? 1 : 0)} latched={LatchedAdc:X3} input={SelectedAdcInput:X3} " +
        $"batt={BatteryVoltage:X3} type={BatteryType:X3} temp={BatteryTemperature:X3} " +
        $"charger={ChargerVoltage:X3}/{(ChargerPresent ? 1 : 0)} pwm={(ChargerPwmEnabled ? 1 : 0)}:{ChargerPwm:X2} " +
        $"current={ChargingCurrent:X3}/{(AutomaticChargingCurrent ? 1 : 0)} vcxo={VcxoTemperature:X3} " +
        $"acc={AccessoryDetect:X3} rssi={Rssi:X3} irq={InterruptPending:X2} wd={Watchdog:X2}/{LastWatchdogCommand:X2}";
}
