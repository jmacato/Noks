namespace Noks.Waku;

public sealed record WakuTransportDiagnosticEvent(
    DateTimeOffset At,
    string Direction,
    string Event,
    string Details);
