namespace Noks.Dct3.Radio;

public sealed record DspRuntimeState(
    byte RssiMeasurement,
    bool Registered,
    bool DedicatedChannelActive,
    int PendingIncomingServices,
    DspExecutionState ExecutionState,
    DspToneState ToneState);
