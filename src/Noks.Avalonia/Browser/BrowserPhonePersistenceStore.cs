#if BROWSER
using Noks.Dct3.State;
using Noks.Application.Persistence;

namespace Noks.AvaloniaApp.Browser;

internal sealed class BrowserPhonePersistenceStore : IPhonePersistenceStore
{
    public ValueTask<Dct3PersistenceSnapshot?> LoadAsync(string key, CancellationToken cancellationToken) =>
        BrowserPersistenceInterop.LoadAsync(key, cancellationToken);

    public ValueTask SaveAsync(string key, Dct3PersistenceSnapshot snapshot, CancellationToken cancellationToken) =>
        BrowserPersistenceInterop.SaveAsync(key, snapshot, cancellationToken);
}
#endif
