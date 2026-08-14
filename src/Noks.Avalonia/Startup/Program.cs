using Noks.Application;
using Noks.Waku;
using Avalonia;
using Avalonia.Media;
using Noks.Dct3.State;
using Noks.AvaloniaApp.Emulation;
using Noks.AvaloniaApp.Views;
using Noks.Waku.Transport;
using Noks.Application.Persistence;

namespace Noks.AvaloniaApp.Startup;

public static class Program
{
    public static void Main(string[] args)
    {
        string flashPath = FlashPathResolver.Resolve(args);
        byte[] firmware = File.ReadAllBytes(flashPath);
        string? externalEepromPath = FlashPathResolver.ResolveExternalEeprom(args, flashPath);
        byte[]? externalEeprom = externalEepromPath is null ? null : File.ReadAllBytes(externalEepromPath);

        Dct3PhoneSettings settings = PhoneSettingsParser.Parse(args);
        settings = SessionOperatorResolver
            .ResolveAsync(firmware, args, settings, SessionOperatorResolver.GetLocaleCountry)
            .GetAwaiter()
            .GetResult();

        string? dataDirOverride = Environment.GetEnvironmentVariable("NOKS_DATA_DIR");
        FileWakuProfileStore profileStore = dataDirOverride is null
            ? FileWakuProfileStore.Default
            : new FileWakuProfileStore(Path.Combine(dataDirOverride, "profile.json"));
        WakuProfileManager profileManager = WakuProfileManager
            .LoadOrCreateAsync(profileStore, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        settings = settings with { OwnPhoneNumber = profileManager.Profile.PhoneNumber };
        Libp2pWakuTransport wakuTransport = new();
        WakuPhoneBridgeOptions wakuOptions = WakuPhoneBridgeOptions.Default with
        {
            EnablePostQuantumRendezvous = true,
            RequirePostQuantumRendezvous = true,
        };
        if (Environment.GetEnvironmentVariable("NOKS_WAKU_DIAGNOSTICS") == "1")
        {
            wakuTransport.DiagnosticsChanged += diagnostics =>
            {
                string detail = diagnostics.RecentEvents.Count > 0
                    ? diagnostics.RecentEvents[^1].Details
                    : "";
                Console.WriteLine(
                    $"Noks waku: {diagnostics.LastEvent} phase={diagnostics.Phase} peers={diagnostics.PeerCount} " +
                    $"detail=\"{detail}\" lastError={diagnostics.LastError}");
            };
        }

        FilePhonePersistenceStore persistenceStore = dataDirOverride is null
            ? FilePhonePersistenceStore.Default
            : new FilePhonePersistenceStore(Path.Combine(dataDirOverride, "persistence"));
        string persistenceKey = PhonePersistence.CreateProfileKey(profileManager.Profile.PqcStableContactId);
        string legacyPersistenceKey = PhonePersistence.CreateKey(firmware, settings);
        Dct3PersistenceSnapshot? storedSnapshot = persistenceStore
            .LoadAsync(persistenceKey, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        bool migratedLegacySnapshot = false;
        if (storedSnapshot is null)
        {
            storedSnapshot = persistenceStore.LoadAsync(legacyPersistenceKey, CancellationToken.None).GetAwaiter().GetResult();
            migratedLegacySnapshot = storedSnapshot is not null;
        }
        Dct3PersistenceSnapshot persistenceSnapshot = WakuSimStateReconciler
            .ReconcileAsync(profileManager, storedSnapshot, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        persistenceStore.SaveAsync(persistenceKey, persistenceSnapshot, CancellationToken.None).GetAwaiter().GetResult();
        PhonePersistenceSession persistence = new(persistenceKey, persistenceStore, persistenceSnapshot);
        Console.WriteLine(
            $"Noks desktop persistence: key={persistenceKey} migratedLegacy={migratedLegacySnapshot} " +
            $"flashBlocks={persistenceSnapshot.FlashBlocks.Length} simFiles={persistenceSnapshot.SimFiles.Length}");

        App.CreateMainView = _ => new MainView(
            firmware,
            args,
            persistence,
            settings,
            profileManager,
            wakuTransport,
            wakuOptions,
            externalEeprom);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();
}
