using System.Security.Cryptography;
using Noks.Cryptography;

namespace Noks.Waku;

public sealed class ContactCardV2
{
    internal ContactCardV2(
        int keyGeneration,
        string stableContactId,
        string userName,
        string number,
        long issuedAtUnixMilliseconds,
        long expiresAtUnixMilliseconds,
        ReadOnlySpan<byte> contactCardPublicKey,
        ReadOnlySpan<byte> envelopePublicKey,
        ReadOnlySpan<byte> mailboxPublicKey,
        ReadOnlySpan<byte> pqcMailboxPublicKey,
        ReadOnlySpan<byte> signature)
    {
        KeyGeneration = keyGeneration;
        StableContactId = stableContactId;
        UserName = userName;
        Number = number;
        IssuedAtUnixMilliseconds = issuedAtUnixMilliseconds;
        ExpiresAtUnixMilliseconds = expiresAtUnixMilliseconds;
        ContactCardPublicKey = contactCardPublicKey.ToArray();
        EnvelopePublicKey = envelopePublicKey.ToArray();
        MailboxPublicKey = mailboxPublicKey.ToArray();
        PqcMailboxPublicKey = pqcMailboxPublicKey.ToArray();
        Signature = signature.ToArray();
    }

    public int Version => ContactCardV2Codec.Version;

    public int KeyGeneration { get; }

    public string StableContactId { get; }

    public string UserName { get; }

    public string Number { get; }

    public long IssuedAtUnixMilliseconds { get; }

    public long ExpiresAtUnixMilliseconds { get; }

    public ReadOnlyMemory<byte> ContactCardPublicKey { get; }

    public ReadOnlyMemory<byte> EnvelopePublicKey { get; }

    public ReadOnlyMemory<byte> MailboxPublicKey { get; }

    public ReadOnlyMemory<byte> PqcMailboxPublicKey { get; }

    public bool HasPqcMailboxKey =>
        PqcMailboxPublicKey.Length == PqcRendezvousCrypto.MlKem768PublicKeySize;

    public ReadOnlyMemory<byte> Signature { get; }

    public bool IsValidAt(DateTimeOffset instant) =>
        instant.ToUnixTimeMilliseconds() >= IssuedAtUnixMilliseconds &&
        instant.ToUnixTimeMilliseconds() < ExpiresAtUnixMilliseconds;

    public bool BindsEnvelopeKey(ReadOnlySpan<byte> publicKey) =>
        publicKey.Length == WakuCrypto.Secp256k1CompressedPublicKeySize &&
        CryptographicOperations.FixedTimeEquals(EnvelopePublicKey.Span, publicKey);

    public bool BindsMailboxKey(ReadOnlySpan<byte> publicKey) =>
        publicKey.Length == WakuCrypto.X25519KeySize &&
        CryptographicOperations.FixedTimeEquals(MailboxPublicKey.Span, publicKey);

    public bool BindsPqcMailboxKey(ReadOnlySpan<byte> publicKey) =>
        publicKey.Length == PqcRendezvousCrypto.MlKem768PublicKeySize &&
        CryptographicOperations.FixedTimeEquals(PqcMailboxPublicKey.Span, publicKey);
}
