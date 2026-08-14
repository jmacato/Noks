#if BROWSER
using Noks.Application;

namespace Noks.AvaloniaApp.Browser;

internal sealed class BrowserWakuProfileStore : IWakuProfileStore
{
    public async ValueTask<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        string? value = await BrowserProfileInterop.LoadProfile();
        cancellationToken.ThrowIfCancellationRequested();
        return value;
    }

    public async ValueTask SaveAsync(string value, CancellationToken cancellationToken = default)
    {
        await BrowserProfileInterop.SaveProfile(value);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
#endif
