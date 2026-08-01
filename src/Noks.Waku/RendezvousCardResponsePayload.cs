using System.Collections.Immutable;

namespace Noks.Waku;

public sealed record RendezvousCardResponsePayload(
    Guid RendezvousId,
    ImmutableArray<byte> ContactCard);
