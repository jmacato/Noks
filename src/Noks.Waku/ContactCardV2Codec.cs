using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Noks.Cryptography;

namespace Noks.Waku;

public static class ContactCardV2Codec
{
    public const byte Version = 2;
    public static readonly TimeSpan MaximumCardLifetime = TimeSpan.FromMinutes(2);
    private const int FixedHeaderSize = 126;
    private const int MaximumSignatureSize = 72;
    private const int StableContactIdLength = 24;
    private const byte PqcExtensionMarker = 0xff;
    private const byte PqcExtensionVersion = 1;
    private const int PqcExtensionHeaderSize = 2;
    private const int PqcExtensionSize =
        PqcExtensionHeaderSize + PqcRendezvousCrypto.MlKem768PublicKeySize;
    private const byte NullTerminatedUtf8UserName = 1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static ReadOnlySpan<byte> Magic => "NCV2"u8;
    private static ReadOnlySpan<byte> SignatureDomain => "Noks ContactCardV2\0"u8;

    public static ContactCardV2 CreateSigned(
        WakuProfileKeys keys,
        string userName,
        string number,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        ReadOnlySpan<byte> pqcMailboxPublicKey = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        string normalizedNumber = NoksPhoneNumber.Normalize(number);
        ContactCardV2 unsignedCard = new(
            WakuProfileKeys.KeyGeneration,
            keys.StableContactId,
            userName,
            normalizedNumber,
            issuedAt.ToUnixTimeMilliseconds(),
            expiresAt.ToUnixTimeMilliseconds(),
            keys.ContactCardPublicKey.Span,
            keys.EnvelopePublicKey.Span,
            keys.MailboxPublicKey.Span,
            pqcMailboxPublicKey,
            []);
        ValidateFields(unsignedCard);
        byte[] unsigned = EncodeUnsigned(unsignedCard);
        byte[] signedData = BuildSignedData(unsigned);
        try
        {
            byte[] signature = WakuCrypto.SignSecp256k1Sha256(keys.ContactCardPrivateKey.Span, signedData);
            try
            {
                return new ContactCardV2(
                    unsignedCard.KeyGeneration,
                    unsignedCard.StableContactId,
                    unsignedCard.UserName,
                    unsignedCard.Number,
                    unsignedCard.IssuedAtUnixMilliseconds,
                    unsignedCard.ExpiresAtUnixMilliseconds,
                    unsignedCard.ContactCardPublicKey.Span,
                    unsignedCard.EnvelopePublicKey.Span,
                    unsignedCard.MailboxPublicKey.Span,
                    unsignedCard.PqcMailboxPublicKey.Span,
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
        }
    }

    public static byte[] Encode(ContactCardV2 card)
    {
        ArgumentNullException.ThrowIfNull(card);
        ValidateFields(card);
        if (card.Signature.Length is < 8 or > MaximumSignatureSize)
            throw new ArgumentException("Contact card signature is missing or too large.", nameof(card));
        byte[] unsigned = EncodeUnsigned(card);
        byte[] encoded = new byte[unsigned.Length + 1 + card.Signature.Length];
        unsigned.CopyTo(encoded, 0);
        encoded[unsigned.Length] = checked((byte)card.Signature.Length);
        card.Signature.Span.CopyTo(encoded.AsSpan(unsigned.Length + 1));
        CryptographicOperations.ZeroMemory(unsigned);
        return encoded;
    }

    public static bool TryDecode(ReadOnlySpan<byte> encoded, out ContactCardV2? card) =>
        TryDecodeCore(encoded, out card, out _);

    public static bool TryValidate(
        ReadOnlySpan<byte> encoded,
        DateTimeOffset now,
        int expectedKeyGeneration,
        out ContactCardV2? card,
        out ContactCardValidationFailure failure,
        ReadOnlySpan<byte> expectedEnvelopePublicKey = default,
        ReadOnlySpan<byte> expectedMailboxPublicKey = default,
        ReadOnlySpan<byte> expectedPqcMailboxPublicKey = default)
    {
        if (!TryDecodeCore(encoded, out card, out bool unsupportedVersion) || card is null)
        {
            failure = unsupportedVersion
                ? ContactCardValidationFailure.UnsupportedVersion
                : ContactCardValidationFailure.Malformed;
            return false;
        }
        if (card.KeyGeneration != expectedKeyGeneration)
        {
            failure = ContactCardValidationFailure.InvalidGeneration;
            card = null;
            return false;
        }
        long nowMilliseconds = now.ToUnixTimeMilliseconds();
        if (card.ExpiresAtUnixMilliseconds <= card.IssuedAtUnixMilliseconds ||
            card.ExpiresAtUnixMilliseconds - card.IssuedAtUnixMilliseconds > MaximumCardLifetime.TotalMilliseconds ||
            nowMilliseconds < card.IssuedAtUnixMilliseconds - TimeSpan.FromMinutes(1).TotalMilliseconds ||
            nowMilliseconds >= card.ExpiresAtUnixMilliseconds)
        {
            failure = ContactCardValidationFailure.InvalidTime;
            card = null;
            return false;
        }
        string expectedId = WakuProfileKeys.CreateStableContactId(card.ContactCardPublicKey.Span);
        if (!string.Equals(expectedId, card.StableContactId, StringComparison.Ordinal))
        {
            failure = ContactCardValidationFailure.InvalidStableId;
            card = null;
            return false;
        }

        int unsignedLength = encoded.Length - 1 - card.Signature.Length;
        byte[] signedData = BuildSignedData(encoded[..unsignedLength]);
        try
        {
            if (!WakuCrypto.VerifySecp256k1Sha256(card.ContactCardPublicKey.Span, signedData, card.Signature.Span))
            {
                failure = ContactCardValidationFailure.InvalidSignature;
                card = null;
                return false;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signedData);
        }

        if (!expectedEnvelopePublicKey.IsEmpty && !card.BindsEnvelopeKey(expectedEnvelopePublicKey))
        {
            failure = ContactCardValidationFailure.WrongEnvelopeBinding;
            card = null;
            return false;
        }
        if (!expectedMailboxPublicKey.IsEmpty && !card.BindsMailboxKey(expectedMailboxPublicKey))
        {
            failure = ContactCardValidationFailure.WrongMailboxBinding;
            card = null;
            return false;
        }
        if (!expectedPqcMailboxPublicKey.IsEmpty && !card.BindsPqcMailboxKey(expectedPqcMailboxPublicKey))
        {
            failure = ContactCardValidationFailure.WrongMailboxBinding;
            card = null;
            return false;
        }

        failure = ContactCardValidationFailure.None;
        return true;
    }

    private static byte[] EncodeUnsigned(ContactCardV2 card)
    {
        byte[] userName = StrictUtf8.GetBytes(card.UserName);
        byte[] number = Encoding.ASCII.GetBytes(card.Number);
        byte[] stableId = Encoding.ASCII.GetBytes(card.StableContactId);
        int pqcExtensionLength = card.HasPqcMailboxKey ? PqcExtensionSize : 0;
        byte[] encoded = new byte[
            FixedHeaderSize + userName.Length + 1 + number.Length + stableId.Length + pqcExtensionLength];
        Magic.CopyTo(encoded);
        encoded[4] = Version;
        encoded[5] = checked((byte)card.KeyGeneration);
        BinaryPrimitives.WriteInt64BigEndian(encoded.AsSpan(8, 8), card.IssuedAtUnixMilliseconds);
        BinaryPrimitives.WriteInt64BigEndian(encoded.AsSpan(16, 8), card.ExpiresAtUnixMilliseconds);
        encoded[24] = 0;
        encoded[25] = checked((byte)number.Length);
        encoded[26] = checked((byte)stableId.Length);
        encoded[27] = NullTerminatedUtf8UserName;
        card.ContactCardPublicKey.Span.CopyTo(encoded.AsSpan(28, WakuCrypto.Secp256k1CompressedPublicKeySize));
        card.EnvelopePublicKey.Span.CopyTo(encoded.AsSpan(61, WakuCrypto.Secp256k1CompressedPublicKeySize));
        card.MailboxPublicKey.Span.CopyTo(encoded.AsSpan(94, WakuCrypto.X25519KeySize));
        int offset = FixedHeaderSize;
        userName.CopyTo(encoded, offset);
        offset += userName.Length;
        encoded[offset++] = 0;
        number.CopyTo(encoded, offset);
        offset += number.Length;
        stableId.CopyTo(encoded, offset);
        offset += stableId.Length;
        if (card.HasPqcMailboxKey)
        {
            encoded[offset++] = PqcExtensionMarker;
            encoded[offset++] = PqcExtensionVersion;
            card.PqcMailboxPublicKey.Span.CopyTo(
                encoded.AsSpan(offset, PqcRendezvousCrypto.MlKem768PublicKeySize));
        }
        return encoded;
    }

    private static bool TryDecodeCore(
        ReadOnlySpan<byte> encoded,
        out ContactCardV2? card,
        out bool unsupportedVersion)
    {
        card = null;
        unsupportedVersion = encoded.Length >= 5 && encoded[..3].SequenceEqual("NCV"u8) && encoded[4] != Version;
        if (encoded.Length < FixedHeaderSize + 1 || !encoded[..4].SequenceEqual(Magic) || encoded[4] != Version ||
            encoded[6] != 0 || encoded[7] != 0 || encoded[24] != 0 ||
            encoded[27] != NullTerminatedUtf8UserName)
            return false;

        int numberLength = encoded[25];
        int stableIdLength = encoded[26];
        if (stableIdLength != StableContactIdLength)
            return false;
        int maximumTerminatedNameLength = encoded.Length - FixedHeaderSize - numberLength - stableIdLength - 1 - 8;
        if (maximumTerminatedNameLength < 2)
            return false;
        int scannedNameLength = Math.Min(maximumTerminatedNameLength, NoksUserName.MaximumUtf8Length + 1);
        int userNameLength = encoded.Slice(FixedHeaderSize, scannedNameLength).IndexOf((byte)0);
        if (userNameLength is < 1 or > NoksUserName.MaximumUtf8Length)
            return false;
        int unsignedLength = FixedHeaderSize + userNameLength + 1 + numberLength + stableIdLength;
        ReadOnlySpan<byte> pqcMailboxPublicKey = default;
        if (encoded.Length > unsignedLength && encoded[unsignedLength] == PqcExtensionMarker)
        {
            if (encoded.Length < unsignedLength + PqcExtensionSize + 1 ||
                encoded[unsignedLength + 1] != PqcExtensionVersion)
            {
                return false;
            }
            pqcMailboxPublicKey = encoded.Slice(
                unsignedLength + PqcExtensionHeaderSize,
                PqcRendezvousCrypto.MlKem768PublicKeySize);
            unsignedLength += PqcExtensionSize;
        }
        if (encoded.Length <= unsignedLength)
            return false;
        int signatureLength = encoded[unsignedLength];
        if (signatureLength is < 8 or > MaximumSignatureSize || encoded.Length != unsignedLength + 1 + signatureLength)
            return false;

        try
        {
            int offset = FixedHeaderSize;
            string userName = StrictUtf8.GetString(encoded.Slice(offset, userNameLength));
            offset += userNameLength + 1;
            string number = Encoding.ASCII.GetString(encoded.Slice(offset, numberLength));
            offset += numberLength;
            string stableId = Encoding.ASCII.GetString(encoded.Slice(offset, stableIdLength));
            ContactCardV2 decoded = new(
                encoded[5],
                stableId,
                userName,
                number,
                BinaryPrimitives.ReadInt64BigEndian(encoded.Slice(8, 8)),
                BinaryPrimitives.ReadInt64BigEndian(encoded.Slice(16, 8)),
                encoded.Slice(28, WakuCrypto.Secp256k1CompressedPublicKeySize),
                encoded.Slice(61, WakuCrypto.Secp256k1CompressedPublicKeySize),
                encoded.Slice(94, WakuCrypto.X25519KeySize),
                pqcMailboxPublicKey,
                encoded.Slice(unsignedLength + 1, signatureLength));
            ValidateFields(decoded);
            card = decoded;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void ValidateFields(ContactCardV2 card)
    {
        if (card.KeyGeneration is < 1 or > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(card), "Contact card key generation is invalid.");
        if (card.ExpiresAtUnixMilliseconds <= card.IssuedAtUnixMilliseconds ||
            card.ExpiresAtUnixMilliseconds - card.IssuedAtUnixMilliseconds > MaximumCardLifetime.TotalMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(card), "Contact card lifetime must be positive and no longer than two minutes.");
        if (!NoksUserName.IsValid(card.UserName))
            throw new ArgumentException("Contact card user name is invalid.", nameof(card));
        if (!NoksPhoneNumber.TryNormalize(card.Number, out string normalizedNumber) ||
            !string.Equals(card.Number, normalizedNumber, StringComparison.Ordinal))
            throw new ArgumentException("Contact card number is not canonical.", nameof(card));
        if (card.StableContactId.Length != StableContactIdLength ||
            card.StableContactId.Any(character =>
                character is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-' and not '_'))
            throw new ArgumentException("Stable contact identifier is invalid.", nameof(card));
        if (card.ContactCardPublicKey.Length != WakuCrypto.Secp256k1CompressedPublicKeySize ||
            card.EnvelopePublicKey.Length != WakuCrypto.Secp256k1CompressedPublicKeySize ||
            card.MailboxPublicKey.Length != WakuCrypto.X25519KeySize ||
            card.PqcMailboxPublicKey.Length is not (0 or PqcRendezvousCrypto.MlKem768PublicKeySize))
            throw new ArgumentException("Contact card public key sizes are invalid.", nameof(card));
        if (CryptographicOperations.FixedTimeEquals(card.ContactCardPublicKey.Span, card.EnvelopePublicKey.Span))
            throw new ArgumentException("Contact-card and envelope keys must be separate.", nameof(card));
    }

    private static byte[] BuildSignedData(ReadOnlySpan<byte> unsignedCard)
    {
        byte[] data = new byte[SignatureDomain.Length + unsignedCard.Length];
        SignatureDomain.CopyTo(data);
        unsignedCard.CopyTo(data.AsSpan(SignatureDomain.Length));
        return data;
    }
}
