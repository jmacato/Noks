using System.Collections.Immutable;

namespace Noks.Application;

public sealed record WakuCallMediaEvent(
    Guid AttemptId,
    WakuCallMediaEventKind Kind,
    ImmutableArray<byte> Payload)
{
    public static WakuCallMediaEvent Signal(
        Guid attemptId,
        WakuCallMediaEventKind kind,
        ReadOnlySpan<byte> payload) =>
        new(attemptId, kind, ImmutableArray.Create(payload.ToArray()));

    public static WakuCallMediaEvent State(Guid attemptId, WakuCallMediaEventKind kind) =>
        new(attemptId, kind, []);
}
