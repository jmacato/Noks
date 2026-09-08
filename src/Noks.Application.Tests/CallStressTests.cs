using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Noks.Application;
using Noks.AvaloniaApp.Audio;
using Noks.Cryptography;
using Noks.Dct3.Radio;
using Noks.Waku;
using Noks.WebRtc;

namespace Noks.Application.Tests;

public sealed class CallStressTests
{
    [Fact]
    public async Task Thirty_alternating_calls_exchange_real_media_and_release_every_session()
    {
        await using StressFixture fixture = await StressFixture.CreateAsync();
        List<TimeSpan> latencies = [];

        for (int index = 0; index != 30; index++)
        {
            Endpoint caller = index % 2 == 0 ? fixture.Left : fixture.Right;
            Endpoint callee = index % 2 == 0 ? fixture.Right : fixture.Left;
            Guid attemptId = Guid.NewGuid();
            DateTimeOffset started = DateTimeOffset.UtcNow;

            Assert.True(caller.Bridge.TryEnqueue(new OutgoingNetworkRequest(
                attemptId, NetworkRequestKind.Call, callee.Number, "")));
            await callee.WaitForIncomingAsync(attemptId);
            Assert.True(callee.Bridge.TryEnqueue(new CallTransition(
                attemptId, CallDirection.Incoming, CallTransitionKind.Answer, caller.Number)));
            await caller.WaitForConnectedAsync(attemptId);
            await callee.WaitForConnectedAsync(attemptId);
            await caller.WaitForCaptureAsync();
            await callee.WaitForCaptureAsync();
            latencies.Add(DateTimeOffset.UtcNow - started);

            await fixture.AssertLateCallbacksAreIsolatedAsync();
            await fixture.ExchangeAudioAsync(caller, callee, index + 1);
            await fixture.ExchangeAudioAsync(callee, caller, index + 101);

            Assert.True(caller.Bridge.TryEnqueue(new CallTransition(
                attemptId, CallDirection.Outgoing, CallTransitionKind.Hangup, callee.Number)));
            await caller.WaitForEndedAsync(attemptId);
            await callee.WaitForEndedAsync(attemptId);
            await fixture.WaitForNoDevicesAsync();
        }

        Assert.Equal(30, fixture.Left.CompletedCalls);
        Assert.Equal(30, fixture.Right.CompletedCalls);
        Assert.Equal(0, fixture.Left.ActiveDevices);
        Assert.Equal(0, fixture.Right.ActiveDevices);
        Assert.All(latencies, value => Assert.True(value < TimeSpan.FromSeconds(15), $"Call setup took {value}."));
        Assert.True(fixture.Left.CommandCallbacks > 0);
        Assert.True(fixture.Right.CommandCallbacks > 0);
        Assert.Equal(0, fixture.Left.CommandQueueDrops);
        Assert.Equal(0, fixture.Right.CommandQueueDrops);
        Console.WriteLine($"CALL_STRESS_SETUP_MS {string.Join(',', latencies.Select(value => value.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)))}");
    }

    [Fact]
    public async Task Overlap_early_hangup_and_media_failure_do_not_block_next_real_call()
    {
        await using StressFixture fixture = await StressFixture.CreateAsync();

        Guid first = Guid.NewGuid();
        Assert.True(fixture.Left.Bridge.TryEnqueue(new OutgoingNetworkRequest(
            first, NetworkRequestKind.Call, fixture.Right.Number, "")));
        await fixture.Right.WaitForIncomingAsync(first);
        Guid overlapping = Guid.NewGuid();
        Assert.True(fixture.Right.Bridge.TryEnqueue(new OutgoingNetworkRequest(
            overlapping, NetworkRequestKind.Call, fixture.Left.Number, "")));
        Assert.Equal(NetworkRequestDecision.Reject, await fixture.Right.WaitForResolutionAsync(overlapping));
        Assert.True(fixture.Left.Bridge.TryEnqueue(new CallTransition(
            first, CallDirection.Outgoing, CallTransitionKind.Hangup, fixture.Right.Number)));
        await fixture.Left.WaitForEndedAsync(first);
        await fixture.Right.WaitForEndedAsync(first);
        await fixture.WaitForNoDevicesAsync();

        Guid failed = Guid.NewGuid();
        Assert.True(fixture.Right.Bridge.TryEnqueue(new OutgoingNetworkRequest(
            failed, NetworkRequestKind.Call, fixture.Left.Number, "")));
        await fixture.Left.WaitForIncomingAsync(failed);
        fixture.Hub.FailNextPublish();
        Assert.True(fixture.Left.Bridge.TryEnqueue(new CallTransition(
            failed, CallDirection.Incoming, CallTransitionKind.Answer, fixture.Right.Number)));
        await fixture.Left.WaitForEndedAsync(failed);
        await fixture.Right.WaitForEndedAsync(failed);
        await fixture.WaitForNoDevicesAsync();

        await fixture.RunConnectedCallAsync(fixture.Left, fixture.Right, sendSms: true);
        Assert.True(fixture.Hub.FailedPublishes >= 1);
        Assert.Equal(0, fixture.Left.CommandQueueDrops);
        Assert.Equal(0, fixture.Right.CommandQueueDrops);
    }

