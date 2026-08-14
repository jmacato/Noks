#if BROWSER
using System.Runtime.InteropServices.JavaScript;

namespace Noks.AvaloniaApp.Browser;

internal static partial class BrowserProfileInterop
{
    public const string ModuleName = "noks-profile";

    [JSImport("loadProfile", ModuleName)]
    internal static partial Task<string?> LoadProfile();

    [JSImport("saveProfile", ModuleName)]
    internal static partial Task SaveProfile(string value);

    [JSImport("applyPendingDataReplacement", ModuleName)]
    internal static partial Task ApplyPendingDataReplacement();

    [JSImport("copyText", ModuleName)]
    internal static partial Task CopyText(string value);

    [JSImport("downloadJson", ModuleName)]
    internal static partial void DownloadJson(string fileName, string value);

    [JSImport("pickJsonFile", ModuleName)]
    internal static partial Task<string?> PickJsonFile();

    [JSImport("confirmDataImport", ModuleName)]
    internal static partial bool ConfirmDataImport();

    [JSImport("confirmFullReset", ModuleName)]
    internal static partial bool ConfirmFullReset();

    [JSImport("stageDataReplacementAndReload", ModuleName)]
    internal static partial void StageDataReplacementAndReload(string profileJson, bool clearAllProfiles);
}
#endif
