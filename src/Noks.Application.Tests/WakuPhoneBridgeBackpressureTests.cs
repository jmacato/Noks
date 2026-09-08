using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Noks.Application;
using Noks.Cryptography;
using Noks.Dct3.Radio;
using Noks.Waku;

namespace Noks.Application.Tests;

public sealed class WakuPhoneBridgeBackpressureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Burst_of_encrypted_sms_is_not_dropped_when_work_queue_is_full(bool fromStore)
    {
        await using var receiverProfile = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        byte[] receiverEntropy = NoksRecoveryPhrase.Decode(receiverProfile.Profile.CreateRecoveryPhrase());
        byte[] senderEntropy = NoksRecoveryPhrase.Decode(WakuProfile.CreateNew().CreateRecoveryPhrase());
        using var receiverKeys = WakuProfileKeys.Create(receiverEntropy);
        using var senderKeys = WakuProfileKeys.Create(senderEntropy);
        var now = DateTimeOffset.UtcNow;
        const string senderNumber = "1234567890123";
        await receiverProfile.UpsertContactAsync(
            WakuProfileContact.FromValidatedCard(ContactCardV2Codec.CreateSigned(senderKeys, "Sender", senderNumber, now, now.AddMinutes(2))),
            senderNumber);
        var messages = Enumerable.Range(0, 64).Select(i =>
        {
            var message = new WakuApplicationMessage(Guid.NewGuid(), WakuEventKind.Sms, now.ToUnixTimeMilliseconds(), now.AddMinutes(1).ToUnixTimeMilliseconds(), senderKeys.EnvelopePublicKey.Span, receiverKeys.MailboxPublicKey.Span, WakuSmsPayloadCodec.Encode($"burst-{i:D2}"));
            return new WakuTransportMessage(WakuTopicProfile.GetSendTopic(receiverKeys.MailboxPublicKey.Span, now), WakuEnvelopeCodec.Encrypt(message, senderKeys.EnvelopePrivateKey.Span), now.ToUnixTimeMilliseconds(), fromStore ? WakuMessageSource.Store : WakuMessageSource.LiveFilter);
        }).ToArray();
        var transport = new BurstTransport(messages, fromStore);
        await using var bridge = new WakuPhoneBridge(receiverProfile, transport, options: WakuPhoneBridgeOptions.Default with { MaximumQueuedWork = 4 });
        var received = new ConcurrentQueue<WakuPhoneCommand>();
        bridge.CommandAvailable += source => { while (source.TryDequeueCommand(out var command) && command is not null) received.Enqueue(command); };
        bridge.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (received.Count(x => x.Kind == WakuPhoneCommandKind.QueueIncomingSms) < 64) await Task.Delay(10, timeout.Token);
        var sms = received.Where(x => x.Kind == WakuPhoneCommandKind.QueueIncomingSms).ToArray();
        Assert.Equal(Enumerable.Range(0, 64).Select(i => $"burst-{i:D2}").Order(), sms.Select(x => x.Text).Order());
        Assert.All(sms, x => Assert.Equal(senderNumber, x.Address));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_cancels_a_transport_writer_waiting_for_queue_space(bool fromStore)
    {
        await using var profile = await WakuProfileManager.LoadOrCreateAsync(new MemoryStore());
        using var keys = WakuProfileKeys.Create(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;
        const string number = "1234567890123";
        await profile.UpsertContactAsync(WakuProfileContact.FromValidatedCard(
            ContactCardV2Codec.CreateSigned(keys, "Peer", number, now, now.AddMinutes(2))), number);
        var transport = new BlockedTransport(fromStore);
        await using var bridge = new WakuPhoneBridge(profile, transport,
            options: WakuPhoneBridgeOptions.Default with { MaximumQueuedWork = 1 });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bridge.Start();
        Assert.True(bridge.TryEnqueue(new OutgoingNetworkRequest(Guid.NewGuid(), NetworkRequestKind.Sms, number, "block publication")));
        await transport.PublishEntered.Task.WaitAsync(deadline.Token);
        transport.StartBurst.TrySetResult();
        await transport.SecondYield.Task.WaitAsync(deadline.Token);
        await Task.Delay(25, deadline.Token);
        // Publication holds the consumer. One message fills the queue; the second writer waits.
        Assert.Equal(2, transport.YieldCount);
        Assert.False(transport.BurstCompleted);
        await bridge.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
    }

    private sealed class BlockedTransport(bool fromStore) : IWakuTransport
    {
        public TaskCompletionSource PublishEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StartBurst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondYield { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int yields;
        private int completed;
        public int YieldCount => Volatile.Read(ref yields);
        public bool BurstCompleted => Volatile.Read(ref completed) != 0;
        public async ValueTask<WakuPublishResult> PublishAsync(WakuPublishRequest request, CancellationToken token = default)
        {
            PublishEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The blocked publish must end through cancellation.");
        }
        public IAsyncEnumerable<WakuTransportMessage> SubscribeAsync(IReadOnlyList<string> topics, CancellationToken token = default) => ReadBurstAsync(!fromStore, token);
        public IAsyncEnumerable<WakuTransportMessage> QueryStoreAsync(WakuStoreQuery query, CancellationToken token = default) => ReadBurstAsync(fromStore, token);
        private async IAsyncEnumerable<WakuTransportMessage> ReadBurstAsync(bool active, [EnumeratorCancellation] CancellationToken token)
        {
            if (!active) { await Task.Delay(Timeout.InfiniteTimeSpan, token); yield break; }
            await StartBurst.Task.WaitAsync(token);
            for (int index = 0; index < 16; index++)
            {
                token.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref yields) == 2) SecondYield.TrySetResult();
                yield return new WakuTransportMessage("synthetic", new byte[] { 1 }, 0,
                    fromStore ? WakuMessageSource.Store : WakuMessageSource.LiveFilter);
            }
            Volatile.Write(ref completed, 1);
        }
    }

    private sealed class BurstTransport(WakuTransportMessage[] messages, bool store) : IWakuTransport
    {
        public ValueTask<WakuPublishResult> PublishAsync(WakuPublishRequest request, CancellationToken token = default) => ValueTask.FromResult(new WakuPublishResult(1));
        public async IAsyncEnumerable<WakuTransportMessage> SubscribeAsync(IReadOnlyList<string> topics, [EnumeratorCancellation] CancellationToken token = default) { if (!store) foreach (var message in messages) { token.ThrowIfCancellationRequested(); yield return message; } await Task.CompletedTask; }
        public async IAsyncEnumerable<WakuTransportMessage> QueryStoreAsync(WakuStoreQuery query, [EnumeratorCancellation] CancellationToken token = default) { if (store) foreach (var message in messages) { token.ThrowIfCancellationRequested(); yield return message; } await Task.CompletedTask; }
    }
    private sealed class MemoryStore : IWakuProfileStore { private string? value; public ValueTask<string?> LoadAsync(CancellationToken token = default) => ValueTask.FromResult(value); public ValueTask SaveAsync(string text, CancellationToken token = default) { value = text; return ValueTask.CompletedTask; } }
}