    private sealed class StressFixture : IAsyncDisposable
    {
        private StressFixture(FaultableWakuHub hub, Endpoint left, Endpoint right)
        {
            Hub = hub; Left = left; Right = right;
        }
        public FaultableWakuHub Hub { get; }
        public Endpoint Left { get; }
        public Endpoint Right { get; }

        public static async Task<StressFixture> CreateAsync()
        {
            FaultableWakuHub hub = new();
            Endpoint left = await Endpoint.CreateAsync(hub);
            Endpoint right = await Endpoint.CreateAsync(hub);
            left.SetRemote(right.Number); right.SetRemote(left.Number);
            await left.AddDurableContactAsync(right);
            await right.AddDurableContactAsync(left);
            left.Bridge.Start(); right.Bridge.Start();
            await WaitUntilAsync(() => hub.SubscriptionCount == 2);
            return new StressFixture(hub, left, right);
        }

        public async Task RunConnectedCallAsync(Endpoint caller, Endpoint callee, bool sendSms = false)
        {
            Guid id = Guid.NewGuid();
            Assert.True(caller.Bridge.TryEnqueue(new OutgoingNetworkRequest(id, NetworkRequestKind.Call, callee.Number, "")));
            await callee.WaitForIncomingAsync(id);
            Assert.True(callee.Bridge.TryEnqueue(new CallTransition(id, CallDirection.Incoming, CallTransitionKind.Answer, caller.Number)));
            await caller.WaitForConnectedAsync(id); await callee.WaitForConnectedAsync(id);
            await caller.WaitForCaptureAsync(); await callee.WaitForCaptureAsync();
            await AssertLateCallbacksAreIsolatedAsync();
            await ExchangeAudioAsync(caller, callee, 777);
            await ExchangeAudioAsync(callee, caller, 888);
            if (sendSms)
            {
                const string text = "SMS while managed DTLS media is connected.";
                Assert.True(caller.Bridge.TryEnqueue(new OutgoingNetworkRequest(
                    Guid.NewGuid(), NetworkRequestKind.Sms, callee.Number, text)));
                await callee.WaitForSmsAsync(caller.Number, text);
            }
            Assert.True(caller.Bridge.TryEnqueue(new CallTransition(id, CallDirection.Outgoing, CallTransitionKind.Hangup, callee.Number)));
            await caller.WaitForEndedAsync(id); await callee.WaitForEndedAsync(id);
            await WaitForNoDevicesAsync();
        }

        public async Task WaitForNoDevicesAsync() =>
            await WaitUntilAsync(() => Left.ActiveDevices == 0 && Right.ActiveDevices == 0);

        public async Task ExchangeAudioAsync(Endpoint sender, Endpoint receiver, int marker)
        {
            short[] source = CreateUniquePattern(marker);
            short[] expected = source.Select(sample => PcmuCodec.Decode(PcmuCodec.Encode(sample))).ToArray();
            receiver.ResetActiveOutput();
            sender.Push(source);
            await receiver.WaitForRenderedPatternAsync(expected);
        }

        public async Task AssertLateCallbacksAreIsolatedAsync()
        {
            Left.InvokeDisposedCallbacks();
            Right.InvokeDisposedCallbacks();
            var until = System.Diagnostics.Stopwatch.StartNew();
            while (until.Elapsed < TimeSpan.FromMilliseconds(150))
            {
                Left.AssertActiveOutputIsSilent();
                Right.AssertActiveOutputIsSilent();
                await Task.Delay(10);
            }
        }

