using System.Security.Cryptography;
using Noks.Cryptography;

namespace Noks.Waku;

public sealed class PqcContactCard
{
    internal PqcContactCard(
        string stableContactId,
        string userName,
        string number,
        long issuedAtUnixMilliseconds,
        long expiresAtUnixMilliseconds,
        ReadOnlySpan<byte> signingPublicKey,
        ReadOnlySpan<byte> mailboxPublicKey,
        ReadOnlySpan<byte> signature)
    {
        StableContactId = stableContactId;
        UserName = userName;
        Number = number;
        IssuedAtUnixMilliseconds = issuedAtUnixMilliseconds;
        ExpiresAtUnixMilliseconds = expiresAtUnixMilliseconds;
        SigningPublicKey = signingPublicKey.ToArray();
        MailboxPublicKey = mailboxPublicKey.ToArray();
        Signature = signature.ToArray();
        ContactCardRoutingKey = PqcContactCardCodec.CreateContactCardRoutingKey(signingPublicKey);
        EnvelopeRoutingKey = PqcContactCardCodec.CreateEnvelopeRoutingKey(signingPublicKey);
        MailboxRoutingKey = PqcContactCardCodec.CreateMailboxRoutingKey(mailboxPublicKey);
    }

    public string StableContactId { get; }

    public string UserName { get; }

    public string Number { get; }

    public long IssuedAtUnixMilliseconds { get; }

    public long ExpiresAtUnixMilliseconds { get; }

    public ReadOnlyMemory<byte> SigningPublicKey { get; }

    public ReadOnlyMemory<byte> MailboxPublicKey { get; }

    public ReadOnlyMemory<byte> Signature { get; }

    public ReadOnlyMemory<byte> ContactCardRoutingKey { get; }

    public ReadOnlyMemory<byte> EnvelopeRoutingKey { get; }

    public ReadOnlyMemory<byte> MailboxRoutingKey { get; }

    public bool BindsEnvelopeRoutingKey(ReadOnlySpan<byte> routingKey) =>
        routingKey.Length == EnvelopeRoutingKey.Length &&
        CryptographicOperations.FixedTimeEquals(EnvelopeRoutingKey.Span, routingKey);
}
