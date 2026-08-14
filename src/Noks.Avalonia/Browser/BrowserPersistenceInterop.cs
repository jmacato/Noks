#if BROWSER
using System.Runtime.InteropServices.JavaScript;
using Noks.Dct3.State;
using Noks.Application.Persistence;

namespace Noks.AvaloniaApp.Browser;

internal static partial class BrowserPersistenceInterop
{
    public const string ModuleName = "noks-persistence";

    [JSImport("loadText", ModuleName)]
    private static partial Task<string?> LoadText(string key);

    [JSImport("saveText", ModuleName)]
    private static partial Task SaveText(string key, string value);

    public static async ValueTask<Dct3PersistenceSnapshot?> LoadAsync(string key, CancellationToken cancellationToken)
    {
        string? text = await LoadText(key);
        cancellationToken.ThrowIfCancellationRequested();
        return text is null ? null : PhonePersistence.Deserialize(text);
    }

    public static async ValueTask SaveAsync(string key, Dct3PersistenceSnapshot snapshot, CancellationToken cancellationToken)
    {
        await SaveText(key, PhonePersistence.Serialize(snapshot)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
#endif
