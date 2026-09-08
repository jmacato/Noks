#if !BROWSER
using System.Runtime.InteropServices;
using Noks.Dct3.Audio;

namespace Noks.AvaloniaApp.Audio;

public sealed class DesktopPhoneAudio : IPhoneAudio
{
    private readonly object stateLock = new();
    private readonly object disposeLock = new();
    private readonly Dct3AudioPcmGenerator generator = new();
    private readonly IDesktopPcmStream stream;
    private bool disposed;
    private bool closed;

    public DesktopPhoneAudio() : this(OpenPlayback)
    {
    }

    internal DesktopPhoneAudio(Func<int, Action<short[]>, IDesktopPcmStream> open)
    {
        stream = open(generator.SampleRate, Render);
    }

    public void Update(Dct3AudioState state)
    {
        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (stream.Failure is { } failure)
                throw new InvalidOperationException("The desktop audio device stopped.", failure);
            generator.Update(state);
        }
    }

    public void Dispose()
    {
        lock (disposeLock)
        {
            if (closed)
                return;
            lock (stateLock)
            {
                disposed = true;
                generator.Reset();
            }

            // Device shutdown waits for its worker. The worker must remain free to enter Render.
            stream.Dispose();
            closed = true;
        }
    }

    private void Render(short[] samples)
    {
        lock (stateLock)
        {
            if (disposed)
                samples.AsSpan().Clear();
            else
                generator.Render(MemoryMarshal.Cast<short, ushort>(samples.AsSpan()));
        }
    }

    private static IDesktopPcmStream OpenPlayback(int sampleRate, Action<short[]> render)
    {
        if (OperatingSystem.IsWindows())
            return new WindowsPcmStream(sampleRate, render, capture: false);
        if (OperatingSystem.IsLinux())
            return new LinuxPcmStream(sampleRate, render, capture: false);
        throw new PlatformNotSupportedException("This PCM output backend requires Windows or Linux.");
    }
}
#endif
