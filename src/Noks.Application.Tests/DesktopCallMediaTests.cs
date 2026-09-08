using Noks.AvaloniaApp.Audio;
using Noks.Application;
using Noks.WebRtc;

namespace Noks.Application.Tests;

public sealed class DesktopCallMediaTests
{
    [Fact]
    public async Task Two_real_peers_exchange_audio_and_replacement_disposes_devices()
    {
        var leftMic = new List<FakeMicrophone>(); var rightMic = new List<FakeMicrophone>();
        var leftOut = new List<FakeOutput>(); var rightOut = new List<FakeOutput>();
        DesktopCallMedia? left = null, right = null;
        left = Create(mediaEvent => _ = right!.ApplyAsync(mediaEvent.AttemptId, ToSignal(mediaEvent.Kind), mediaEvent.Payload.AsMemory()), leftMic, leftOut);
        right = Create(mediaEvent => _ = left!.ApplyAsync(mediaEvent.AttemptId, ToSignal(mediaEvent.Kind), mediaEvent.Payload.AsMemory()), rightMic, rightOut);
        await using (left) await using (right)
        {
            Guid id = Guid.NewGuid();
            await right.BeginAsync(id, false); await left.BeginAsync(id, true);
            Assert.Empty(leftMic); Assert.Empty(rightMic);
            await left.ActivateAsync(id); await right.ActivateAsync(id);
            await Task.Delay(3000);
            Assert.Single(leftMic); Assert.Single(rightMic);
            leftMic[0].Push(Enumerable.Range(0, 320).Select(i => (short)(i + 1)).ToArray());
            await Task.Delay(500);
            short[] rendered = rightOut[0].Render(256);
            Assert.Contains(rendered, x => x != 0);
            await left.BeginAsync(Guid.NewGuid(), true);
            Assert.True(leftMic[0].Disposed); Assert.True(leftOut[0].Disposed);
        }
    }

    private sealed class FakeMicrophone(Action<ReadOnlyMemory<short>> callback) : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Push(short[] data) => callback(data);
        public void Dispose() => Disposed = true;
    }
    private static DesktopCallMedia Create(Action<WakuCallMediaEvent> emit, List<FakeMicrophone> microphones, List<FakeOutput> outputs) => new(emit, caller => new ManagedPeerConnection(caller, []), callback => { var x = new FakeMicrophone(callback); microphones.Add(x); return x; }, callback => { var x = new FakeOutput(callback); outputs.Add(x); return x; });
    private static Noks.Waku.WakuEventKind ToSignal(WakuCallMediaEventKind kind) => kind switch { WakuCallMediaEventKind.SdpOffer => Noks.Waku.WakuEventKind.SdpOffer, WakuCallMediaEventKind.SdpAnswer => Noks.Waku.WakuEventKind.SdpAnswer, _ => Noks.Waku.WakuEventKind.IceCandidate };
    private sealed class FakeOutput(Action<short[]> callback) : IDisposable
    {
        public bool Disposed { get; private set; }
        public short[] Render(int count) { var result = new short[count]; callback(result); return result; }
        public void Dispose() => Disposed = true;
    }
}
