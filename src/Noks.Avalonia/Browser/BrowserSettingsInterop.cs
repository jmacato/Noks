#if BROWSER
using System.Runtime.InteropServices.JavaScript;

namespace Noks.AvaloniaApp.Browser;

internal static partial class BrowserSettingsInterop
{
    public const string ModuleName = "noks-settings";

    [JSImport("applyPhoneSettings", ModuleName)]
    internal static partial void ApplyPhoneSettings(string simImsi, string networkName);

    [JSImport("getBrowserCountry", ModuleName)]
    internal static partial string GetBrowserCountry();
}
#endif
