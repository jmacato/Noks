#if BROWSER
using System.Runtime.InteropServices.JavaScript;

namespace Noks.AvaloniaApp.Browser;

internal static partial class BrowserVibrationInterop
{
    public const string ModuleName = "noks-vibration";

    [JSImport("update", ModuleName)]
    internal static partial void Update(bool enabled, int control);

    [JSImport("dispose", ModuleName)]
    internal static partial void Dispose();
}
#endif
