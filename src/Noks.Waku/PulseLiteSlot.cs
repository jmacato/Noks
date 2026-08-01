namespace Noks.Waku;

public readonly record struct PulseLiteSlot(
    long Sequence,
    long ScheduledAtUnixMilliseconds,
    int Bucket,
    string ContentTopic);
