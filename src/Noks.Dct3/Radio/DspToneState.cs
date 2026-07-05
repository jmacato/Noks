namespace Noks.Dct3.Radio;

/// <summary>
/// This record is a snapshot of the DSP earpiece-tone mailbox. The oscillator
/// frequencies use the firmware's quarter-Hz register format.
/// </summary>
public sealed record DspToneState(
    ushort ToneEnable,
    ushort Oscillator1QuarterHz,
    ushort Oscillator2QuarterHz,
    ushort Amplitude,
    ushort AudioCommand)
{
    public static DspToneState Off { get; } = new(0, 0, 0, 0, 0);

    public ushort AudioCommandKind => (ushort)(AudioCommand & 0x1F);

    public double Oscillator1Hz => Oscillator1QuarterHz / 4.0;

    public double Oscillator2Hz => Oscillator2QuarterHz / 4.0;

    public bool Audible =>
        (ToneEnable & 1) != 0 &&
        Amplitude != 0 &&
        (Oscillator1QuarterHz != 0 || Oscillator2QuarterHz != 0);
}
