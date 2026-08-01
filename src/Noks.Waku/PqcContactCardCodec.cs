using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Noks.Cryptography;

namespace Noks.Waku;

/// <summary>
/// This class defines an ML-DSA-65 authenticated contact card. The 33-byte and
/// 32-byte routing keys are domain-separated SHA-256 fingerprints, not
/// elliptic-curve keys.
/// </summary>
public static class PqcContactCardCodec
{
    public static readonly TimeSpan MaximumCardLifetime = WakuEventPolicy.MaximumDurableLifetime;

    private const byte Version = 1;
    public const int KeyGeneration = 1;
    private const int StableContactIdLength = 24;
    private const int FixedHeaderSize =
        28 +
        PqcRendezvousCrypto.MlDsa65PublicKeySize +
        PqcRendezvousCrypto.MlKem768PublicKeySize;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static ReadOnlySpan<byte> Magic => "NPK1"u8;
    private static ReadOnlySpan<byte> SignatureDomain => "Noks PqcContactCardV1\0"u8;
    private static ReadOnlySpan<byte> ContactRoutingDomain => "noks/pqc/contact-routing/v1"u8;
    private static ReadOnlySpan<byte> EnvelopeRoutingDomain => "noks/pqc/envelope-routing/v1"u8;
    private static ReadOnlySpan<byte> MailboxRoutingDomain => "noks/pqc/mailbox-routing/v1"u8;

    public static PqcContactCard CreateSigned(
        PqcRendezvousIdentity identity,
        string userName,
        string number,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string normalizedNumber = NoksPhoneNumber.Normalize(number);
        byte[] contactRoutingKey = CreateContactCardRoutingKey(identity.SigningPublicKey);
        string stableContactId = CreateStableContactId(contactRoutingKey);
        var unsignedCard = new PqcContactCard(
            stableContactId,
            userName,
            normalizedNumber,
            issuedAt.ToUnixTimeMilliseconds(),
            expiresAt.ToUnixTimeMilliseconds(),
            identity.SigningPublicKey,
            identity.ChallengePublicKey,
            []);
        ValidateFields(unsignedCard);
        byte[] unsigned = EncodeUnsigned(unsignedCard);
        byte[] signedData = BuildSignedData(unsigned);
        try
        {
            byte[] signature = PqcRendezvousCrypto.SignMlDsa65(identity, signedData);
            try
            {
                return new PqcContactCard(
                    unsignedCard.StableContactId,
                    unsignedCard.UserName,
                    unsignedCard.Number,
                    unsignedCard.IssuedAtUnixMilliseconds,
                    unsignedCard.ExpiresAtUnixMilliseconds,
                    unsignedCard.SigningPublicKey.Span,
                    unsignedCard.MailboxPublicKey.Span,
                    signature);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signature);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(unsigned);
            CryptographicOperations.ZeroMemory(signedData);
            CryptographicOperations.ZeroMemory(contactRoutingKey);
        }
    }

    public static byte[] Encode(PqcContactCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        ValidateFields(card);
        if (card.Signature.Length != PqcRendezvousCrypto.MlDsa65SignatureSize)
            throw new ArgumentException("The contact card requires an ML-DSA-65 signature.", nameof(card));

        byte[] unsigned = EncodeUnsigned(card);
        byte[] encoded = new byte[unsigned.Length + 2 + card.Signature.Length];
        unsigned.CopyTo(encoded, 0);
        BinaryPrimitives.WriteUInt16BigEndian(
            encoded.AsSpan(unsigned.Length, 2),
            checked((ushort)card.Signature.Length));
        card.Signature.Span.CopyTo(encoded.AsSpan(unsigned.Length + 2));
        CryptographicOperations.ZeroMemory(unsigned);
        return encoded;
    }

