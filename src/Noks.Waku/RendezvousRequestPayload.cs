using System.Collections.Immutable;

namespace Noks.Waku;

public sealed record RendezvousRequestPayload(
    Guid RendezvousId,
    RendezvousRouteKind RouteKind,
    string TargetNumber,
    ImmutableArray<byte> ContactCard);
