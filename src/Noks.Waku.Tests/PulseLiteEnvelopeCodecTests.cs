using Noks.Cryptography;

namespace Noks.Waku.Tests;

public sealed class PulseLiteEnvelopeCodecTests
{
    [Fact]
    public void PacketRoundTripsWithoutCleartextProtocolMarkers()
    {
        var fixture = CreateFixture(WakuEventKind.Sms, "opaque pulse"u8.ToArray());

        var packet = PulseLiteEnvelopeCodec.Encrypt(fixture.Message, fixture.SenderPrivateKey);

        Assert.Equal(PulseLiteEnvelopeCodec.PacketSize, packet.Length);
        Assert.Equal(-1, packet.AsSpan().IndexOf("NWE1"u8));
        Assert.Equal(-1, packet.AsSpan().IndexOf("NWM1"u8));
        Assert.Equal(-1, packet.AsSpan().IndexOf(fixture.Message.Payload.Span));
        Assert.Equal(-1, packet.AsSpan().IndexOf(fixture.Message.SenderIdentityPublicKey.Span));
        Assert.Equal(-1, packet.AsSpan().IndexOf(fixture.Message.RecipientMailboxPublicKey.Span));
        Assert.True(PulseLiteEnvelopeCodec.TryDecrypt(packet, fixture.RecipientPrivateKey, out var decoded));
        Assert.NotNull(decoded);
        Assert.Equal(fixture.Message.EventId, decoded.EventId);
        Assert.Equal(fixture.Message.Kind, decoded.Kind);
        Assert.Equal(fixture.Message.Payload.ToArray(), decoded.Payload.ToArray());
    }

    [Fact]
    public void RealAndCoverPacketsHaveTheSameOpaqueShape()
    {
        var real = CreateFixture(WakuEventKind.CallInvite, "call"u8.ToArray());
        var coverMessage = PulseLitePacketFactory.CreateCoverMessage(
            DateTimeOffset.FromUnixTimeMilliseconds(real.Message.IssuedAtUnixMilliseconds),
            real.Message.SenderIdentityPublicKey.Span,
            real.Message.RecipientMailboxPublicKey.Span);

        var realPacket = PulseLiteEnvelopeCodec.Encrypt(real.Message, real.SenderPrivateKey);
        var coverPacket = PulseLiteEnvelopeCodec.Encrypt(coverMessage, real.SenderPrivateKey);

        Assert.Equal(PulseLiteEnvelopeCodec.PacketSize, realPacket.Length);
        Assert.Equal(realPacket.Length, coverPacket.Length);
        Assert.False(realPacket.SequenceEqual(coverPacket));
        Assert.True(PulseLiteEnvelopeCodec.TryDecrypt(coverPacket, real.RecipientPrivateKey, out var decoded));
        Assert.Equal(WakuEventKind.PulseCover, decoded!.Kind);
        Assert.Empty(decoded.Payload.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(48)]
    [InlineData(1000)]
    [InlineData(2303)]
    public void PacketRejectsTampering(int offset)
    {
        var fixture = CreateFixture(WakuEventKind.Sms, "authenticated"u8.ToArray());
        var packet = PulseLiteEnvelopeCodec.Encrypt(fixture.Message, fixture.SenderPrivateKey);
        packet[offset] ^= 1;

        Assert.False(PulseLiteEnvelopeCodec.TryDecrypt(packet, fixture.RecipientPrivateKey, out _));
    }

    [Fact]
    public void PacketRejectsWrongMailbox()
    {
        var fixture = CreateFixture(WakuEventKind.Sms, "private"u8.ToArray());
        var wrongRecipient = new byte[WakuCrypto.X25519KeySize];
        WakuCrypto.GenerateX25519PrivateKey(wrongRecipient);
        var packet = PulseLiteEnvelopeCodec.Encrypt(fixture.Message, fixture.SenderPrivateKey);

        Assert.False(PulseLiteEnvelopeCodec.TryDecrypt(packet, wrongRecipient, out _));
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
        var message = new WakuApplicationMessage(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            kind,
            issuedAt,
            issuedAt + (long)TimeSpan.FromMinutes(2).TotalMilliseconds,
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
