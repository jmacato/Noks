namespace Noks.Dct3.Audio;

/// <summary>
/// Converts the MAD2 buzzer registers into the phone's shaped mono PCM signal.
/// Audio backends only transport and play these samples.
/// </summary>
public sealed class BuzzerPcmGenerator
{
    public const int DefaultSampleRate = 44_100;

    private const double Mad2ClockHz = 13_000_000.0;
    private const double BuzzerPrescaler = 512.0;
    private const double MaxGain = 0.70;
    // Tuned from a Nokia 3310 C5-class buzzer recording: weak fundamental and a resonant upper body.
    private const double AcousticDutyCycle = 0.56;
    private const double ResonanceFrequencyHz = 4_850.0;
    private const double ResonanceBandwidthHz = 700.0;
    private const double RisingEdgeExcitation = 0.16;
    private const double FallingEdgeExcitation = 0.08;
    private const double ResonatorMix = 1.0;
    private const double HighPassMix = 0.02;
    private const double DirectMix = 0.002;
    private const double AcousticDrive = 2.4;
    private const double AcousticOutputTrim = 2.25;
    private const double LoudnessInputGain = 2.5;
    private const double LimiterCeiling = 0.96;
    private const double HighPassCutoffHz = 900.0;
    private const double OutputHighPassCutoffHz = 2_000.0;

    private readonly double highPassCoefficient;
    private readonly double outputHighPassCoefficient;
    private readonly double resonatorCoefficient;
    private readonly double resonatorRadiusSquared;
    private double frequencyHz;
    private double gain;
    private double phase;
    private double previousSource;
    private double highPassPreviousInput;
    private double highPassOutput;
    private double outputHighPass1PreviousInput;
    private double outputHighPass1Output;
    private double outputHighPass2PreviousInput;
    private double outputHighPass2Output;
    private double outputHighPass3PreviousInput;
    private double outputHighPass3Output;
    private double resonatorY1;
    private double resonatorY2;
    private bool hasPreviousSource;

    public BuzzerPcmGenerator(int sampleRate = DefaultSampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        SampleRate = sampleRate;
        highPassCoefficient = FirstOrderHighPassCoefficient(HighPassCutoffHz, sampleRate);
        outputHighPassCoefficient = FirstOrderHighPassCoefficient(OutputHighPassCutoffHz, sampleRate);
        double resonatorRadius = Math.Exp(-Math.PI * ResonanceBandwidthHz / sampleRate);
        resonatorCoefficient = 2.0 * resonatorRadius * Math.Cos(2.0 * Math.PI * ResonanceFrequencyHz / sampleRate);
        resonatorRadiusSquared = resonatorRadius * resonatorRadius;
    }

    public int SampleRate { get; }

    public bool Audible => frequencyHz > 0.0 && gain > 0.0;

    public void Update(Mad2AudioState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        double nextFrequencyHz = state.Audible ? BuzzerFrequencyHz(state.BuzzerDivider) : 0.0;
        double nextGain = state.Audible ? BuzzerGain(state.BuzzerVolume) : 0.0;

        if (nextGain <= 0.0)
        {
            nextFrequencyHz = 0.0;
        }

        frequencyHz = nextFrequencyHz;
        gain = nextGain;

        if (!Audible)
        {
            ResetAcousticState();
        }
    }

