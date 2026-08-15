using Noks.Cryptography;
using Noks.Waku;
using Noks.Dct3.Sim;

namespace Noks.Application.Tests;

public sealed class WakuProfileManagerTests
{
    [Fact]
    public async Task LoadEditAndRoundTripKeepIdentityAndMetadata()
    {
        MemoryStore store = new();
        await using WakuProfileManager manager = await WakuProfileManager.LoadOrCreateAsync(store);
        string originalStableId = manager.Profile.StableContactId;
        string phrase = manager.Profile.CreateRecoveryPhrase();
        Assert.Equal(1, store.SaveCount);

        await manager.UpdateUserNameAsync("quiet-river-1234");
        WakuProfileContact contact = CreateContact("1234567890123");
        await manager.UpsertContactAsync(contact, "1234567890123");
        Assert.DoesNotContain(phrase, store.Value, StringComparison.Ordinal);

        await using WakuProfileManager reloaded = await WakuProfileManager.LoadOrCreateAsync(store);
        Assert.Equal(originalStableId, reloaded.Profile.StableContactId);
        Assert.Equal("quiet-river-1234", reloaded.Profile.UserName);
        WakuProfileContact? reloadedContact =
            reloaded.Profile.FindContactByLocalNumber("1234567890123");
        Assert.Equal(contact.StableContactId, reloadedContact?.StableContactId);
        Assert.Equal(contact.PqcMailboxPublicKey, reloadedContact?.PqcMailboxPublicKey);
        Assert.Equal(contact.PqcSigningPublicKey, reloadedContact?.PqcSigningPublicKey);
        Assert.Equal(phrase, reloaded.Profile.CreateRecoveryPhrase());
    }

    [Fact]
    public async Task RestoreReproducesKeysAndInitialNameButNotContactsOrNumber()
    {
        MemoryStore sourceStore = new();
        await using WakuProfileManager source = await WakuProfileManager.LoadOrCreateAsync(sourceStore);
        string phrase = source.Profile.CreateRecoveryPhrase();
        string stableId = source.Profile.StableContactId;
        string initialName = source.Profile.UserName;
        string oldNumber = source.Profile.PhoneNumber;
        await source.UpdateUserNameAsync("edited-name-42");
        await source.UpsertContactAsync(CreateContact("1234567890123"), "1234567890123");

        MemoryStore targetStore = new();
        await using WakuProfileManager target = await WakuProfileManager.LoadOrCreateAsync(targetStore);
        await target.RestoreAsync(phrase);

        Assert.Equal(stableId, target.Profile.StableContactId);
        Assert.Equal(initialName, target.Profile.UserName);
        Assert.NotEqual(oldNumber, target.Profile.PhoneNumber);
        Assert.Empty(target.Profile.Contacts);
        Assert.Empty(target.Profile.NumberBindings);
    }

