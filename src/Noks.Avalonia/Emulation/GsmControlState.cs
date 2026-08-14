using Noks.Dct3.Radio;

namespace Noks.AvaloniaApp.Emulation;

public sealed record GsmControlState(bool Registered, bool DedicatedChannelActive, int PendingIncomingServices)
{
    public static GsmControlState Default { get; } = new(
        Registered: false,
        DedicatedChannelActive: false,
        PendingIncomingServices: 0);

    public static GsmControlState From(Dsp dsp)
        => new(dsp.RegisteredOnFacadeNetwork, dsp.DedicatedChannelActive, dsp.PendingIncomingServiceCount);

    public static GsmControlState From(DspRuntimeState state)
        => new(state.Registered, state.DedicatedChannelActive, state.PendingIncomingServices);
}
