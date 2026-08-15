using Avalonia;
using Avalonia.Browser;
using Noks.Application;
using Noks.AvaloniaApp;
using Noks.Waku;
#if BROWSER
using System.Runtime.InteropServices.JavaScript;
using Noks.Dct3.State;
using Noks.AvaloniaApp.Emulation;
using Noks.AvaloniaApp.Startup;
using Noks.AvaloniaApp.Views;
using Noks.Waku.Transport;
using Noks.Application.Persistence;
#endif

namespace Noks.AvaloniaApp.Browser;

internal static class Program
{
    private const string AssetBaseArgumentPrefix = "--asset-base=";
    private const string CacheBustArgumentPrefix = "--cache-bust=";
    private const string FirmwareArgumentPrefix = "--firmware-base64=";

    private static async Task Main(string[] args)
    {
        byte[] firmware = LoadDefaultFirmware(args);
        Console.WriteLine($"Noks browser firmware: bytes={firmware.Length} hash={Fnv1A(firmware):X8}");
        Dct3PhoneSettings settings = PhoneSettingsParser.Parse(args);
        string assetBasePath = NormalizeAssetBasePath(TryGetArgument(args, AssetBaseArgumentPrefix));
        string? cacheBustVersion = TryGetArgument(args, CacheBustArgumentPrefix);
        string[] appArgs = args
            .Where(arg => !arg.StartsWith(AssetBaseArgumentPrefix, StringComparison.Ordinal))
            .Where(arg => !arg.StartsWith(FirmwareArgumentPrefix, StringComparison.Ordinal))
            .Where(arg => !arg.StartsWith(CacheBustArgumentPrefix, StringComparison.Ordinal))
            .Where(arg => !string.Equals(arg, "--classic-rendezvous", StringComparison.Ordinal))
            .ToArray();
#if BROWSER
        await JSHost.ImportAsync(
            BrowserSettingsInterop.ModuleName,
            VersionedAssetPath(assetBasePath, "settings.js", cacheBustVersion));
        settings = await SessionOperatorResolver.ResolveAsync(
            firmware,
            appArgs,
            settings,
            BrowserSettingsInterop.GetBrowserCountry);
#else
        settings = await SessionOperatorResolver.ResolveAsync(
            firmware,
            appArgs,
            settings,
            SessionOperatorResolver.GetLocaleCountry);
#endif
        Console.WriteLine(
            $"Noks browser configuration: sim={settings.SimImsi ?? "auto"} network=\"{settings.EffectiveNetworkName}\"");
#if BROWSER
        await JSHost.ImportAsync(
            BrowserAudioInterop.ModuleName,
            VersionedAssetPath(assetBasePath, "audio.js", cacheBustVersion));
        await JSHost.ImportAsync(
            BrowserVibrationInterop.ModuleName,
            VersionedAssetPath(assetBasePath, "vibration.js", cacheBustVersion));
        await JSHost.ImportAsync(
            BrowserPersistenceInterop.ModuleName,
            VersionedAssetPath(assetBasePath, "persistence.js", cacheBustVersion));
        await JSHost.ImportAsync(
            BrowserProfileInterop.ModuleName,
            VersionedAssetPath(assetBasePath, "profile.js", cacheBustVersion));
        await JSHost.ImportAsync(
            BrowserCallMediaInterop.ModuleName,
            VersionedAssetPath(assetBasePath, "call-media.js", cacheBustVersion));

        await BrowserProfileInterop.ApplyPendingDataReplacement();

        WakuProfileManager profileManager = await WakuProfileManager.LoadOrCreateAsync(
            new BrowserWakuProfileStore(),
            CancellationToken.None);
        settings = settings with { OwnPhoneNumber = profileManager.Profile.PhoneNumber };
        Libp2pWakuTransport wakuTransport = new();
        WakuPhoneBridgeOptions wakuOptions = WakuPhoneBridgeOptions.Default with
        {
            EnablePostQuantumRendezvous = true,
            RequirePostQuantumRendezvous = true,
        };

        BrowserPhonePersistenceStore persistenceStore = new();
        string persistenceKey = PhonePersistence.CreateProfileKey(profileManager.Profile.PqcStableContactId);
        string legacyPersistenceKey = PhonePersistence.CreateKey(firmware, settings);
        Dct3PersistenceSnapshot? storedSnapshot =
            await persistenceStore.LoadAsync(persistenceKey, CancellationToken.None);
        bool migratedLegacySnapshot = false;
        if (storedSnapshot is null)
        {
            storedSnapshot = await persistenceStore.LoadAsync(legacyPersistenceKey, CancellationToken.None);
            migratedLegacySnapshot = storedSnapshot is not null;
        }
        Dct3PersistenceSnapshot persistenceSnapshot = await WakuSimStateReconciler.ReconcileAsync(
            profileManager,
            storedSnapshot,
            CancellationToken.None);
        await persistenceStore.SaveAsync(persistenceKey, persistenceSnapshot, CancellationToken.None);
        PhonePersistenceSession persistence = new(persistenceKey, persistenceStore, persistenceSnapshot);
        Console.WriteLine(
            $"Noks browser persistence: key={persistenceKey} migratedLegacy={migratedLegacySnapshot} " +
            $"flashBlocks={persistenceSnapshot.FlashBlocks.Length} simFiles={persistenceSnapshot.SimFiles.Length}");
        App.CreateMainView = _ => new MainView(
            firmware,
            appArgs,
            persistence,
            settings,
            profileManager,
            wakuTransport,
            wakuOptions);
#else
        App.CreateMainView = _ => new MainView(firmware, appArgs, settings: settings);
#endif

        try
        {
            await BuildAvaloniaApp().StartBrowserAppAsync(
                "out",
                new BrowserPlatformOptions
                {
                    PreferManagedThreadDispatcher = true,
                    FrameworkAssetPathResolver = fileName => ResolveFrameworkAsset(fileName, cacheBustVersion),
                    RenderingMode = [
                        BrowserRenderingMode.WebGL2,
                        BrowserRenderingMode.Software2D,
                        BrowserRenderingMode.WebGL1,
                    ],
                });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Noks Avalonia startup failed: {ex}");
            throw;
        }
    }

