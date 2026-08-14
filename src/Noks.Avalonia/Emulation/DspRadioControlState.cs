using Noks.Dct3.Radio;

namespace Noks.AvaloniaApp.Emulation;

public sealed record DspRadioControlState(byte Rssi)
{
    public static DspRadioControlState Default { get; } = new(Dsp.DefaultRssiMeasurement);

    public static DspRadioControlState From(Dsp dsp)
        => new(dsp.RssiMeasurement);

    public static DspRadioControlState From(DspRuntimeState state)
        => new(state.RssiMeasurement);
}
