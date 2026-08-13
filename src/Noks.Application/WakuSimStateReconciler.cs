using Noks.Dct3.Sim;
using Noks.Dct3.State;

namespace Noks.Application;

public static class WakuSimStateReconciler
{
    // The projection copies the Waku state before the baseband and firmware start.
    // Thus, EF_ADN and EF_SMS are authoritative at the first read.
    // The standard phone can rebuild its EEPROM and PMM indexes without an unsafe live-flash restore.
    public static async ValueTask<Dct3PersistenceSnapshot> ReconcileAsync(
        WakuProfileManager profiles,
        Dct3PersistenceSnapshot? phoneSnapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        Dct3PersistenceSnapshot snapshot = phoneSnapshot?.Version == Dct3PersistenceSnapshot.CurrentVersion
            ? phoneSnapshot
            : Dct3PersistenceSnapshot.Empty;

        byte[]? snapshotAdn = FindFile(snapshot, WakuProfile.TelecomDirectoryFileId, WakuProfile.AdnFileId);
        byte[]? snapshotSms = FindFile(snapshot, WakuProfile.TelecomDirectoryFileId, WakuProfile.SmsFileId);
        if (profiles.Profile.DurableAdnFile is null)
        {
            byte[]? initialAdn = snapshotAdn ?? BuildAdnFromContacts(profiles.Profile);
            if (initialAdn is not null)
            {
                await profiles.SetDurableSimFileAsync(
                    WakuProfile.TelecomDirectoryFileId,
                    WakuProfile.AdnFileId,
                    initialAdn,
                    cancellationToken);
            }
        }
        if (profiles.Profile.DurableSmsFile is null && snapshotSms is not null)
        {
            await profiles.SetDurableSimFileAsync(
                WakuProfile.TelecomDirectoryFileId,
                WakuProfile.SmsFileId,
                snapshotSms,
                cancellationToken);
        }

        List<SimFileOverlay> files = snapshot.SimFiles
            .Where(file => file.Parent != WakuProfile.TelecomDirectoryFileId ||
                file.Id is not (WakuProfile.AdnFileId or WakuProfile.SmsFileId))
            .ToList();
        if (profiles.Profile.DurableAdnFile.HasValue)
        {
            files.Add(new SimFileOverlay(
                WakuProfile.TelecomDirectoryFileId,
                WakuProfile.AdnFileId,
                profiles.Profile.DurableAdnFile.Value.ToArray()));
        }
        if (profiles.Profile.DurableSmsFile.HasValue)
        {
            files.Add(new SimFileOverlay(
                WakuProfile.TelecomDirectoryFileId,
                WakuProfile.SmsFileId,
                profiles.Profile.DurableSmsFile.Value.ToArray()));
        }
        return snapshot with { SimFiles = files.ToArray() };
    }

    private static byte[]? FindFile(Dct3PersistenceSnapshot snapshot, ushort parent, ushort id) =>
        snapshot.SimFiles.LastOrDefault(file => file.Parent == parent && file.Id == id)?.Data;

    private static byte[]? BuildAdnFromContacts(WakuProfile profile)
    {
        if (profile.NumberBindings.Count == 0)
            return null;
        byte[] file = WakuProfile.CreateBlankSimFile(WakuProfile.AdnFileLength);
        int record = 0;
        foreach (WakuNumberBinding binding in profile.NumberBindings)
        {
            if (record >= SimCard.OrdinaryAdnRecordCount)
                break;
            WakuProfileContact? contact = profile.FindContactByStableId(binding.StableContactId);
            if (contact is null)
                continue;
            SimPhonebookCodec.Encode(contact.UserName, binding.LocalNumber)
                .CopyTo(file, record * SimPhonebookCodec.RecordLength);
            record++;
        }
        return file;
    }
}
