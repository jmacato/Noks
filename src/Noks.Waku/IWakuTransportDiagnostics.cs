namespace Noks.Waku;

public interface IWakuTransportDiagnostics
{
    WakuTransportDiagnostics Diagnostics { get; }

    string DiagnosticsReport { get; }

    event Action<WakuTransportDiagnostics>? DiagnosticsChanged;

    ValueTask RefreshDiagnosticsAsync(CancellationToken cancellationToken = default);
}