        private static short[] CreateUniquePattern(int marker)
        {
            // Encode the marker in signs so PCMU quantization cannot merge adjacent markers.
            return Enumerable.Range(0, 160)
                .Select(index => (short)((marker & (1 << (index / 8 % 16))) == 0 ? 4_000 : -4_000))
                .ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await Left.DisposeAsync(); await Right.DisposeAsync();
        }
    }

    private sealed class Endpoint : IAsyncDisposable
    {
        private readonly WakuProfileManager profile;
        private readonly DesktopCallMedia media;
        private readonly Channel<WakuPhoneCommand> commands = Channel.CreateBounded<WakuPhoneCommand>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        private readonly Channel<Guid> incoming = Channel.CreateUnbounded<Guid>();
        private readonly HashSet<Guid> connected = [];
        private readonly Dictionary<Guid, NetworkRequestDecision> resolutions = [];
        private readonly object connectedSync = new();
        private readonly object resolutionSync = new();
        private readonly Channel<Guid> ended = Channel.CreateUnbounded<Guid>();
        private readonly Channel<WakuPhoneCommand> sms = Channel.CreateUnbounded<WakuPhoneCommand>();
        private readonly List<FakeMicrophone> microphones = [];
        private readonly List<FakeOutput> outputs = [];
        private readonly object devicesSync = new();
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task worker;
        private string remote = "";
        private int commandCallbacks;
        private int commandQueueDrops;
        private int completedCalls;

        private Endpoint(WakuProfileManager profile, WakuPhoneBridge bridge)
        {
            this.profile = profile; Bridge = bridge;
            media = new DesktopCallMedia(OnMediaEvent, caller => new ManagedPeerConnection(caller, []), AddMicrophone, AddOutput);
            Bridge.CommandAvailable += DrainCommands;
            worker = RunCommandsAsync();
        }
        public WakuPhoneBridge Bridge { get; }
        public string Number => profile.Profile.PhoneNumber;
        public int ActiveDevices
        {
            get { lock (devicesSync) return microphones.Count(value => !value.Disposed) + outputs.Count(value => !value.Disposed); }
        }
        public int CommandCallbacks => Volatile.Read(ref commandCallbacks);
        public int CommandQueueDrops => Volatile.Read(ref commandQueueDrops);
        public int CompletedCalls => Volatile.Read(ref completedCalls);

