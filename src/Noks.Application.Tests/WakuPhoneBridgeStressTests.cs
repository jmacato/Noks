using Noks.Dct3.Radio;
using System.Collections.Concurrent;
using Noks.Cryptography;
using Noks.Waku;

namespace Noks.Application.Tests;

public sealed partial class WakuPhoneBridgeTests
{
    [Fact]
    public async Task PqcStress_reordered_duplicate_batches_deliver_two_hundred_exact_messages()
    {
        var hub = new InMemoryWakuHub();
        await using var aliceProfile = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        await using var bobProfile = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        var now = DateTimeOffset.UtcNow;
        await aliceProfile.UpsertContactAsync(CreatePqcContact(bobProfile, now), bobProfile.Profile.PhoneNumber);
        await bobProfile.UpsertContactAsync(CreatePqcContact(aliceProfile, now), aliceProfile.Profile.PhoneNumber);
        var aliceTransport = new BufferedPqcTransport(hub.CreateTransport());
        var bobTransport = new BufferedPqcTransport(hub.CreateTransport());
        var options = WakuPhoneBridgeOptions.Default with { EnablePostQuantumRendezvous = true, RequirePostQuantumRendezvous = true, PostQuantumMinimumWorkBits = 1 };
        await using var alice = new WakuPhoneBridge(aliceProfile, aliceTransport, options: options);
        await using var bob = new WakuPhoneBridge(bobProfile, bobTransport, options: options);
        var a = new BridgeHarness(alice); var b = new BridgeHarness(bob);
        var actualAlice = new List<string>(); var actualBob = new List<string>();
        var expectedAlice = new List<string>(); var expectedBob = new List<string>();
        alice.Start(); bob.Start();
        await WaitUntilAsync(() => hub.SubscriptionCount == 2);
        for (int batch = 0; batch < 20; batch++)
        {
            bool left = batch % 2 == 0;
            var sender = left ? alice : bob;
            var sendHarness = left ? a : b;
            var receiveHarness = left ? b : a;
            var transport = left ? aliceTransport : bobTransport;
            var expected = left ? expectedBob : expectedAlice;
            var actual = left ? actualBob : actualAlice;
            transport.Hold = true;
            for (int index = 0; index < 10; index++)
            {
                string text = $"PQC-{batch:D2}-{index:D2} café 日本語";
                expected.Add(text);
                Guid id = Guid.NewGuid();
                Assert.True(sender.TryEnqueue(new OutgoingNetworkRequest(id, NetworkRequestKind.Sms,
                    left ? bobProfile.Profile.PhoneNumber : aliceProfile.Profile.PhoneNumber, text)));
                var decision = await sendHarness.WaitForAsync(command => command.Kind == WakuPhoneCommandKind.ResolveNetworkRequest && command.RequestId == id);
                Assert.Equal(NetworkRequestDecision.Accept, decision.Decision);
            }
            Assert.False(receiveHarness.TryTake(command => command.Kind == WakuPhoneCommandKind.QueueIncomingSms, out _));
            await transport.FlushReversedWithDuplicatesAsync();
            for (int index = 0; index < 10; index++)
            {
                var received = await receiveHarness.WaitForAsync(command => command.Kind == WakuPhoneCommandKind.QueueIncomingSms);
                actual.Add(received.Text);
                Assert.Equal(left ? aliceProfile.Profile.PhoneNumber : bobProfile.Profile.PhoneNumber, received.Address);
            }
        }
        await Task.Delay(100);
        while (a.TryTake(command => command.Kind == WakuPhoneCommandKind.QueueIncomingSms, out var extra)) actualAlice.Add(extra!.Text);
        while (b.TryTake(command => command.Kind == WakuPhoneCommandKind.QueueIncomingSms, out var extra)) actualBob.Add(extra!.Text);
        Assert.Equal(expectedAlice.Order(), actualAlice.Order());
        Assert.Equal(expectedBob.Order(), actualBob.Order());
        Assert.Equal(200, actualAlice.Count + actualBob.Count);
        Assert.DoesNotContain(hub.PublishedRequests, request => request.Payload.Span.StartsWith("NWE1"u8));
        Assert.True(aliceTransport.FlushedPackets + bobTransport.FlushedPackets >= 400);
        Console.WriteLine($"PQC stress: 200 exact SMS; {aliceTransport.FlushedPackets + bobTransport.FlushedPackets} reordered/duplicated encrypted packets, including contact updates.");
    }

    [Fact]
    public async Task PqcStress_ten_fresh_pairings_and_ten_offline_store_recoveries()
    {
        for (int iteration = 0; iteration < 10; iteration++)
        {
            await PqcAsyncRendezvousPairsThenDeliversOverPqcDirectEnvelope();
            await PqcRendezvousCompletesFromStoreWhenPeerWasOfflineAtSendTime();
        }
    }

    private sealed class BufferedPqcTransport(IWakuTransport inner) : IWakuTransport
    {
        private readonly ConcurrentQueue<WakuPublishRequest> pending = new();
        public bool Hold { get; set; }
        public int FlushedPackets { get; private set; }
        public ValueTask<WakuPublishResult> PublishAsync(WakuPublishRequest request, CancellationToken cancellationToken = default)
        {
            if (Hold && request.Payload.Span.StartsWith("NQP2"u8))
            {
                pending.Enqueue(request with { Payload = request.Payload.ToArray() });
                return ValueTask.FromResult(new WakuPublishResult(1));
            }
            return inner.PublishAsync(request, cancellationToken);
        }
        public async Task FlushReversedWithDuplicatesAsync()
        {
            Hold = false;
            var batch = new List<WakuPublishRequest>();
            while (pending.TryDequeue(out var request)) batch.Add(request);
            // Contact-sync offers use the same encrypted envelope as SMS.
            Assert.True(batch.Count >= 10);
            foreach (var request in batch.AsEnumerable().Reverse())
            {
                await inner.PublishAsync(request); await inner.PublishAsync(request);
                FlushedPackets += 2;
            }
        }
        public IAsyncEnumerable<WakuTransportMessage> SubscribeAsync(IReadOnlyList<string> topics, CancellationToken cancellationToken = default) => inner.SubscribeAsync(topics, cancellationToken);
        public IAsyncEnumerable<WakuTransportMessage> QueryStoreAsync(WakuStoreQuery query, CancellationToken cancellationToken = default) => inner.QueryStoreAsync(query, cancellationToken);
    }
}
