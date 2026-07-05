using Noks.Dct3.Messaging;
namespace Noks.Dct3.Peripherals;

public readonly record struct CcontRtcState(
    byte Control,
    byte InterruptPending,
    byte InterruptMask,
    int Second,
    int Minute,
    int Hour,
    int Day);
