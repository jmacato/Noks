#if BROWSER
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Noks.Dct3.Audio;
using Noks.AvaloniaApp.Audio;

namespace Noks.AvaloniaApp.Browser;

public sealed class BrowserBuzzerAudio : IPhoneAudio
{
    private const int MaximumRequestedFrames = 8_192;
    private readonly object generatorLock = new();
    private readonly DispatcherTimer pcmPumpTimer;
    private Action<Guid>? announcementEndedHandler;
    private Action<string>? browserAnnouncementEndedHandler;
    private Dct3AudioPcmGenerator? generator;
    private Dct3AudioState state = Dct3AudioState.Off;
    private ushort[] pcmBuffer = [];
    private int pcmSampleRate;
    private bool pumpFailureLogged;
    private bool disposed;

    public BrowserBuzzerAudio()
    {
        pcmPumpTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(10),
        };
        pcmPumpTimer.Tick += OnPcmPumpTick;
    }

    public bool SupportsAnnouncements => true;

    public void Update(Dct3AudioState state)
    {
        ThrowIfDisposed();
        bool changed;

        lock (generatorLock)
        {
            changed = this.state != state;
            if (!changed)
            {
                return;
            }

            this.state = state;
            if (generator is not null)
            {
                // Any queued browser PCM belongs to the previous firmware state and is discarded.
                // Reset the generator too so it cannot resume from synthesis state ahead of playback.
                generator.Reset();
                generator.Update(state);
            }
        }

        BrowserAudioInterop.SetPcmActive(state.Audible);
        if (state.Audible)
        {
            BrowserInteractionLatencyBenchmark.MarkBackendUpdate();
            pcmPumpTimer.Start();
            PumpPcm(MaximumRequestedFrames);
        }
        else
        {
            pcmPumpTimer.Stop();
        }
    }

    public void PlayAnnouncement(CallAudioAnnouncement announcement)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(announcement);
        BrowserAudioInterop.PlayAnnouncement(
            (int)announcement.Kind,
            announcement.CallId.ToString("D"));
    }

    public void StopAnnouncement(Guid callId)
    {
        ThrowIfDisposed();
        BrowserAudioInterop.StopAnnouncement(callId.ToString("D"));
    }

    public void SetAnnouncementEndedHandler(Action<Guid>? handler)
    {
        ThrowIfDisposed();
        announcementEndedHandler = handler;
        if (handler is null)
        {
            BrowserAudioInterop.ClearAnnouncementEndedHandler();
            return;
        }

        browserAnnouncementEndedHandler ??= OnBrowserAnnouncementEnded;
        BrowserAudioInterop.SetAnnouncementEndedHandler(browserAnnouncementEndedHandler);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        pcmPumpTimer.Stop();
        pcmPumpTimer.Tick -= OnPcmPumpTick;
        announcementEndedHandler = null;
        browserAnnouncementEndedHandler = null;
        BrowserAudioInterop.SetPcmActive(false);
        BrowserAudioInterop.ClearAnnouncementEndedHandler();
        BrowserAudioInterop.Dispose();
    }

    private void OnBrowserAnnouncementEnded(string callId)
    {
        if (Guid.TryParseExact(callId, "D", out Guid parsed))
        {
            announcementEndedHandler?.Invoke(parsed);
        }
    }

    private void OnPcmPumpTick(object? sender, EventArgs e)
    {
        try
        {
            PumpPcm();
            pumpFailureLogged = false;
        }
        catch (Exception ex) when (!disposed)
        {
            if (!pumpFailureLogged)
            {
                Console.Error.WriteLine($"Browser PCM pump unavailable: {ex.Message}");
                pumpFailureLogged = true;
            }
        }
    }

    private void PumpPcm(int requestedFrames = 0)
    {
        if (disposed)
        {
            return;
        }

        if (requestedFrames <= 0)
        {
            requestedFrames = BrowserAudioInterop.TakePcmDemandFrames();
        }

        if (requestedFrames <= 0)
        {
            return;
        }

        int latencyTraceId = BrowserInteractionLatencyBenchmark.MarkPcmDemand();
        int sampleRate = pcmSampleRate;
        if (sampleRate is < 8_000 or > 192_000)
        {
            sampleRate = BrowserAudioInterop.GetPcmSampleRate();
            if (sampleRate is >= 8_000 and <= 192_000)
            {
                pcmSampleRate = sampleRate;
            }
        }

        int frames = Math.Clamp(requestedFrames, 128, MaximumRequestedFrames);
        int effectiveSampleRate = sampleRate is >= 8_000 and <= 192_000
            ? sampleRate
            : Dct3AudioPcmGenerator.DefaultSampleRate;

        lock (generatorLock)
        {
            if (generator is null || generator.SampleRate != effectiveSampleRate)
            {
                generator = new Dct3AudioPcmGenerator(effectiveSampleRate);
                generator.Update(state);
            }

            if (!generator.Audible)
            {
                return;
            }

            if (pcmBuffer.Length != frames)
            {
                pcmBuffer = new ushort[frames];
            }

            BrowserInteractionLatencyBenchmark.MarkPcmRenderStarted(latencyTraceId);
            generator.Render(pcmBuffer);
            BrowserInteractionLatencyBenchmark.MarkPcmRenderCompleted(latencyTraceId);
            Span<byte> pcmBytes = MemoryMarshal.AsBytes(pcmBuffer.AsSpan());
            if (latencyTraceId > 0)
            {
                BrowserAudioInterop.EnqueuePcmWithLatency(
                    pcmBytes,
                    latencyTraceId,
                    BrowserInteractionLatencyBenchmark.CreateSnapshot(latencyTraceId));
            }
            else
            {
                BrowserAudioInterop.EnqueuePcm(pcmBytes);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(BrowserBuzzerAudio));
        }
    }
}
#endif
