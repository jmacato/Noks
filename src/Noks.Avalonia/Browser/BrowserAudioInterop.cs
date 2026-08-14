#if BROWSER
using System.Runtime.InteropServices.JavaScript;

namespace Noks.AvaloniaApp.Browser;

internal static partial class BrowserAudioInterop
{
    public const string ModuleName = "noks-audio";

    [JSImport("setPcmActive", ModuleName)]
    internal static partial void SetPcmActive(bool active);

    [JSImport("enqueuePcm", ModuleName)]
    internal static partial void EnqueuePcm(
        [JSMarshalAs<JSType.MemoryView>]
        Span<byte> samples);

    [JSImport("enqueuePcm", ModuleName)]
    internal static partial void EnqueuePcmWithLatency(
        [JSMarshalAs<JSType.MemoryView>]
        Span<byte> samples,
        int latencyTraceId,
        string latencyMetadata);

    [JSImport("takeLatencyInputId", ModuleName)]
    internal static partial int TakeLatencyInputId();

    [JSImport("takePcmDemandFrames", ModuleName)]
    internal static partial int TakePcmDemandFrames();

    [JSImport("getPcmSampleRate", ModuleName)]
    internal static partial int GetPcmSampleRate();

    [JSImport("reactivate", ModuleName)]
    internal static partial Task<bool> Reactivate();

    [JSImport("playAnnouncement", ModuleName)]
    internal static partial void PlayAnnouncement(int kind, string callId);

    [JSImport("stopAnnouncement", ModuleName)]
    internal static partial void StopAnnouncement(string callId);

    [JSImport("setAnnouncementEndedHandler", ModuleName)]
    internal static partial void SetAnnouncementEndedHandler(
        [JSMarshalAs<JSType.Function<JSType.String>>]
        Action<string> handler);

    [JSImport("clearAnnouncementEndedHandler", ModuleName)]
    internal static partial void ClearAnnouncementEndedHandler();

    [JSImport("dispose", ModuleName)]
    internal static partial void Dispose();
}
#endif
