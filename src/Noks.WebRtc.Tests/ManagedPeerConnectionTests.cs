using System.Threading.Channels;

namespace Noks.WebRtc.Tests;

public sealed class ManagedPeerConnectionTests
{
    [Fact]
    public async Task PeersExchangeSyntheticAudioOverUdpAndStop()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        await using ManagedPeerConnection alice = new(true);
        await using ManagedPeerConnection bob = new(false);
        Channel<WebRtcSignal> toAlice = Channel.CreateUnbounded<WebRtcSignal>();
        Channel<WebRtcSignal> toBob = Channel.CreateUnbounded<WebRtcSignal>();
        alice.Signal += signal => toBob.Writer.TryWrite(signal);
        bob.Signal += signal => toAlice.Writer.TryWrite(signal);
        await bob.StartAsync(timeout.Token);
        await alice.StartAsync(timeout.Token);
        Task Relay(ChannelReader<WebRtcSignal> source, ManagedPeerConnection target) => Task.Run(async () =>
        {
            try
            {
                await foreach (WebRtcSignal signal in source.ReadAllAsync(timeout.Token))
                    await target.ApplySignalAsync(signal, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        });
        Task relayAlice = Relay(toAlice.Reader, alice), relayBob = Relay(toBob.Reader, bob);
        Task bothConnected = Task.WhenAll(alice.WaitForConnectedAsync(timeout.Token), bob.WaitForConnectedAsync(timeout.Token));
        await await Task.WhenAny(bothConnected, relayAlice, relayBob);
        await bothConnected;
        long alicePlayout = 0, bobPlayout = 0;
        alice.AudioReceived += frame => Interlocked.Add(ref alicePlayout, frame.Span.ToArray().Count(sample => sample != 0));
        bob.AudioReceived += frame => Interlocked.Add(ref bobPlayout, frame.Span.ToArray().Count(sample => sample != 0));
        for (int i = 0; i < 80; i++)
        {
            await alice.SendAudioAsync(Tone(i, 440), timeout.Token);
            await bob.SendAudioAsync(Tone(i, 660), timeout.Token);
            await Task.Delay(20, timeout.Token);
        }
        Assert.True(alice.Statistics.ReceivedPackets >= 60, $"Alice {alice.Statistics}; Bob {bob.Statistics}; failure={alice.Failure}");
        Assert.True(bob.Statistics.ReceivedPackets >= 60, $"Bob {bob.Statistics}; failure={bob.Failure}");
        Assert.True(alicePlayout > 8000);
        Assert.True(bobPlayout > 8000);
        Assert.True(alice.Statistics.ReceivedControlPackets > 0);
        Assert.True(bob.Statistics.ReceivedControlPackets > 0);
        await alice.DisposeAsync();
        await bob.DisposeAsync();
        long finalAlice = alicePlayout, finalBob = bobPlayout;
        await Task.Delay(60, timeout.Token);
        Assert.Equal(finalAlice, alicePlayout);
        Assert.Equal(finalBob, bobPlayout);
        timeout.Cancel();
        await Task.WhenAll(relayAlice, relayBob);
    }

    internal static short[] Tone(int frame, double frequency) => Enumerable.Range(0, 160)
        .Select(i => (short)(6000 * Math.Sin(2 * Math.PI * frequency * (frame * 160 + i) / 8000))).ToArray();

    [Theory]
    [InlineData(0, 255)]
    [InlineData(32124, 128)]
    [InlineData(-32124, 0)]
    [InlineData(8, 254)]
    public void PcmuKnownValues(short pcm, byte expected)
    {
        Assert.Equal(expected, PcmuCodec.Encode(pcm));
        Assert.Equal(pcm, PcmuCodec.Decode(expected));
    }
}
