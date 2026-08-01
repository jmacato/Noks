using Noks.Cryptography;

namespace Noks.Waku.Tests;

public sealed class PqcWakuEnvelopeCodecTests
{
    private static readonly DateTimeOffset IssuedAt =
        DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);

    [Fact]
    public void MlKemAesEnvelopeRoundTripsWithoutUsingTemporaryNumber()
    {
        PqcRendezvousIdentity sender = PqcRendezvousCrypto.CreateIdentity(
            Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        PqcRendezvousIdentity recipientPqc = PqcRendezvousCrypto.CreateIdentity(
            Enumerable.Repeat((byte)0x5a, 32).ToArray());
        WakuApplicationMessage message = CreateMessage(sender, recipientPqc);

        byte[] envelope = PqcWakuEnvelopeCodec.Encrypt(
            message,
            sender,
            recipientPqc.ChallengePublicKey);

        Assert.Equal(PqcWakuEnvelopeCodec.EnvelopeSize, envelope.Length);
        Assert.Equal("NQP2", System.Text.Encoding.ASCII.GetString(envelope, 0, 4));
        Assert.True(PqcWakuEnvelopeCodec.TryDecrypt(
            envelope,
            recipientPqc,
            out WakuApplicationMessage? decoded));
        Assert.NotNull(decoded);
        Assert.Equal(message.EventId, decoded.EventId);
        Assert.Equal(message.Kind, decoded.Kind);
        Assert.Equal(message.Payload.ToArray(), decoded.Payload.ToArray());
    }

    [Fact]
    public void TamperingAndWrongRecipientAreRejected()
    {
        PqcRendezvousIdentity sender =
            PqcRendezvousCrypto.CreateIdentity(Enumerable.Repeat((byte)0x11, 32).ToArray());
        PqcRendezvousIdentity recipientPqc =
            PqcRendezvousCrypto.CreateIdentity(Enumerable.Repeat((byte)0x33, 32).ToArray());
        PqcRendezvousIdentity wrongPqc =
            PqcRendezvousCrypto.CreateIdentity(Enumerable.Repeat((byte)0x44, 32).ToArray());
        byte[] envelope = PqcWakuEnvelopeCodec.Encrypt(
            CreateMessage(sender, recipientPqc),
            sender,
            recipientPqc.ChallengePublicKey);

        Assert.False(PqcWakuEnvelopeCodec.TryDecrypt(
            envelope,
            wrongPqc,
            out _));

        envelope[^1] ^= 0x80;
        Assert.False(PqcWakuEnvelopeCodec.TryDecrypt(
            envelope,
            recipientPqc,
            out _));
    }

    private static WakuApplicationMessage CreateMessage(
        PqcRendezvousIdentity sender,
        PqcRendezvousIdentity recipient)
    {
        byte[] senderRoutingKey =
            PqcContactCardCodec.CreateEnvelopeRoutingKey(sender.SigningPublicKey);
        byte[] recipientRoutingKey =
            PqcContactCardCodec.CreateMailboxRoutingKey(recipient.ChallengePublicKey);
        return new WakuApplicationMessage(
            Guid.Parse("5bbd5c2d-681a-4aa9-8df6-67250eb3780a"),
            WakuEventKind.Sms,
            IssuedAt.ToUnixTimeMilliseconds(),
            IssuedAt.AddMinutes(10).ToUnixTimeMilliseconds(),
            senderRoutingKey,
            recipientRoutingKey,
            "regular packet over ML-KEM-768 and AES-256-GCM"u8);
    }
}
