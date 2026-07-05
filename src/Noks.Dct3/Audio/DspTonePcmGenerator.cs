using Noks.Dct3.Radio;
namespace Noks.Dct3.Audio;

/// <summary>
/// Synthesizes the DSP's two earpiece oscillators as signed PCM16 sample bits.
/// </summary>
public sealed class DspTonePcmGenerator
{
    private const double MaximumOutputGain = 0.45;
    private const double MaximumMailboxAmplitude = 32_767.0;
    private const double AttackSeconds = 0.003;
    private const double ReleaseSeconds = 0.004;
    private const double MinimumAudibleGain = 1.0 / short.MaxValue;

    private readonly double attackStep;
    private readonly double releaseStep;
    private double oscillator1FrequencyHz;
    private double oscillator2FrequencyHz;
    private double oscillator1Phase;
    private double oscillator2Phase;
    private double gain;
    private double targetGain;

    public DspTonePcmGenerator(int sampleRate = BuzzerPcmGenerator.DefaultSampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        SampleRate = sampleRate;
        attackStep = MaximumOutputGain / (AttackSeconds * sampleRate);
        releaseStep = MaximumOutputGain / (ReleaseSeconds * sampleRate);
    }

    public int SampleRate { get; }

    public bool Audible => targetGain > 0.0 || gain >= MinimumAudibleGain;

    public void Update(DspToneState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.Audible)
        {
            targetGain = 0.0;
            return;
        }

        bool starting = !Audible;
        oscillator1FrequencyHz = state.Oscillator1Hz;
        oscillator2FrequencyHz = state.Oscillator2Hz;
        targetGain = MaximumOutputGain * Math.Clamp(
            state.Amplitude / MaximumMailboxAmplitude,
            0.0,
            1.0);

        if (starting)
        {
            oscillator1Phase = 0.0;
            oscillator2Phase = 0.0;
        }
    }

    /// <summary>
    /// Writes signed PCM16 two's-complement sample bits into an unsigned buffer.
    /// </summary>
    public void Render(Span<ushort> destination)
    {
        if (!Audible)
        {
            destination.Clear();
            return;
        }

        int oscillatorCount =
            (oscillator1FrequencyHz > 0.0 ? 1 : 0) +
            (oscillator2FrequencyHz > 0.0 ? 1 : 0);

        for (int i = 0; i < destination.Length; i++)
        {
            UpdateEnvelope();
            double sample = 0.0;

            if (oscillator1FrequencyHz > 0.0)
            {
                sample += Math.Sin(oscillator1Phase * 2.0 * Math.PI);
                oscillator1Phase = AdvancePhase(oscillator1Phase, oscillator1FrequencyHz);
            }

            if (oscillator2FrequencyHz > 0.0)
            {
                sample += Math.Sin(oscillator2Phase * 2.0 * Math.PI);
                oscillator2Phase = AdvancePhase(oscillator2Phase, oscillator2FrequencyHz);
            }

            if (oscillatorCount > 1)
            {
                sample /= oscillatorCount;
            }

            short signedSample = (short)Math.Clamp(
                sample * gain * short.MaxValue,
                short.MinValue,
                short.MaxValue);
            destination[i] = unchecked((ushort)signedSample);
        }

        if (!Audible)
        {
            ClearOscillators();
        }
    }

    public void Reset()
    {
        targetGain = 0.0;
        gain = 0.0;
        ClearOscillators();
    }

    private void UpdateEnvelope()
    {
        if (gain < targetGain)
        {
            gain = Math.Min(targetGain, gain + attackStep);
        }
        else if (gain > targetGain)
        {
            gain = Math.Max(targetGain, gain - releaseStep);
        }

        if (targetGain == 0.0 && gain < MinimumAudibleGain)
        {
            gain = 0.0;
        }
    }

    private double AdvancePhase(double phase, double frequencyHz)
    {
        phase += frequencyHz / SampleRate;
        return phase >= 1.0 ? phase - Math.Floor(phase) : phase;
    }

    private void ClearOscillators()
    {
        oscillator1FrequencyHz = 0.0;
        oscillator2FrequencyHz = 0.0;
        oscillator1Phase = 0.0;
        oscillator2Phase = 0.0;
    }
}
