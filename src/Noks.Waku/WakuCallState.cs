namespace Noks.Waku;

public enum WakuCallState
{
    Idle,
    OutgoingRinging,
    IncomingRinging,
    Negotiating,
    Connected,
    Ended,
    Failed,
}