        public static async Task<Endpoint> CreateAsync(FaultableWakuHub hub)
        {
            WakuProfileManager profile = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
            return new Endpoint(profile, new WakuPhoneBridge(profile, hub.CreateTransport()));
        }
        public async Task AddDurableContactAsync(Endpoint remote)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            byte[] entropy = NoksRecoveryPhrase.Decode(remote.profile.Profile.CreateRecoveryPhrase());
            using WakuProfileKeys keys = WakuProfileKeys.Create(entropy);
            WakuProfileContact contact = WakuProfileContact.FromValidatedCard(ContactCardV2Codec.CreateSigned(
                keys, remote.profile.Profile.UserName, remote.Number, now, now.AddMinutes(2)));
            await profile.UpsertContactAsync(contact, remote.Number);
        }
        public void SetRemote(string value) => remote = value;
        public void Push(short[] pcm)
        {
            FakeMicrophone microphone;
            lock (devicesSync)
                microphone = Assert.Single(microphones, value => !value.Disposed);
            microphone.Push(pcm);
        }
        public async Task WaitForIncomingAsync(Guid id) => await WaitForAsync(incoming.Reader, id);
        public async Task WaitForConnectedAsync(Guid id) =>
            await WaitUntilAsync(() => { lock (connectedSync) return connected.Contains(id); });
        public async Task WaitForCaptureAsync() =>
            await WaitUntilAsync(() => { lock (devicesSync) return microphones.Count(value => !value.Disposed) == 1; });
        public async Task<NetworkRequestDecision> WaitForResolutionAsync(Guid id)
        {
            await WaitUntilAsync(() => { lock (resolutionSync) return resolutions.ContainsKey(id); });
            lock (resolutionSync)
                return resolutions[id];
        }
        public async Task WaitForEndedAsync(Guid id)
        {
            await WaitForAsync(ended.Reader, id);
            Interlocked.Increment(ref completedCalls);
        }
        public async Task WaitForSmsAsync(string address, string text)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            WakuPhoneCommand value = await sms.Reader.ReadAsync(timeout.Token);
            Assert.Equal(address, value.Address);
            Assert.Equal(text, value.Text);
        }
        public void ResetActiveOutput()
        {
            lock (devicesSync)
                Assert.Single(outputs, value => !value.Disposed).ResetObservation();
        }
        public async Task WaitForRenderedPatternAsync(short[] expected)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            while (true)
            {
                FakeOutput output;
                lock (devicesSync)
                    output = Assert.Single(outputs, value => !value.Disposed);
                output.Render();
                if (output.ContainsSubsequence(expected))
                    return;
                await Task.Delay(20, timeout.Token);
            }
        }
        public void InvokeDisposedCallbacks()
        {
            FakeMicrophone[] oldMicrophones;
            lock (devicesSync)
                oldMicrophones = microphones.Where(value => value.Disposed).ToArray();
            foreach (FakeMicrophone microphone in oldMicrophones)
                microphone.Push(Enumerable.Repeat((short)12_000, 160).ToArray());
        }
        public void AssertActiveOutputIsSilent()
        {
            FakeOutput activeOutput;
            lock (devicesSync)
                activeOutput = Assert.Single(outputs, value => !value.Disposed);
            Assert.All(activeOutput.Render(), sample => Assert.Equal((short)0, sample));
        }

        private void OnMediaEvent(WakuCallMediaEvent value)
        {
            if (value.Kind == WakuCallMediaEventKind.Connected)
            {
                lock (connectedSync)
                    connected.Add(value.AttemptId);
            }
            Bridge.TryEnqueue(value);
        }
        private IDisposable AddMicrophone(Action<ReadOnlyMemory<short>> callback)
        {
            FakeMicrophone result = new(callback);
            lock (devicesSync) microphones.Add(result);
            return result;
        }
        private IDisposable AddOutput(Action<short[]> callback)
        {
            FakeOutput result = new(callback);
            lock (devicesSync) outputs.Add(result);
            return result;
        }
        private void DrainCommands(WakuPhoneBridge source)
        {
            Interlocked.Increment(ref commandCallbacks);
            while (source.TryDequeueCommand(out WakuPhoneCommand? command) && command is not null)
            {
                if (!commands.Writer.TryWrite(command))
                    Interlocked.Increment(ref commandQueueDrops);
            }
        }
        private async Task RunCommandsAsync()
        {
            try
            {
                await foreach (WakuPhoneCommand command in commands.Reader.ReadAllAsync(cancellation.Token))
                {
                    switch (command.Kind)
                    {
                        case WakuPhoneCommandKind.ResolveNetworkRequest:
                            lock (resolutionSync)
                                resolutions[command.RequestId] = command.Decision;
                            break;
                        case WakuPhoneCommandKind.QueueIncomingSms:
                            sms.Writer.TryWrite(command); break;
                        case WakuPhoneCommandKind.BeginCallMedia:
                            await media.BeginAsync(command.RequestId, command.IsCaller); break;
                        case WakuPhoneCommandKind.ApplyCallMediaSignal:
                            await media.ApplyAsync(command.RequestId, command.EventKind, command.Payload.AsMemory()); break;
                        case WakuPhoneCommandKind.ActivateCallMedia:
                            await media.ActivateAsync(command.RequestId); break;
                        case WakuPhoneCommandKind.EndCallMedia:
                            await media.EndAsync(command.RequestId); ended.Writer.TryWrite(command.RequestId); break;
                        case WakuPhoneCommandKind.QueueIncomingCall:
                            incoming.Writer.TryWrite(command.RequestId); break;
                        case WakuPhoneCommandKind.ConnectNetworkCall:
                            Bridge.TryEnqueue(new CallTransition(command.RequestId, CallDirection.Outgoing, CallTransitionKind.Connect, remote)); break;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }
        public async ValueTask DisposeAsync()
        {
            Bridge.CommandAvailable -= DrainCommands;
            cancellation.Cancel(); commands.Writer.TryComplete();
            try { await worker; } catch (OperationCanceledException) { }
            await media.DisposeAsync(); await Bridge.DisposeAsync(); await profile.DisposeAsync(); cancellation.Dispose();
        }
        private static async Task WaitForAsync(ChannelReader<Guid> reader, Guid expected)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            while (true)
                if (await reader.ReadAsync(timeout.Token) == expected) return;
        }
    }

    private sealed class FakeMicrophone(Action<ReadOnlyMemory<short>> callback) : IDisposable
    {
        private readonly object sync = new();
        private bool disposed;
        public bool Disposed { get { lock (sync) return disposed; } }
        public void Push(short[] samples) => callback(samples);
        public void Dispose() { lock (sync) disposed = true; }
    }
    private sealed class FakeOutput(Action<short[]> callback) : IDisposable
    {
        private readonly object sync = new();
        private bool disposed;
        public bool Disposed { get { lock (sync) return disposed; } }
        private readonly List<short> observed = [];
        public void Dispose() { lock (sync) disposed = true; }
        public short[] Render()
        {
            short[] buffer = new short[256];
            callback(buffer);
            observed.AddRange(buffer);
            return buffer;
        }
        public void ResetObservation() => observed.Clear();
        public bool ContainsSubsequence(ReadOnlySpan<short> expected)
        {
            if (expected.Length == 0 || observed.Count < expected.Length)
                return false;
            for (int offset = 0; offset <= observed.Count - expected.Length; offset++)
            {
                int index = 0;
                while (index < expected.Length && observed[offset + index] == expected[index])
                    index++;
                if (index == expected.Length)
                    return true;
            }
            return false;
        }
    }

    private sealed class MemoryStore : IWakuProfileStore
    {
        private string? value;
        public ValueTask<string?> LoadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(value);
        public ValueTask SaveAsync(string next, CancellationToken cancellationToken = default) { value = next; return ValueTask.CompletedTask; }
    }

    private sealed class FaultableWakuHub
    {
        private readonly object sync = new();
        private readonly List<Transport> transports = [];
        private readonly List<WakuTransportMessage> store = [];
        private int subscriptions;
        private int failNextPublish;
        private int failedPublishes;
        public int SubscriptionCount => Volatile.Read(ref subscriptions);
        public int FailedPublishes => Volatile.Read(ref failedPublishes);
        public IWakuTransport CreateTransport() { Transport transport = new(this); lock (sync) transports.Add(transport); return transport; }
        public void FailNextPublish() => Interlocked.Exchange(ref failNextPublish, 1);
        private WakuPublishResult Publish(WakuPublishRequest request)
        {
            if (Interlocked.Exchange(ref failNextPublish, 0) != 0) { Interlocked.Increment(ref failedPublishes); throw new IOException("Injected Waku publish failure."); }
            WakuTransportMessage message = new(request.ContentTopic, request.Payload, request.TimestampUnixMilliseconds, WakuMessageSource.LiveFilter);
            Transport[] targets;
            lock (sync) { if (!request.Ephemeral) store.Add(message with { Source = WakuMessageSource.Store }); targets = transports.Where(value => value.Accepts(request.ContentTopic)).ToArray(); }
            foreach (Transport target in targets) target.Deliver(message);
            return new WakuPublishResult(1);
        }
        private WakuTransportMessage[] Query(WakuStoreQuery query) { lock (sync) return store.Where(message => query.ContentTopics.Contains(message.ContentTopic, StringComparer.Ordinal) && message.TimestampUnixMilliseconds >= query.StartUnixMilliseconds && message.TimestampUnixMilliseconds <= query.EndUnixMilliseconds).ToArray(); }
        private sealed class Transport(FaultableWakuHub hub) : IWakuTransport
        {
            private readonly Channel<WakuTransportMessage> inbox = Channel.CreateUnbounded<WakuTransportMessage>();
            private HashSet<string> topics = new(StringComparer.Ordinal);
            public ValueTask<WakuPublishResult> PublishAsync(WakuPublishRequest request, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(hub.Publish(request)); }
            public async IAsyncEnumerable<WakuTransportMessage> SubscribeAsync(IReadOnlyList<string> contentTopics, [EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.Yield(); topics = contentTopics.ToHashSet(StringComparer.Ordinal); Interlocked.Increment(ref hub.subscriptions); await foreach (WakuTransportMessage message in inbox.Reader.ReadAllAsync(cancellationToken)) yield return message; }
            public async IAsyncEnumerable<WakuTransportMessage> QueryStoreAsync(WakuStoreQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default) { foreach (WakuTransportMessage message in hub.Query(query)) { cancellationToken.ThrowIfCancellationRequested(); yield return message; await Task.Yield(); } }
            public bool Accepts(string topic) => topics.Contains(topic);
            public void Deliver(WakuTransportMessage message) => inbox.Writer.TryWrite(message);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }
}
