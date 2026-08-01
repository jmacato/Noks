namespace Noks.Waku;

public readonly record struct WakuCallSignal(Guid EventId, Guid AttemptId, WakuEventKind Kind);
