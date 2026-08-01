namespace Noks.Waku.Transport.Protocols;

internal readonly record struct WakuWireMessage(
    string ContentTopic,
    byte[] Payload,
    long TimestampUnixMilliseconds,
    bool Ephemeral);