    [Fact]
    public async Task InvalidOrLegacyStateCreatesFreshCurrentProfile()
    {
        MemoryStore store = new() { Value = "{\"version\":0,\"privateKey\":\"legacy-p256\"}" };
        await using WakuProfileManager manager = await WakuProfileManager.LoadOrCreateAsync(store);
        Assert.True(NoksUserName.IsValid(manager.Profile.UserName));
        Assert.True(NoksTemporaryNumber.IsCanonical(manager.Profile.PhoneNumber));
        Assert.Equal(1, store.SaveCount);
        Assert.Contains($"\"version\":{WakuProfile.CurrentVersion}", store.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionOneProfileMigratesIdentityAndNameToThirteenDigitNumber()
    {
        byte[] entropy = Enumerable.Range(0, NoksRecoveryPhrase.EntropySize)
            .Select(value => (byte)value)
            .ToArray();
        Guid rememberedEventId = Guid.Parse("2df45544-70f8-4ea3-811a-b6df2289894f");
        MemoryStore store = new()
        {
            Value = $$"""
                {"version":1,"entropy":"{{Convert.ToBase64String(entropy)}}","userName":"edited-name-42","phoneNumber":"123456789012345","phoneNumberGeneration":4,"contacts":[{"legacy":"ignored"}],"bindings":[{"legacy":"ignored"}],"rememberedEvents":[{"eventId":"{{rememberedEventId}}","expiresAtUnixMilliseconds":1900000000000}]}
                """,
        };

        await using WakuProfileManager manager = await WakuProfileManager.LoadOrCreateAsync(store);

        Assert.Equal(NoksRecoveryPhrase.Encode(entropy), manager.Profile.CreateRecoveryPhrase());
        Assert.Equal("edited-name-42", manager.Profile.UserName);
        Assert.True(NoksTemporaryNumber.IsCanonical(manager.Profile.PhoneNumber));
        Assert.Equal(5, manager.Profile.PhoneNumberGeneration);
        Assert.Empty(manager.Profile.Contacts);
        Assert.Empty(manager.Profile.NumberBindings);
        Assert.Equal(1, store.SaveCount);
        Assert.Contains($"\"version\":{WakuProfile.CurrentVersion}", store.Value, StringComparison.Ordinal);
        Assert.Contains(rememberedEventId.ToString(), store.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("123456789012345", store.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RememberedIncomingEventIsRejectedAfterProfileReload()
    {
        MemoryStore store = new();
        DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
        WakuApplicationMessage message = CreateMessage(now);

        await using (WakuProfileManager manager = await WakuProfileManager.LoadOrCreateAsync(store))
        {
            Assert.True(await manager.TryRememberIncomingEventAsync(message, now));
            Assert.False(await manager.TryRememberIncomingEventAsync(message, now));
        }

        await using WakuProfileManager reloaded = await WakuProfileManager.LoadOrCreateAsync(store);
        Assert.False(await reloaded.TryRememberIncomingEventAsync(message, now));
        Assert.Contains(message.EventId.ToString(), store.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DurableSimFilesRoundTripWithWakuProfile()
    {
        MemoryStore store = new();
        byte[] adn = Enumerable.Repeat(
            (byte)0xFF,
            SimCard.AdnRecordCount * SimPhonebookCodec.RecordLength).ToArray();
        byte[] sms = Enumerable.Repeat(
            (byte)0xFF,
            SimCard.SmsStorageRecordCount * SimCard.SmsStorageRecordLength).ToArray();
        SimPhonebookCodec.Encode("Receiver", "1234567890123").CopyTo(adn, 0);
        sms[0] = 0x03;

        await using (WakuProfileManager manager = await WakuProfileManager.LoadOrCreateAsync(store))
        {
            await manager.SetDurableSimFileAsync(0x7F10, 0x6F3A, adn);
            await manager.SetDurableSimFileAsync(0x7F10, 0x6F3C, sms);
        }

        await using WakuProfileManager reloaded = await WakuProfileManager.LoadOrCreateAsync(store);
        Assert.Equal(adn, reloaded.Profile.DurableAdnFile?.ToArray());
        Assert.Equal(sms, reloaded.Profile.DurableSmsFile?.ToArray());
    }

    [Fact]
    public async Task JsonBackupRoundTripsTheCompleteWakuProfile()
    {
        MemoryStore store = new();
        byte[] adn = Enumerable.Repeat(
            (byte)0xFF,
            SimCard.AdnRecordCount * SimPhonebookCodec.RecordLength).ToArray();
        byte[] sms = Enumerable.Repeat(
            (byte)0xFF,
            SimCard.SmsStorageRecordCount * SimCard.SmsStorageRecordLength).ToArray();
        SimPhonebookCodec.Encode("Receiver", "1234567890123").CopyTo(adn, 0);
        sms[0] = 0x03;

        await using WakuProfileManager manager = await WakuProfileManager.LoadOrCreateAsync(store);
        await manager.UpdateUserNameAsync("backup-owner-42");
        await manager.UpsertContactAsync(CreateContact("1234567890123"), "1234567890123");
        await manager.SetDurableSimFileAsync(0x7F10, 0x6F3A, adn);
        await manager.SetDurableSimFileAsync(0x7F10, 0x6F3C, sms);

        DateTimeOffset exportedAt = DateTimeOffset.Parse("2026-07-18T12:34:56Z");
        string backup = WakuProfileBackupCodec.Serialize(manager.Profile, exportedAt);

        Assert.Contains("\"format\": \"noks-waku-data\"", backup, StringComparison.Ordinal);
        Assert.Contains("\"exportedAtUtc\": \"2026-07-18T12:34:56+00:00\"", backup, StringComparison.Ordinal);
        Assert.True(WakuProfileBackupCodec.TryDeserialize(backup, out WakuProfile? restored));
        using (restored)
        {
            Assert.NotNull(restored);
            Assert.Equal(WakuProfileCodec.Serialize(manager.Profile), WakuProfileCodec.Serialize(restored));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"format\":\"noks-waku-data\",\"version\":2,\"exportedAtUtc\":\"2026-07-18T12:34:56Z\",\"profile\":{}}")]
    [InlineData("{\"format\":\"another-app\",\"version\":1,\"exportedAtUtc\":\"2026-07-18T12:34:56Z\",\"profile\":{}}")]
    public void JsonBackupRejectsInvalidOrUnsupportedData(string value)
    {
        Assert.False(WakuProfileBackupCodec.TryDeserialize(value, out WakuProfile? profile));
        Assert.Null(profile);
    }

    private static WakuProfileContact CreateContact(string number)
    {
        byte[] entropy = Enumerable.Repeat((byte)0x91, NoksRecoveryPhrase.EntropySize).ToArray();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PqcContactCard card = PqcContactCardCodec.CreateSigned(
            PqcRendezvousCrypto.CreateIdentity(entropy),
            "bright-beacon-ab12",
            number,
            now,
            now.AddMinutes(1));
        return WakuProfileContact.FromValidatedPqcCard(card);
    }

    private static WakuApplicationMessage CreateMessage(DateTimeOffset now)
    {
        byte[] entropy = Enumerable.Repeat((byte)0x62, NoksRecoveryPhrase.EntropySize).ToArray();
        using WakuProfileKeys keys = WakuProfileKeys.Create(entropy);
        return new WakuApplicationMessage(
            Guid.NewGuid(),
            WakuEventKind.Sms,
            now.AddMinutes(-1).ToUnixTimeMilliseconds(),
            now.AddDays(1).ToUnixTimeMilliseconds(),
            keys.EnvelopePublicKey.Span,
            keys.MailboxPublicKey.Span,
            "remember me"u8);
    }

    private sealed class MemoryStore : IWakuProfileStore
    {
        public string? Value { get; set; }

        public int SaveCount { get; private set; }

        public ValueTask<string?> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Value);
        }

        public ValueTask SaveAsync(string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Value = value;
            SaveCount++;
            return ValueTask.CompletedTask;
        }
    }
}
