namespace Noks.Dct3.Peripherals;

public sealed class CcontAdcInputs
{
    public const ushort MaximumAdcValue = 0x3FF;

    public ushort AccessoryDetect { get; set; }

    public ushort Rssi { get; set; }

    public ushort BatteryVoltage { get; set; }

    public ushort BatteryType { get; set; }

    public ushort BatteryTemperature { get; set; }

    public ushort ChargerVoltage { get; set; }

    public ushort VcxoTemperature { get; set; }

    public ushort ChargingCurrent { get; set; }

    public bool AutomaticChargingCurrent { get; private set; } = true;

    public static CcontAdcInputs NormalBattery()
    {
        return new CcontAdcInputs
        {
            AccessoryDetect = 0x000,
            Rssi = 0x220,
            BatteryVoltage = 0x2E6,
            BatteryType = 0x280,
            BatteryTemperature = 0x100,
            ChargerVoltage = 0x000,
            VcxoTemperature = 0x1A0,
            ChargingCurrent = 0x000,
        };
    }

    public ushort Get(int channel)
    {
        return channel switch
        {
            0 => AccessoryDetect,
            1 => Rssi,
            2 => BatteryVoltage,
            3 => BatteryType,
            4 => BatteryTemperature,
            5 => ChargerVoltage,
            6 => VcxoTemperature,
            _ => ChargingCurrent,
        };
    }

    public void Set(int channel, ushort value)
    {
        value = Clamp(value);

        switch (channel)
        {
            case 0:
                AccessoryDetect = value;
                break;
            case 1:
                Rssi = value;
                break;
            case 2:
                BatteryVoltage = value;
                break;
            case 3:
                BatteryType = value;
                break;
            case 4:
                BatteryTemperature = value;
                break;
            case 5:
                ChargerVoltage = value;
                if (value == 0)
                {
                    AutomaticChargingCurrent = true;
                }

                break;
            case 6:
                VcxoTemperature = value;
                break;
            default:
                ChargingCurrent = value;
                AutomaticChargingCurrent = false;
                break;
        }
    }

    public void CopyFrom(CcontAdcInputs source)
    {
        AccessoryDetect = source.AccessoryDetect;
        Rssi = source.Rssi;
        BatteryVoltage = source.BatteryVoltage;
        BatteryType = source.BatteryType;
        BatteryTemperature = source.BatteryTemperature;
        ChargerVoltage = source.ChargerVoltage;
        VcxoTemperature = source.VcxoTemperature;
        ChargingCurrent = source.ChargingCurrent;
        AutomaticChargingCurrent = source.AutomaticChargingCurrent;
    }

    public bool SetAutomaticChargingCurrent(ushort value)
    {
        if (!AutomaticChargingCurrent)
        {
            return false;
        }

        value = Clamp(value);
        if (ChargingCurrent == value)
        {
            return false;
        }

        ChargingCurrent = value;
        return true;
    }

    public void UseAutomaticChargingCurrent()
    {
        AutomaticChargingCurrent = true;
    }

    private static ushort Clamp(ushort value)
        => Math.Min(value, MaximumAdcValue);
}
