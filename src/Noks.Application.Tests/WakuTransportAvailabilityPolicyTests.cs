using Noks.AvaloniaApp;
using Noks.Waku;
using Noks.AvaloniaApp.Messaging;

namespace Noks.Application.Tests;

public sealed class WakuTransportAvailabilityPolicyTests
{
    [Theory]
    [InlineData("starting", 1, true, true, false)]
    [InlineData("ready", 0, true, true, false)]
    [InlineData("ready", 1, false, true, false)]
    [InlineData("ready", 1, true, false, false)]
    [InlineData("ready", 1, true, true, true)]
    public void HasRealtimeRoute_RequiresReadyPeerWithPushAndFilter(
        string phase,
        int peerCount,
        bool lightPushReady,
        bool filterReady,
        bool expected)
    {
        WakuTransportDiagnostics diagnostics = CreateDiagnostics(
            phase,
            peerCount,
            lightPushReady,
            filterReady);

        Assert.Equal(expected, WakuTransportAvailabilityPolicy.HasRealtimeRoute(diagnostics));
    }

    [Fact]
    public void HasRealtimeRoute_RecheckWithNoCurrentPeersIsOffline()
    {
        WakuTransportDiagnostics diagnostics = CreateDiagnostics(
            "ready",
            peerCount: 0,
            lightPushReady: true,
            filterReady: true,
            lastEvent: "STATE recheck");

        Assert.False(WakuTransportAvailabilityPolicy.HasRealtimeRoute(diagnostics));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void HasRealtimeRoute_RecheckWithPartialRealtimeProtocolsIsOffline(
        bool lightPushReady,
        bool filterReady)
    {
        WakuTransportDiagnostics diagnostics = CreateDiagnostics(
            "ready",
            peerCount: 1,
            lightPushReady,
            filterReady,
            lastEvent: "STATE recheck");

        Assert.False(WakuTransportAvailabilityPolicy.HasRealtimeRoute(diagnostics));
    }

    private static WakuTransportDiagnostics CreateDiagnostics(
        string phase,
        int peerCount,
        bool lightPushReady,
        bool filterReady,
        string lastEvent = "STATE ready") =>
        new(
            phase,
            "public",
            peerCount,
            lightPushReady,
            filterReady,
            StoreReady: true,
            TopicCount: 4,
            PublishAttempts: 0,
            PublishSuccesses: 0,
            PublishFailures: 0,
            LiveMessages: 0,
            StoreQueries: 0,
            StoreRecords: 0,
            lastEvent,
            LastError: null,
            Peers: [],
            RecentEvents: []);
}
