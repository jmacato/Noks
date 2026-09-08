#if !BROWSER
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Noks.Application;
using Noks.WebRtc;
using Noks.Waku;

namespace Noks.AvaloniaApp.Audio;

/// <summary>Desktop bridge from Waku call media commands to one managed WebRTC PCMU track.</summary>
public sealed class DesktopCallMedia : IAsyncDisposable
{
    private readonly Action<WakuCallMediaEvent> emit;
    private readonly Func<bool, ManagedPeerConnection>? peerFactory;
    private readonly Func<Action<ReadOnlyMemory<short>>, IDisposable> microphoneFactory;
    private readonly Func<Action<short[]>, IDisposable> outputFactory;
    private readonly Channel<short[]> captured = Channel.CreateBounded<short[]>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Channel<short[]> received = Channel.CreateBounded<short[]>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private ManagedPeerConnection? peer;
    private IDisposable? microphone;
    private IDisposable? output;
    private Guid attempt;
    private bool disposed;
    private Task? sender;
    private CancellationTokenSource? callLifetime;
    private short[] captureRemainder = new short[160];
    private int captureCount;
    private short[]? renderFrame;
    private int renderOffset;

    public DesktopCallMedia(Action<WakuCallMediaEvent> emit,
        Func<bool, ManagedPeerConnection>? peerFactory = null,
        Func<Action<ReadOnlyMemory<short>>, IDisposable>? microphoneFactory = null,
        Func<Action<short[]>, IDisposable>? outputFactory = null)
    {
        this.emit = emit;
        this.peerFactory = peerFactory;
        this.microphoneFactory = microphoneFactory ?? (callback => PhoneMicrophone.Start(callback, 8_000));
        this.outputFactory = outputFactory ?? OpenOutput;
    }