    private static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .WithInterFont();
    }

    private static string ResolveFrameworkAsset(string fileName, string? cacheBustVersion)
    {
        return string.Equals(fileName, "avalonia.js", StringComparison.Ordinal)
            ? VersionedModulePath("../avalonia-adaptive.js", cacheBustVersion)
            : $"./{fileName}";
    }

    private static byte[] LoadDefaultFirmware(IReadOnlyList<string> args)
    {
        string? encoded = TryGetArgument(args, FirmwareArgumentPrefix);

        if (encoded is null)
        {
            throw new InvalidOperationException("Missing bundled firmware argument.");
        }

        return Convert.FromBase64String(encoded);
    }

    private static string? TryGetArgument(IEnumerable<string> args, string prefix)
    {
        string? encoded = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.Ordinal));
        return encoded?[prefix.Length..];
    }

    private static string VersionedModulePath(string path, string? cacheBustVersion)
    {
        return string.IsNullOrWhiteSpace(cacheBustVersion)
            ? path
            : $"{path}?v={Uri.EscapeDataString(cacheBustVersion)}";
    }

    private static string VersionedAssetPath(
        string basePath,
        string fileName,
        string? cacheBustVersion) =>
        VersionedModulePath($"{basePath}{fileName}", cacheBustVersion);

    private static string NormalizeAssetBasePath(string? path)
    {
        if (string.IsNullOrEmpty(path) ||
            path[0] != '/' ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            path.Contains('?') ||
            path.Contains('#'))
        {
            return "/";
        }

        return path.EndsWith('/') ? path : $"{path}/";
    }

    private static uint Fnv1A(byte[] bytes)
    {
        uint hash = 2166136261;

        foreach (byte value in bytes)
        {
            hash = (hash ^ value) * 16777619;
        }

        return hash;
    }
}