    /// <summary>
    /// Writes signed PCM16 two's-complement sample bits into an unsigned buffer.
    /// This gives every output backend the same compact representation.
    /// </summary>
    public void Render(Span<ushort> destination)
    {
        if (!Audible)
        {
            destination.Clear();
            return;
        }

        double step = frequencyHz / SampleRate;

        for (int i = 0; i < destination.Length; i++)
        {
            double source = phase < AcousticDutyCycle ? 1.0 : -1.0;
            double excitation = EdgeExcitation(source);
            double highPassed = HighPass(source);
            double resonated = Resonator(excitation);
            double acoustic = (ResonatorMix * resonated) + (HighPassMix * highPassed) + (DirectMix * source);
            double sample = Limit(OutputHighPass(Math.Tanh(acoustic * AcousticDrive)) * AcousticOutputTrim * LoudnessInputGain) * gain;

            short signedSample = (short)Math.Clamp(
                sample * short.MaxValue,
                short.MinValue,
                short.MaxValue);
            destination[i] = unchecked((ushort)signedSample);
            phase += step;

            if (phase >= 1.0)
            {
                phase -= Math.Floor(phase);
            }
        }
    }

    public void Reset()
    {
        frequencyHz = 0.0;
        gain = 0.0;
        ResetAcousticState();
    }

    private static double BuzzerFrequencyHz(byte divider)
    {
        if (divider == 0)
        {
            return 0.0;
        }

        // Blacksphere documents the source as 13 MHz / divider. Observed firmware values need
        // the PUP clock prescaler to land in the audible range.
        return Math.Clamp(Mad2ClockHz / (divider * BuzzerPrescaler), 80.0, 4_000.0);
    }

    private static double BuzzerGain(byte volume)
        => Math.Clamp(volume / 31.0, 0.0, 1.0) * MaxGain;

    private double EdgeExcitation(double source)
    {
        if (!hasPreviousSource)
        {
            previousSource = source;
            highPassPreviousInput = source;
            hasPreviousSource = true;
            return 0.0;
        }

        if (source == previousSource)
        {
            return 0.0;
        }

        double excitation = (source - previousSource) *
            (source > previousSource ? RisingEdgeExcitation : FallingEdgeExcitation);
        previousSource = source;
        return excitation;
    }

    private double HighPass(double source)
        => FirstOrderHighPass(source, ref highPassPreviousInput, ref highPassOutput, highPassCoefficient);

    private double OutputHighPass(double source)
    {
        double first = FirstOrderHighPass(
            source,
            ref outputHighPass1PreviousInput,
            ref outputHighPass1Output,
            outputHighPassCoefficient);
        double second = FirstOrderHighPass(
            first,
            ref outputHighPass2PreviousInput,
            ref outputHighPass2Output,
            outputHighPassCoefficient);
        return FirstOrderHighPass(
            second,
            ref outputHighPass3PreviousInput,
            ref outputHighPass3Output,
            outputHighPassCoefficient);
    }

    private static double FirstOrderHighPass(
        double source,
        ref double previousInput,
        ref double previousOutput,
        double coefficient)
    {
        double output = coefficient * (previousOutput + source - previousInput);
        previousInput = source;
        previousOutput = output;
        return output;
    }

    private double Resonator(double excitation)
    {
        double output = excitation + (resonatorCoefficient * resonatorY1) -
            (resonatorRadiusSquared * resonatorY2);
        resonatorY2 = resonatorY1;
        resonatorY1 = output;
        return output;
    }

    private static double Limit(double sample)
        => Math.Tanh(sample / LimiterCeiling) * LimiterCeiling;

    private void ResetAcousticState()
    {
        phase = 0.0;
        previousSource = 0.0;
        highPassPreviousInput = 0.0;
        highPassOutput = 0.0;
        outputHighPass1PreviousInput = 0.0;
        outputHighPass1Output = 0.0;
        outputHighPass2PreviousInput = 0.0;
        outputHighPass2Output = 0.0;
        outputHighPass3PreviousInput = 0.0;
        outputHighPass3Output = 0.0;
        resonatorY1 = 0.0;
        resonatorY2 = 0.0;
        hasPreviousSource = false;
    }

    private static double FirstOrderHighPassCoefficient(double cutoffHz, int sampleRate)
    {
        double rc = 1.0 / (2.0 * Math.PI * cutoffHz);
        double dt = 1.0 / sampleRate;
        return rc / (rc + dt);
    }
}
