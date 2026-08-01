using Noks.Cryptography;

namespace Noks.Waku.Tests;

public sealed class PqcContactCardCodecTests
{
    private static readonly DateTimeOffset IssuedAt =
        DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);

    [Fact]
    public void MlDsaContactCardRoundTripsAndBindsKemRoutingIdentity()
    {
        PqcRendezvousIdentity identity =
            PqcRendezvousCrypto.CreateIdentity(Enumerable.Repeat((byte)0x6d, 32).ToArray());
        PqcContactCard card = PqcContactCardCodec.CreateSigned(
            identity,
            "post-quantum-42",
            "1234567890123",
            IssuedAt,
            IssuedAt.AddHours(6));

        byte[] encoded = PqcContactCardCodec.Encode(card);

        Assert.Equal("NPK1", System.Text.Encoding.ASCII.GetString(encoded, 0, 4));
        Assert.True(PqcContactCardCodec.TryValidate(
            encoded,
            IssuedAt.AddMinutes(1),
            out PqcContactCard? decoded,
            card.EnvelopeRoutingKey.Span));
        Assert.NotNull(decoded);
        Assert.Equal(identity.SigningPublicKey, decoded.SigningPublicKey.ToArray());
        Assert.Equal(identity.ChallengePublicKey, decoded.MailboxPublicKey.ToArray());
        Assert.Equal(PqcRendezvousCrypto.MlDsa65SignatureSize, decoded.Signature.Length);
        Assert.Equal(33, decoded.EnvelopeRoutingKey.Length);
        Assert.Equal(32, decoded.MailboxRoutingKey.Length);
    }

    [Fact]
    public void TamperingAndWrongPqcIdentityAreRejected()
    {
        PqcRendezvousIdentity identity =
            PqcRendezvousCrypto.CreateIdentity(Enumerable.Repeat((byte)0x31, 32).ToArray());
        PqcRendezvousIdentity other =
            PqcRendezvousCrypto.CreateIdentity(Enumerable.Repeat((byte)0x32, 32).ToArray());
        byte[] encoded = PqcContactCardCodec.Encode(PqcContactCardCodec.CreateSigned(
            identity,
            "post-quantum-43",
            "1234567890123",
            IssuedAt,
            IssuedAt.AddHours(1)));

        Assert.False(PqcContactCardCodec.TryValidate(
            encoded,
            IssuedAt.AddMinutes(1),
            out _,
            PqcContactCardCodec.CreateEnvelopeRoutingKey(other.SigningPublicKey)));

        encoded[^1] ^= 1;
        Assert.False(PqcContactCardCodec.TryValidate(
            encoded,
            IssuedAt.AddMinutes(1),
            out _));
    }
}
