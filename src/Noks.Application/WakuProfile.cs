using System.Security.Cryptography;
using Noks.Cryptography;
using Noks.Waku;
using Noks.Dct3.Sim;

namespace Noks.Application;

public sealed class WakuProfile : IDisposable
{
    public const int CurrentVersion = 5;
    internal const int MaximumRememberedEvents = 4096;
    internal const ushort TelecomDirectoryFileId = 0x7F10;
    internal const ushort AdnFileId = 0x6F3A;
    internal const ushort SmsFileId = 0x6F3C;
    internal const int AdnFileLength = SimCard.AdnRecordCount * SimPhonebookCodec.RecordLength;
    internal const int SmsFileLength = SimCard.SmsStorageRecordCount * SimCard.SmsStorageRecordLength;

    private readonly byte[] entropy;
    private readonly List<WakuProfileContact> contacts;
    private readonly Dictionary<string, string> bindings;
    private readonly Dictionary<Guid, long> rememberedEvents;
    private byte[]? durableAdnFile;
    private byte[]? durableSmsFile;
    private WakuProfileKeys? keys;
    private PqcRendezvousIdentity? pqcRendezvousIdentity;
    private bool disposed;

    internal WakuProfile(
        ReadOnlySpan<byte> entropy,
        string userName,
        string phoneNumber,
        int phoneNumberGeneration,
        IEnumerable<WakuProfileContact>? contacts = null,
        IEnumerable<WakuNumberBinding>? bindings = null,
        IEnumerable<WakuRememberedEvent>? rememberedEvents = null,
        ReadOnlyMemory<byte>? durableAdnFile = null,
        ReadOnlyMemory<byte>? durableSmsFile = null)
    {
        if (entropy.Length != NoksRecoveryPhrase.EntropySize)
            throw new ArgumentException("Profile entropy must contain 32 bytes.", nameof(entropy));
        if (!SimPhonebookCodec.IsValidAlphaIdentifier(userName))
            throw new ArgumentException("User name is invalid.", nameof(userName));
        if (!NoksTemporaryNumber.IsCanonical(phoneNumber))
            throw new ArgumentException("Phone number is invalid.", nameof(phoneNumber));
        if (phoneNumberGeneration < 1)
            throw new ArgumentOutOfRangeException(nameof(phoneNumberGeneration));

        this.entropy = entropy.ToArray();
        UserName = userName;
        PhoneNumber = phoneNumber;
        PhoneNumberGeneration = phoneNumberGeneration;
        this.contacts = contacts?.ToList() ?? [];
        this.bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (WakuNumberBinding binding in bindings ?? [])
        {
            if (!NoksTemporaryNumber.IsCanonical(binding.LocalNumber) ||
                this.contacts.All(contact => contact.StableContactId != binding.StableContactId))
            {
                throw new ArgumentException("Profile contains an invalid number binding.", nameof(bindings));
            }
            this.bindings[binding.LocalNumber] = binding.StableContactId;
        }
        this.rememberedEvents = new Dictionary<Guid, long>();
        foreach (WakuRememberedEvent remembered in rememberedEvents ?? [])
        {
            if (remembered.EventId == Guid.Empty ||
                remembered.ExpiresAtUnixMilliseconds <= 0 ||
                this.rememberedEvents.Count >= MaximumRememberedEvents)
            {
                continue;
            }
            this.rememberedEvents.TryAdd(remembered.EventId, remembered.ExpiresAtUnixMilliseconds);
        }
        this.durableAdnFile = ValidateDurableSimFile(durableAdnFile, AdnFileLength, nameof(durableAdnFile));
        this.durableSmsFile = ValidateDurableSimFile(durableSmsFile, SmsFileLength, nameof(durableSmsFile));
    }

    public string UserName { get; private set; }

    public string PhoneNumber { get; private set; }

    public string FormattedPhoneNumber => NoksTemporaryNumber.Format(PhoneNumber);

    public int PhoneNumberGeneration { get; private set; }

    public string StableContactId => Keys.StableContactId;

