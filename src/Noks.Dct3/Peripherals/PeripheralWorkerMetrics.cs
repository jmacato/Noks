namespace Noks.Dct3.Peripherals;

public sealed record PeripheralWorkerMetrics(
    string Peripheral,
    bool Enabled,
    long QueuedInvocations,
    long InlineInvocations,
    long WorkerWakeups,
    long CompletedInvocations,
    long SynchronizationAllocations,
    TimeSpan RequestReplyTime);
