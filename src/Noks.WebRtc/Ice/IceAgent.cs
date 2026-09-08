using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Noks.WebRtc;

/// <summary>A small, regular ICE UDP agent.  Relay candidates are deliberately unsupported.</summary>
public sealed class IceAgent : IAsyncDisposable
{
    private const int MaxDatagram = 65535;
    private bool _controlling;
    private readonly IReadOnlyList<IPEndPoint> _stunServers;
    private readonly ulong _tieBreaker;
    private readonly Socket _socket;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _incoming = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropWrite, SingleWriter = true });
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Stun.Message>> _transactions = new();
    private readonly ConcurrentDictionary<string, bool> _transactionIntegrity = new();
    private readonly ConcurrentDictionary<string, IPEndPoint> _transactionEndpoints = new();
    private readonly Dictionary<string, DateTime> _authenticatedReceiveEndpoints = [];
    private readonly List<Candidate> _remote = [];
    private readonly List<string> _local = [];
    private readonly object _gate = new();
    private readonly TaskCompletionSource<IPEndPoint> _nominated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _receiveTask;
    private Task? _checkTask;
    private string? _remoteUfrag, _remotePassword;
    private IPEndPoint? _selected;
    private bool _gathered, _disposed;
    private DateTime _lastAuthenticated = DateTime.UtcNow;

    public IceAgent(bool controlling, IReadOnlyList<IPEndPoint>? stunServers = null)
    {
        _controlling = controlling;
        _stunServers = stunServers ?? [];
        _tieBreaker = BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8));
        LocalUfrag = RandomToken(8);
        LocalPassword = RandomToken(24);
        _socket = CreateSocket();
        _receiveTask = Task.Run(ReceiveLoopAsync);
    }

    public string LocalUfrag { get; }
    public string LocalPassword { get; }
    public IReadOnlyList<string> LocalCandidates { get { lock (_gate) return _local.ToArray(); } }
    public event Action<string>? CandidateDiscovered;
    public ChannelReader<byte[]> Incoming => _incoming.Reader;
    public Task Completion => _completion.Task;

    public async Task GatherAsync(CancellationToken token)
    {
        ThrowIfDisposed();
        lock (_gate) { if (_gathered) return; _gathered = true; }
        var port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        var addresses = NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up)
            .SelectMany(x => x.GetIPProperties().UnicastAddresses).Select(x => x.Address)
            .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 && !IPAddress.IsLoopback(a) && !a.IsIPv6LinkLocal)
            .Distinct().ToList();
        // Loopback is necessary for local tests and harmless as a host candidate.
        addresses.Add(IPAddress.Loopback); addresses.Add(IPAddress.IPv6Loopback);
        foreach (var address in addresses.Distinct()) AddLocal(new Candidate(address, port, "host", 126));
        var tasks = _stunServers.Select(server => GatherSrflxAsync(server, token));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public void SetRemoteCredentials(string ufrag, string password)
    {
        if (string.IsNullOrWhiteSpace(ufrag) || string.IsNullOrWhiteSpace(password)) throw new ArgumentException("ICE credentials are required.");
        lock (_gate) { _remoteUfrag = ufrag; _remotePassword = password; }
    }

    public async Task AddRemoteCandidateAsync(string candidate, CancellationToken token)
    {
        ThrowIfDisposed(); token.ThrowIfCancellationRequested();
        var fields = candidate.Trim().Replace("a=", "", StringComparison.Ordinal).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var typeAt = Array.FindIndex(fields, x => x == "typ");
        if (fields.Length >= 3 && (fields[2].ToLowerInvariant() != "udp" || typeAt >= 0 && typeAt + 1 < fields.Length && fields[typeAt + 1].Equals("relay", StringComparison.OrdinalIgnoreCase))) return;
        Candidate c;
        try { c = await Candidate.ParseAsync(candidate, token).ConfigureAwait(false); }
        catch (Exception error) when (fields.Length > 4 && fields[4].EndsWith(".local", StringComparison.OrdinalIgnoreCase) &&
            error is SocketException or TimeoutException)
        {
            // A browser can hide its host address behind mDNS. Authenticated incoming checks
            // still discover a peer-reflexive candidate when this host cannot resolve mDNS.
            return;
        }
        // Signaling commonly carries TCP/TURN candidates beside useful UDP candidates.  This direct-only
        // agent skips them so one unsupported candidate cannot abort trickle processing.
        if (c.Type == "relay" || c.Protocol != "udp") return;
        lock (_gate) { if (_remote.Count >= 128) return; if (!_remote.Any(x => x.EndPoint.Equals(c.EndPoint))) _remote.Add(c); }
    }

    public async Task<IPEndPoint> ConnectAsync(CancellationToken token)
    {
        ThrowIfDisposed();
        string? ufrag, pwd;
        lock (_gate) { ufrag = _remoteUfrag; pwd = _remotePassword; }
        if (ufrag is null || pwd is null) throw new InvalidOperationException("SetRemoteCredentials must be called before ConnectAsync.");
        lock (_gate) _checkTask ??= Task.Run(async () =>
        {
            try { await CheckLoopAsync(_stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) { _completion.TrySetException(ex); _incoming.Writer.TryComplete(ex); _nominated.TrySetException(ex); }
        }, _stop.Token);
        return await _nominated.Task.WaitAsync(token).ConfigureAwait(false);
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        ThrowIfDisposed();
        if (_completion.Task.IsFaulted) throw new IOException("ICE transport has failed.", _completion.Task.Exception);
        var peer = _selected ?? throw new InvalidOperationException("ICE has not nominated a peer.");
        return new ValueTask(_socket.SendToAsync(data, SocketFlags.None, peer, token).AsTask());
    }

    private async Task CheckLoopAsync(CancellationToken token)
    {
        var failures = 0;
        while (!token.IsCancellationRequested && !_nominated.Task.IsCompleted)
        {
            Candidate[] candidates; lock (_gate) candidates = _remote.ToArray();
            foreach (var c in candidates)
            {
                if (_nominated.Task.IsCompleted) break;
                try
                {
                    var response = await RequestAsync(c.EndPoint, useCandidate: _controlling, token).ConfigureAwait(false);
                    if (response is not null && _controlling) Nominate(c.EndPoint);
                }
                catch (OperationCanceledException) { throw; }
                catch { failures++; }
            }
            if (failures > 30 && candidates.Length > 0) throw new IOException("ICE connectivity checks failed.");
            await Task.Delay(150, token).ConfigureAwait(false);
        }
        if (_selected is not null) await ConsentLoopAsync(token).ConfigureAwait(false);
    }

    private async Task ConsentLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            if (_selected is null) return;
            try { await RequestAsync(_selected, false, token).ConfigureAwait(false); _lastAuthenticated = DateTime.UtcNow; }
            catch when (DateTime.UtcNow - _lastAuthenticated > TimeSpan.FromSeconds(20))
            { var error = new IOException("ICE consent expired."); _completion.TrySetException(error); _incoming.Writer.TryComplete(error); _stop.Cancel(); _socket.Dispose(); return; }
            catch { }
        }
    }

    private async Task<Stun.Message?> RequestAsync(IPEndPoint endpoint, bool useCandidate, CancellationToken token)
    {
        string ufrag, password; lock (_gate) { ufrag = _remoteUfrag!; password = _remotePassword!; }
        var tx = RandomNumberGenerator.GetBytes(12); var key = Convert.ToHexString(tx);
        var waiter = new TaskCompletionSource<Stun.Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        _transactions[key] = waiter; _transactionIntegrity[key] = true; _transactionEndpoints[key] = Normalize(endpoint);
        try
        {
            var packet = Stun.BuildRequest(tx, $"{ufrag}:{LocalUfrag}", password, _controlling, _tieBreaker, useCandidate);
            await _socket.SendToAsync(packet, SocketFlags.None, Normalize(endpoint), token).ConfigureAwait(false);
            return await waiter.Task.WaitAsync(TimeSpan.FromMilliseconds(900), token).ConfigureAwait(false);
        }
        finally { _transactions.TryRemove(key, out _); _transactionIntegrity.TryRemove(key, out _); _transactionEndpoints.TryRemove(key, out _); }
    }

    private async Task GatherSrflxAsync(IPEndPoint server, CancellationToken token)
    {
        try
        {
            var tx = RandomNumberGenerator.GetBytes(12); var key = Convert.ToHexString(tx);
            var waiter = new TaskCompletionSource<Stun.Message>(TaskCreationOptions.RunContinuationsAsynchronously); _transactions[key] = waiter; _transactionIntegrity[key] = false; _transactionEndpoints[key] = Normalize(server);
            try
            {
                await _socket.SendToAsync(Stun.BuildBareRequest(tx), SocketFlags.None, Normalize(server), token).ConfigureAwait(false);
                var response = await waiter.Task.WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                if (response.XorMapped is { } mapped) AddLocal(new Candidate(mapped.Address, mapped.Port, "srflx", 100));
            }
            finally { _transactions.TryRemove(key, out _); _transactionIntegrity.TryRemove(key, out _); _transactionEndpoints.TryRemove(key, out _); }
        }
        catch (OperationCanceledException) { throw; } catch { /* a STUN server is optional */ }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[MaxDatagram];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var any = _socket.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any;
                var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(any, 0), _stop.Token).ConfigureAwait(false);
                var bytes = buffer.AsSpan(0, result.ReceivedBytes).ToArray(); var remote = Normalize((IPEndPoint)result.RemoteEndPoint);
                if (Stun.TryParse(bytes, out var message)) { await HandleStunAsync(message!, remote).ConfigureAwait(false); continue; }
                if (_selected is not null && IsAuthenticatedReceiveEndpoint(remote)) _incoming.Writer.TryWrite(bytes);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _completion.TrySetException(ex); _incoming.Writer.TryComplete(ex); }
    }

    private async Task HandleStunAsync(Stun.Message message, IPEndPoint remote)
    {
        var key = Convert.ToHexString(message.TransactionId);
        if (message.IsSuccess && _transactions.TryGetValue(key, out var pending) && _transactionEndpoints.TryGetValue(key, out var expected) && EndPointEquals(expected, remote))
        {
            if (_transactionIntegrity.TryGetValue(key, out var required) && required)
            {
                string? password; lock (_gate) password = _remotePassword;
                if (password is null || !Stun.VerifyIntegrity(message, password)) return;
                AuthorizeReceiveEndpoint(remote);
                if (_selected is not null && EndPointEquals(_selected, remote)) _lastAuthenticated = DateTime.UtcNow;
            }
            pending.TrySetResult(message); return;
        }
        if (message.Type == 0x0111 && _transactions.TryGetValue(key, out var conflict) && _transactionEndpoints.TryGetValue(key, out var conflictPeer) && EndPointEquals(conflictPeer, remote))
        {
            string? password; lock (_gate) password = _remotePassword;
            if (password is null || !Stun.VerifyIntegrity(message, password)) return;
            if (message.ErrorCode == 487) { _controlling = !_controlling; conflict.TrySetException(new IOException("ICE role conflict; retrying with the resolved role.")); }
            else conflict.TrySetException(new IOException($"STUN error {message.ErrorCode}."));
            return;
        }
        if (!message.IsRequest) return;
        string? remotePassword; lock (_gate) remotePassword = _remotePassword;
        // A valid connectivity request names our ufrag first and is authenticated with our password.
        if (remotePassword is null || message.Username != LocalUfrag + ":" + _remoteUfrag || !Stun.VerifyIntegrity(message, LocalPassword)) return;
        AuthorizeReceiveEndpoint(remote);
        if (message.RoleControlling.HasValue && message.RoleControlling.Value == _controlling)
        {
            var remoteTie = message.TieBreaker;
            if ((_controlling && _tieBreaker >= remoteTie) || (!_controlling && _tieBreaker < remoteTie))
            { await SendStunAsync(Stun.BuildError(message.TransactionId, 487, LocalPassword), remote).ConfigureAwait(false); return; }
            // Switch role only for this negotiation; tie breaker protects both peers from remaining equal.
            _controlling = ! _controlling;
        }
        lock (_gate) if (_remote.Count < 128 && !_remote.Any(x => EndPointEquals(x.EndPoint, remote))) _remote.Add(new Candidate(remote.Address, remote.Port, "prflx", 110));
        await SendStunAsync(Stun.BuildSuccess(message.TransactionId, remote, LocalPassword), remote).ConfigureAwait(false);
        if (!_controlling && message.UseCandidate) Nominate(remote);
    }

    private Task SendStunAsync(byte[] data, IPEndPoint endpoint) => _socket.SendToAsync(data, SocketFlags.None, endpoint, _stop.Token).AsTask();
    private void Nominate(IPEndPoint peer) { if (_selected is not null) return; _selected = Normalize(peer); AuthorizeReceiveEndpoint(_selected); _nominated.TrySetResult(_selected); }
    private void AuthorizeReceiveEndpoint(IPEndPoint endpoint)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow; var expired = _authenticatedReceiveEndpoints.Where(x => now - x.Value > TimeSpan.FromSeconds(30)).Select(x => x.Key).ToArray();
            foreach (var stale in expired) _authenticatedReceiveEndpoints.Remove(stale);
            var key = EndpointKey(endpoint); if (_authenticatedReceiveEndpoints.Count < 128 || _authenticatedReceiveEndpoints.ContainsKey(key)) _authenticatedReceiveEndpoints[key] = now;
        }
    }
    private bool IsAuthenticatedReceiveEndpoint(IPEndPoint endpoint)
    {
        if (_selected is not null && EndPointEquals(_selected, endpoint)) return true;
        lock (_gate)
        {
            var key = EndpointKey(endpoint); return _authenticatedReceiveEndpoints.TryGetValue(key, out var at) && DateTime.UtcNow - at <= TimeSpan.FromSeconds(30);
        }
    }
    private static string EndpointKey(IPEndPoint endpoint) => $"{endpoint.Address.MapToIPv6()}:{endpoint.Port}";
    private void AddLocal(Candidate c) { var line = c.ToSdp(); lock (_gate) { if (_local.Contains(line)) return; _local.Add(line); } CandidateDiscovered?.Invoke(line); }
    private static Socket CreateSocket() { try { var s = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true }; s.Bind(new IPEndPoint(IPAddress.IPv6Any, 0)); return s; } catch { var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); s.Bind(new IPEndPoint(IPAddress.Any, 0)); return s; } }
    private IPEndPoint Normalize(IPEndPoint ep) => _socket.AddressFamily == AddressFamily.InterNetworkV6 && ep.Address.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(ep.Address.MapToIPv6(), ep.Port) : ep;
    private static bool EndPointEquals(IPEndPoint a, IPEndPoint b) => a.Port == b.Port && a.Address.Equals(b.Address) || (a.Port == b.Port && a.Address.MapToIPv6().Equals(b.Address.MapToIPv6()));
    private static string RandomToken(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=');
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(IceAgent)); }
    public async ValueTask DisposeAsync() { if (_disposed) return; _disposed = true; _stop.Cancel(); _socket.Dispose(); try { await _receiveTask.ConfigureAwait(false); if (_checkTask is not null) await _checkTask.ConfigureAwait(false); } catch { } _incoming.Writer.TryComplete(); _completion.TrySetCanceled(); _stop.Dispose(); }

    private sealed record Candidate(IPAddress Address, int Port, string Type, int TypePreference, string Protocol = "udp")
    {
        public IPEndPoint EndPoint => new(Address, Port);
        public string ToSdp() => $"candidate:1 1 UDP {TypePreference * 16777216 + 65535} {Address} {Port} typ {Type}";
        public static async Task<Candidate> ParseAsync(string text, CancellationToken token)
        {
            var p = text.Trim().Replace("a=", "", StringComparison.Ordinal).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 8 || !p[0].StartsWith("candidate:", StringComparison.OrdinalIgnoreCase) || p[1] != "1" || !int.TryParse(p[5], out var port) || port is < 1 or > 65535) throw new FormatException("Invalid ICE candidate.");
            var typeIndex = Array.FindIndex(p, x => x == "typ"); if (typeIndex < 0 || typeIndex + 1 >= p.Length) throw new FormatException("ICE candidate type is missing.");
            IPAddress address;
            if (!IPAddress.TryParse(p[4], out address!)) { var all = await Dns.GetHostAddressesAsync(p[4], token).WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false); address = all.FirstOrDefault(x => x.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) ?? throw new SocketException(); }
            if (!uint.TryParse(p[3], out var priority)) throw new FormatException("Invalid ICE candidate priority.");
            return new Candidate(address, port, p[typeIndex + 1].ToLowerInvariant(), (int)(priority / 16777216), p[2].ToLowerInvariant());
        }
    }
}
