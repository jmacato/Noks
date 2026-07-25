#nullable enable

using System;
using System.Security.Cryptography;
using System.Text;

namespace Noks.Cryptography;

public sealed class WakuProfileKeys : IDisposable
{
    public const uint NoksPurpose = 0x4E4F4B53;
    public const int KeyGeneration = 1;
    private static readonly byte[] ProfileSeedKey = Encoding.UTF8.GetBytes("Noks profile seed v1");
    private static readonly uint[] ContactCardPath = [NoksPurpose, 0, 0, 0];
    private static readonly uint[] EnvelopePath = [NoksPurpose, 0, 1, 0];
    private static readonly uint[] MailboxPath = [NoksPurpose, 0, 2, 0];
    private readonly byte[] contactCardPrivateKey;
    private readonly byte[] envelopePrivateKey;
    private readonly byte[] mailboxPrivateKey;
    private bool disposed;

    private WakuProfileKeys(byte[] contactCardPrivateKey, byte[] envelopePrivateKey, byte[] mailboxPrivateKey)
    {
        this.contactCardPrivateKey = contactCardPrivateKey;
        this.envelopePrivateKey = envelopePrivateKey;
        this.mailboxPrivateKey = mailboxPrivateKey;
        byte[] contactPublic = new byte[WakuCrypto.Secp256k1CompressedPublicKeySize];
        byte[] envelopePublic = new byte[WakuCrypto.Secp256k1CompressedPublicKeySize];
        byte[] mailboxPublic = new byte[WakuCrypto.X25519KeySize];
        WakuCrypto.GetSecp256k1PublicKey(contactCardPrivateKey, contactPublic);
        WakuCrypto.GetSecp256k1PublicKey(envelopePrivateKey, envelopePublic);
        WakuCrypto.GetX25519PublicKey(mailboxPrivateKey, mailboxPublic);
        ContactCardPublicKey = contactPublic;
        EnvelopePublicKey = envelopePublic;
        MailboxPublicKey = mailboxPublic;
        StableContactId = CreateStableContactId(contactPublic);
    }

    public ReadOnlyMemory<byte> ContactCardPrivateKey => GetSecret(contactCardPrivateKey);

    public ReadOnlyMemory<byte> ContactCardPublicKey { get; }

    public ReadOnlyMemory<byte> EnvelopePrivateKey => GetSecret(envelopePrivateKey);

    public ReadOnlyMemory<byte> EnvelopePublicKey { get; }

    public ReadOnlyMemory<byte> MailboxPrivateKey => GetSecret(mailboxPrivateKey);

    public ReadOnlyMemory<byte> MailboxPublicKey { get; }

    public string StableContactId { get; }

    public static WakuProfileKeys Create(ReadOnlySpan<byte> entropy)
    {
        byte[] profileSeed = DeriveProfileSeed(entropy);
        byte[]? contact = null;
        byte[]? envelope = null;
        byte[]? mailbox = null;
        try
        {
            contact = Slip10.DeriveSecp256k1(profileSeed, ContactCardPath);
            envelope = Slip10.DeriveSecp256k1(profileSeed, EnvelopePath);
            mailbox = Slip10.DeriveCurve25519(profileSeed, MailboxPath);
            WakuProfileKeys keys = new(contact, envelope, mailbox);
            contact = null;
            envelope = null;
            mailbox = null;
            return keys;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(profileSeed);
            if (contact is not null) CryptographicOperations.ZeroMemory(contact);
            if (envelope is not null) CryptographicOperations.ZeroMemory(envelope);
            if (mailbox is not null) CryptographicOperations.ZeroMemory(mailbox);
        }
    }

    public static byte[] DeriveProfileSeed(ReadOnlySpan<byte> entropy)
    {
        if (entropy.Length != NoksRecoveryPhrase.EntropySize)
            throw new ArgumentException("Profile derivation requires 32 bytes of recovery entropy.", nameof(entropy));
        return HMACSHA512.HashData(ProfileSeedKey, entropy);
    }

    public static string CreateStableContactId(ReadOnlySpan<byte> compressedContactCardPublicKey)
    {
        if (compressedContactCardPublicKey.Length != WakuCrypto.Secp256k1CompressedPublicKeySize)
            throw new ArgumentException("A compressed 33-byte secp256k1 public key is required.", nameof(compressedContactCardPublicKey));
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(compressedContactCardPublicKey, hash);
        string encoded = Convert.ToBase64String(hash[..18]);
        CryptographicOperations.ZeroMemory(hash);
        return encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public void Dispose()
    {
        if (disposed)
            return;
        CryptographicOperations.ZeroMemory(contactCardPrivateKey);
        CryptographicOperations.ZeroMemory(envelopePrivateKey);
        CryptographicOperations.ZeroMemory(mailboxPrivateKey);
        disposed = true;
    }

    private ReadOnlyMemory<byte> GetSecret(byte[] value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return value;
    }
}
