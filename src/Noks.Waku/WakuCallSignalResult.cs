namespace Noks.Waku;

public enum WakuCallSignalResult
{
    Applied,
    Duplicate,
    IgnoredStaleAttempt,
    RejectedForState,
}