    public string PqcStableContactId
    {
        get
        {
            byte[] routingKey = PqcContactCardCodec.CreateContactCardRoutingKey(
                GetPqcRendezvousIdentity().SigningPublicKey);
            try
            {
                return PqcContactCardCodec.CreateStableContactId(routingKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(routingKey);
            }
        }
    }

    public IReadOnlyList<WakuProfileContact> Contacts => contacts;

    public IReadOnlyList<WakuNumberBinding> NumberBindings =>
        bindings.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new WakuNumberBinding(pair.Key, pair.Value))
            .ToArray();

    internal IReadOnlyList<WakuRememberedEvent> RememberedEvents =>
        rememberedEvents.OrderBy(pair => pair.Key)
            .Select(pair => new WakuRememberedEvent(pair.Key, pair.Value))
            .ToArray();

    public ReadOnlyMemory<byte>? DurableAdnFile
    {
        get
        {
            if (durableAdnFile is null)
                return null;
            return new ReadOnlyMemory<byte>(durableAdnFile);
        }
    }

    public ReadOnlyMemory<byte>? DurableSmsFile
    {
        get
        {
            if (durableSmsFile is null)
                return null;
            return new ReadOnlyMemory<byte>(durableSmsFile);
        }
    }

    internal WakuProfileKeys Keys
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return keys ??= WakuProfileKeys.Create(entropy);
        }
    }

    internal PqcRendezvousIdentity GetPqcRendezvousIdentity() =>
        pqcRendezvousIdentity ??= PqcRendezvousCrypto.CreateIdentity(entropy);

    public string CreateRecoveryPhrase()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return NoksRecoveryPhrase.Encode(entropy);
    }

    public WakuProfileContact? FindContactByStableId(string stableContactId) =>
        contacts.FirstOrDefault(contact =>
            string.Equals(contact.StableContactId, stableContactId, StringComparison.Ordinal));

    public WakuProfileContact? FindContactByEnvelopeKey(ReadOnlySpan<byte> envelopePublicKey)
    {
        foreach (WakuProfileContact contact in contacts)
        {
            if (contact.MatchesEnvelopeKey(envelopePublicKey))
                return contact;
        }
        return null;
    }

    public WakuProfileContact? FindContactByLocalNumber(string localNumber)
    {
        if (!bindings.TryGetValue(localNumber, out string? stableContactId))
            return null;
        return FindContactByStableId(stableContactId);
    }

    public string? FindLocalNumberForStableId(string stableContactId) =>
        bindings.Where(pair => string.Equals(pair.Value, stableContactId, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();

    internal byte[] CopyEntropy()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return entropy.ToArray();
    }

    internal void SetUserName(string value)
    {
        if (!SimPhonebookCodec.IsValidAlphaIdentifier(value))
            throw new ArgumentException("User name must fit the 16-byte EF_ADN phonebook alphabet.", nameof(value));
        UserName = value;
    }

    internal void RotatePhoneNumber(string value)
    {
        if (!NoksTemporaryNumber.IsCanonical(value))
            throw new ArgumentException("Phone number is invalid.", nameof(value));
        PhoneNumber = value;
        PhoneNumberGeneration = checked(PhoneNumberGeneration + 1);
    }

    internal void UpsertContact(WakuProfileContact contact, string localNumber)
    {
        ArgumentNullException.ThrowIfNull(contact);
        if (!NoksTemporaryNumber.IsCanonical(localNumber))
            throw new ArgumentException("Local phonebook number is invalid.", nameof(localNumber));
        int existingIndex = contacts.FindIndex(candidate =>
            string.Equals(candidate.StableContactId, contact.StableContactId, StringComparison.Ordinal));
        if (existingIndex >= 0)
            contacts[existingIndex] = contact;
        else
            contacts.Add(contact);
        bindings[localNumber] = contact.StableContactId;
    }

    internal bool RemoveBinding(string localNumber)
    {
        if (!bindings.Remove(localNumber, out string? stableContactId))
            return false;
        if (!bindings.Values.Any(value => string.Equals(value, stableContactId, StringComparison.Ordinal)))
        {
            contacts.RemoveAll(contact =>
                string.Equals(contact.StableContactId, stableContactId, StringComparison.Ordinal));
        }
        return true;
    }

    internal bool SetDurableSimFile(ushort parentFileId, ushort fileId, ReadOnlySpan<byte> data)
    {
        if (parentFileId != TelecomDirectoryFileId)
            return false;
        ref byte[]? destination = ref GetDurableSimFileReference(fileId, out int expectedLength);
        if (data.Length != expectedLength)
            throw new ArgumentException("Durable SIM file has the wrong length.", nameof(data));
        if (destination is not null && data.SequenceEqual(destination))
            return false;
        destination = data.ToArray();
        return true;
    }

    internal bool ApplyDurableSimMutation(SimMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (mutation.ParentFileId != TelecomDirectoryFileId ||
            mutation.FileId is not (AdnFileId or SmsFileId))
        {
            return false;
        }

        ref byte[]? destination = ref GetDurableSimFileReference(mutation.FileId, out int expectedLength);
        if (mutation.RecordNumber == 0)
            return SetDurableSimFile(mutation.ParentFileId, mutation.FileId, mutation.NewValue.AsSpan());

        int recordLength = mutation.FileId == AdnFileId
            ? SimPhonebookCodec.RecordLength
            : SimCard.SmsStorageRecordLength;
        if (mutation.RecordNumber < 1 ||
            mutation.NewValue.Length != recordLength ||
            mutation.RecordNumber > expectedLength / recordLength)
        {
            return false;
        }

        destination ??= CreateBlankSimFile(expectedLength);
        Span<byte> record = destination.AsSpan((mutation.RecordNumber - 1) * recordLength, recordLength);
        if (mutation.NewValue.AsSpan().SequenceEqual(record))
            return false;
        mutation.NewValue.AsSpan().CopyTo(record);
        return true;
    }

    internal bool TryRememberEvent(Guid eventId, long expiresAtUnixMilliseconds, long nowUnixMilliseconds)
    {
        if (eventId == Guid.Empty || expiresAtUnixMilliseconds <= nowUnixMilliseconds)
            return false;
        foreach (Guid expiredEventId in rememberedEvents
                     .Where(pair => pair.Value <= nowUnixMilliseconds)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            rememberedEvents.Remove(expiredEventId);
        }
        if (rememberedEvents.ContainsKey(eventId) || rememberedEvents.Count >= MaximumRememberedEvents)
            return false;
        rememberedEvents.Add(eventId, expiresAtUnixMilliseconds);
        return true;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        keys?.Dispose();
        CryptographicOperations.ZeroMemory(entropy);
        disposed = true;
    }

    private ref byte[]? GetDurableSimFileReference(ushort fileId, out int expectedLength)
    {
        if (fileId == AdnFileId)
        {
            expectedLength = AdnFileLength;
            return ref durableAdnFile;
        }
        if (fileId == SmsFileId)
        {
            expectedLength = SmsFileLength;
            return ref durableSmsFile;
        }
        throw new ArgumentOutOfRangeException(nameof(fileId), fileId, "Only EF_ADN and EF_SMS are durable Waku files.");
    }

    private static byte[]? ValidateDurableSimFile(
        ReadOnlyMemory<byte>? data,
        int expectedLength,
        string parameterName)
    {
        if (data is null)
            return null;
        if (data.Value.Length != expectedLength)
            throw new ArgumentException("Durable SIM file has the wrong length.", parameterName);
        return data.Value.ToArray();
    }

    internal static byte[] CreateBlankSimFile(int length)
    {
        byte[] data = new byte[length];
        Array.Fill(data, (byte)0xFF);
        return data;
    }

    public static WakuProfile CreateNew()
    {
        byte[] entropy = NoksRecoveryPhrase.GenerateEntropy();
        try
        {
            return new WakuProfile(
                entropy,
                NoksUserName.GenerateInitial(entropy),
                NoksTemporaryNumber.Generate(),
                phoneNumberGeneration: 1);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    public static WakuProfile Restore(string recoveryPhrase)
    {
        byte[] entropy = NoksRecoveryPhrase.Decode(recoveryPhrase);
        try
        {
            return new WakuProfile(
                entropy,
                NoksUserName.GenerateInitial(entropy),
                NoksTemporaryNumber.Generate(),
                phoneNumberGeneration: 1);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }
}
