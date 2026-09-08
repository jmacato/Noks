using System.Net;
using System.Net.Sockets;

namespace Noks.WebRtc.Tests;

public sealed class IceAgentTests
{
    [Fact]
    public async Task Two_local_agents_nominate_and_only_exchange_selected_media()
    {
        await using var left = new IceAgent(true);
        await using var right = new IceAgent(false);
        await left.GatherAsync(CancellationToken.None);
        await right.GatherAsync(CancellationToken.None);
        left.SetRemoteCredentials(right.LocalUfrag, right.LocalPassword);
        right.SetRemoteCredentials(left.LocalUfrag, left.LocalPassword);
        foreach (var c in right.LocalCandidates.Where(x => x.Contains("::1", StringComparison.Ordinal))) await left.AddRemoteCandidateAsync(c, CancellationToken.None);
        foreach (var c in left.LocalCandidates.Where(x => x.Contains("::1", StringComparison.Ordinal))) await right.AddRemoteCandidateAsync(c, CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var peers = await Task.WhenAll(left.ConnectAsync(deadline.Token), right.ConnectAsync(deadline.Token));
        Assert.Equal(AddressFamily.InterNetworkV6, peers[0].AddressFamily);
        await left.SendAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
        var bytes = await right.Incoming.ReadAsync(deadline.Token);
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    }

    [Fact]
    public async Task Relay_candidates_are_ignored_by_the_direct_only_agent()
    {
        await using var agent = new IceAgent(true);
        await agent.AddRemoteCandidateAsync("candidate:1 1 UDP 1 127.0.0.1 9 typ relay", CancellationToken.None);
    }

    [Fact]
    public async Task Connect_observes_cancellation_before_a_candidate_arrives()
    {
        await using var agent = new IceAgent(true);
        agent.SetRemoteCredentials("remote", "password");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.ConnectAsync(cancel.Token));
    }

    [Fact]
    public async Task Equal_initial_roles_resolve_the_authenticated_role_conflict()
    {
        await using var first = new IceAgent(true);
        await using var second = new IceAgent(true);
        await first.GatherAsync(CancellationToken.None); await second.GatherAsync(CancellationToken.None);
        first.SetRemoteCredentials(second.LocalUfrag, second.LocalPassword); second.SetRemoteCredentials(first.LocalUfrag, first.LocalPassword);
        foreach (var c in second.LocalCandidates.Where(x => x.Contains("127.0.0.1", StringComparison.Ordinal))) await first.AddRemoteCandidateAsync(c, CancellationToken.None);
        foreach (var c in first.LocalCandidates.Where(x => x.Contains("127.0.0.1", StringComparison.Ordinal))) await second.AddRemoteCandidateAsync(c, CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await Task.WhenAll(first.ConnectAsync(deadline.Token), second.ConnectAsync(deadline.Token));
    }

    [Fact]
    public async Task Authenticated_second_source_can_send_media_but_unvalidated_sources_cannot()
    {
        await using var left = new IceAgent(true); await using var right = new IceAgent(false);
        await left.GatherAsync(CancellationToken.None); await right.GatherAsync(CancellationToken.None);
        left.SetRemoteCredentials(right.LocalUfrag, right.LocalPassword); right.SetRemoteCredentials(left.LocalUfrag, left.LocalPassword);
        foreach (var c in right.LocalCandidates.Where(x => x.Contains("::1", StringComparison.Ordinal))) await left.AddRemoteCandidateAsync(c, CancellationToken.None);
        foreach (var c in left.LocalCandidates.Where(x => x.Contains("::1", StringComparison.Ordinal))) await right.AddRemoteCandidateAsync(c, CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Task.WhenAll(left.ConnectAsync(deadline.Token), right.ConnectAsync(deadline.Token));
        int port = int.Parse(right.LocalCandidates.First(x => x.Contains(" ::1 ", StringComparison.Ordinal)).Split(' ')[5]);
        var target = new IPEndPoint(IPAddress.IPv6Loopback, port);
        using var bad = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp); bad.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        var wrongUser = Stun.BuildRequest(Enumerable.Repeat((byte)3, 12).ToArray(), $"wrong:{left.LocalUfrag}", right.LocalPassword, true, 1, false);
        await bad.SendToAsync(wrongUser, SocketFlags.None, target, deadline.Token);
        var tampered = Stun.BuildRequest(Enumerable.Repeat((byte)4, 12).ToArray(), $"{right.LocalUfrag}:{left.LocalUfrag}", right.LocalPassword, true, 1, false);
        tampered[^1] ^= 1;
        await bad.SendToAsync(tampered, SocketFlags.None, target, deadline.Token);
        await bad.SendToAsync(new byte[] { 9 }, SocketFlags.None, target, deadline.Token);
        using (var shortWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await right.Incoming.ReadAsync(shortWait.Token));

        using var alternate = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp); alternate.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        var tx = Enumerable.Range(0, 12).Select(i => (byte)(i + 20)).ToArray();
        var check = Stun.BuildRequest(tx, $"{right.LocalUfrag}:{left.LocalUfrag}", right.LocalPassword, true, 1, false);
        await alternate.SendToAsync(check, SocketFlags.None, target, deadline.Token);
        var replyBuffer = new byte[256];
        await alternate.ReceiveFromAsync(replyBuffer, SocketFlags.None, new IPEndPoint(IPAddress.IPv6Any, 0), deadline.Token);
        await alternate.SendToAsync(new byte[] { 7, 8, 9 }, SocketFlags.None, target, deadline.Token);
        Assert.Equal(new byte[] { 7, 8, 9 }, await right.Incoming.ReadAsync(deadline.Token));
    }
}
