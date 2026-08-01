namespace Noks.Waku;

public readonly record struct WakuTransportMessage(
    string ContentTopic,
    ReadOnlyMemory<byte> Payload,
    long TimestampUnixMilliseconds,
    WakuMessageSource Source);
