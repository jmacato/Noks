namespace Noks.Dct3.Audio;

/// <summary>
/// Generates the phone's single mono PCM stream.
/// The generator mixes the MAD2 buzzer with the DSP earpiece oscillators.
/// </summary>
public sealed class Dct3AudioPcmGenerator
{
    public const int DefaultSampleRate = BuzzerPcmGenerator.DefaultSampleRate;

    private readonly BuzzerPcmGenerator buzzer;
    private readonly DspTonePcmGenerator dspTone;
    private ushort[] buzzerBuffer = [];

    public Dct3AudioPcmGenerator(int sampleRate = DefaultSampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        SampleRate = sampleRate;
        buzzer = new BuzzerPcmGenerator(sampleRate);
        dspTone = new DspTonePcmGenerator(sampleRate);
    }

    public int SampleRate { get; }

    public bool Audible => buzzer.Audible || dspTone.Audible;

    public void Update(Dct3AudioState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        buzzer.Update(state.Buzzer);
        dspTone.Update(state.DspTone);
    }

    /// <summary>
    /// Writes the mixed signed PCM16 two's-complement sample bits into an unsigned buffer.
    /// </summary>
    public void Render(Span<ushort> destination)
    {
        bool buzzerAudible = buzzer.Audible;
        bool dspToneAudible = dspTone.Audible;

        if (!buzzerAudible && !dspToneAudible)
        {
            destination.Clear();
            return;
        }

        if (!dspToneAudible)
        {
            buzzer.Render(destination);
            return;
        }

        if (!buzzerAudible)
        {
            dspTone.Render(destination);
            return;
        }

        if (buzzerBuffer.Length < destination.Length)
        {
            buzzerBuffer = new ushort[destination.Length];
        }

        Span<ushort> buzzerSamples = buzzerBuffer.AsSpan(0, destination.Length);
        buzzer.Render(buzzerSamples);
        dspTone.Render(destination);

        for (int i = 0; i < destination.Length; i++)
        {
            int mixed = unchecked((short)buzzerSamples[i]) + unchecked((short)destination[i]);
            destination[i] = unchecked((ushort)(short)Math.Clamp(mixed, short.MinValue, short.MaxValue));
        }
    }

    public void Reset()
    {
        buzzer.Reset();
        dspTone.Reset();
    }
}