    public async Task BeginAsync(Guid attemptId, bool caller)
    {
        await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
        ObjectDisposedException.ThrowIf(disposed, this);
        await EndCoreAsync(attempt).ConfigureAwait(false);
        attempt = attemptId;
        callLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        peer = peerFactory?.Invoke(caller) ?? new ManagedPeerConnection(caller, await ResolveStunAsync(callLifetime.Token).ConfigureAwait(false));
        ManagedPeerConnection sessionPeer = peer;
        peer.Signal += signal => { if (ReferenceEquals(peer, sessionPeer) && attempt == attemptId) EmitSignal(attemptId, signal); };
        peer.StateChanged += state => { if (!ReferenceEquals(peer, sessionPeer) || attempt != attemptId) return; if (state == WebRtcState.Connected) emit(WakuCallMediaEvent.State(attemptId, WakuCallMediaEventKind.Connected)); if (state == WebRtcState.Failed) emit(WakuCallMediaEvent.State(attemptId, WakuCallMediaEventKind.Failed)); };
        peer.AudioReceived += samples => { if (ReferenceEquals(peer, sessionPeer) && attempt == attemptId) received.Writer.TryWrite(samples.ToArray()); };
        try { output = this.outputFactory(RenderReceived); } catch { output = null; }
        try { await peer.StartAsync(callLifetime.Token).ConfigureAwait(false); }
        catch { emit(WakuCallMediaEvent.State(attemptId, WakuCallMediaEventKind.Failed)); }
        }
        finally { gate.Release(); }
    }

    public async Task ApplyAsync(Guid attemptId, WakuEventKind kind, ReadOnlyMemory<byte> payload)
    {
        await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
        if (peer is null || attempt != attemptId) return;
        WebRtcSignalKind signalKind = kind switch { WakuEventKind.SdpOffer => WebRtcSignalKind.Offer, WakuEventKind.SdpAnswer => WebRtcSignalKind.Answer, WakuEventKind.IceCandidate => WebRtcSignalKind.Candidate, _ => throw new FormatException("Unknown call-media signal.") };
        try { await peer.ApplySignalAsync(new WebRtcSignal(signalKind, System.Text.Encoding.UTF8.GetString(payload.Span)), callLifetime?.Token ?? lifetime.Token).ConfigureAwait(false); }
        catch { emit(WakuCallMediaEvent.State(attemptId, WakuCallMediaEventKind.Failed)); }
        }
        finally { gate.Release(); }
    }

    public Task ActivateAsync(Guid attemptId)
    {
        return ActivateCoreAsync(attemptId);
    }
    private async Task ActivateCoreAsync(Guid attemptId)
    {
        await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
        if (peer is null || attempt != attemptId || microphone is not null) return;
        try
        {
            ManagedPeerConnection target = peer;
            CancellationToken token = callLifetime!.Token;
            microphone = microphoneFactory(samples => { if (!token.IsCancellationRequested) OnCaptured(samples); });
            sender = Task.Run(() => SendCapturedAsync(target, attemptId, token));
        }
        catch { /* no microphone is receive-only; signaling and playback remain available */ }
        }
        finally { gate.Release(); }
    }

    private void OnCaptured(ReadOnlyMemory<short> samples)
    {
        foreach (short sample in samples.Span)
        {
            captureRemainder[captureCount++] = sample;
            if (captureCount == 160) { captured.Writer.TryWrite(captureRemainder); captureRemainder = new short[160]; captureCount = 0; }
        }
    }

    private async Task SendCapturedAsync(ManagedPeerConnection target, Guid id, CancellationToken token)
    {
        try { await target.WaitForConnectedAsync(token).ConfigureAwait(false); await foreach (short[] frame in captured.Reader.ReadAllAsync(token)) if (!token.IsCancellationRequested) await target.SendAudioAsync(frame, token); }
        catch (OperationCanceledException) { }
        catch { if (!token.IsCancellationRequested && attempt == id) emit(WakuCallMediaEvent.State(id, WakuCallMediaEventKind.Failed)); }
    }

    private void RenderReceived(short[] target)
    {
        target.AsSpan().Clear();
        int offset = 0;
        while (offset < target.Length)
        {
            if (renderFrame is null && !received.Reader.TryRead(out renderFrame)) break;
            int count = Math.Min(target.Length - offset, renderFrame!.Length - renderOffset);
            renderFrame.AsSpan(renderOffset, count).CopyTo(target.AsSpan(offset));
            offset += count;
            renderOffset += count;
            if (renderOffset == renderFrame.Length) { renderFrame = null; renderOffset = 0; }
        }
    }

    private static IDisposable OpenOutput(Action<short[]> render)
    {
        if (OperatingSystem.IsWindows()) return new WindowsPcmStream(8_000, render, capture: false);
        if (OperatingSystem.IsLinux()) return new LinuxPcmStream(8_000, render, capture: false);
        if (OperatingSystem.IsMacOS()) return new BuzzerAudio(8_000, render);
        throw new PlatformNotSupportedException("PCM output requires macOS, Windows, or Linux.");
    }

    private static async Task<IReadOnlyList<IPEndPoint>> ResolveStunAsync(CancellationToken token)
    {
        string[] hosts = ["global.stun.twilio.com", "singapore.stun.twilio.com", "tokyo.stun.twilio.com"];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var endpoints = new List<IPEndPoint>();
        foreach (string host in hosts)
        {
            try
            {
                foreach (IPAddress address in await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false))
                    endpoints.Add(new IPEndPoint(address, 3478));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { break; }
            catch (SocketException) { }
        }
        return endpoints;
    }

    private void EmitSignal(Guid id, WebRtcSignal signal)
    {
        WakuCallMediaEventKind kind = signal.Kind switch { WebRtcSignalKind.Offer => WakuCallMediaEventKind.SdpOffer, WebRtcSignalKind.Answer => WakuCallMediaEventKind.SdpAnswer, _ => WakuCallMediaEventKind.IceCandidate };
        emit(WakuCallMediaEvent.Signal(id, kind, System.Text.Encoding.UTF8.GetBytes(signal.Json)));
    }

    public async Task EndAsync(Guid attemptId)
    {
        await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try { await EndCoreAsync(attemptId).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private async Task EndCoreAsync(Guid attemptId)
    {
        if (attemptId != Guid.Empty && attempt != attemptId) return;
        attempt = Guid.Empty;
        callLifetime?.Cancel();
        microphone?.Dispose(); microphone = null;
        if (sender is not null) { try { await sender.ConfigureAwait(false); } catch { } sender = null; }
        if (peer is { } closing) { peer = null; await closing.DisposeAsync().ConfigureAwait(false); }
        output?.Dispose(); output = null;
        while (captured.Reader.TryRead(out _)) { }
        while (received.Reader.TryRead(out _)) { }
        captureCount = 0;
        captureRemainder = new short[160];
        renderFrame = null;
        renderOffset = 0;
        callLifetime?.Dispose(); callLifetime = null;
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            await EndCoreAsync(Guid.Empty).ConfigureAwait(false);
            lifetime.Cancel();
        }
        finally { gate.Release(); }
    }
}
#endif
