namespace Noks.Waku;

public readonly record struct WakuStoreQuery(
    IReadOnlyList<string> ContentTopics,
    long StartUnixMilliseconds,
    long EndUnixMilliseconds);
