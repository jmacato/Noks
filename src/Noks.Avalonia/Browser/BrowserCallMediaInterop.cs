#if BROWSER
using System.Runtime.InteropServices.JavaScript;

namespace Noks.AvaloniaApp.Browser;

internal static partial class BrowserCallMediaInterop
{
    public const string ModuleName = "noks-call-media";

    [JSImport("start", ModuleName)]
    internal static partial void Start(
        [JSMarshalAs<JSType.Function<JSType.String, JSType.Number, JSType.String>>]
        Action<string, int, string> eventHandler);

    [JSImport("begin", ModuleName)]
    internal static partial Task Begin(string attemptId, bool isCaller);

    [JSImport("activate", ModuleName)]
    internal static partial Task Activate(string attemptId);

    [JSImport("apply", ModuleName)]
    internal static partial Task Apply(
        string attemptId,
        int eventKind,
        string payloadBase64);

    [JSImport("end", ModuleName)]
    internal static partial Task End(string attemptId);

    [JSImport("reactivatePlayback", ModuleName)]
    internal static partial Task<bool> ReactivatePlayback();

    [JSImport("dispose", ModuleName)]
    internal static partial void Dispose();
}
#endif
