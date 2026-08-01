namespace Noks.Waku;

public sealed record WakuTransportPeerDiagnostic(
    string Id,
    string Address,
    IReadOnlyList<string> Services);
