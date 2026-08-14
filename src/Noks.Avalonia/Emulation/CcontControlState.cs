using System.Buffers.Binary;
using Noks.Dct3.Core;
using Noks.Dct3.Peripherals;

namespace Noks.AvaloniaApp.Emulation;

public sealed record CcontControlState(
    ushort AccessoryDetect,
    ushort Rssi,
    ushort BatteryVoltage,
    ushort BatteryType,
    ushort BatteryTemperature,
    ushort ChargerVoltage,
    ushort VcxoTemperature,
    ushort ChargingCurrent,
    bool ChargerPresent,
    bool ChargerPwmEnabled,
    byte ChargerPwm,
    byte FirmwarePowerState,
    byte FirmwareBatteryPercent,
    byte FirmwareBatteryClass,
    byte FirmwareBatteryFlags,
    ushort FirmwareBatterySample,
    bool FirmwareBatteryThresholdsLoaded)
{
    private const int FirmwarePowerStateOffset = 0x1FDDA;
    private const int FirmwareBatteryStateOffset = 0x17210;
    private const int FirmwareBatteryThresholdOffset = 0x17270;
    private const int FirmwareBatteryThresholdLength = 0x20;

    public static CcontControlState Normal { get; } = From(CcontAdcInputs.NormalBattery(), chargerPresent: false, chargerPwmEnabled: false, chargerPwm: 0);

    public static CcontControlState From(CcontAdcInputs inputs, Ccont ccont)
        => From(inputs, ccont.ChargerPresent, ccont.ChargerPwmEnabled, ccont.ChargerPwm);

    public static CcontControlState From(CcontAdcInputs inputs, Ccont ccont, Dct3Bus bus)
        => From(
            inputs,
            ccont.ChargerPresent,
            ccont.ChargerPwmEnabled,
            ccont.ChargerPwm,
            bus.Ram[FirmwarePowerStateOffset],
            bus.Ram[FirmwareBatteryStateOffset],
            bus.Ram[FirmwareBatteryStateOffset + 2],
            bus.Ram[FirmwareBatteryStateOffset + 4],
            BinaryPrimitives.ReadUInt16BigEndian(bus.Ram.AsSpan(FirmwareBatteryStateOffset + 0x58, 2)),
            HasNonZeroThreshold(bus.Ram));

    public ushort Get(CcontAdcChannel channel)
        => channel switch
        {
            CcontAdcChannel.AccessoryDetect => AccessoryDetect,
            CcontAdcChannel.Rssi => Rssi,
            CcontAdcChannel.BatteryVoltage => BatteryVoltage,
            CcontAdcChannel.BatteryType => BatteryType,
            CcontAdcChannel.BatteryTemperature => BatteryTemperature,
            CcontAdcChannel.ChargerVoltage => ChargerVoltage,
            CcontAdcChannel.VcxoTemperature => VcxoTemperature,
            CcontAdcChannel.ChargingCurrent => ChargingCurrent,
            _ => 0,
        };

    private static CcontControlState From(CcontAdcInputs inputs, bool chargerPresent, bool chargerPwmEnabled, byte chargerPwm)
        => From(inputs, chargerPresent, chargerPwmEnabled, chargerPwm, 0, 0, 0, 0, 0, false);

    private static CcontControlState From(
        CcontAdcInputs inputs,
        bool chargerPresent,
        bool chargerPwmEnabled,
        byte chargerPwm,
        byte firmwarePowerState,
        byte firmwareBatteryPercent,
        byte firmwareBatteryClass,
        byte firmwareBatteryFlags,
        ushort firmwareBatterySample,
        bool firmwareBatteryThresholdsLoaded)
        => new(
            inputs.AccessoryDetect,
            inputs.Rssi,
            inputs.BatteryVoltage,
            inputs.BatteryType,
            inputs.BatteryTemperature,
            inputs.ChargerVoltage,
            inputs.VcxoTemperature,
            inputs.ChargingCurrent,
            chargerPresent,
            chargerPwmEnabled,
            chargerPwm,
            firmwarePowerState,
            firmwareBatteryPercent,
            firmwareBatteryClass,
            firmwareBatteryFlags,
            firmwareBatterySample,
            firmwareBatteryThresholdsLoaded);

    private static bool HasNonZeroThreshold(byte[] ram)
        => ram.AsSpan(FirmwareBatteryThresholdOffset, FirmwareBatteryThresholdLength).IndexOfAnyExcept((byte)0) >= 0;
}
