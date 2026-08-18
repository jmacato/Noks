#:project ../src/Noks.Avalonia/Noks.Avalonia.csproj
#:project ../src/Noks.Application/Noks.Application.csproj

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Noks.Application;
using Noks.AvaloniaApp;
using Noks.Dct3;
using Noks.Waku;

string firmwarePath = args.Length > 0
    ? args[0]
    : Path.Combine("3310", "My 3310 NR2 v.4.18.en.fls");
if (!File.Exists(firmwarePath))
    throw new FileNotFoundException("Pass the stock Nokia 3310 v4.18 flash image.", firmwarePath);

const string destination = "1234567890123";
List<ScheduledPhoneKeyChange> keys = [];
AddTap(keys, 160_000_000, PhoneKey.Main);
AddTap(keys, 165_000_000, PhoneKey.Right);
AddTap(keys, 170_000_000, PhoneKey.Main);
AddTap(keys, 180_000_000, PhoneKey.Main);
AddTap(keys, 190_000_000, PhoneKey.Digit2);
AddTap(keys, 205_000_000, PhoneKey.Main);
AddTap(keys, 215_000_000, PhoneKey.Main);
for (int index = 0; index < destination.Length; index++)
{
    PhoneKey digit = (PhoneKey)((int)PhoneKey.Digit0 + destination[index] - '0');
    AddTap(keys, 225_000_000 + index * 5_000_000L, digit);
}
AddTap(keys, 295_000_000, PhoneKey.Main);

await using WakuProfileManager profile =
    await WakuProfileManager.LoadOrCreateAsync(new MemoryProfileStore());
await using WakuPhoneBridge bridge = new(
    profile,
    new AcceptingOfflineTransport(),
    options: WakuPhoneBridgeOptions.Default with
    {
        EnablePostQuantumRendezvous = true,
        RequirePostQuantumRendezvous = true,
        PostQuantumMinimumWorkBits = 1,
    });
using PhoneEmulator phone = new(firmwarePath, scheduledKeyChanges: keys);
ConcurrentQueue<EmulationLogEntry> trace = new();
TaskCompletionSource<OutgoingNetworkRequest> submitted =
    new(TaskCreationOptions.RunContinuationsAsynchronously);
TaskCompletionSource<bool> handsetCompleted =
    new(TaskCreationOptions.RunContinuationsAsynchronously);
int resolveCount = 0;
NetworkRequestDecision? decision = null;

phone.SetLoggingEnabled(true);
phone.LogAvailable += source =>
{
    while (source.TryDequeueLog(out EmulationLogEntry? entry) && entry is not null)
    {
        trace.Enqueue(entry);
        if (entry.Text.Contains("DSP SMS CP-ACK received", StringComparison.Ordinal))
            handsetCompleted.TrySetResult(true);
    }
};
phone.NetworkRequestAvailable += source =>
{
    while (source.TryDequeueOutgoingNetworkRequest(out OutgoingNetworkRequest? request) &&
           request is not null)
    {
        submitted.TrySetResult(request);
        if (!bridge.TryEnqueue(request))
        {
            source.ResolveNetworkRequest(new ResolveNetworkRequest(
                request.RequestId,
                NetworkRequestDecision.Reject));
        }
    }
};
bridge.CommandAvailable += source =>
{
    while (source.TryDequeueCommand(out WakuPhoneCommand? command) && command is not null)
    {
        if (command.Kind != WakuPhoneCommandKind.ResolveNetworkRequest)
            continue;
        Interlocked.Increment(ref resolveCount);
        decision = command.Decision;
        phone.ResolveNetworkRequest(new ResolveNetworkRequest(command.RequestId, command.Decision));
    }
};

bridge.Start();
phone.Start();
using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
OutgoingNetworkRequest outgoing;
try
{
    outgoing = await submitted.Task.WaitAsync(timeout.Token);
    await handsetCompleted.Task.WaitAsync(timeout.Token);
    await Task.Delay(100, timeout.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine(
        $"TIMEOUT steps={phone.ExecutedSteps:N0} cycles={phone.Cycles:N0} " +
        $"status=\"{phone.Status}\" held=\"{phone.Telemetry.HeldInputKeys}\"");
    foreach (EmulationLogEntry entry in trace.TakeLast(100))
        Console.Error.WriteLine(entry.DisplayText);
    throw;
}

if (outgoing.Kind != NetworkRequestKind.Sms ||
    outgoing.NormalizedDestination != destination ||
    outgoing.SmsText != "A")
{
    throw new InvalidOperationException(
        $"Unexpected firmware request: {outgoing.Kind} {outgoing.NormalizedDestination} \"{outgoing.SmsText}\"");
}
if (decision != NetworkRequestDecision.Accept || resolveCount != 1)
    throw new InvalidOperationException($"Expected one Accept decision, got {resolveCount} {decision}.");

Console.WriteLine(
    $"PASS firmware SMS submit -> PQC deferred outbox -> handset RP-ACK: " +
    $"destination={outgoing.NormalizedDestination} text=\"{outgoing.SmsText}\" " +
    $"steps={phone.ExecutedSteps:N0} decision={decision}");
foreach (EmulationLogEntry entry in trace.Where(entry =>
             entry.Text.Contains("SMS", StringComparison.Ordinal) ||
             entry.Text.Contains("RP-ACK", StringComparison.Ordinal) ||
             entry.Text.Contains("channel release", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine(entry.DisplayText);
}

static void AddTap(
    List<ScheduledPhoneKeyChange> keys,
    long step,
    PhoneKey key,
    long hold = 1_000_000)
{
    keys.Add(new ScheduledPhoneKeyChange(step, key, Pressed: true));
    keys.Add(new ScheduledPhoneKeyChange(step + hold, key, Pressed: false));
}

sealed class MemoryProfileStore : IWakuProfileStore
{
    private string? value;

    public ValueTask<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(value);
    }

    public ValueTask SaveAsync(string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.value = value;
        return ValueTask.CompletedTask;
    }
}

sealed class AcceptingOfflineTransport : IWakuTransport
{
    public ValueTask<WakuPublishResult> PublishAsync(
        WakuPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new WakuPublishResult(1));
    }

    public async IAsyncEnumerable<WakuTransportMessage> SubscribeAsync(
        IReadOnlyList<string> contentTopics,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    public async IAsyncEnumerable<WakuTransportMessage> QueryStoreAsync(
        WakuStoreQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }
}
