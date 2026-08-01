using Noks.Cryptography;

namespace Noks.Waku.Tests;

public sealed class WakuReplayGuardTests
{
    [Fact]
    public void EventIsAcceptedOnceWithinItsLifetime()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_750_000_000_000);
        var message = CreateMessage(now.AddMinutes(-1), now.AddMinutes(1));
        var guard = new WakuReplayGuard();

        Assert.True(guard.TryAccept(message, now));
        Assert.False(guard.TryAccept(message, now));
    }

    [Fact]
    public void ExpiredEventsAreRejected()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_750_000_000_000);
        var guard = new WakuReplayGuard();

        Assert.False(guard.TryAccept(CreateMessage(now.AddMinutes(-2), now.AddMinutes(-1)), now));
    }

    [Fact]
    public void EventExactlyOneMinuteAheadIsAcceptedWithinClockSkewAllowance()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_750_000_000_000);
        var guard = new WakuReplayGuard();

        Assert.True(guard.TryAccept(CreateMessage(now.AddMinutes(1), now.AddMinutes(2)), now));
    }

    [Fact]
    public void EventMoreThanOneMinuteAheadIsRejected()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_750_000_000_000);
        var guard = new WakuReplayGuard();

        Assert.False(guard.TryAccept(
            CreateMessage(now.AddMinutes(1).AddMilliseconds(1), now.AddMinutes(2)),
            now));
    }

    private static WakuApplicationMessage CreateMessage(DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        var senderPrivate = new byte[WakuCrypto.Secp256k1PrivateKeySize];
        senderPrivate[^1] = 1;
        var senderPublic = new byte[WakuCrypto.Secp256k1CompressedPublicKeySize];
        WakuCrypto.GetSecp256k1PublicKey(senderPrivate, senderPublic);
        var mailboxPrivate = Enumerable.Repeat((byte)7, WakuCrypto.X25519KeySize).ToArray();
        var mailboxPublic = new byte[WakuCrypto.X25519KeySize];
        WakuCrypto.GetX25519PublicKey(mailboxPrivate, mailboxPublic);
        return new WakuApplicationMessage(
            Guid.NewGuid(),
            WakuEventKind.Sms,
            issuedAt.ToUnixTimeMilliseconds(),
            expiresAt.ToUnixTimeMilliseconds(),
            senderPublic,
            mailboxPublic,
            "message"u8);
    }
}
