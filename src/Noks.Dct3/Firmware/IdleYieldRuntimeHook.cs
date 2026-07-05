using Noks.Dct3.Messaging;
namespace Noks.Dct3.Firmware;

public readonly record struct IdleYieldRuntimeHook(
    uint LoopStartAddress,
    uint LoopEndAddress,
    uint LoopFetchStartAddress,
    uint LoopFetchEndAddress,
    uint AliveFlagAddress,
    uint FiqClearAddress);
