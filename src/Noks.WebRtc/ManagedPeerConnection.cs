using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace Noks.WebRtc;

/// <summary>A single bidirectional PCMU track over authenticated ICE and DTLS-SRTP.</summary>
public sealed class ManagedPeerConnection : IAsyncDisposable
{
    private readonly bool caller;
    private readonly DtlsIdentity identity = new();
    private readonly IceAgent ice;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim signaling = new(1, 1);
    private readonly SemaphoreSlim sending = new(1, 1);
    private readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<byte[]> dtlsPackets = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = true });
    private readonly object jitterLock = new();
    private readonly Dictionary<ushort, short[]> jitter = [];
    private readonly uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4));
    private ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(RandomNumberGenerator.GetBytes(2));
    private uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4));
    private ushort? nextPlayout;
    private uint? remoteSsrc;
    private int payloadType;
    private int jitterWarmup;
    private bool? dtlsClient;
    private string mid = "0";
    private long version;
    private SessionDescription? remote;
    private DtlsSession? dtls;
    private SrtpContext? outbound, inbound;
    private Task? connectionTask, receiveTask, playoutTask, controlTask;
    private Task? disposal;
    private readonly object disposeLock = new();
    private bool started, descriptionPublished;
    private int state;
    private long sentPackets, receivedPackets, receivedSamples, receivedEnergy, receivedControl;

    public ManagedPeerConnection(bool isCaller, IReadOnlyList<IPEndPoint>? stunServers = null)
    {
        caller = isCaller;
        ice = new IceAgent(isCaller, stunServers);
        ice.CandidateDiscovered += candidate =>
        {
            if (descriptionPublished)
                Emit(WebRtcSignalKind.Candidate, candidate);
        };
    }

    public event Action<WebRtcSignal>? Signal;
    public event Action<WebRtcState>? StateChanged;
    /// <summary>Receives paced 20ms mono frames at 8000Hz. The callback must return promptly.</summary>
    public event Action<ReadOnlyMemory<short>>? AudioReceived;
    public WebRtcState State => (WebRtcState)Volatile.Read(ref state);
    public Exception? Failure { get; private set; }
    public string LocalFingerprint => identity.Fingerprint;
    public WebRtcStatistics Statistics => new(Interlocked.Read(ref sentPackets), Interlocked.Read(ref receivedPackets),
        Interlocked.Read(ref receivedSamples), Interlocked.Read(ref receivedEnergy), Interlocked.Read(ref receivedControl));

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await signaling.WaitAsync(cancellationToken);
        try
        {
            if (started) throw new InvalidOperationException("The peer is already started.");
            started = true;
            SetState(WebRtcState.Connecting);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            await ice.GatherAsync(linked.Token);
            receiveTask = ReceiveAsync();
            if (caller) PublishDescription("offer", "actpass");
        }
        catch (Exception error) { Fail(error); throw; }
        finally { signaling.Release(); }
    }

    public async Task ApplySignalAsync(WebRtcSignal signal, CancellationToken cancellationToken = default)
    {
        if (signal.Json.Length is 0 or > 262144) throw new FormatException("Invalid call signal size.");
        await signaling.WaitAsync(cancellationToken);
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            if (!started) throw new InvalidOperationException("Start the peer before applying signals.");
            using JsonDocument document = JsonDocument.Parse(signal.Json);
            JsonElement root = document.RootElement;
            if (signal.Kind == WebRtcSignalKind.Candidate)
            {
                if (root.ValueKind == JsonValueKind.Array)
                {
                    if (root.GetArrayLength() > 128) throw new FormatException("Too many ICE candidates.");
                    foreach (JsonElement candidate in root.EnumerateArray()) await ApplyCandidateAsync(candidate, cancellationToken);
                }
                else await ApplyCandidateAsync(root, cancellationToken);
                return;
            }
            string type = root.GetProperty("type").GetString() ?? "";
            if (type != (signal.Kind == WebRtcSignalKind.Offer ? "offer" : "answer"))
                throw new FormatException("The SDP type does not match its signal.");
            SessionDescription description = SessionDescription.Parse(root.GetProperty("sdp").GetString() ?? "");
            if (type == "answer" && (!caller || remote is not null))
                throw new InvalidOperationException("An SDP answer was not expected.");
            if (type == "answer" && description.Setup == "actpass")
                throw new FormatException("An SDP answer must select a DTLS role.");
            if (remote is not null && (description.Ufrag != remote.Ufrag || description.Password != remote.Password ||
                !string.Equals(description.Fingerprint, remote.Fingerprint, StringComparison.OrdinalIgnoreCase) ||
                description.PayloadType != payloadType || description.Mid != mid))
                throw new NotSupportedException("ICE restarts and transport changes require a new call.");
            remote = description;
            mid = description.Mid;
            payloadType = description.PayloadType;
            ice.SetRemoteCredentials(description.Ufrag, description.Password);
            foreach (string candidate in description.Candidates)
                await ice.AddRemoteCandidateAsync(candidate, cancellationToken);
            bool isClient = dtlsClient ?? description.Setup != "active";
            if (dtlsClient is not null && description.Setup != "actpass" &&
                (description.Setup == "passive") != isClient)
                throw new NotSupportedException("The DTLS role cannot change during a call.");
            dtlsClient = isClient;
            if (type == "offer") PublishDescription("answer", isClient ? "active" : "passive",
                description.Direction switch { "recvonly" => "sendonly", "sendonly" => "recvonly", "inactive" => "inactive", _ => "sendrecv" });
            connectionTask ??= ConnectAsync(isClient, description.Fingerprint);
        }
        catch (Exception error) { Fail(error); throw; }
        finally { signaling.Release(); }
    }

    public Task WaitForConnectedAsync(CancellationToken cancellationToken = default) => connected.Task.WaitAsync(cancellationToken);

    public async ValueTask SendAudioAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
    {
        if (samples.Length != 160) throw new ArgumentException("A PCMU frame requires 160 samples at 8000Hz.", nameof(samples));
        await sending.WaitAsync(cancellationToken);
        try
        {
            if (State != WebRtcState.Connected || outbound is null) throw new InvalidOperationException("The peer is not connected.");
            byte[] packet = new byte[172];
            packet[0] = 0x80;
            packet[1] = (byte)payloadType;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence++);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), ssrc);
            timestamp += 160;
            for (int i = 0; i < samples.Length; i++) packet[12 + i] = PcmuCodec.Encode(samples.Span[i]);
            await ice.SendAsync(outbound.ProtectRtp(packet), lifetime.Token);
            Interlocked.Increment(ref sentPackets);
        }
        finally { sending.Release(); }
    }

    private async Task ApplyCandidateAsync(JsonElement value, CancellationToken token)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException("Invalid ICE candidate.");
        if (value.TryGetProperty("sdpMLineIndex", out JsonElement index) && index.ValueKind == JsonValueKind.Number && index.GetInt32() != 0)
            throw new FormatException("Unknown ICE media section.");
        string candidate = value.GetProperty("candidate").GetString() ?? "";
        if (candidate.Length > 4096) throw new FormatException("Invalid ICE candidate size.");
        if (candidate.Length != 0) await ice.AddRemoteCandidateAsync(candidate, token);
    }

    private void PublishDescription(string type, string setup, string direction = "sendrecv")
    {
        string sdp = SessionDescription.Create(ice.LocalUfrag, ice.LocalPassword, identity.Fingerprint, mid, setup,
            payloadType, ssrc, ice.LocalCandidates, ++version, direction);
        Emit(type == "offer" ? WebRtcSignalKind.Offer : WebRtcSignalKind.Answer, sdp);
        descriptionPublished = true;
    }

    private void Emit(WebRtcSignalKind kind, string value)
    {
        // JsonDocument/Utf8JsonWriter keep signaling independent of reflection metadata and native code.
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            if (kind == WebRtcSignalKind.Candidate)
            {
                writer.WriteString("candidate", value);
                writer.WriteString("sdpMid", mid);
                writer.WriteNumber("sdpMLineIndex", 0);
            }
            else
            {
                writer.WriteString("type", kind == WebRtcSignalKind.Offer ? "offer" : "answer");
                writer.WriteString("sdp", value);
            }
            writer.WriteEndObject();
        }
        if (!lifetime.IsCancellationRequested)
            Signal?.Invoke(new(kind, System.Text.Encoding.UTF8.GetString(buffer.ToArray())));
    }

    private async Task ConnectAsync(bool isClient, string fingerprint)
    {
        try
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            await ice.ConnectAsync(deadline.Token);
            dtls = await DtlsSession.HandshakeAsync(identity, isClient, fingerprint, dtlsPackets.Reader, ice.SendAsync, deadline.Token);
            outbound = new SrtpContext(isClient ? dtls.Keys.ClientKey : dtls.Keys.ServerKey, isClient ? dtls.Keys.ClientSalt : dtls.Keys.ServerSalt);
            inbound = new SrtpContext(isClient ? dtls.Keys.ServerKey : dtls.Keys.ClientKey, isClient ? dtls.Keys.ServerSalt : dtls.Keys.ClientSalt);
            SetState(WebRtcState.Connected);
            connected.TrySetResult();
            playoutTask = PlayoutAsync();
            controlTask = ControlAsync();
            await await Task.WhenAny(ice.Completion, dtls.Completion).WaitAsync(lifetime.Token);
            if (!lifetime.IsCancellationRequested) throw new IOException("The ICE transport ended.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Fail(error); }
    }

    private async Task ReceiveAsync()
    {
        try
        {
            await foreach (byte[] packet in ice.Incoming.ReadAllAsync(lifetime.Token))
            {
                if (packet.Length == 0) continue;
                if (packet[0] is >= 20 and <= 63) { dtlsPackets.Writer.TryWrite(packet); continue; }
                SrtpContext? context = inbound;
                if (context is null || packet.Length < 12 || (packet[0] >> 6) != 2) continue;
                if (packet[1] is >= 192 and <= 223)
                {
                    if (context.TryUnprotectRtcp(packet, out byte[] control) && RtcpReports.TryParse(control, out _))
                        Interlocked.Increment(ref receivedControl);
                    continue;
                }
                if (!context.TryUnprotectRtp(packet, out byte[] plaintext) || (plaintext[1] & 127) != payloadType) continue;
                int header = 12 + (plaintext[0] & 15) * 4;
                if (header > plaintext.Length) continue;
                if ((plaintext[0] & 16) != 0)
                {
                    if (header + 4 > plaintext.Length) continue;
                    header += 4 + BinaryPrimitives.ReadUInt16BigEndian(plaintext.AsSpan(header + 2)) * 4;
                }
                int size = plaintext.Length - header;
                if ((plaintext[0] & 32) != 0) size -= plaintext[^1];
                if (size is <= 0 or > 960 || size % 160 != 0 || header < 12 || header + size > plaintext.Length) continue;
                uint packetSsrc = BinaryPrimitives.ReadUInt32BigEndian(plaintext.AsSpan(8));
                ushort seq = BinaryPrimitives.ReadUInt16BigEndian(plaintext.AsSpan(2));
                short[] decoded = new short[size];
                long energy = 0;
                for (int i = 0; i < size; i++) { decoded[i] = PcmuCodec.Decode(plaintext[header + i]); energy += Math.Abs((int)decoded[i]); }
                lock (jitterLock)
                {
                    if (remoteSsrc is not null && remoteSsrc != packetSsrc) continue;
                    remoteSsrc = packetSsrc;
                    nextPlayout ??= seq;
                    int distance = (short)(seq - nextPlayout.Value);
                    if (distance < 0) continue;
                    if (distance > 50) { jitter.Clear(); nextPlayout = seq; jitterWarmup = 0; }
                    if (jitter.Count < 50) jitter.TryAdd(seq, decoded);
                }
                Interlocked.Increment(ref receivedPackets);
                Interlocked.Add(ref receivedSamples, size);
                Interlocked.Add(ref receivedEnergy, energy);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Fail(error); }
        finally { dtlsPackets.Writer.TryComplete(); }
    }

    private async Task PlayoutAsync()
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(20));
            Queue<short[]> frames = new();
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                short[]? output = null;
                lock (jitterLock)
                {
                    if (nextPlayout is null || jitterWarmup++ < 2) continue;
                    if (frames.Count == 0)
                    {
                        ushort seq = nextPlayout.Value;
                        // Pause the sequence clock on underrun. Device and network clocks can drift;
                        // advancing through an empty queue would discard all later audio.
                        if (jitter.Count != 0) nextPlayout = unchecked((ushort)(seq + 1));
                        if (jitter.Remove(seq, out short[]? decoded))
                            for (int i = 0; i < decoded.Length; i += 160) frames.Enqueue(decoded.AsSpan(i, 160).ToArray());
                        else frames.Enqueue(new short[160]);
                    }
                    output = frames.Dequeue();
                }
                AudioReceived?.Invoke(output);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Fail(error); }
    }

    private async Task ControlAsync()
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                await sending.WaitAsync(lifetime.Token);
                try
                {
                    uint count = (uint)Interlocked.Read(ref sentPackets);
                    byte[] report = RtcpReports.CreateSenderReport(ssrc, timestamp, count, count * 160, DateTimeOffset.UtcNow, "noks");
                    await ice.SendAsync(outbound!.ProtectRtcp(report), lifetime.Token);
                }
                finally { sending.Release(); }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Fail(error); }
    }

    private void SetState(WebRtcState value)
    {
        Volatile.Write(ref state, (int)value);
        StateChanged?.Invoke(value);
    }

    private void Fail(Exception error)
    {
        if (lifetime.IsCancellationRequested) return;
        Failure = error;
        connected.TrySetException(error);
        lifetime.Cancel();
        SetState(WebRtcState.Failed);
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeLock) return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        lifetime.Cancel();
        connected.TrySetCanceled();
        dtlsPackets.Writer.TryComplete();
        await ice.DisposeAsync();
        foreach (Task? task in new[] { connectionTask, receiveTask, playoutTask, controlTask })
            if (task is not null) try { await task; } catch (OperationCanceledException) { }
        await sending.WaitAsync();
        try { dtls?.Dispose(); outbound?.Dispose(); inbound?.Dispose(); identity.Dispose(); }
        finally { sending.Release(); }
        SetState(WebRtcState.Closed);
    }
}
