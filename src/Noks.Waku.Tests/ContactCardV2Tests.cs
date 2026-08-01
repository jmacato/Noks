using Noks.Cryptography;
using System.Security.Cryptography;

namespace Noks.Waku.Tests;

public sealed class ContactCardV2Tests
{
    private static readonly DateTimeOffset IssuedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);

    [Fact]
    public void SignedCardRoundTripsCanonically()
    {
        using WakuProfileKeys keys = CreateKeys();
        ContactCardV2 card = ContactCardV2Codec.CreateSigned(
            keys,
            "lively-orbit-jqqx",
            "1234567890123",
            IssuedAt,
            IssuedAt.AddMinutes(2));

        byte[] encoded = ContactCardV2Codec.Encode(card);

        Assert.True(ContactCardV2Codec.TryValidate(
            encoded,
            IssuedAt.AddSeconds(30),
            WakuProfileKeys.KeyGeneration,
            out ContactCardV2? decoded,
            out ContactCardValidationFailure failure,
            keys.EnvelopePublicKey.Span,
            keys.MailboxPublicKey.Span));
        Assert.Equal(ContactCardValidationFailure.None, failure);
        Assert.NotNull(decoded);
        Assert.Equal(keys.StableContactId, decoded.StableContactId);
        Assert.Equal("lively-orbit-jqqx", decoded.UserName);
        Assert.Equal("1234567890123", decoded.Number);
        Assert.Equal(encoded, ContactCardV2Codec.Encode(decoded));
        Assert.Equal(
            "90eeca079b01c81f2996264f71e8959b13ccf656ace4a492cc80c6a8d0b35275",
            Convert.ToHexStringLower(SHA256.HashData(encoded)));
    }

    [Fact]
    public void TamperingIsRejected()
    {
        using WakuProfileKeys keys = CreateKeys();
        byte[] encoded = CreateEncodedCard(keys);
        encoded[126] ^= 1;

        Assert.False(ContactCardV2Codec.TryValidate(
            encoded,
            IssuedAt.AddSeconds(30),
            WakuProfileKeys.KeyGeneration,
            out _,
            out ContactCardValidationFailure failure));
        Assert.Equal(ContactCardValidationFailure.InvalidSignature, failure);
    }

    [Fact]
    public void WrongEnvelopeAndMailboxBindingsAreRejected()
    {
        using WakuProfileKeys keys = CreateKeys();
        using WakuProfileKeys other = WakuProfileKeys.Create(Enumerable.Repeat((byte)0xA5, 32).ToArray());
        byte[] encoded = CreateEncodedCard(keys);

        Assert.False(ContactCardV2Codec.TryValidate(
            encoded,
            IssuedAt.AddSeconds(30),
            WakuProfileKeys.KeyGeneration,
            out _,
            out ContactCardValidationFailure envelopeFailure,
            other.EnvelopePublicKey.Span));
        Assert.Equal(ContactCardValidationFailure.WrongEnvelopeBinding, envelopeFailure);

        Assert.False(ContactCardV2Codec.TryValidate(
            encoded,
            IssuedAt.AddSeconds(30),
            WakuProfileKeys.KeyGeneration,
            out _,
            out ContactCardValidationFailure mailboxFailure,
            keys.EnvelopePublicKey.Span,
            other.MailboxPublicKey.Span));
        Assert.Equal(ContactCardValidationFailure.WrongMailboxBinding, mailboxFailure);
    }

    [Fact]
    public void SignedPqcMailboxExtensionRoundTripsAndBindsKemKey()
    {
        using WakuProfileKeys keys = CreateKeys();
        PqcRendezvousIdentity pqc =
            PqcRendezvousCrypto.CreateIdentity(Enumerable.Repeat((byte)0x73, 32).ToArray());
        ContactCardV2 card = ContactCardV2Codec.CreateSigned(
            keys,
            "lively-orbit-jqqx",
            "1234567890123",
            IssuedAt,
            IssuedAt.AddMinutes(2),
            pqc.ChallengePublicKey);

        byte[] encoded = ContactCardV2Codec.Encode(card);

        Assert.True(ContactCardV2Codec.TryValidate(
            encoded,
            IssuedAt.AddSeconds(30),
            WakuProfileKeys.KeyGeneration,
            out ContactCardV2? decoded,
            out ContactCardValidationFailure failure,
            keys.EnvelopePublicKey.Span,
            keys.MailboxPublicKey.Span,
            pqc.ChallengePublicKey));
        Assert.Equal(ContactCardValidationFailure.None, failure);
        Assert.NotNull(decoded);
        Assert.True(decoded.HasPqcMailboxKey);
        Assert.Equal(pqc.ChallengePublicKey, decoded.PqcMailboxPublicKey.ToArray());

        encoded[^1] ^= 1;
        Assert.False(ContactCardV2Codec.TryValidate(
            encoded,
            IssuedAt.AddSeconds(30),
            WakuProfileKeys.KeyGeneration,
            out _,
            out ContactCardValidationFailure tampered));
        Assert.Equal(ContactCardValidationFailure.InvalidSignature, tampered);
    }

    [Fact]
    public void ExpiryGenerationAndCardV1AreRejected()
    {
        using WakuProfileKeys keys = CreateKeys();
        byte[] encoded = CreateEncodedCard(keys);

        Assert.False(ContactCardV2Codec.TryValidate(
            encoded,
            IssuedAt.AddMinutes(2),
            WakuProfileKeys.KeyGeneration,
            out _,
            out ContactCardValidationFailure expired));
        Assert.Equal(ContactCardValidationFailure.InvalidTime, expired);

        Assert.False(ContactCardV2Codec.TryValidate(
            encoded,
            IssuedAt.AddSeconds(1),
            WakuProfileKeys.KeyGeneration + 1,
            out _,
            out ContactCardValidationFailure generation));
        Assert.Equal(ContactCardValidationFailure.InvalidGeneration, generation);

        encoded[3] = (byte)'1';
        encoded[4] = 1;
        Assert.False(ContactCardV2Codec.TryValidate(
            encoded,
            IssuedAt.AddSeconds(1),
            WakuProfileKeys.KeyGeneration,
            out _,
            out ContactCardValidationFailure version));
        Assert.Equal(ContactCardValidationFailure.UnsupportedVersion, version);
    }

    [Fact]
    public void TemporaryNumbersUseAllThirteenRandomDigitsAndGroupedFormat()
    {
        HashSet<string> numbers = [];
        for (int index = 0; index < 100; index++)
        {
            string number = NoksTemporaryNumber.Generate();
            Assert.True(NoksTemporaryNumber.IsCanonical(number));
            Assert.Matches("^[0-9]{13}$", number);
            Assert.Matches("^[0-9]{3}-[0-9]{3}-[0-9]{3}-[0-9]{4}$", NoksTemporaryNumber.Format(number));
            numbers.Add(number);
        }
        Assert.Equal(100, numbers.Count);
        Assert.Equal("123-456-789-0123", NoksTemporaryNumber.Format("1234567890123"));
    }

    [Fact]
    public void UnicodeUserNameUsesNullTerminatedUtf8AndRoundTrips()
    {
        using WakuProfileKeys keys = CreateKeys();
        string userName = string.Concat(Enumerable.Repeat("Mañana 🚀 ", 20));
        ContactCardV2 card = ContactCardV2Codec.CreateSigned(
            keys,
            userName,
            "1234567890123",
            IssuedAt,
            IssuedAt.AddMinutes(2));

        byte[] encoded = ContactCardV2Codec.Encode(card);

        Assert.Equal(0, encoded[24]);
        Assert.Equal(1, encoded[27]);
        Assert.Equal(0, encoded[126 + System.Text.Encoding.UTF8.GetByteCount(userName)]);
        Assert.True(ContactCardV2Codec.TryDecode(encoded, out ContactCardV2? decoded));
        Assert.Equal(userName, decoded?.UserName);
    }

    private static WakuProfileKeys CreateKeys() =>
        WakuProfileKeys.Create(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());

    private static byte[] CreateEncodedCard(WakuProfileKeys keys) =>
        ContactCardV2Codec.Encode(ContactCardV2Codec.CreateSigned(
            keys,
            "lively-orbit-jqqx",
            "1234567890123",
            IssuedAt,
            IssuedAt.AddMinutes(2)));
}
