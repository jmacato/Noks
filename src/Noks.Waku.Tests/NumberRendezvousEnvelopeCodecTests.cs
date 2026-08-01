using System.Security.Cryptography;
using Noks.Cryptography;

namespace Noks.Waku.Tests;

public sealed class NumberRendezvousEnvelopeCodecTests
{
    [Fact]
    public void RoundTrip_AuthenticatesEnvelopeAndKeepsFixedPacketShape()
    {
        byte[] entropy = Enumerable.Range(0, NoksRecoveryPhrase.EntropySize).Select(value => (byte)value).ToArray();
        using WakuProfileKeys sender = WakuProfileKeys.Create(entropy);
        using WakuProfileKeys recipient = WakuProfileKeys.Create(SHA256.HashData(entropy));
        DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
        ContactCardV2 card = ContactCardV2Codec.CreateSigned(
            sender,
            NoksUserName.GenerateInitial(entropy),
            "1234567890123",
            now,
            now.AddMinutes(2));
        Guid rendezvousId = Guid.Parse("1ec35d3a-101a-44f1-a29c-c45e49f416c4");
        byte[] payload = RendezvousPayloadCodec.EncodeRequest(
            rendezvousId,
            RendezvousRouteKind.Call,
            "9876543210987",
            ContactCardV2Codec.Encode(card));
        WakuApplicationMessage message = new(
            Guid.Parse("4f27fd72-4ca4-4e25-9399-d5bf185e7bfd"),
            WakuEventKind.RendezvousRequest,
            now.ToUnixTimeMilliseconds(),
            now.AddMinutes(2).ToUnixTimeMilliseconds(),
            sender.EnvelopePublicKey.Span,
            recipient.MailboxPublicKey.Span,
            payload);

        byte[] packet = NumberRendezvousEnvelopeCodec.Encrypt(
            message,
            sender.EnvelopePrivateKey.Span,
            "9876543210987");

        Assert.Equal(NumberRendezvousEnvelopeCodec.PacketSize, packet.Length);
        Assert.True(NumberRendezvousEnvelopeCodec.TryDecrypt(
            packet,
            "9876543210987",
            out WakuApplicationMessage? decoded));
        Assert.NotNull(decoded);
        Assert.Equal(message.EventId, decoded.EventId);
        Assert.Equal(message.SenderIdentityPublicKey.ToArray(), decoded.SenderIdentityPublicKey.ToArray());
        Assert.True(RendezvousPayloadCodec.TryDecodeRequest(decoded.Payload.Span, out RendezvousRequestPayload? request));
        Assert.NotNull(request);
        Assert.Equal(rendezvousId, request.RendezvousId);
        Assert.Equal(RendezvousRouteKind.Call, request.RouteKind);
        Assert.Equal("9876543210987", request.TargetNumber);
    }

    [Fact]
    public void WrongNumberAndTamperingAreRejected()
    {
        byte[] entropy = new byte[NoksRecoveryPhrase.EntropySize];
        entropy[^1] = 7;
        using WakuProfileKeys sender = WakuProfileKeys.Create(entropy);
        using WakuProfileKeys recipient = WakuProfileKeys.Create(SHA256.HashData(entropy));
        DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
        ContactCardV2 card = ContactCardV2Codec.CreateSigned(
            sender,
            "quiet-river-0abc",
            "1234567890123",
            now,
            now.AddMinutes(1));
        byte[] request = RendezvousPayloadCodec.EncodeRequest(
            Guid.NewGuid(),
            RendezvousRouteKind.Sms,
            "1234567890123",
            ContactCardV2Codec.Encode(card));
        WakuApplicationMessage message = new(
            Guid.NewGuid(),
            WakuEventKind.RendezvousRequest,
            now.ToUnixTimeMilliseconds(),
            now.AddMinutes(1).ToUnixTimeMilliseconds(),
            sender.EnvelopePublicKey.Span,
            recipient.MailboxPublicKey.Span,
            request);
        byte[] packet = NumberRendezvousEnvelopeCodec.Encrypt(
            message,
            sender.EnvelopePrivateKey.Span,
            "1234567890123");

        Assert.False(NumberRendezvousEnvelopeCodec.TryDecrypt(packet, "1234567890124", out _));
        packet[^1] ^= 0x80;
        Assert.False(NumberRendezvousEnvelopeCodec.TryDecrypt(packet, "1234567890123", out _));
    }
}
