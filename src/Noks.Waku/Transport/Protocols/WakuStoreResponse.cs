namespace Noks.Waku.Transport.Protocols;

internal readonly record struct WakuStoreResponse(
    string RequestId,
    uint StatusCode,
    string? StatusDescription,
    IReadOnlyList<WakuWireMessage> Messages,
    byte[]? PaginationCursor);
