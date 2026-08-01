namespace Noks.Waku.Tests;

public sealed class WakuCallSessionTests
{
    [Fact]
    public void OutgoingCallCanNegotiateConnectAndHangUp()
    {
        var session = new WakuCallSession();
        var attemptId = Guid.NewGuid();
        session.BeginOutgoing(attemptId);

        Assert.Equal(WakuCallState.OutgoingRinging, session.State);
        Assert.Equal(WakuCallSignalResult.Applied, session.ApplyRemote(Signal(attemptId, WakuEventKind.CallRinging)));
        Assert.Equal(WakuCallSignalResult.Applied, session.ApplyRemote(Signal(attemptId, WakuEventKind.CallAccept)));
        Assert.Equal(WakuCallState.Negotiating, session.State);
        Assert.Equal(WakuCallSignalResult.Applied, session.ApplyRemote(Signal(attemptId, WakuEventKind.SdpAnswer)));
        Assert.True(session.MarkWebRtcConnected(attemptId));
        Assert.Equal(WakuCallState.Connected, session.State);
        Assert.Equal(WakuCallSignalResult.Applied, session.ApplyRemote(Signal(attemptId, WakuEventKind.CallHangup)));
        Assert.Equal(WakuCallState.Ended, session.State);
    }

    [Fact]
    public void StaleAttemptCannotDisturbCurrentCall()
    {
        var session = new WakuCallSession();
        var currentAttempt = Guid.NewGuid();
        session.BeginOutgoing(currentAttempt);
        var staleSignal = Signal(Guid.NewGuid(), WakuEventKind.CallReject);

        Assert.Equal(WakuCallSignalResult.IgnoredStaleAttempt, session.ApplyRemote(staleSignal));
        Assert.Equal(WakuCallState.OutgoingRinging, session.State);
        Assert.Equal(WakuCallSignalResult.Duplicate, session.ApplyRemote(staleSignal));
    }

    [Fact]
    public void IncomingCallRequiresLocalConsent()
    {
        var session = new WakuCallSession();
        var attemptId = Guid.NewGuid();

        Assert.Equal(WakuCallSignalResult.Applied, session.ApplyRemote(Signal(attemptId, WakuEventKind.CallInvite)));
        Assert.Equal(WakuCallState.IncomingRinging, session.State);
        Assert.True(session.AcceptIncoming());
        Assert.Equal(WakuCallState.Negotiating, session.State);
        Assert.Equal(WakuCallSignalResult.Applied, session.ApplyRemote(Signal(attemptId, WakuEventKind.SdpOffer)));
    }

    [Fact]
    public void WebRtcCanConnectBeforeIncomingCallConsentWithoutAcceptingCall()
    {
        var session = new WakuCallSession();
        var attemptId = Guid.NewGuid();

        Assert.Equal(WakuCallSignalResult.Applied, session.ApplyRemote(Signal(attemptId, WakuEventKind.CallInvite)));
        Assert.Equal(WakuCallSignalResult.Applied, session.ApplyRemote(Signal(attemptId, WakuEventKind.SdpOffer)));
        Assert.True(session.MarkWebRtcConnected(attemptId));
        Assert.True(session.IsWebRtcConnected);
        Assert.Equal(WakuCallState.IncomingRinging, session.State);

        Assert.True(session.AcceptIncoming());
        Assert.Equal(WakuCallState.Connected, session.State);
        Assert.Equal(
            WakuCallSignalResult.Applied,
            session.ApplyRemote(Signal(attemptId, WakuEventKind.CallConnected)));
    }

    private static WakuCallSignal Signal(Guid attemptId, WakuEventKind kind) =>
        new(Guid.NewGuid(), attemptId, kind);
}
