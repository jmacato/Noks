using Noks.Dct3.Messaging;
namespace Noks.Dct3.Firmware;

internal readonly record struct NitzClockRuntimeHook(
    uint DispatcherAddress,
    uint IgnoredMessageHandlerAddress,
    uint DateTimeZeroAddress,
    uint CalcTimestampAddress,
    uint SetTimestampAddress,
    uint ClockStateAddress,
    uint CcontElapsedSourceReturnAddress,
    uint CcontRegisterCacheAddress);
