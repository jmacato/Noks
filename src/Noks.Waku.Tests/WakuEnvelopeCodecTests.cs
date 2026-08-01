using System.Security.Cryptography;
using System.Text;
using Noks.Cryptography;

namespace Noks.Waku.Tests;

public sealed class WakuEnvelopeCodecTests
{
    [Fact]
    public void EnvelopeRoundTripsAndHasFixedSize()
    {
        var fixture = CreateFixture(WakuEventKind.Sms, Encoding.UTF8.GetBytes("hello through Waku"));

        var first = WakuEnvelopeCodec.Encrypt(fixture.Message, fixture.SenderPrivateKey);
        var second = WakuEnvelopeCodec.Encrypt(fixture.Message, fixture.SenderPrivateKey);

        Assert.Equal(WakuEnvelopeCodec.EnvelopeSize, first.Length);
        Assert.Equal(WakuEnvelopeCodec.EnvelopeSize, second.Length);
        Assert.False(first.SequenceEqual(second));
        Assert.True(WakuEnvelopeCodec.TryDecrypt(first, fixture.RecipientPrivateKey, out var decoded));
        Assert.NotNull(decoded);
        Assert.Equal(fixture.Message.EventId, decoded.EventId);
        Assert.Equal(WakuEventKind.Sms, decoded.Kind);
        Assert.Equal(fixture.Message.IssuedAtUnixMilliseconds, decoded.IssuedAtUnixMilliseconds);
        Assert.Equal(fixture.Message.ExpiresAtUnixMilliseconds, decoded.ExpiresAtUnixMilliseconds);
        Assert.Equal(fixture.Message.SenderIdentityPublicKey.ToArray(), decoded.SenderIdentityPublicKey.ToArray());
        Assert.Equal(fixture.Message.RecipientMailboxPublicKey.ToArray(), decoded.RecipientMailboxPublicKey.ToArray());
        Assert.Equal(fixture.Message.Payload.ToArray(), decoded.Payload.ToArray());
    }

    [Theory]
    [InlineData(5)]
    [InlineData(80)]
    [InlineData(2290)]
    public void EnvelopeRejectsTampering(int offset)
    {
        var fixture = CreateFixture(WakuEventKind.Sms, "authenticated"u8.ToArray());
        var envelope = WakuEnvelopeCodec.Encrypt(fixture.Message, fixture.SenderPrivateKey);
        envelope[offset] ^= 1;

        Assert.False(WakuEnvelopeCodec.TryDecrypt(envelope, fixture.RecipientPrivateKey, out _));
    }

    [Fact]
    public void EnvelopeRejectsWrongMailbox()
    {
        var fixture = CreateFixture(WakuEventKind.Sms, "private"u8.ToArray());
        var wrongRecipient = new byte[WakuCrypto.X25519KeySize];
        WakuCrypto.GenerateX25519PrivateKey(wrongRecipient);

        var envelope = WakuEnvelopeCodec.Encrypt(fixture.Message, fixture.SenderPrivateKey);

        Assert.False(WakuEnvelopeCodec.TryDecrypt(envelope, wrongRecipient, out _));
    }

    [Fact]
    public void EnvelopeSupportsMaximumPayload()
    {
        var payload = RandomNumberGenerator.GetBytes(WakuEnvelopeCodec.MaximumPayloadSize);
        var fixture = CreateFixture(WakuEventKind.SdpOffer, payload);

        var envelope = WakuEnvelopeCodec.Encrypt(fixture.Message, fixture.SenderPrivateKey);

        Assert.True(WakuEnvelopeCodec.TryDecrypt(envelope, fixture.RecipientPrivateKey, out var decoded));
        Assert.Equal(payload, decoded!.Payload.ToArray());
    }

    [Fact]
    public void PublishRequestUsesEventDeliveryPolicy()
    {
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1_750_000_000_000);
        var durable = CreateFixture(WakuEventKind.Sms, "sms"u8.ToArray());
        var realtime = CreateFixture(WakuEventKind.CallInvite, "call"u8.ToArray());
        var durableEnvelope = WakuEnvelopeCodec.Encrypt(durable.Message, durable.SenderPrivateKey);
        var realtimeEnvelope = WakuEnvelopeCodec.Encrypt(realtime.Message, realtime.SenderPrivateKey);

        var durableRequest = WakuPublishRequestFactory.Create(durable.Message, durableEnvelope, timestamp);
        var realtimeRequest = WakuPublishRequestFactory.Create(realtime.Message, realtimeEnvelope, timestamp);

        Assert.False(durableRequest.Ephemeral);
        Assert.True(realtimeRequest.Ephemeral);
        Assert.Equal(WakuEnvelopeCodec.EnvelopeSize, durableRequest.Payload.Length);
        Assert.Equal(WakuTopicProfile.GetSendTopic(durable.Message.RecipientMailboxPublicKey.Span, timestamp), durableRequest.ContentTopic);
    }

    private static EnvelopeFixture CreateFixture(WakuEventKind kind, byte[] payload)
    {
        var senderPrivate = new byte[WakuCrypto.Secp256k1PrivateKeySize];
        senderPrivate[^1] = 1;
        var senderPublic = new byte[WakuCrypto.Secp256k1CompressedPublicKeySize];
        WakuCrypto.GetSecp256k1PublicKey(senderPrivate, senderPublic);

        var recipientPrivate = Enumerable.Range(1, WakuCrypto.X25519KeySize).Select(value => (byte)value).ToArray();
        var recipientPublic = new byte[WakuCrypto.X25519KeySize];
        WakuCrypto.GetX25519PublicKey(recipientPrivate, recipientPublic);

        var issuedAt = 1_750_000_000_000L;
        var lifetime = WakuEventPolicy.GetDeliveryClass(kind) == WakuDeliveryClass.Durable
            ? TimeSpan.FromDays(2)
            : TimeSpan.FromMinutes(2);
        var message = new WakuApplicationMessage(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            kind,
            issuedAt,
            issuedAt + checked((long)lifetime.TotalMilliseconds),
            senderPublic,
            recipientPublic,
            payload);
        return new EnvelopeFixture(message, senderPrivate, recipientPrivate);
    }

    private sealed record EnvelopeFixture(
        WakuApplicationMessage Message,
        byte[] SenderPrivateKey,
        byte[] RecipientPrivateKey);
}
