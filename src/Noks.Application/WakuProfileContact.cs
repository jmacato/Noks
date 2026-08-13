using System.Collections.Immutable;
using System.Security.Cryptography;
using Noks.Cryptography;
using Noks.Waku;

namespace Noks.Application;

public sealed class WakuProfileContact
{
    public WakuProfileContact(
        string stableContactId,
        string userName,
        string currentNumber,
        int keyGeneration,
        ReadOnlySpan<byte> contactCardPublicKey,
        ReadOnlySpan<byte> envelopePublicKey,
        ReadOnlySpan<byte> mailboxPublicKey,
        ReadOnlySpan<byte> pqcMailboxPublicKey = default,
        ReadOnlySpan<byte> pqcSigningPublicKey = default)
    {
        if (stableContactId.Length != 24 ||
            stableContactId.Any(character =>
                character is not (>= 'A' and <= 'Z') and
                not (>= 'a' and <= 'z') and
                not (>= '0' and <= '9') and not '-' and not '_'))
        {
            throw new ArgumentException("Stable contact ID is invalid.", nameof(stableContactId));
        }
        if (!NoksUserName.IsValid(userName))
            throw new ArgumentException("Contact user name is invalid.", nameof(userName));
        if (!NoksTemporaryNumber.IsCanonical(currentNumber))
            throw new ArgumentException("Contact number is invalid.", nameof(currentNumber));
        if (keyGeneration != WakuProfileKeys.KeyGeneration)
            throw new ArgumentOutOfRangeException(nameof(keyGeneration));
        if (contactCardPublicKey.Length != WakuCrypto.Secp256k1CompressedPublicKeySize)
            throw new ArgumentException("Contact-card key is invalid.", nameof(contactCardPublicKey));
        if (envelopePublicKey.Length != WakuCrypto.Secp256k1CompressedPublicKeySize)
            throw new ArgumentException("Envelope key is invalid.", nameof(envelopePublicKey));
        if (mailboxPublicKey.Length != WakuCrypto.X25519KeySize)
            throw new ArgumentException("Mailbox key is invalid.", nameof(mailboxPublicKey));
        if (pqcMailboxPublicKey.Length is not (0 or PqcRendezvousCrypto.MlKem768PublicKeySize))
            throw new ArgumentException("PQC mailbox key is invalid.", nameof(pqcMailboxPublicKey));
        if (pqcSigningPublicKey.Length is not (0 or PqcRendezvousCrypto.MlDsa65PublicKeySize))
            throw new ArgumentException("PQC signing key is invalid.", nameof(pqcSigningPublicKey));
        if (pqcSigningPublicKey.IsEmpty)
        {
            if (!string.Equals(
                    WakuProfileKeys.CreateStableContactId(contactCardPublicKey),
                    stableContactId,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException("Stable contact ID does not match the contact-card key.", nameof(stableContactId));
            }
        }
        else
        {
            if (!PqcRendezvousCrypto.IsValidMlDsa65PublicKey(pqcSigningPublicKey) ||
                !PqcRendezvousCrypto.IsValidMlKem768PublicKey(pqcMailboxPublicKey))
            {
                throw new ArgumentException("PQC contact keys are malformed.", nameof(pqcSigningPublicKey));
            }
            byte[] expectedContactKey = PqcContactCardCodec.CreateContactCardRoutingKey(pqcSigningPublicKey);
            byte[] expectedEnvelopeKey = PqcContactCardCodec.CreateEnvelopeRoutingKey(pqcSigningPublicKey);
            byte[] expectedMailboxKey = PqcContactCardCodec.CreateMailboxRoutingKey(pqcMailboxPublicKey);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(contactCardPublicKey, expectedContactKey) ||
                    !CryptographicOperations.FixedTimeEquals(envelopePublicKey, expectedEnvelopeKey) ||
                    !CryptographicOperations.FixedTimeEquals(mailboxPublicKey, expectedMailboxKey) ||
                    !string.Equals(
                        PqcContactCardCodec.CreateStableContactId(expectedContactKey),
                        stableContactId,
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException("PQC routing fingerprints do not match their public keys.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedContactKey);
                CryptographicOperations.ZeroMemory(expectedEnvelopeKey);
                CryptographicOperations.ZeroMemory(expectedMailboxKey);
            }
        }

        StableContactId = stableContactId;
        UserName = userName;
        CurrentNumber = currentNumber;
        KeyGeneration = keyGeneration;
        ContactCardPublicKey = ImmutableArray.Create(contactCardPublicKey.ToArray());
        EnvelopePublicKey = ImmutableArray.Create(envelopePublicKey.ToArray());
        MailboxPublicKey = ImmutableArray.Create(mailboxPublicKey.ToArray());
        PqcMailboxPublicKey = ImmutableArray.Create(pqcMailboxPublicKey.ToArray());
        PqcSigningPublicKey = ImmutableArray.Create(pqcSigningPublicKey.ToArray());
    }

    public string StableContactId { get; }

    public string UserName { get; }

    public string CurrentNumber { get; }

    public int KeyGeneration { get; }

    public ImmutableArray<byte> ContactCardPublicKey { get; }

    public ImmutableArray<byte> EnvelopePublicKey { get; }

    public ImmutableArray<byte> MailboxPublicKey { get; }

    public ImmutableArray<byte> PqcMailboxPublicKey { get; }

    public ImmutableArray<byte> PqcSigningPublicKey { get; }

    public bool HasPqcIdentity =>
        PqcMailboxPublicKey.Length == PqcRendezvousCrypto.MlKem768PublicKeySize &&
        PqcSigningPublicKey.Length == PqcRendezvousCrypto.MlDsa65PublicKeySize;

    public bool MatchesEnvelopeKey(ReadOnlySpan<byte> publicKey) =>
        publicKey.Length == EnvelopePublicKey.Length &&
        CryptographicOperations.FixedTimeEquals(publicKey, EnvelopePublicKey.AsSpan());

    public static WakuProfileContact FromValidatedCard(ContactCardV2 card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!NoksTemporaryNumber.IsCanonical(card.Number))
            throw new ArgumentException("Contact card does not contain a current Noks number.", nameof(card));
        return new WakuProfileContact(
            card.StableContactId,
            card.UserName,
            card.Number,
            card.KeyGeneration,
            card.ContactCardPublicKey.Span,
            card.EnvelopePublicKey.Span,
            card.MailboxPublicKey.Span,
            card.PqcMailboxPublicKey.Span);
    }

    public static WakuProfileContact FromValidatedPqcCard(PqcContactCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return new WakuProfileContact(
            card.StableContactId,
            card.UserName,
            card.Number,
            PqcContactCardCodec.KeyGeneration,
            card.ContactCardRoutingKey.Span,
            card.EnvelopeRoutingKey.Span,
            card.MailboxRoutingKey.Span,
            card.MailboxPublicKey.Span,
            card.SigningPublicKey.Span);
    }
}
