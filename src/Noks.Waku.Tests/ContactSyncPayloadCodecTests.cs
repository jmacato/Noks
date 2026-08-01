using Noks.Cryptography;

namespace Noks.Waku.Tests;

public sealed class ContactSyncPayloadCodecTests
{
    [Theory]
    [InlineData(ContactSyncKind.Offer)]
    [InlineData(ContactSyncKind.Acknowledge)]
    public void SignedCardRoundTrips(ContactSyncKind kind)
    {
        byte[] entropy = Enumerable.Repeat((byte)0x31, NoksRecoveryPhrase.EntropySize).ToArray();
        using WakuProfileKeys keys = WakuProfileKeys.Create(entropy);
        DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
        byte[] card = ContactCardV2Codec.Encode(ContactCardV2Codec.CreateSigned(
            keys,
            "quiet-river-ab12",
            "1234567890123",
            now,
            now.AddMinutes(1)));
        Guid transactionId = Guid.Parse("06652cc8-1ac8-4a35-b978-e3b1551fa424");

        byte[] encoded = ContactSyncPayloadCodec.Encode(transactionId, kind, card);

        Assert.True(ContactSyncPayloadCodec.TryDecode(encoded, out ContactSyncPayload? decoded));
        Assert.NotNull(decoded);
        Assert.Equal(transactionId, decoded.TransactionId);
        Assert.Equal(kind, decoded.Kind);
        Assert.Equal(card, decoded.ContactCard.ToArray());
    }

    [Fact]
    public void MalformedPayloadsAreRejected()
    {
        Assert.False(ContactSyncPayloadCodec.TryDecode([], out _));
        Assert.False(ContactSyncPayloadCodec.TryDecode(new byte[24], out _));
    }
}
