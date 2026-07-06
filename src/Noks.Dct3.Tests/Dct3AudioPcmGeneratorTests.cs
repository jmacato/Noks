using Noks.Dct3.Audio;
using Noks.Dct3.Radio;
namespace Noks.Dct3.Tests;

public sealed class Dct3AudioPcmGeneratorTests
{
    private static readonly DspToneState DtmfOne = new(
        ToneEnable: 0x00E1,
        Oscillator1QuarterHz: 1209 * 4,
        Oscillator2QuarterHz: 697 * 4,
        Amplitude: 0x7FFF,
        AudioCommand: 0);

    [Fact]
    public void DspToneRendersBothDtmfOscillatorsAtMailboxFrequencies()
    {
        DspTonePcmGenerator generator = new();
        ushort[] samples = new ushort[Dct3AudioPcmGenerator.DefaultSampleRate];

        generator.Update(DtmfOne);
        generator.Render(samples);

        short[] signed = samples.Select(sample => unchecked((short)sample)).ToArray();
        double rowMagnitude = SpectralMagnitude(signed, 697);
        double columnMagnitude = SpectralMagnitude(signed, 1209);
        double unrelatedMagnitude = SpectralMagnitude(signed, 1_000);
        Assert.True(rowMagnitude > unrelatedMagnitude * 20.0);
        Assert.True(columnMagnitude > unrelatedMagnitude * 20.0);
    }

    [Fact]
    public void DspToneReleaseReachesDeterministicSilence()
    {
        DspTonePcmGenerator generator = new();
        ushort[] attack = new ushort[512];
        ushort[] release = new ushort[512];

        generator.Update(DtmfOne);
        generator.Render(attack);
        generator.Update(DspToneState.Off);
        generator.Render(release);

        Assert.Contains(release, sample => sample != 0);
        Assert.All(release.AsSpan(256).ToArray(), sample => Assert.Equal((ushort)0, sample));
        Assert.False(generator.Audible);
    }

    [Fact]
    public void DspToneChunkBoundariesDoNotChangeGeneratedMusic()
    {
        DspTonePcmGenerator contiguousGenerator = new();
        DspTonePcmGenerator chunkedGenerator = new();
        ushort[] contiguous = new ushort[4_096];
        ushort[] chunked = new ushort[contiguous.Length];

        contiguousGenerator.Update(DtmfOne);
        chunkedGenerator.Update(DtmfOne);
        contiguousGenerator.Render(contiguous);
        chunkedGenerator.Render(chunked.AsSpan(0, 1_137));
        chunkedGenerator.Render(chunked.AsSpan(1_137, 2_003));
        chunkedGenerator.Render(chunked.AsSpan(3_140));

        Assert.Equal(contiguous, chunked);
    }

    [Fact]
    public void UnifiedGeneratorSaturatesSumOfBuzzerAndDspToneIntoOneStream()
    {
        Mad2AudioState buzzerState = new(true, 64, 31);
        Dct3AudioState state = new(buzzerState, DtmfOne);
        BuzzerPcmGenerator buzzer = new();
        DspTonePcmGenerator dspTone = new();
        Dct3AudioPcmGenerator unified = new();
        ushort[] buzzerSamples = new ushort[4_096];
        ushort[] dspToneSamples = new ushort[4_096];
        ushort[] actual = new ushort[4_096];

        buzzer.Update(buzzerState);
        dspTone.Update(DtmfOne);
        unified.Update(state);
        buzzer.Render(buzzerSamples);
        dspTone.Render(dspToneSamples);
        unified.Render(actual);

        ushort[] expected = new ushort[actual.Length];
        for (int i = 0; i < expected.Length; i++)
        {
            int mixed = unchecked((short)buzzerSamples[i]) + unchecked((short)dspToneSamples[i]);
            expected[i] = unchecked((ushort)(short)Math.Clamp(mixed, short.MinValue, short.MaxValue));
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UnifiedOffStateRendersSignedPcmSilence()
    {
        Dct3AudioPcmGenerator generator = new();
        ushort[] samples = Enumerable.Repeat(ushort.MaxValue, 512).ToArray();

        generator.Update(Dct3AudioState.Off);
        generator.Render(samples);

        Assert.All(samples, sample => Assert.Equal((ushort)0, sample));
    }

    private static double SpectralMagnitude(short[] samples, double frequencyHz)
    {
        int start = Dct3AudioPcmGenerator.DefaultSampleRate / 10;
        double real = 0.0;
        double imaginary = 0.0;

        for (int i = start; i < samples.Length; i++)
        {
            double angle = 2.0 * Math.PI * frequencyHz * i / Dct3AudioPcmGenerator.DefaultSampleRate;
            double sample = samples[i] / 32_768.0;
            real += sample * Math.Cos(angle);
            imaginary -= sample * Math.Sin(angle);
        }

        return Math.Sqrt((real * real) + (imaginary * imaginary));
    }
}
