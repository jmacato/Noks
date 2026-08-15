using Noks.Cryptography;
using Noks.Waku;
using Noks.Dct3.Sim;
using Noks.Dct3.State;

namespace Noks.Application.Tests;

public sealed class WakuSimStateReconcilerTests
{
    [Fact]
    public async Task DurableWakuFilesReplaceMissingPhoneSnapshot()
    {
        MemoryStore store = new();
        await using WakuProfileManager manager = await WakuProfileManager.LoadOrCreateAsync(store);
        byte[] adn = Blank(WakuProfile.AdnFileLength);
        byte[] sms = Blank(WakuProfile.SmsFileLength);
        byte[] contact = SimPhonebookCodec.Encode("Receiver", "1234567890123");
        contact.CopyTo(adn, 0);
        sms[0] = 0x03;
        sms[1] = 0x00;
        await manager.SetDurableSimFileAsync(0x7F10, 0x6F3A, adn);
        await manager.SetDurableSimFileAsync(0x7F10, 0x6F3C, sms);

        Dct3PersistenceSnapshot restored = await WakuSimStateReconciler.ReconcileAsync(
            manager,
            Dct3PersistenceSnapshot.Empty);

        Assert.Equal(adn, Find(restored, 0x6F3A));
        Assert.Equal(sms, Find(restored, 0x6F3C));
    }

    [Fact]
    public async Task ExistingPhoneFilesMigrateIntoWakuAuthority()
    {
        MemoryStore store = new();
        await using WakuProfileManager manager = await WakuProfileManager.LoadOrCreateAsync(store);
        byte[] adn = Blank(WakuProfile.AdnFileLength);
        byte[] sms = Blank(WakuProfile.SmsFileLength);
        SimPhonebookCodec.Encode("Legacy", "1234567890123").CopyTo(adn, 0);
        sms[0] = 0x01;
        Dct3PersistenceSnapshot legacy = new(
            Dct3PersistenceSnapshot.CurrentVersion,
            [],
            [new(0x7F10, 0x6F3A, adn), new(0x7F10, 0x6F3C, sms)]);

        Dct3PersistenceSnapshot migrated = await WakuSimStateReconciler.ReconcileAsync(manager, legacy);

        Assert.Equal(adn, manager.Profile.DurableAdnFile?.ToArray());
        Assert.Equal(sms, manager.Profile.DurableSmsFile?.ToArray());
        Assert.Equal(adn, Find(migrated, 0x6F3A));
        Assert.Equal(sms, Find(migrated, 0x6F3C));
        Assert.Contains("durableSimFiles", store.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructuredWakuContactRecreatesEmptyAdn()
    {
        MemoryStore store = new();
        await using WakuProfileManager manager = await WakuProfileManager.LoadOrCreateAsync(store);
        WakuProfileContact contact = CreateContact("1234567890123");
        await manager.UpsertContactAsync(contact, contact.CurrentNumber);

        Dct3PersistenceSnapshot restored = await WakuSimStateReconciler.ReconcileAsync(
            manager,
            Dct3PersistenceSnapshot.Empty);

        byte[] adn = Find(restored, 0x6F3A);
        Assert.True(SimPhonebookCodec.TryDecode(
            adn.AsSpan(0, SimPhonebookCodec.RecordLength),
            out string name,
            out string number));
        Assert.Equal(contact.UserName, name);
        Assert.Equal(contact.CurrentNumber, number);
    }

    [Fact]
    public async Task CoherentSmsRecordWriteIsImmediatelyDurable()
    {
        MemoryStore store = new();
        await using WakuProfileManager manager = await WakuProfileManager.LoadOrCreateAsync(store);
        byte[] record = Enumerable.Repeat((byte)0xFF, SimCard.SmsStorageRecordLength).ToArray();
        record[0] = 0x03;
        SimMutation mutation = new(
            0x7F10,
            0x6F3C,
            7,
            Blank(SimCard.SmsStorageRecordLength),
            record,
            SimMutationOrigin.Firmware);

        await manager.ApplyCoherentSimMutationAsync(mutation, [], null, null);

        Assert.True(manager.Profile.DurableSmsFile.HasValue);
        ReadOnlyMemory<byte> durable = manager.Profile.DurableSmsFile.Value;
        Assert.Equal(
            record,
            durable.Span.Slice(6 * SimCard.SmsStorageRecordLength, SimCard.SmsStorageRecordLength).ToArray());
        Assert.Contains("durableSimFiles", store.Value, StringComparison.Ordinal);
    }

    private static byte[] Blank(int length)
    {
        byte[] value = new byte[length];
        Array.Fill(value, (byte)0xFF);
        return value;
    }

    private static byte[] Find(Dct3PersistenceSnapshot snapshot, ushort fileId) =>
        Assert.Single(snapshot.SimFiles, file => file.Parent == 0x7F10 && file.Id == fileId).Data;

    private static WakuProfileContact CreateContact(string number)
    {
        byte[] entropy = Enumerable.Repeat((byte)0x47, NoksRecoveryPhrase.EntropySize).ToArray();
        using WakuProfileKeys keys = WakuProfileKeys.Create(entropy);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return WakuProfileContact.FromValidatedCard(ContactCardV2Codec.CreateSigned(
            keys,
            "durable-peer-47",
            number,
            now,
            now.AddMinutes(1)));
    }

    private sealed class MemoryStore : IWakuProfileStore
    {
        public string? Value { get; private set; }

        public ValueTask<string?> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Value);

        public ValueTask SaveAsync(string value, CancellationToken cancellationToken = default)
        {
            Value = value;
            return ValueTask.CompletedTask;
        }
    }
}
