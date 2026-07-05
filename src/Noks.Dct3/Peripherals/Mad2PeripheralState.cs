namespace Noks.Dct3.Peripherals;

public sealed record Mad2PeripheralState(
    bool VibratorEnabled,
    byte VibratorControl,
    bool LcdBacklightOn,
    bool KeypadBacklightOn,
    bool LedDriveEnabled)
{
    public static Mad2PeripheralState Off { get; } = new(false, 0, false, false, false);
}
