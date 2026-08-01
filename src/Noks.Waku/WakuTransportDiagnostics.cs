namespace Noks.Waku;

public sealed record WakuTransportDiagnostics(
    string Phase,
    string Mode,
    int PeerCount,
    bool LightPushReady,
    bool FilterReady,
    bool StoreReady,
    int TopicCount,
    long PublishAttempts,
    long PublishSuccesses,
    long PublishFailures,
    long LiveMessages,
    long StoreQueries,
    long StoreRecords,
    string LastEvent,
    string? LastError,
    IReadOnlyList<WakuTransportPeerDiagnostic> Peers,
    IReadOnlyList<WakuTransportDiagnosticEvent> RecentEvents)
{
    public static WakuTransportDiagnostics Empty { get; } = new(
        "idle",
        "public",
        0,
        false,
        false,
        false,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        "idle",
        null,
        [],
        []);
}
