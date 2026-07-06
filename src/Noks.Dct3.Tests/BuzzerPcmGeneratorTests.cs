using Noks.Dct3.Audio;
namespace Noks.Dct3.Tests;

public sealed class BuzzerPcmGeneratorTests
{
    [Fact]
    public void OffStateRendersSignedPcmSilence()
    {
        BuzzerPcmGenerator generator = new();
        ushort[] samples = Enumerable.Repeat(ushort.MaxValue, 512).ToArray();

        generator.Update(Mad2AudioState.Off);
        generator.Render(samples);

        Assert.All(samples, sample => Assert.Equal((ushort)0, sample));
    }

    [Fact]
    public void AudibleStateRendersBoundedResonantPcm()
    {
        BuzzerPcmGenerator generator = new();
        ushort[] samples = new ushort[BuzzerPcmGenerator.DefaultSampleRate];

        generator.Update(new Mad2AudioState(true, 64, 31));
        generator.Render(samples);

        short[] signed = samples.Select(sample => unchecked((short)sample)).ToArray();
        Assert.Contains(signed, sample => sample > 1_000);
        Assert.Contains(signed, sample => sample < -1_000);
        double fundamentalHz = 13_000_000.0 / (64 * 512.0);
        double fundamentalMagnitude = SpectralMagnitude(signed, fundamentalHz);
        double resonantBodyMagnitude = SpectralMagnitude(signed, fundamentalHz * 12.0);
        Assert.True(
            resonantBodyMagnitude > fundamentalMagnitude * 20.0,
            $"Expected a resonant body above the fundamental. fundamental={fundamentalMagnitude:F3}, body={resonantBodyMagnitude:F3}");
    }

    [Fact]
    public void ChunkBoundariesDoNotChangeGeneratedMusic()
    {
        Mad2AudioState state = new(true, 53, 24);
        BuzzerPcmGenerator contiguousGenerator = new();
        BuzzerPcmGenerator chunkedGenerator = new();
        ushort[] contiguous = new ushort[4_096];
        ushort[] chunked = new ushort[contiguous.Length];

        contiguousGenerator.Update(state);
        chunkedGenerator.Update(state);
        contiguousGenerator.Render(contiguous);
        chunkedGenerator.Render(chunked.AsSpan(0, 1_137));
        chunkedGenerator.Render(chunked.AsSpan(1_137, 2_003));
        chunkedGenerator.Render(chunked.AsSpan(3_140));

        Assert.Equal(contiguous, chunked);
    }

    [Fact]
    public void SilenceResetsAcousticStateDeterministically()
    {
        Mad2AudioState state = new(true, 72, 31);
        BuzzerPcmGenerator generator = new();
        ushort[] firstAttack = new ushort[1_024];
        ushort[] secondAttack = new ushort[firstAttack.Length];

        generator.Update(state);
        generator.Render(firstAttack);
        generator.Update(Mad2AudioState.Off);
        generator.Update(state);
        generator.Render(secondAttack);

        Assert.Equal(firstAttack, secondAttack);
    }

    private static double SpectralMagnitude(short[] samples, double frequencyHz)
    {
        int start = BuzzerPcmGenerator.DefaultSampleRate / 10;
        double real = 0.0;
        double imaginary = 0.0;

        for (int i = start; i < samples.Length; i++)
        {
            double angle = 2.0 * Math.PI * frequencyHz * i / BuzzerPcmGenerator.DefaultSampleRate;
            double sample = samples[i] / 32_768.0;
            real += sample * Math.Cos(angle);
            imaginary -= sample * Math.Sin(angle);
        }

        return Math.Sqrt((real * real) + (imaginary * imaginary));
    }
}
