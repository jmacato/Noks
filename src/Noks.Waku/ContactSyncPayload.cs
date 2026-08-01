using System.Collections.Immutable;

namespace Noks.Waku;

public sealed record ContactSyncPayload(
    Guid TransactionId,
    ContactSyncKind Kind,
    ImmutableArray<byte> ContactCard);
