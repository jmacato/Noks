using Noks.Cryptography;
using Noks.Waku;
using Noks.Dct3.Sim;

namespace Noks.Application;

public sealed class WakuProfileManager : IAsyncDisposable
{
    private readonly IWakuProfileStore store;
    private readonly SemaphoreSlim saveLock = new(1, 1);
    private bool disposed;

    private WakuProfileManager(IWakuProfileStore store, WakuProfile profile)
    {
        this.store = store;
        Profile = profile;
    }

    public WakuProfile Profile { get; private set; }

    public event Action<WakuProfile>? ProfileChanged;

    public static async ValueTask<WakuProfileManager> LoadOrCreateAsync(
        IWakuProfileStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        string? encoded = await store.LoadAsync(cancellationToken);
        bool loaded = WakuProfileCodec.TryDeserialize(
            encoded,
            out WakuProfile? restored,
            out bool requiresSave);
        WakuProfile profile = loaded && restored is not null
            ? restored
            : WakuProfile.CreateNew();
        WakuProfileManager manager = new(store, profile);
        if (restored is null || requiresSave)
            await manager.SaveAsync(cancellationToken);
        return manager;
    }

    public async ValueTask UpdateUserNameAsync(string value, CancellationToken cancellationToken = default)
    {
        string normalized = NoksUserName.Normalize(value);
        Profile.SetUserName(normalized);
        await SaveAndNotifyAsync(cancellationToken);
    }

    public async ValueTask RestoreAsync(string recoveryPhrase, CancellationToken cancellationToken = default)
    {
        WakuProfile replacement = WakuProfile.Restore(recoveryPhrase);
        WakuProfile previous = Profile;
        Profile = replacement;
        try
        {
            await SaveAndNotifyAsync(cancellationToken);
            previous.Dispose();
        }
        catch
        {
            Profile = previous;
            replacement.Dispose();
            throw;
        }
    }

    public async ValueTask<string> RotatePhoneNumberAsync(CancellationToken cancellationToken = default)
    {
        string number;
        do
        {
            number = NoksTemporaryNumber.Generate();
        }
        while (string.Equals(number, Profile.PhoneNumber, StringComparison.Ordinal));
        Profile.RotatePhoneNumber(number);
        await SaveAndNotifyAsync(cancellationToken);
        return number;
    }

    public async ValueTask UpsertContactAsync(
        WakuProfileContact contact,
        string localNumber,
        CancellationToken cancellationToken = default)
    {
        Profile.UpsertContact(contact, localNumber);
        await SaveAndNotifyAsync(cancellationToken);
    }

    public async ValueTask RemoveBindingAsync(string localNumber, CancellationToken cancellationToken = default)
    {
        if (!Profile.RemoveBinding(localNumber))
            return;
        await SaveAndNotifyAsync(cancellationToken);
    }

    public async ValueTask SetDurableSimFileAsync(
        ushort parentFileId,
        ushort fileId,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (!Profile.SetDurableSimFile(parentFileId, fileId, data.Span))
            return;
        await SaveAsync(cancellationToken);
    }

    public async ValueTask ApplyCoherentSimMutationAsync(
        SimMutation mutation,
        IReadOnlyCollection<string> removedNumbers,
        WakuProfileContact? upsertContact,
        string? upsertLocalNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(removedNumbers);
        bool changed = Profile.ApplyDurableSimMutation(mutation);
        foreach (string removedNumber in removedNumbers)
            changed |= Profile.RemoveBinding(removedNumber);
        if (upsertContact is not null)
        {
            ArgumentNullException.ThrowIfNull(upsertLocalNumber);
            Profile.UpsertContact(upsertContact, upsertLocalNumber);
            changed = true;
        }
        if (!changed)
            return;
        await SaveAndNotifyAsync(cancellationToken);
    }

    internal async ValueTask<bool> TryRememberIncomingEventAsync(
        WakuApplicationMessage message,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!Profile.TryRememberEvent(
                message.EventId,
                message.ExpiresAtUnixMilliseconds,
                now.ToUnixTimeMilliseconds()))
        {
            return false;
        }
        await SaveAsync(cancellationToken);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        await saveLock.WaitAsync();
        try
        {
            Profile.Dispose();
        }
        finally
        {
            saveLock.Release();
            saveLock.Dispose();
        }
    }

    private async ValueTask SaveAndNotifyAsync(CancellationToken cancellationToken)
    {
        await SaveAsync(cancellationToken);
        ProfileChanged?.Invoke(Profile);
    }

    private async ValueTask SaveAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await saveLock.WaitAsync(cancellationToken);
        try
        {
            await store.SaveAsync(WakuProfileCodec.Serialize(Profile), cancellationToken);
        }
        finally
        {
            saveLock.Release();
        }
    }
}
