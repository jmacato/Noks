#if !BROWSER
using System.Runtime.InteropServices;
using System.Threading;

namespace Noks.AvaloniaApp.Audio;

internal sealed class LinuxPcmStream : IDesktopPcmStream
{
    private const string AlsaLibrary = "libasound.so.2";
    private const int Playback = 0;
    private const int Capture = 1;
    private const int NonBlocking = 1;
    private const int RwInterleaved = 3;
    private const int Signed16LittleEndian = 2;
    private const int FramesPerBuffer = 256;
    private const int WaitTimeoutMilliseconds = 20;
    private const int Again = -11;
    private const int Interrupted = -4;
    private const int Pipe = -32;
    private const int StreamPipe = -86;
    private const int MaximumConsecutiveRecoveries = 8;

    private readonly object disposeLock = new();
    private readonly bool capture;
    private readonly Action<short[]> process;
    private readonly Thread worker;
    private IntPtr pcm;
    private Exception? failure;
    private int stopping;
    private int disposed;

    internal LinuxPcmStream(int sampleRate, Action<short[]> process, bool capture)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentNullException.ThrowIfNull(process);

        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("ALSA PCM output is only available on Linux.");

        this.capture = capture;
        this.process = process;

        IntPtr openedPcm = IntPtr.Zero;

