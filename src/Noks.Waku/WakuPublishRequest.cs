namespace Noks.Waku;

public readonly record struct WakuPublishRequest(
    string ContentTopic,
    ReadOnlyMemory<byte> Payload,
    bool Ephemeral,
    long TimestampUnixMilliseconds);
