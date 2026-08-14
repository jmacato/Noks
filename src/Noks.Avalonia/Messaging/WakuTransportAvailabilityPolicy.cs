using Noks.Waku;

namespace Noks.AvaloniaApp.Messaging;

internal static class WakuTransportAvailabilityPolicy
{
    public static bool HasRealtimeRoute(WakuTransportDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return string.Equals(diagnostics.Phase, "ready", StringComparison.Ordinal) &&
            diagnostics.PeerCount > 0 &&
            diagnostics.LightPushReady &&
            diagnostics.FilterReady;
    }
}