    public static bool TryDecode(ReadOnlySpan<byte> encoded, out PqcContactCard? card)
    {
        card = null;
        if (encoded.Length < FixedHeaderSize + 2 ||
            !encoded[..4].SequenceEqual(Magic) ||
            encoded[4] != Version ||
            encoded[5] != KeyGeneration ||
            encoded[6] != 0 ||
            encoded[7] != 0)
        {
            return false;
        }

        int userNameLength = BinaryPrimitives.ReadUInt16BigEndian(encoded.Slice(24, 2));
        int numberLength = encoded[26];
        int stableIdLength = encoded[27];
        if (userNameLength is < 1 or > NoksUserName.MaximumUtf8Length ||
            numberLength != NoksTemporaryNumber.DigitCount ||
            stableIdLength != StableContactIdLength)
        {
            return false;
        }

        int unsignedLength = FixedHeaderSize + userNameLength + numberLength + stableIdLength;
        if (encoded.Length != unsignedLength + 2 + PqcRendezvousCrypto.MlDsa65SignatureSize ||
            BinaryPrimitives.ReadUInt16BigEndian(encoded.Slice(unsignedLength, 2)) !=
            PqcRendezvousCrypto.MlDsa65SignatureSize)
        {
            return false;
        }

        try
        {
            int offset = FixedHeaderSize;
            string userName = StrictUtf8.GetString(encoded.Slice(offset, userNameLength));
            offset += userNameLength;
            string number = Encoding.ASCII.GetString(encoded.Slice(offset, numberLength));
            offset += numberLength;
            string stableContactId = Encoding.ASCII.GetString(encoded.Slice(offset, stableIdLength));
            PqcContactCard decoded = new(
                stableContactId,
                userName,
                number,
                BinaryPrimitives.ReadInt64BigEndian(encoded.Slice(8, 8)),
                BinaryPrimitives.ReadInt64BigEndian(encoded.Slice(16, 8)),
                encoded.Slice(28, PqcRendezvousCrypto.MlDsa65PublicKeySize),
                encoded.Slice(
                    28 + PqcRendezvousCrypto.MlDsa65PublicKeySize,
                    PqcRendezvousCrypto.MlKem768PublicKeySize),
                encoded.Slice(unsignedLength + 2, PqcRendezvousCrypto.MlDsa65SignatureSize));
            ValidateFields(decoded);
            card = decoded;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool TryValidate(
        ReadOnlySpan<byte> encoded,
        DateTimeOffset now,
        out PqcContactCard? card,
        ReadOnlySpan<byte> expectedEnvelopeRoutingKey = default)
    {
        if (!TryDecode(encoded, out card) || card is null)
            return false;
        long nowMilliseconds = now.ToUnixTimeMilliseconds();
        if (nowMilliseconds < card.IssuedAtUnixMilliseconds - TimeSpan.FromMinutes(1).TotalMilliseconds ||
            nowMilliseconds >= card.ExpiresAtUnixMilliseconds ||
            (!expectedEnvelopeRoutingKey.IsEmpty &&
             !card.BindsEnvelopeRoutingKey(expectedEnvelopeRoutingKey)))
        {
            card = null;
            return false;
        }

        int unsignedLength = encoded.Length - 2 - PqcRendezvousCrypto.MlDsa65SignatureSize;
        byte[] signedData = BuildSignedData(encoded[..unsignedLength]);
        try
        {
            if (!PqcRendezvousCrypto.VerifyMlDsa65(
                    card.SigningPublicKey.Span,
                    signedData,
                    card.Signature.Span))
            {
                card = null;
                return false;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signedData);
        }
        return true;
    }

    public static byte[] CreateContactCardRoutingKey(ReadOnlySpan<byte> signingPublicKey) =>
        CreateRoutingKey(signingPublicKey, ContactRoutingDomain, 33, 0x50);

    public static byte[] CreateEnvelopeRoutingKey(ReadOnlySpan<byte> signingPublicKey) =>
        CreateRoutingKey(signingPublicKey, EnvelopeRoutingDomain, 33, 0x51);

    public static byte[] CreateMailboxRoutingKey(ReadOnlySpan<byte> mailboxPublicKey) =>
        CreateRoutingKey(mailboxPublicKey, MailboxRoutingDomain, 32, 0);

    public static string CreateStableContactId(ReadOnlySpan<byte> contactRoutingKey)
    {
        if (contactRoutingKey.Length != 33)
            throw new ArgumentException("A 33-byte PQC contact routing fingerprint is required.", nameof(contactRoutingKey));
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(contactRoutingKey, hash);
        string encoded = Convert.ToBase64String(hash[..18]);
        CryptographicOperations.ZeroMemory(hash);
        return encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] EncodeUnsigned(PqcContactCard card)
    {
        byte[] userName = StrictUtf8.GetBytes(card.UserName);
        byte[] number = Encoding.ASCII.GetBytes(card.Number);
        byte[] stableId = Encoding.ASCII.GetBytes(card.StableContactId);
        byte[] encoded = new byte[
            FixedHeaderSize + userName.Length + number.Length + stableId.Length];
        Magic.CopyTo(encoded);
        encoded[4] = Version;
        encoded[5] = KeyGeneration;
        BinaryPrimitives.WriteInt64BigEndian(encoded.AsSpan(8, 8), card.IssuedAtUnixMilliseconds);
        BinaryPrimitives.WriteInt64BigEndian(encoded.AsSpan(16, 8), card.ExpiresAtUnixMilliseconds);
        BinaryPrimitives.WriteUInt16BigEndian(encoded.AsSpan(24, 2), checked((ushort)userName.Length));
        encoded[26] = checked((byte)number.Length);
        encoded[27] = checked((byte)stableId.Length);
        card.SigningPublicKey.Span.CopyTo(
            encoded.AsSpan(28, PqcRendezvousCrypto.MlDsa65PublicKeySize));
        card.MailboxPublicKey.Span.CopyTo(
            encoded.AsSpan(
                28 + PqcRendezvousCrypto.MlDsa65PublicKeySize,
                PqcRendezvousCrypto.MlKem768PublicKeySize));
        int offset = FixedHeaderSize;
        userName.CopyTo(encoded, offset);
        offset += userName.Length;
        number.CopyTo(encoded, offset);
        offset += number.Length;
        stableId.CopyTo(encoded, offset);
        return encoded;
    }

    private static void ValidateFields(PqcContactCard card)
    {
        if (card.ExpiresAtUnixMilliseconds <= card.IssuedAtUnixMilliseconds ||
            card.ExpiresAtUnixMilliseconds - card.IssuedAtUnixMilliseconds >
            MaximumCardLifetime.TotalMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(card), "PQC contact-card lifetime is invalid.");
        }
        if (!NoksUserName.IsValid(card.UserName))
            throw new ArgumentException("PQC contact-card user name is invalid.", nameof(card));
        if (!NoksTemporaryNumber.IsCanonical(card.Number))
            throw new ArgumentException("PQC contact-card number is invalid.", nameof(card));
        if (!PqcRendezvousCrypto.IsValidMlDsa65PublicKey(card.SigningPublicKey.Span) ||
            !PqcRendezvousCrypto.IsValidMlKem768PublicKey(card.MailboxPublicKey.Span))
        {
            throw new ArgumentException("PQC contact-card public key is invalid.", nameof(card));
        }
        byte[] contactRoutingKey = CreateContactCardRoutingKey(card.SigningPublicKey.Span);
        try
        {
            string expectedStableId = CreateStableContactId(contactRoutingKey);
            if (!string.Equals(card.StableContactId, expectedStableId, StringComparison.Ordinal))
                throw new ArgumentException("PQC stable contact identifier is invalid.", nameof(card));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contactRoutingKey);
        }
    }

    private static byte[] BuildSignedData(ReadOnlySpan<byte> unsigned)
    {
        byte[] signedData = new byte[SignatureDomain.Length + unsigned.Length];
        SignatureDomain.CopyTo(signedData);
        unsigned.CopyTo(signedData.AsSpan(SignatureDomain.Length));
        return signedData;
    }

    private static byte[] CreateRoutingKey(
        ReadOnlySpan<byte> publicKey,
        ReadOnlySpan<byte> domain,
        int outputLength,
        byte prefix)
    {
        if (publicKey.IsEmpty)
            throw new ArgumentException("A PQC public key is required.", nameof(publicKey));
        byte[] material = new byte[domain.Length + publicKey.Length];
        domain.CopyTo(material);
        publicKey.CopyTo(material.AsSpan(domain.Length));
        try
        {
            byte[] hash = SHA256.HashData(material);
            if (outputLength == hash.Length)
                return hash;
            byte[] result = new byte[outputLength];
            result[0] = prefix;
            hash.CopyTo(result, 1);
            CryptographicOperations.ZeroMemory(hash);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }
}
