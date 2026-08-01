namespace Noks.Waku;

public sealed class WakuCallSession
{
    private readonly HashSet<Guid> remoteEventIds = [];

    public WakuCallState State { get; private set; } = WakuCallState.Idle;

    public Guid? AttemptId { get; private set; }

    public bool IsCaller { get; private set; }

    public bool IsWebRtcConnected { get; private set; }

    public void BeginOutgoing(Guid attemptId)
    {
        RequireNewAttempt(attemptId);
        AttemptId = attemptId;
        IsCaller = true;
        State = WakuCallState.OutgoingRinging;
        IsWebRtcConnected = false;
        remoteEventIds.Clear();
    }

    public bool AcceptIncoming()
    {
        if (State != WakuCallState.IncomingRinging)
            return false;
        State = IsWebRtcConnected ? WakuCallState.Connected : WakuCallState.Negotiating;
        return true;
    }

    public bool RejectIncoming()
    {
        if (State != WakuCallState.IncomingRinging)
            return false;
        State = WakuCallState.Ended;
        return true;
    }

    public bool MarkWebRtcConnected(Guid attemptId)
    {
        if (AttemptId != attemptId || !IsActive(State))
            return false;
        IsWebRtcConnected = true;
        if (State == WakuCallState.Negotiating)
            State = WakuCallState.Connected;
        return true;
    }

    public bool EndLocally(Guid attemptId)
    {
        if (AttemptId != attemptId || !IsActive(State))
            return false;
        State = WakuCallState.Ended;
        return true;
    }

    public bool FailLocally(Guid attemptId)
    {
        if (AttemptId != attemptId || !IsActive(State))
            return false;
        State = WakuCallState.Failed;
        return true;
    }

    public WakuCallSignalResult ApplyRemote(WakuCallSignal signal)
    {
        if (signal.EventId == Guid.Empty || signal.AttemptId == Guid.Empty)
            return WakuCallSignalResult.RejectedForState;
        if (!IsCallSignal(signal.Kind))
            return WakuCallSignalResult.RejectedForState;
        if (!remoteEventIds.Add(signal.EventId))
            return WakuCallSignalResult.Duplicate;

        if (signal.Kind == WakuEventKind.CallInvite)
            return ApplyInvite(signal.AttemptId);
        if (AttemptId != signal.AttemptId)
            return WakuCallSignalResult.IgnoredStaleAttempt;

        return signal.Kind switch
        {
            WakuEventKind.CallRinging when State == WakuCallState.OutgoingRinging => WakuCallSignalResult.Applied,
            WakuEventKind.CallAccept when State == WakuCallState.OutgoingRinging =>
                SetState(IsWebRtcConnected ? WakuCallState.Connected : WakuCallState.Negotiating),
            WakuEventKind.CallReject when State is WakuCallState.OutgoingRinging or WakuCallState.Negotiating => SetState(WakuCallState.Ended),
            WakuEventKind.CallHangup when IsActive(State) => SetState(WakuCallState.Ended),
            WakuEventKind.CallFailed when IsActive(State) => SetState(WakuCallState.Failed),
            WakuEventKind.CallConnected when !IsCaller && State == WakuCallState.Connected => WakuCallSignalResult.Applied,
            WakuEventKind.SdpOffer when IsActive(State) => WakuCallSignalResult.Applied,
            WakuEventKind.SdpAnswer when IsActive(State) => WakuCallSignalResult.Applied,
            WakuEventKind.IceCandidate when IsActive(State) => WakuCallSignalResult.Applied,
            _ => WakuCallSignalResult.RejectedForState,
        };
    }

    private WakuCallSignalResult ApplyInvite(Guid attemptId)
    {
        if (AttemptId == attemptId && State == WakuCallState.IncomingRinging)
            return WakuCallSignalResult.Duplicate;
        if (IsActive(State))
            return WakuCallSignalResult.RejectedForState;

        AttemptId = attemptId;
        IsCaller = false;
        State = WakuCallState.IncomingRinging;
        IsWebRtcConnected = false;
        return WakuCallSignalResult.Applied;
    }

    private void RequireNewAttempt(Guid attemptId)
    {
        if (attemptId == Guid.Empty)
            throw new ArgumentException("A call attempt identifier is required.", nameof(attemptId));
        if (IsActive(State))
            throw new InvalidOperationException("A call attempt is already active.");
    }

    private WakuCallSignalResult SetState(WakuCallState state)
    {
        State = state;
        return WakuCallSignalResult.Applied;
    }

    private static bool IsActive(WakuCallState state) => state is
        WakuCallState.OutgoingRinging or
        WakuCallState.IncomingRinging or
        WakuCallState.Negotiating or
        WakuCallState.Connected;

    private static bool IsCallSignal(WakuEventKind kind) => kind is
        WakuEventKind.CallInvite or
        WakuEventKind.CallRinging or
        WakuEventKind.CallAccept or
        WakuEventKind.CallReject or
        WakuEventKind.CallHangup or
        WakuEventKind.CallFailed or
        WakuEventKind.CallConnected or
        WakuEventKind.SdpOffer or
        WakuEventKind.SdpAnswer or
        WakuEventKind.IceCandidate;
}
