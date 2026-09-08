using Noks.AvaloniaApp.Audio;
using Noks.Dct3.Audio;
using Noks.Dct3.Radio;

namespace Noks.Application.Tests;

public sealed class DesktopPhoneAudioTests
{
    [Fact]
    public void OutputUsesSharedGeneratorAndBecomesSilentWhenMuted()
    {
        FakeStream device = new();
        using DesktopPhoneAudio audio = new(device.Open);
        Dct3AudioState tone = new(new Mad2AudioState(true, 80, 8), DspToneState.Off);
        Dct3AudioPcmGenerator expected = new();
        expected.Update(tone);
        audio.Update(tone);

        short[] samples = new short[256];
        ushort[] expectedSamples = new ushort[256];
        for (int i = 0; i < 3; i++)
        {
            device.Render!(samples);
            expected.Render(expectedSamples);
            Assert.Equal(expectedSamples.Select(value => unchecked((short)value)), samples);
        }
        Assert.Contains(samples, sample => sample != 0);

        audio.Update(Dct3AudioState.Off);
        device.Render!(samples);
        Assert.All(samples, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void DisposeAllowsFinalDeviceCallbackAndClosesDeviceOnce()
    {
        FakeStream device = new();
        DesktopPhoneAudio audio = new(device.Open);
        audio.Update(new Dct3AudioState(new Mad2AudioState(true, 80, 8), DspToneState.Off));
        device.OnDispose = () =>
        {
            short[] samples = Enumerable.Repeat((short)123, 256).ToArray();
            // A different thread models a native callback concurrent with device shutdown.
            Task finalCallback = Task.Run(() => device.Render!(samples));
            Assert.True(finalCallback.Wait(TimeSpan.FromSeconds(2)));
            Assert.All(samples, sample => Assert.Equal(0, sample));
        };

        audio.Dispose();
        audio.Dispose();

        Assert.Equal(1, device.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => audio.Update(Dct3AudioState.Off));
    }

    [Fact]
    public void FailedDeviceShutdownCanBeRetried()
    {
        FakeStream device = new();
        DesktopPhoneAudio audio = new(device.Open);
        device.OnDispose = () => throw new IOException("Device shutdown failed.");
        Assert.Throws<IOException>(audio.Dispose);
        Assert.Throws<ObjectDisposedException>(() => audio.Update(Dct3AudioState.Off));

        device.OnDispose = null;
        audio.Dispose();
        audio.Dispose();

        Assert.Equal(2, device.DisposeCount);
    }

    [Fact]
    public void DeviceFailureReachesTheApplication()
    {
        FakeStream device = new();
        using DesktopPhoneAudio audio = new(device.Open);
        device.Failure = new IOException("Device disconnected.");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => audio.Update(Dct3AudioState.Off));

        Assert.Same(device.Failure, error.InnerException);
    }

    private sealed class FakeStream : IDesktopPcmStream
    {
        public Action<short[]>? Render { get; private set; }
        public Exception? Failure { get; set; }
        public Action? OnDispose { get; set; }
        public int DisposeCount { get; private set; }

        public IDesktopPcmStream Open(int sampleRate, Action<short[]> render)
        {
            Assert.Equal(Dct3AudioPcmGenerator.DefaultSampleRate, sampleRate);
            Render = render;
            return this;
        }

        public void Dispose()
        {
            DisposeCount++;
            OnDispose?.Invoke();
        }
    }
}
