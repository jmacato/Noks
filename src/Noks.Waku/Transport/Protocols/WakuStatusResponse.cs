namespace Noks.Waku.Transport.Protocols;

internal readonly record struct WakuStatusResponse(
    string RequestId,
    uint StatusCode,
    string? StatusDescription,
    uint? RelayPeerCount);
