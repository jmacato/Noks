namespace Noks.Waku.Transport.Libp2p.Protocols;

internal sealed record Libp2pIdentifyResponse(
    byte[]? PublicKey,
    IReadOnlySet<string> Protocols,
    string? ProtocolVersion,
    string? AgentVersion);