        try
        {
            Check(
                snd_pcm_open(out openedPcm, "default", capture ? Capture : Playback, NonBlocking),
                nameof(snd_pcm_open));
            Check(
                snd_pcm_set_params(
                    openedPcm,
                    Signed16LittleEndian,
                    RwInterleaved,
                    channels: 1,
                    rate: checked((uint)sampleRate),
                    softResample: 1,
                    latency: 20_000),
                nameof(snd_pcm_set_params));

            pcm = openedPcm;
            openedPcm = IntPtr.Zero;
            worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "Noks ALSA PCM",
            };
            worker.Start();
        }
        catch
        {
            if (openedPcm != IntPtr.Zero)
                _ = snd_pcm_close(openedPcm);
            else
                ClosePcm();
            throw;
        }
    }

    public Exception? Failure => Volatile.Read(ref failure);

    public void Dispose()
    {
        if (Thread.CurrentThread == worker)
            throw new InvalidOperationException("An ALSA PCM callback cannot dispose its own stream.");

        lock (disposeLock)
        {
            if (Volatile.Read(ref disposed) != 0)
                return;

            Volatile.Write(ref stopping, 1);
            worker.Join();
            Volatile.Write(ref disposed, 1);
        }
    }

    private void WorkerLoop()
    {
        try
        {
            short[] samples = new short[FramesPerBuffer];
            GCHandle samplesHandle = GCHandle.Alloc(samples, GCHandleType.Pinned);

            try
            {
                while (Volatile.Read(ref stopping) == 0)
                {
                    if (capture)
                    {
                        if (!Read(samplesHandle.AddrOfPinnedObject(), samples.Length))
                            break;
                    }
                    else
                    {
                        process(samples);
                    }

                    if (Volatile.Read(ref stopping) != 0)
                        break;

                    if (capture)
                        process(samples);
                    else
                        Write(samplesHandle.AddrOfPinnedObject(), samples.Length);
                }
            }
            finally
            {
                samplesHandle.Free();
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref failure, exception);
            Volatile.Write(ref stopping, 1);
        }
        finally
        {
            ClosePcm();
        }
    }

    private void Write(IntPtr samples, int count)
    {
        int offset = 0;
        int consecutiveRecoveries = 0;

        while (offset < count && Volatile.Read(ref stopping) == 0)
        {
            IntPtr currentPcm = pcm;

            if (currentPcm == IntPtr.Zero)
                return;

            nint written = snd_pcm_writei(
                currentPcm,
                IntPtr.Add(samples, checked(offset * sizeof(short))),
                checked((nuint)(count - offset)));

            if (written > 0)
            {
                offset += checked((int)written);
                consecutiveRecoveries = 0;
                continue;
            }

            if (written == 0 || written == Again)
            {
                WaitForWritable(currentPcm, ref consecutiveRecoveries);
                continue;
            }

            if (written == Interrupted)
                continue;

            _ = RecoverOrThrow(currentPcm, checked((int)written), nameof(snd_pcm_writei), ref consecutiveRecoveries);
        }
    }

    private bool Read(IntPtr samples, int count)
    {
        int offset = 0;
        int consecutiveRecoveries = 0;

        while (offset < count && Volatile.Read(ref stopping) == 0)
        {
            IntPtr currentPcm = pcm;

            if (currentPcm == IntPtr.Zero)
                return false;

            nint read = snd_pcm_readi(
                currentPcm,
                IntPtr.Add(samples, checked(offset * sizeof(short))),
                checked((nuint)(count - offset)));

            if (read > 0)
            {
                offset += checked((int)read);
                consecutiveRecoveries = 0;
                continue;
            }

            if (read == 0 || read == Again)
            {
                if (WaitForReady(currentPcm, ref consecutiveRecoveries))
                    offset = 0;
                continue;
            }

            if (read == Interrupted)
                continue;

            _ = RecoverOrThrow(currentPcm, checked((int)read), nameof(snd_pcm_readi), ref consecutiveRecoveries);
            // Recovery invalidates the partial capture buffer. Refill it before notifying
            // the callback so it never observes audio from before and after an xrun.
            offset = 0;
        }

        return offset == count;
    }

    private void WaitForWritable(IntPtr currentPcm, ref int consecutiveRecoveries)
        => _ = WaitForReady(currentPcm, ref consecutiveRecoveries);

    private bool WaitForReady(IntPtr currentPcm, ref int consecutiveRecoveries)
    {
        int result = snd_pcm_wait(currentPcm, WaitTimeoutMilliseconds);

        if (result >= 0 || result == Interrupted)
            return false;

        return RecoverOrThrow(currentPcm, result, nameof(snd_pcm_wait), ref consecutiveRecoveries);
    }

    private static bool RecoverOrThrow(IntPtr currentPcm, int error, string operation, ref int consecutiveRecoveries)
    {
        if (error is not (Pipe or StreamPipe))
            throw CreateException(operation, error);

        if (++consecutiveRecoveries > MaximumConsecutiveRecoveries)
        {
            throw new InvalidOperationException(
                $"ALSA PCM did not recover after {MaximumConsecutiveRecoveries} consecutive device errors.");
        }

        if (error == Pipe)
        {
            Check(snd_pcm_prepare(currentPcm), nameof(snd_pcm_prepare));
            return true;
        }

        if (error == StreamPipe)
        {
            // snd_pcm_recover retries a suspended device with one-second sleeps. A single
            // resume attempt avoids that unbounded path; prepare is the bounded fallback.
            if (snd_pcm_resume(currentPcm) < 0)
                Check(snd_pcm_prepare(currentPcm), nameof(snd_pcm_prepare));
            return true;
        }

        throw new InvalidOperationException("ALSA PCM recovery reached an invalid error state.");
    }

    private void ClosePcm()
    {
        IntPtr currentPcm = Interlocked.Exchange(ref pcm, IntPtr.Zero);

        if (currentPcm == IntPtr.Zero)
            return;

        _ = snd_pcm_drop(currentPcm);
        _ = snd_pcm_close(currentPcm);
    }

    private static void Check(int result, string operation)
    {
        if (result < 0)
            throw CreateException(operation, result);
    }

    private static InvalidOperationException CreateException(string operation, int error)
    {
        string? description = Marshal.PtrToStringAnsi(snd_strerror(error));
        return new InvalidOperationException($"{operation} failed with ALSA error {error}: {description ?? "unknown error"}.");
    }

    [DllImport(AlsaLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_open(out IntPtr pcm, string name, int stream, int mode);

    [DllImport(AlsaLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_set_params(
        IntPtr pcm,
        int format,
        int access,
        uint channels,
        uint rate,
        int softResample,
        uint latency);

    [DllImport(AlsaLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint snd_pcm_writei(IntPtr pcm, IntPtr buffer, nuint size);

    [DllImport(AlsaLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint snd_pcm_readi(IntPtr pcm, IntPtr buffer, nuint size);

    [DllImport(AlsaLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_wait(IntPtr pcm, int timeout);

    [DllImport(AlsaLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_prepare(IntPtr pcm);

    [DllImport(AlsaLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_resume(IntPtr pcm);

    [DllImport(AlsaLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_drop(IntPtr pcm);

    [DllImport(AlsaLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_close(IntPtr pcm);

    [DllImport(AlsaLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr snd_strerror(int error);
}
#endif
