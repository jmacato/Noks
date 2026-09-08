#if !BROWSER
namespace Noks.AvaloniaApp.Audio;

/// <summary>An explicitly opened desktop microphone. Samples are signed 16-bit mono PCM.</summary>
public sealed class PhoneMicrophone : IDisposable
{
    private readonly IDesktopPcmStream stream;

    private PhoneMicrophone(IDesktopPcmStream stream, int sampleRate)
    {
        this.stream = stream;
        SampleRate = sampleRate;
    }

    public int SampleRate { get; }
    public Exception? Failure => stream.Failure;

    /// <summary>
    /// Opens the default input device. The callback runs on an audio thread and must return promptly.
    /// Its memory is valid only during the callback. Copy samples before retaining them.
    /// Dispose must run outside the callback. Creating phone output never opens a microphone.
    /// </summary>
    public static PhoneMicrophone Start(Action<ReadOnlyMemory<short>> onSamples, int sampleRate = 48_000)
    {
        ArgumentNullException.ThrowIfNull(onSamples);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleRate, 192_000);
        Action<short[]> receive = samples => onSamples(samples);
        IDesktopPcmStream stream;
        if (OperatingSystem.IsWindows())
            stream = new WindowsPcmStream(sampleRate, receive, capture: true);
        else if (OperatingSystem.IsLinux())
            stream = new LinuxPcmStream(sampleRate, receive, capture: true);
        else if (OperatingSystem.IsMacOS())
            stream = new MacMicrophoneStream(sampleRate, receive);
        else
            throw new PlatformNotSupportedException("Microphone capture requires macOS, Windows, or Linux.");
        return new PhoneMicrophone(stream, sampleRate);
    }

    public void Dispose() => stream.Dispose();
}
#endif
