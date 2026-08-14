using Avalonia;
using Avalonia.Animation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text;
using Noks.Application;
using Noks.Waku;
using Noks.AvaloniaApp.ViewModels;
using PhonePath = Avalonia.Controls.Shapes.Path;
using Noks.Dct3.Audio;
using Noks.Dct3.Core;
using Noks.Dct3.Messaging;
using Noks.Dct3.Peripherals;
using Noks.Dct3.Radio;
using Noks.Dct3.Sim;
using Noks.Dct3.State;
using Noks.AvaloniaApp.Audio;
#if BROWSER
using Noks.AvaloniaApp.Browser;
#endif
using Noks.AvaloniaApp.Controls;
using Noks.AvaloniaApp.Emulation;
using Noks.AvaloniaApp.Messaging;
using Noks.AvaloniaApp.Startup;
using Noks.Application.Input;
using Noks.Application.Persistence;

namespace Noks.AvaloniaApp.Views;

public sealed class MainView : UserControl
{
    private static readonly FontFamily EmbeddedFontFamily = FontFamily.Parse("fonts:Inter#Inter");
    private const int MaximumLogEntries = 10_000;
    internal PhoneEmulator Emulator => emulator;
    private static readonly IBrush WindowBackground = new SolidColorBrush(Color.Parse("#111312"));
    private static readonly IBrush SurfaceBrush = new SolidColorBrush(Color.Parse("#202322"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#f4f6f2"));
    private static readonly IBrush MutedTextBrush = new SolidColorBrush(Color.Parse("#aeb7b0"));
    private static readonly IBrush KeyLegendOffBrush = new SolidColorBrush(Color.Parse("#424645"));
    private static readonly IBrush KeyLegendOnBrush = new SolidColorBrush(Color.Parse("#dfff91"));
    private static readonly IBrush ConfirmLegendOffBrush = new SolidColorBrush(Color.Parse("#62b4c2"));
    private static readonly IBrush ConfirmLegendOnBrush = new SolidColorBrush(Color.Parse("#91e7df"));
    private static readonly IBrush MainKeyInnerBrush = CreateVerticalGradient("#f5f6f1", "#a4a59f");
    private static readonly IBrush CancelKeyInnerBrush = CreateGradient(
        "#e5dbd3",
        "#a2a39e",
        new RelativePoint(0, 0.277714337, RelativeUnit.Relative),
        new RelativePoint(1, 0.5, RelativeUnit.Relative));
    private static readonly IBrush DirectionKeyInnerBrush = CreateGradient(
        "#c8c9c1",
        "#747467",
        new RelativePoint(0.927670144, 0.404988624, RelativeUnit.Relative),
        new RelativePoint(0, 0.760602257, RelativeUnit.Relative));
    private static readonly IBrush NumericKeyEdgeBrush = CreateVerticalGradient("#484c4f", "#727273");
    private static readonly IBrush NumericKeyInnerBrush = CreateVerticalGradient("#e8e6d2", "#7a817c");
    private static readonly IBrush PressedKeyOverlayBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Color.Parse("#76000000"), 0),
            new GradientStop(Color.Parse("#28000000"), 0.48),
            new GradientStop(Color.Parse("#34ffffff"), 1),
        ],
    };
    private static readonly IBrush PressedKeyEdgeBrush = new SolidColorBrush(Color.Parse("#80151718"));
    private static readonly IPen PressedKeyEdgePen = new Pen(PressedKeyEdgeBrush, 2);
    private static readonly IBrush BadgeOnBrush = new SolidColorBrush(Color.Parse("#ddff78"));
    private static readonly IBrush BadgeOffBrush = new SolidColorBrush(Color.Parse("#343b36"));
    private static readonly IBrush BadgeNeutralBrush = new SolidColorBrush(Color.Parse("#2b312e"));
    private static readonly IBrush BadgeOnTextBrush = new SolidColorBrush(Color.Parse("#111606"));
    private static readonly IBrush BadgeOffTextBrush = new SolidColorBrush(Color.Parse("#b8c1b8"));
    private static readonly IBrush BadgeNeutralBorderBrush = new SolidColorBrush(Color.Parse("#566056"));
    private static readonly IBrush BadgeOverrideBorderBrush = new SolidColorBrush(Color.Parse("#f4f6f2"));
    private static readonly IBrush BadgeResetBorderBrush = new SolidColorBrush(Color.Parse("#73f6d2"));
    private static readonly IBrush ErrorTextBrush = new SolidColorBrush(Color.Parse("#ff806f"));
    private const ushort DefaultChargerVoltage = 0x2E0;
    private const long OverrideResetHoldMilliseconds = 650;
    private const long OverrideResetFlashMilliseconds = 700;
#if BROWSER
    private const long BrowserVibrationPulseMilliseconds = 140;
    private const int MaximumBrowserCallSignalBase64Length = 400_000;
#endif
    private PhoneEmulator emulator;
    private readonly Func<Dct3PhoneSettings, ValueTask<PhoneEmulator>> recreateEmulator;
    private readonly LcdControl lcd;
    private readonly TextBlock runtimeText;
    private readonly TextBlock vibrationText;
    private readonly TextBlock ccontPwmText;
    private readonly TextBlock dspRssiText;
    private readonly TextBlock gsmStateText;
    private readonly TextBlock firmwareBatteryText;
    private readonly TextBlock firmwarePowerStateText;
    private readonly TextBlock firmwareThresholdText;
    private readonly TextBlock telemetryText;
    private readonly Border runtimeBadge;
    private readonly Border vibrationBadge;
    private readonly Border ccontPwmBadge;
    private readonly Border gsmStateBadge;
    private readonly Border firmwareBatteryBadge;
    private readonly Border firmwarePowerStateBadge;
    private readonly Border firmwareThresholdBadge;
    private readonly ToggleButton telemetryToggle;
    private readonly ToggleButton lcdBacklightToggle;
    private readonly ToggleButton keypadBacklightToggle;
    private readonly ToggleButton chargerPresentToggle;
    private readonly ToggleButton audioMuteToggle;
    private readonly Button speakerReactivateButton;
    private readonly TextBox simImsiBox;
    private readonly TextBox networkNameBox;
    private readonly TextBox incomingNumberBox;
    private readonly TextBox incomingSmsTextBox;
    private readonly TextBox ringtoneNameBox;
    private readonly TextBox ringtoneTempoBox;
    private readonly TextBox ringtoneNotationBox;
    private readonly TextBlock phoneSettingsStatusText;
    private readonly TextBlock ringtoneStatusText;
    private readonly Button applyPhoneSettingsButton;
    private readonly Button incomingCallButton;
    private readonly Button incomingSmsButton;
    private readonly Button incomingRingtoneButton;
    private readonly Slider dspRssiSlider;
    private readonly PhoneKeyFaceControl powerButton;
    private readonly PhoneKeyFaceControl directionKeySurface;
    private readonly ComboBox logFilterBox;
    private readonly ToggleButton logVisibilityToggle;
    private readonly ToggleButton logPauseToggle;
    private readonly Button logClearButton;
    private readonly TextBox userNameBox;
    private readonly TextBlock myNumberText;
    private readonly TextBlock networkStatusText;
    private readonly TextBlock pqcRendezvousText;
    private readonly ToggleButton pqcRendezvousToggle;
    private readonly TextBlock wakuDiagnosticsSummaryText;
    private readonly TextBlock wakuDiagnosticsDetailText;
    private readonly TextBox wakuDiagnosticsLogBox;
    private readonly Button saveUserNameButton;
    private readonly Button copyNumberButton;
    private readonly Button backUpButton;
    private readonly Button restoreButton;
    private readonly Button exportWakuDataButton;
    private readonly Button importWakuDataButton;
    private readonly Button resetAllDataButton;
    private readonly Button applyRecoveryButton;
    private readonly Button cancelRecoveryButton;
    private readonly Button recheckWakuButton;
    private readonly Button copyWakuDiagnosticsButton;
    private readonly TextBox recoveryPhraseBox;
    private readonly TextBlock recoveryStatusText;
    private readonly TextBlock dataManagementStatusText;
    private readonly Border recoveryPanel;
    private readonly ListBox logList;
    private readonly AvaloniaList<EmulationLogEntry> visibleLogEntries = new(MaximumLogEntries);
    private readonly List<EmulationLogEntry> logHistory = [];
    private readonly TranslateTransform shakeTransform = new();
    private readonly Dictionary<CcontAdcChannel, CcontAdcControl> ccontAdcControls = [];
    private readonly DispatcherTimer resetFlashTimer;
    private readonly WakuProfileManager? profileManager;
    private readonly IWakuTransport? wakuTransport;
    private readonly IWakuTransportDiagnostics? wakuDiagnostics;
    private readonly WakuPhoneBridge? wakuBridge;
    private readonly PhoneInputState inputState = new();
    private readonly Dictionary<long, long> pointerPressedAtMillisecondsByPointerId = [];
    private readonly Dictionary<long, PointerType> pointerTypeByPointerId = [];
    private readonly Dictionary<PhoneKey, PhoneKeyFaceControl> phoneKeyControls = [];
    private Grid? layoutRoot;
    private Border? controlPanelFrame;
    private Grid? controlOverlayHost;
    private ToggleButton? controlOverlayButton;
    private bool? lcdBacklightOverride;
    private bool? keypadBacklightOverride;
    private bool syncingControlPanel;
    private long lcdBacklightPressedAtMilliseconds;
    private long keypadBacklightPressedAtMilliseconds;
    private long lcdBacklightResetAtMilliseconds;
    private long keypadBacklightResetAtMilliseconds;
    private long lastInvalidatedLcdDataWrites = -1;
    private bool? lastInvalidatedLcdBacklightOn;
    private int lcdRefreshQueued;
    private bool lcdBitmapUpdateQueued;
    private int audioRefreshQueued;
    private int stateRefreshQueued;
    private int telemetryRefreshQueued;
    private int logRefreshQueued;
    private Mad2PeripheralState? lastPeripheralUiState;
    private CcontControlState? lastCcontUiState;
    private GsmControlState? lastGsmUiState;
    private DspRadioControlState? lastDspRadioUiState;
    private PhoneTelemetryState? lastTelemetryUiState;
    private bool? lastLcdBacklightUiState;
    private bool? lastKeypadBacklightUiState;
    private bool? lastLcdResetFlashUiState;
    private bool? lastKeypadResetFlashUiState;
    private bool dspRssiEditing;
    private IPhoneAudio? audio;
    private bool audioMuted = Environment.GetEnvironmentVariable("NOKS_MUTE_AUDIO") == "1";
    private bool logPaused;
    private bool logEnabled;
    private bool logTailScrollQueued;
    private bool restoringProfile;
    private bool runtimeStarted;
    private ScrollViewer? logScrollViewer;
    private CancellationTokenSource? vibrationAnimationCancellation;
    private Border? displayFrame;
    private Border? telemetryPanelFrame;
#if BROWSER
    private bool browserVibrationActive;
    private int browserVibrationLastControl = -1;
    private long browserVibrationNextPulseAtMilliseconds;
    private readonly DispatcherTimer browserVibrationTimer;
    private Action<string, int, string>? browserCallMediaEventHandler;
#endif

    public MainView(IReadOnlyList<string> args)
        : this(args, null)
    {
    }

    public MainView(IReadOnlyList<string> args, Dct3PhoneSettings? settings)
        : this(
            CreateDesktopEmulator(args, settings),
            nextSettings => new ValueTask<PhoneEmulator>(Task.Run(() => CreateDesktopEmulator(args, nextSettings))))
    {
    }

    public MainView(
        byte[] flashImage,
        IReadOnlyList<string> args,
        PhonePersistenceSession? persistence = null,
        Dct3PhoneSettings? settings = null,
        WakuProfileManager? profileManager = null,
        IWakuTransport? wakuTransport = null,
        WakuPhoneBridgeOptions? wakuOptions = null,
        byte[]? externalEepromImage = null)
        : this(
            CreateEmulator(flashImage, externalEepromImage, ParseScheduledKeys(args), persistence, settings ?? ParsePhoneSettings(args)),
            nextSettings => CreateEmulatorAsync(
                flashImage,
                externalEepromImage,
                ParseScheduledKeys(args),
                persistence?.Store,
                persistence?.Key,
                profileManager,
                nextSettings),
            profileManager,
            wakuTransport,
            wakuOptions)
    {
#if BROWSER
        BrowserInteractionLatencyBenchmark.Configure(args);
        BrowserFrameBenchmark.TryAttach(this, args);
#endif
    }

    private MainView(
        PhoneEmulator emulator,
        Func<Dct3PhoneSettings, ValueTask<PhoneEmulator>> recreateEmulator,
        WakuProfileManager? profileManager = null,
        IWakuTransport? wakuTransport = null,
        WakuPhoneBridgeOptions? wakuOptions = null)
    {
        FontFamily = EmbeddedFontFamily;
        this.emulator = emulator;
        emulator.FrameChanged += OnEmulatorFrameChanged;
        emulator.AudioStateChanged += OnEmulatorAudioStateChanged;
        emulator.AudioAnnouncementAvailable += OnEmulatorAudioAnnouncementAvailable;
        emulator.CallTransitionAvailable += OnEmulatorCallTransitionAvailable;
        emulator.StateChanged += OnEmulatorStateChanged;
        this.recreateEmulator = recreateEmulator;
        this.profileManager = profileManager;
        this.wakuTransport = wakuTransport;
        wakuDiagnostics = wakuTransport as IWakuTransportDiagnostics;
        if (profileManager is not null && wakuTransport is not null)
        {
            wakuBridge = new WakuPhoneBridge(profileManager, wakuTransport, options: wakuOptions);
            AttachBridgeToEmulator(emulator);
            wakuBridge.CommandAvailable += OnBridgeCommandAvailable;
            wakuBridge.StatusChanged += OnBridgeStatusChanged;
            profileManager.ProfileChanged += OnProfileChanged;
        }
#if BROWSER
        if (wakuBridge is not null)
        {
            browserCallMediaEventHandler = OnBrowserCallMediaEvent;
            BrowserCallMediaInterop.Start(browserCallMediaEventHandler);
        }
#endif
        DataContext = new MainViewModel("Noks");
        lcd = new LcdControl(emulator);
        runtimeText = CreateBadgeText();
        vibrationText = CreateBadgeText();
        ccontPwmText = CreateBadgeText();
        dspRssiText = CreateBadgeText();
        gsmStateText = CreateBadgeText();
        firmwareBatteryText = CreateBadgeText();
        firmwarePowerStateText = CreateBadgeText();
        firmwareThresholdText = CreateBadgeText();
        telemetryText = CreateTelemetryText();
        dspRssiText.Text = FormatByteValue(DspRadioControlState.Default.Rssi);
        dspRssiText.Foreground = TextBrush;
        gsmStateText.Text = "NO NET";
        runtimeBadge = CreateIndicatorBadge(runtimeText);
        vibrationBadge = CreateIndicatorBadge(vibrationText);
        ccontPwmBadge = CreateIndicatorBadge(ccontPwmText);
        gsmStateBadge = CreateIndicatorBadge(gsmStateText);
        firmwareBatteryBadge = CreateIndicatorBadge(firmwareBatteryText);
        firmwarePowerStateBadge = CreateIndicatorBadge(firmwarePowerStateText);
        firmwareThresholdBadge = CreateIndicatorBadge(firmwareThresholdText);
        telemetryToggle = CreateLightToggle();
        pqcRendezvousToggle = CreateLightToggle();
        pqcRendezvousToggle.IsEnabled = wakuBridge?.PostQuantumRendezvousRequired != true;
        lcdBacklightToggle = CreateLightToggle();
        keypadBacklightToggle = CreateLightToggle();
        chargerPresentToggle = CreateLightToggle();
        audioMuteToggle = CreateLightToggle();
        speakerReactivateButton = CreatePillButton("SPEAKER");
        simImsiBox = CreateCompactTextBox(emulator.Settings.SimImsi ?? "");
        networkNameBox = CreateCompactTextBox(emulator.Settings.EffectiveNetworkName);
        phoneSettingsStatusText = CreateBadgeText();
        phoneSettingsStatusText.Foreground = MutedTextBrush;
        applyPhoneSettingsButton = CreatePillButton("APPLY");
        incomingNumberBox = CreateCompactTextBox("12345");
        incomingSmsTextBox = CreateCompactTextBox("Hello from Noks");
        ringtoneNameBox = CreateCompactTextBox(NokiaSmartMessagingRingtone.DemoRingtoneName);
        ringtoneTempoBox = CreateCompactTextBox("140");
        ringtoneNotationBox = CreateCompactTextBox(NokiaSmartMessagingRingtone.DemoRingtoneNotation);
        ringtoneNotationBox.AcceptsReturn = true;
        ringtoneNotationBox.TextWrapping = TextWrapping.Wrap;
        ringtoneNotationBox.Height = 70;
        ringtoneNotationBox.VerticalContentAlignment = VerticalAlignment.Top;
        ringtoneStatusText = CreateBadgeText();
        ringtoneStatusText.Foreground = BadgeOnTextBrush;
        ringtoneStatusText.Text = "READY";
        incomingCallButton = CreatePillButton("CALL");
        incomingSmsButton = CreatePillButton("SMS");
        incomingRingtoneButton = CreatePillButton("TONE");
        dspRssiSlider = CreateDspRssiSlider();
        powerButton = CreatePhoneKeyFaceControl(PhoneKey.Power);
        directionKeySurface = CreateDirectionKeySurface();
        logFilterBox = CreateLogFilterBox();
        logVisibilityToggle = CreateLightToggle();
        logPauseToggle = CreateLightToggle();
        logClearButton = CreatePillButton("CLEAR");
        userNameBox = CreateCompactTextBox(profileManager?.Profile.UserName ?? "");
        myNumberText = CreateBadgeText();
        myNumberText.Foreground = TextBrush;
        networkStatusText = CreateBadgeText();
        networkStatusText.Foreground = MutedTextBrush;
        pqcRendezvousText = CreateBadgeText();
        UpdatePqcRendezvousUi(wakuBridge?.PostQuantumRendezvousEnabled == true);
        wakuDiagnosticsSummaryText = new TextBlock
        {
            Text = "Waiting for the Waku transport",
            Foreground = TextBrush,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        wakuDiagnosticsDetailText = new TextBlock
        {
            Text = "No transport telemetry yet.",
            Foreground = MutedTextBrush,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 6),
        };
        wakuDiagnosticsLogBox = CreateCompactTextBox("");
        wakuDiagnosticsLogBox.IsReadOnly = true;
        wakuDiagnosticsLogBox.AcceptsReturn = true;
        wakuDiagnosticsLogBox.TextWrapping = TextWrapping.NoWrap;
        wakuDiagnosticsLogBox.Height = 116;
        wakuDiagnosticsLogBox.VerticalContentAlignment = VerticalAlignment.Top;
        saveUserNameButton = CreatePillButton("Save");
        saveUserNameButton.Focusable = true;
        copyNumberButton = CreatePillButton("Copy");
        backUpButton = CreatePillButton("Back up");
        restoreButton = CreatePillButton("Restore");
        exportWakuDataButton = CreatePillButton("Export JSON");
        importWakuDataButton = CreatePillButton("Import JSON");
        resetAllDataButton = CreatePillButton("Reset all");
        applyRecoveryButton = CreatePillButton("Restore");
        cancelRecoveryButton = CreatePillButton("Cancel");
        recheckWakuButton = CreatePillButton("Recheck");
        copyWakuDiagnosticsButton = CreatePillButton("Copy diagnostics");
        recoveryPhraseBox = CreateCompactTextBox("");
        recoveryPhraseBox.AcceptsReturn = true;
        recoveryPhraseBox.TextWrapping = TextWrapping.Wrap;
        recoveryPhraseBox.Height = 92;
        recoveryPhraseBox.VerticalContentAlignment = VerticalAlignment.Top;
        recoveryStatusText = new TextBlock
        {
            Text = "Noks recovery phrase — do not enter into a cryptocurrency wallet.",
            Foreground = MutedTextBrush,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        };
        dataManagementStatusText = new TextBlock
        {
            Text = "JSON contains the private Waku identity. Store it securely.",
            Foreground = MutedTextBrush,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        recoveryPanel = CreateRecoveryPanel();
        recoveryPanel.IsVisible = false;
        logList = CreateLogList();
        lcdBacklightToggle.AddHandler(PointerPressedEvent, OnLcdBacklightTogglePressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        lcdBacklightToggle.Click += OnLcdBacklightToggleClick;
        keypadBacklightToggle.AddHandler(PointerPressedEvent, OnKeypadBacklightTogglePressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        keypadBacklightToggle.Click += OnKeypadBacklightToggleClick;
        chargerPresentToggle.Click += OnChargerPresentToggleClick;
        audioMuteToggle.Click += OnAudioMuteToggleClick;
        pqcRendezvousToggle.Click += OnPqcRendezvousToggleClick;
#if BROWSER
        speakerReactivateButton.Click += OnSpeakerReactivateClick;
#else
        speakerReactivateButton.IsVisible = false;
#endif
        telemetryToggle.Click += OnTelemetryToggleClick;
        applyPhoneSettingsButton.Click += OnApplyPhoneSettingsClick;
        incomingCallButton.Click += OnIncomingCallClick;
        incomingSmsButton.Click += OnIncomingSmsClick;
        incomingRingtoneButton.Click += OnIncomingRingtoneClick;
        ringtoneNotationBox.TextChanged += OnRingtoneNotationChanged;
        powerButton.AddHandler(PointerPressedEvent, OnPowerButtonPressed, RoutingStrategies.Bubble, true);
        powerButton.AddHandler(PointerReleasedEvent, OnPowerButtonReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        powerButton.AddHandler(PointerCaptureLostEvent, OnPowerButtonCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        logFilterBox.SelectionChanged += OnLogFilterChanged;
        logPauseToggle.Click += OnLogPauseClick;
        logClearButton.Click += OnLogClearClick;
        saveUserNameButton.Click += OnSaveUserNameClick;
        copyNumberButton.Click += OnCopyNumberClick;
        backUpButton.Click += OnBackUpClick;
        restoreButton.Click += OnRestoreClick;
        exportWakuDataButton.Click += OnExportWakuDataClick;
        importWakuDataButton.Click += OnImportWakuDataClick;
        resetAllDataButton.Click += OnResetAllDataClick;
        applyRecoveryButton.Click += OnApplyRecoveryClick;
        cancelRecoveryButton.Click += OnCancelRecoveryClick;
        recheckWakuButton.Click += OnRecheckWakuClick;
        copyWakuDiagnosticsButton.Click += OnCopyWakuDiagnosticsClick;
        if (wakuDiagnostics is not null)
            wakuDiagnostics.DiagnosticsChanged += OnWakuDiagnosticsChanged;
        SetPowerButtonPressed(false);
        TryCreateAudio();

        Focusable = true;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(PointerReleasedEvent, OnAnyPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(PointerCaptureLostEvent, OnAnyPointerCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(LostFocusEvent, OnLostFocus, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        Content = BuildContent();
        UpdateProfileUi();
        UpdateWakuDiagnosticsUi();

        resetFlashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(OverrideResetFlashMilliseconds) };
        resetFlashTimer.Tick += (_, _) =>
        {
            resetFlashTimer.Stop();
            UpdateFromCurrentState(force: true);
        };
#if BROWSER
        browserVibrationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(BrowserVibrationPulseMilliseconds) };
        browserVibrationTimer.Tick += (_, _) => UpdateBrowserVibration(emulator.PeripheralState);
#endif
        if (wakuBridge is not null)
        {
            ApplyWakuNetworkStatus(emulator, wakuBridge.Status);
        }
        QueueStateRefresh(emulator);
    }

    private static PhoneEmulator CreateDesktopEmulator(
        IReadOnlyList<string> args,
        Dct3PhoneSettings? settingsOverride = null)
    {
        string flashPath = FlashPathResolver.Resolve(args);
        byte[] firmware = File.ReadAllBytes(flashPath);
        string? externalEepromPath = FlashPathResolver.ResolveExternalEeprom(args, flashPath);
        byte[]? externalEeprom = externalEepromPath is null ? null : File.ReadAllBytes(externalEepromPath);
        Dct3PhoneSettings settings = settingsOverride ?? ParsePhoneSettings(args);
        PhonePersistenceSession persistence = CreateFilePersistenceSession(firmware, FilePhonePersistenceStore.Default, settings);
        return CreateEmulator(firmware, externalEeprom, ParseScheduledKeys(args), persistence, settings);
    }

    private static PhonePersistenceSession CreateFilePersistenceSession(
        byte[] firmware,
        FilePhonePersistenceStore persistenceStore,
        Dct3PhoneSettings settings)
    {
        string persistenceKey = PhonePersistence.CreateKey(firmware, settings);
        Dct3PersistenceSnapshot persistenceSnapshot =
            persistenceStore.Load(persistenceKey) ??
            Dct3PersistenceSnapshot.Empty;

        return new PhonePersistenceSession(persistenceKey, persistenceStore, persistenceSnapshot);
    }

    private static PhoneEmulator CreateEmulator(
        byte[] firmware,
        byte[]? externalEepromImage,
        IEnumerable<ScheduledPhoneKeyChange> scheduledKeys,
        PhonePersistenceSession? persistence,
        Dct3PhoneSettings settings)
        => new(firmware, externalEepromImage, scheduledKeys, persistence, settings);

    private static async ValueTask<PhoneEmulator> CreateEmulatorAsync(
        byte[] firmware,
        byte[]? externalEepromImage,
        IEnumerable<ScheduledPhoneKeyChange> scheduledKeys,
        IPhonePersistenceStore? persistenceStore,
        string? persistenceKey,
        WakuProfileManager? profileManager,
        Dct3PhoneSettings settings)
    {
        PhonePersistenceSession? persistence = persistenceStore is null
            ? null
            : await CreatePersistenceSessionAsync(
                firmware,
                persistenceStore,
                persistenceKey,
                profileManager,
                settings,
                CancellationToken.None);

        return CreateEmulator(firmware, externalEepromImage, scheduledKeys, persistence, settings);
    }

    private static async ValueTask<PhonePersistenceSession> CreatePersistenceSessionAsync(
        byte[] firmware,
        IPhonePersistenceStore persistenceStore,
        string? persistenceKey,
        WakuProfileManager? profileManager,
        Dct3PhoneSettings settings,
        CancellationToken cancellationToken)
    {
        persistenceKey ??= PhonePersistence.CreateKey(firmware, settings);
        Dct3PersistenceSnapshot persistenceSnapshot =
            await persistenceStore.LoadAsync(persistenceKey, cancellationToken) ??
            Dct3PersistenceSnapshot.Empty;
        if (profileManager is not null)
        {
            persistenceSnapshot = await WakuSimStateReconciler.ReconcileAsync(
                profileManager,
                persistenceSnapshot,
                cancellationToken);
            await persistenceStore.SaveAsync(persistenceKey, persistenceSnapshot, cancellationToken);
        }
        return new PhonePersistenceSession(persistenceKey, persistenceStore, persistenceSnapshot);
    }

    private Control BuildContent()
    {
        Grid root = new()
        {
            Background = WindowBackground,
            ClipToBounds = true,
            RenderTransform = shakeTransform,
        };
        layoutRoot = root;

        Viewbox phoneViewport = new()
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = BuildPhoneFace(),
        };
        root.Children.Add(phoneViewport);

        controlPanelFrame = BuildControlPanel();
        controlOverlayHost = new Grid
        {
            Background = new SolidColorBrush(Color.Parse("#d90a0c0b")),
            IsVisible = false,
            Children = { controlPanelFrame },
        };
        root.Children.Add(controlOverlayHost);

        controlOverlayButton = new ToggleButton
        {
            Content = "☰",
            Width = 46,
            Height = 46,
            Margin = new Thickness(14),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = BadgeNeutralBrush,
            BorderBrush = BadgeNeutralBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(23),
            Foreground = TextBrush,
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            Focusable = false,
        };
        ToolTip.SetTip(controlOverlayButton, "Open controls");
        controlOverlayButton.Click += OnControlOverlayButtonClick;
        root.Children.Add(controlOverlayButton);

        return root;
    }

    private Control BuildPhoneFace()
    {
        Canvas phone = new()
        {
            Width = 402,
            Height = 958,
            Background = Brushes.Transparent,
        };
        AddCanvasChild(phone, new PhoneShellControl());

        displayFrame = new Border
        {
            Width = 299,
            Height = 213,
            Background = LcdControl.BackgroundOffBrush,
            BorderThickness = new Thickness(0),
            // The window path starts about 6 px inside its 299 px bounds.
            // Fifteen px keeps the corrected LCD clear of the sides. Its tall-pixel
            // aspect adds the vertical margin and nearly meets the rounded corners.
            Padding = new Thickness(15),
            Clip = PhoneFaceGeometry.CreateLcdWindowClip(),
            ClipToBounds = true,
            Child = lcd,
        };
        AddCanvasChild(phone, displayFrame, 51, 254);

        AddPhoneKeyControl(phone, powerButton);
        AddPhoneKeyControl(phone, directionKeySurface);

        PhoneKey[] faceKeys =
        [
            PhoneKey.Cancel,
            PhoneKey.Main,
            PhoneKey.Left,
            PhoneKey.Right,
            PhoneKey.Digit1,
            PhoneKey.Digit2,
            PhoneKey.Digit3,
            PhoneKey.Digit4,
            PhoneKey.Digit5,
            PhoneKey.Digit6,
            PhoneKey.Digit7,
            PhoneKey.Digit8,
            PhoneKey.Digit9,
            PhoneKey.Star,
            PhoneKey.Digit0,
            PhoneKey.Hash,
        ];
        foreach (PhoneKey key in faceKeys)
        {
            PhoneKeyFaceControl control = CreatePhoneKeyFaceControl(key);
            control.AddHandler(PointerPressedEvent, OnButtonPressed, RoutingStrategies.Bubble, true);
            control.AddHandler(PointerReleasedEvent, OnButtonReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
            control.AddHandler(PointerCaptureLostEvent, OnButtonCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
            AddPhoneKeyControl(phone, control);
        }

        AddPhoneLegends();

        return phone;
    }

    private static void AddCanvasChild(Canvas canvas, Control child, double left = 0, double top = 0)
    {
        Canvas.SetLeft(child, left);
        Canvas.SetTop(child, top);
        canvas.Children.Add(child);
    }

    private static void AddPhoneKeyControl(Canvas phone, PhoneKeyFaceControl control)
    {
        AddCanvasChild(phone, control, control.PhoneBounds.X, control.PhoneBounds.Y);
    }

    private void AddPhoneLegends()
    {
        AddPathLegend(
            PhoneKey.Main,
            PhoneFaceGeometry.CreateMainLegend(),
            ConfirmLegendOffBrush,
            ConfirmLegendOnBrush);
        AddPathLegend(PhoneKey.Cancel, PhoneFaceGeometry.CreateCancelLegend());
        AddPathLegend(PhoneKey.Left, PhoneFaceGeometry.CreateUpperDirectionLegend());
        AddPathLegend(PhoneKey.Right, PhoneFaceGeometry.CreateLowerDirectionLegend());

        AddTextLegend(PhoneKey.Digit1, "1", 57, 639, 22, FontWeight.Bold);
        AddPathLegend(PhoneKey.Digit1, PhoneFaceGeometry.CreateVoicemailLegend());
        AddTextLegend(PhoneKey.Digit2, "2", 173, 657, 22, FontWeight.Bold);
        AddTextLegend(PhoneKey.Digit2, "abc", 195, 664, 16, FontWeight.Bold, 2);
        AddTextLegend(PhoneKey.Digit3, "3", 327, 636, 22, FontWeight.Bold);
        AddTextLegend(PhoneKey.Digit3, "def", 291, 655, 16, FontWeight.Bold, 2);

        AddTextLegend(PhoneKey.Digit4, "4", 60, 707, 22, FontWeight.Bold);
        AddTextLegend(PhoneKey.Digit4, "ghi", 81, 724, 16, FontWeight.Bold, 2);
        AddTextLegend(PhoneKey.Digit5, "5", 175, 721, 22, FontWeight.Bold);
        AddTextLegend(PhoneKey.Digit5, "jkl", 202, 728, 16, FontWeight.Bold, 2);
        AddTextLegend(PhoneKey.Digit6, "6", 329, 705, 22, FontWeight.Bold);
        AddTextLegend(PhoneKey.Digit6, "mno", 292, 722, 16, FontWeight.Bold, 2);

        AddTextLegend(PhoneKey.Digit7, "7", 63, 772, 22, FontWeight.Bold);
        AddTextLegend(PhoneKey.Digit7, "pqrs", 76, 789, 16, FontWeight.Bold, 2);
        AddTextLegend(PhoneKey.Digit8, "8", 175, 790, 22, FontWeight.Bold);
        AddTextLegend(PhoneKey.Digit8, "tuv", 197, 797, 16, FontWeight.Bold, 2);
        AddTextLegend(PhoneKey.Digit9, "9", 329, 771, 22, FontWeight.Bold);
        AddTextLegend(PhoneKey.Digit9, "wxyz", 292, 790, 16, FontWeight.Bold, 1);

        AddTextLegend(PhoneKey.Star, "*", 63, 834, 54, FontWeight.Normal);
        AddTextLegend(PhoneKey.Star, "+", 94, 854, 20, FontWeight.Normal);
        AddTextLegend(PhoneKey.Digit0, "0", 175, 856, 22, FontWeight.Bold);
        AddPathLegend(PhoneKey.Digit0, PhoneFaceGeometry.CreateZeroLegend());
        AddPathLegend(PhoneKey.Hash, PhoneFaceGeometry.CreateHashLegend());
        AddPathLegend(PhoneKey.Hash, PhoneFaceGeometry.CreateHomeLegend());
    }

    private void AddTextLegend(
        PhoneKey key,
        string text,
        double left,
        double top,
        double fontSize,
        FontWeight fontWeight,
        double letterSpacing = 0)
    {
        phoneKeyControls[key].AddTextLegend(
            text,
            left,
            top,
            EmbeddedFontFamily,
            fontSize,
            fontWeight,
            KeyLegendOffBrush,
            KeyLegendOnBrush,
            letterSpacing);
    }

    private void AddPathLegend(
        PhoneKey key,
        Geometry geometry,
        IBrush? offBrush = null,
        IBrush? onBrush = null)
    {
        offBrush ??= KeyLegendOffBrush;
        onBrush ??= KeyLegendOnBrush;
        phoneKeyControls[key].AddPathLegend(geometry, offBrush, onBrush);
    }

    private static IBrush CreateVerticalGradient(string start, string end)
        => CreateGradient(start, end, new RelativePoint(0.5, 0, RelativeUnit.Relative), new RelativePoint(0.5, 1, RelativeUnit.Relative));

    private static IBrush CreateGradient(string start, string end, RelativePoint startPoint, RelativePoint endPoint)
        => new LinearGradientBrush
        {
            StartPoint = startPoint,
            EndPoint = endPoint,
            GradientStops =
            [
                new GradientStop(Color.Parse(start), 0),
                new GradientStop(Color.Parse(end), 1),
            ],
        };

    private Control BuildIdentityStrip()
    {
        userNameBox.MinWidth = 160;
        userNameBox.MaxWidth = 260;
        userNameBox.MaxLength = SimPhonebookCodec.AlphaIdentifierLength;
        userNameBox.IsEnabled = profileManager is not null;
        ToolTip.SetTip(
            userNameBox,
            "Use no more than 16 EF_ADN bytes. The field supports mixed case and Nokia phonebook characters.");
        ToolTip.SetTip(copyNumberButton, "Copy My number");

        StackPanel nameGroup = CreateIdentityGroup("User name", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { userNameBox, saveUserNameButton },
        });
        StackPanel numberGroup = CreateIdentityGroup("My number", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { CreateIndicatorBadge(myNumberText), copyNumberButton },
        });
        StackPanel networkGroup = CreateIdentityGroup("Network", CreateIndicatorBadge(networkStatusText));
        pqcRendezvousToggle.IsVisible = wakuBridge is not null;
        StackPanel rendezvousGroup = CreateIdentityGroup("Packet crypto", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { CreateIndicatorBadge(pqcRendezvousText), pqcRendezvousToggle },
        });
        Control diagnosticsPanel = BuildWakuDiagnosticsPanel();
        diagnosticsPanel.IsVisible = false;
        ToggleButton diagnosticsToggle = CreateLightToggle();
        diagnosticsToggle.IsVisible = wakuDiagnostics is not null;
        SetPanelToggle(diagnosticsToggle, "Diagnostics ▸", false, "Show Waku diagnostics");
        diagnosticsToggle.Click += (_, _) =>
        {
            bool expanded = diagnosticsToggle.IsChecked == true;
            diagnosticsPanel.IsVisible = expanded && wakuDiagnostics is not null;
            SetPanelToggle(
                diagnosticsToggle,
                expanded ? "Diagnostics ▾" : "Diagnostics ▸",
                expanded,
                expanded ? "Hide Waku diagnostics" : "Show Waku diagnostics");
        };

        WrapPanel contents = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { nameGroup, numberGroup, networkGroup, rendezvousGroup, diagnosticsToggle },
        };
        return new Border
        {
            IsVisible = profileManager is not null,
            Background = WindowBackground,
            BorderBrush = BadgeNeutralBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 14),
            Padding = new Thickness(12, 8),
            Child = new StackPanel
            {
                Children = { contents, diagnosticsPanel },
            },
        };
    }

    private Control BuildWakuDiagnosticsPanel()
    {
        WrapPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { recheckWakuButton, copyWakuDiagnosticsButton },
        };
        return new Border
        {
            IsVisible = wakuDiagnostics is not null,
            Background = WindowBackground,
            BorderBrush = BadgeNeutralBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(8, 10, 8, 0),
            Padding = new Thickness(10, 8),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "Live network path",
                        Foreground = MutedTextBrush,
                        FontSize = 10,
                        Margin = new Thickness(0, 0, 0, 3),
                    },
                    wakuDiagnosticsSummaryText,
                    wakuDiagnosticsDetailText,
                    wakuDiagnosticsLogBox,
                    new TextBlock
                    {
                        Text = "Diagnostics can expose Waku peer endpoints and public topic identifiers. They do not expose private keys or message contents.",
                        Foreground = MutedTextBrush,
                        FontSize = 9,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 6, 0, 4),
                    },
                    actions,
                },
            },
        };
    }

    private static StackPanel CreateIdentityGroup(string label, Control value) => new()
    {
        Margin = new Thickness(8, 0),
        Children =
        {
            new TextBlock
            {
                Text = label,
                Foreground = MutedTextBrush,
                FontSize = 10,
                Margin = new Thickness(2, 0, 0, 2),
            },
            value,
        },
    };

    private PhoneKeyFaceControl CreatePhoneKeyFaceControl(PhoneKey key)
    {
        PhoneKeyFaceControl control = new(
            key,
            PhoneFaceGeometry.Create(key),
            GetPhoneKeyBrush(key),
            GetPhoneKeyOpacity(key),
            PressedKeyOverlayBrush,
            PressedKeyEdgePen,
            showPressedOverlay: key != PhoneKey.Power);

        Geometry? middle = key is PhoneKey.Left or PhoneKey.Right
            ? null
            : PhoneFaceGeometry.CreateMiddle(key);
        if (middle is not null)
        {
            control.AddBodyLayer(middle, GetMiddleKeyBrush(key), GetMiddleKeyOpacity(key));
        }

        Geometry? inner = PhoneFaceGeometry.CreateInner(key);
        if (inner is not null)
        {
            control.AddBodyLayer(inner, GetInnerKeyBrush(key));
        }

        phoneKeyControls.Add(key, control);
        return control;
    }

    private static PhoneKeyFaceControl CreateDirectionKeySurface()
    {
        PhoneKeyFaceControl control = new(
            PhoneKey.Left,
            PhoneFaceGeometry.CreateDirectionOutline(),
            new SolidColorBrush(Color.Parse("#424645")),
            1,
            PressedKeyOverlayBrush,
            PressedKeyEdgePen,
            showPressedOverlay: false)
        {
            IsHitTestVisible = false,
        };
        control.AddBodyLayer(
            PhoneFaceGeometry.CreateMiddle(PhoneKey.Left)!,
            DirectionKeyInnerBrush);
        return control;
    }

    private static double GetPhoneKeyOpacity(PhoneKey key) => key switch
    {
        PhoneKey.Digit1 or PhoneKey.Digit2 or PhoneKey.Digit3 or
        PhoneKey.Digit5 or PhoneKey.Digit8 or PhoneKey.Digit0 => 0.840076959,
        PhoneKey.Digit4 or PhoneKey.Digit6 or PhoneKey.Digit7 or
        PhoneKey.Digit9 or PhoneKey.Star or PhoneKey.Hash => 0.519298041,
        _ => 1,
    };

    private static IBrush GetPhoneKeyBrush(PhoneKey key) => key switch
    {
        PhoneKey.Power => Brushes.Transparent,
        PhoneKey.Main => new SolidColorBrush(Color.Parse("#353432")),
        PhoneKey.Cancel => new SolidColorBrush(Color.Parse("#3f3e3c")),
        PhoneKey.Left or PhoneKey.Right => Brushes.Transparent,
        _ => NumericKeyEdgeBrush,
    };

    private static double GetMiddleKeyOpacity(PhoneKey key) => key switch
    {
        PhoneKey.Main => 0.4453125,
        PhoneKey.Digit1 or PhoneKey.Digit2 or PhoneKey.Digit3 or
        PhoneKey.Digit4 or PhoneKey.Digit5 or PhoneKey.Digit6 or
        PhoneKey.Digit7 or PhoneKey.Digit8 or PhoneKey.Digit9 or
        PhoneKey.Star or PhoneKey.Digit0 or PhoneKey.Hash => 0.895813899,
        _ => 1,
    };

    private static IBrush GetMiddleKeyBrush(PhoneKey key) => key switch
    {
        PhoneKey.Main => new SolidColorBrush(Color.Parse("#585754")),
        PhoneKey.Cancel => CancelKeyInnerBrush,
        PhoneKey.Left => DirectionKeyInnerBrush,
        PhoneKey.Digit1 or PhoneKey.Digit2 or PhoneKey.Digit3 or
        PhoneKey.Digit5 or PhoneKey.Digit8 or PhoneKey.Digit0 =>
            new SolidColorBrush(Color.Parse("#20262b")),
        _ => new SolidColorBrush(Color.Parse("#2a3238")),
    };

    private static IBrush GetInnerKeyBrush(PhoneKey key) => key switch
    {
        PhoneKey.Main => MainKeyInnerBrush,
        _ => NumericKeyInnerBrush,
    };

    private Border BuildControlPanel()
    {
        StackPanel panelContents = new()
        {
            Children =
            {
                BuildIdentityStrip(),
                CreateProfileSectionLabel(),
                CreateProfileSettingsRow(),
                CreateSectionLabel("RUNTIME"),
                CreateRuntimeOptionsRow(),
                CreateSectionLabel("GSM"),
                CreatePhoneSettingsRow(),
                CreateGsmStateRow(),
                CreateIncomingCallRow(),
                CreateIncomingSmsRow(),
                CreateSectionLabel("SMART RINGTONE"),
                CreateIncomingRingtoneRow(),
                CreateSectionLabel("RADIO"),
                CreateDspRssiRow(),
                CreateSectionLabel("CCONT"),
                BuildCcontTopRow(),
                BuildCcontFirmwareRow(),
                CreateCcontAdcRow("ACC", CcontAdcChannel.AccessoryDetect),
                CreateCcontAdcRow("VBAT", CcontAdcChannel.BatteryVoltage),
                CreateCcontAdcRow("BSI", CcontAdcChannel.BatteryType),
                CreateCcontAdcRow("BTEMP", CcontAdcChannel.BatteryTemperature),
                CreateCcontAdcRow("VCHG", CcontAdcChannel.ChargerVoltage),
                CreateCcontAdcRow("VCXO", CcontAdcChannel.VcxoTemperature),
                CreateCcontAdcRow("ICHG", CcontAdcChannel.ChargingCurrent),
                BuildTelemetryTopRow(),
                CreateTelemetryPanel(),
                CreateLogSection(),
            },
        };

        ScrollViewer scroll = new()
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panelContents,
        };

        TextBlock title = new()
        {
            Text = "NOKS CONTROLS",
            Foreground = TextBrush,
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(2, 0, 0, 12),
        };

        Grid contents = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { title, scroll },
        };
        Grid.SetRow(scroll, 1);

        return new Border
        {
            Background = SurfaceBrush,
            BorderBrush = BadgeNeutralBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Margin = new Thickness(16, 68, 16, 16),
            Padding = new Thickness(18),
            MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = contents,
        };
    }

    private Control CreateProfileSettingsRow()
    {
        StackPanel recoveryActions = new()
        {
            Orientation = Orientation.Horizontal,
            Children = { backUpButton, restoreButton },
        };
        StackPanel dataActions = new()
        {
            Orientation = Orientation.Horizontal,
            Children = { exportWakuDataButton, importWakuDataButton, resetAllDataButton },
        };
        ToolTip.SetTip(backUpButton, "Show the identity recovery phrase");
        ToolTip.SetTip(restoreButton, "Restore only the private identity from a recovery phrase");
        ToolTip.SetTip(
            exportWakuDataButton,
            "Download identity, pairings, contacts, messages, and canonical Waku SIM files as JSON");
        ToolTip.SetTip(
            importWakuDataButton,
            "Restore a Noks Waku JSON backup and rebuild the SIM from it");
        ToolTip.SetTip(
            resetAllDataButton,
            "Replace the Waku identity and clear all browser profiles, SIM, EEPROM, flash, contacts, and messages");
        StackPanel contents = new()
        {
            Children = { recoveryActions, recoveryPanel, dataActions, dataManagementStatusText },
        };
        Border row = CreateControlRow(contents, bottomMargin: 10);
        row.IsVisible = profileManager is not null;
        return row;
    }

    private Control CreateProfileSectionLabel()
    {
        Control label = CreateSectionLabel("PROFILE");
        label.IsVisible = profileManager is not null;
        return label;
    }

    private Border CreateRecoveryPanel()
    {
        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancelRecoveryButton, applyRecoveryButton },
        };
        return new Border
        {
            Background = BadgeOffBrush,
            BorderBrush = BadgeNeutralBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(8),
            Child = new StackPanel
            {
                Children = { recoveryStatusText, recoveryPhraseBox, actions },
            },
        };
    }

    private Control CreateRuntimeOptionsRow()
    {
        SetPanelToggle(audioMuteToggle, "MUTE", audioMuted, "Mute host audio without changing emulated buzzer timing");
        ToolTip.SetTip(speakerReactivateButton, "Reactivate browser speaker audio and remote call playback");
        return CreateControlRow(new StackPanel
        {
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { runtimeBadge, lcdBacklightToggle, keypadBacklightToggle, vibrationBadge },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { audioMuteToggle, speakerReactivateButton },
                },
            },
        }, bottomMargin: 10);
    }

    private Control BuildCcontTopRow()
    {
        Button resetButton = CreatePillButton("RST");
        resetButton.Click += OnResetCcontClick;

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                chargerPresentToggle,
                ccontPwmBadge,
                resetButton,
            },
        };
    }

    private Control BuildCcontFirmwareRow()
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                firmwareBatteryBadge,
                firmwarePowerStateBadge,
                firmwareThresholdBadge,
            },
        };
    }

    private Control BuildTelemetryTopRow()
    {
        SetPanelToggle(telemetryToggle, "TEL", false, "Show telemetry");

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                CreateSectionLabel("TEL"),
                telemetryToggle,
            },
        };
    }

    private Control CreateGsmStateRow()
    {
        TextBlock labelText = CreateControlLabel("NET");
        gsmStateBadge.HorizontalAlignment = HorizontalAlignment.Right;

        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        };

        Grid.SetColumn(labelText, 0);
        row.Children.Add(labelText);
        Grid.SetColumn(gsmStateBadge, 1);
        row.Children.Add(gsmStateBadge);

        return CreateControlRow(row, bottomMargin: 6);
    }

    private Control CreatePhoneSettingsRow()
    {
        TextBlock simLabel = CreateControlLabel("SIM");
        TextBlock netLabel = CreateControlLabel("OPERATOR");

        simImsiBox.PlaceholderText = "auto";
        networkNameBox.PlaceholderText = "";
        ToolTip.SetTip(simImsiBox, "15-digit SIM IMSI. Empty uses firmware/default profile.");
        ToolTip.SetTip(networkNameBox, "Optional SIM SPN and GSM full network name. Empty uses the firmware PLMN operator name.");
        ToolTip.SetTip(applyPhoneSettingsButton, "Apply SIM/network profile");

        Grid fields = new()
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        };

        Grid.SetRow(simLabel, 0);
        Grid.SetColumn(simLabel, 0);
        fields.Children.Add(simLabel);
        Grid.SetRow(simImsiBox, 0);
        Grid.SetColumn(simImsiBox, 1);
        fields.Children.Add(simImsiBox);

        Grid.SetRow(netLabel, 1);
        Grid.SetColumn(netLabel, 0);
        fields.Children.Add(netLabel);
        Grid.SetRow(networkNameBox, 1);
        Grid.SetColumn(networkNameBox, 1);
        fields.Children.Add(networkNameBox);

        StackPanel actionRow = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                CreateIndicatorBadge(phoneSettingsStatusText),
                applyPhoneSettingsButton,
            },
        };

        Grid.SetRow(actionRow, 2);
        Grid.SetColumnSpan(actionRow, 2);
        fields.Children.Add(actionRow);

        return CreateControlRow(fields, bottomMargin: 6);
    }


    private Control CreateIncomingCallRow()
    {
        TextBlock labelText = CreateControlLabel("FROM");

        ToolTip.SetTip(incomingCallButton, "Queue incoming call");

        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        };

        Grid.SetColumn(labelText, 0);
        row.Children.Add(labelText);
        Grid.SetColumn(incomingNumberBox, 1);
        row.Children.Add(incomingNumberBox);
        Grid.SetColumn(incomingCallButton, 2);
        row.Children.Add(incomingCallButton);

        return CreateControlRow(row, bottomMargin: 6);
    }

    private Control CreateIncomingSmsRow()
    {
        TextBlock labelText = CreateControlLabel("MSG");

        ToolTip.SetTip(incomingSmsButton, "Queue incoming SMS");

        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        };

        Grid.SetColumn(labelText, 0);
        row.Children.Add(labelText);
        Grid.SetColumn(incomingSmsTextBox, 1);
        row.Children.Add(incomingSmsTextBox);
        Grid.SetColumn(incomingSmsButton, 2);
        row.Children.Add(incomingSmsButton);

        return CreateControlRow(row, bottomMargin: 6);
    }

    private Control CreateIncomingRingtoneRow()
    {
        TextBlock nameLabel = CreateControlLabel("TITLE");
        TextBlock tempoLabel = CreateControlLabel("BPM");
        TextBlock notesLabel = CreateControlLabel("NOTES");
        ringtoneNameBox.MaxLength = 15;
        ringtoneTempoBox.MaxLength = 3;
        ringtoneNameBox.PlaceholderText = "1-15 ASCII characters";
        ringtoneTempoBox.PlaceholderText = "140";
        ringtoneNotationBox.PlaceholderText = "8c2 8- ... or name:d=8,o=5,b=140:c,p,g";
        ToolTip.SetTip(
            ringtoneNameBox,
            "Enter a ringtone title with no more than 15 ASCII characters. RTTTL uses its embedded title instead.");
        ToolTip.SetTip(
            ringtoneTempoBox,
            "Enter the tempo in beats per minute. The encoder selects the nearest supported Nokia tempo. RTTTL uses its embedded BPM instead.");
        ToolTip.SetTip(
            ringtoneNotationBox,
            "Paste Composer notation (for example 8c2 16- 8g1) or a full RTTTL value (name:d=8,o=5,b=140:c,p,g).");
        ToolTip.SetTip(
            incomingRingtoneButton,
            "Queue an incoming Smart Messaging ringtone from these fields. The encoder uses no more than three SMS parts for a long tone.");

        Grid fields = new()
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        };
        Grid.SetRow(nameLabel, 0);
        fields.Children.Add(nameLabel);
        Grid.SetRow(ringtoneNameBox, 0);
        Grid.SetColumn(ringtoneNameBox, 1);
        fields.Children.Add(ringtoneNameBox);
        Grid.SetRow(tempoLabel, 1);
        fields.Children.Add(tempoLabel);
        Grid.SetRow(ringtoneTempoBox, 1);
        Grid.SetColumn(ringtoneTempoBox, 1);
        fields.Children.Add(ringtoneTempoBox);
        Grid.SetRow(notesLabel, 2);
        fields.Children.Add(notesLabel);
        Grid.SetRow(ringtoneNotationBox, 2);
        Grid.SetColumn(ringtoneNotationBox, 1);
        fields.Children.Add(ringtoneNotationBox);

        StackPanel actionRow = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                CreateIndicatorBadge(ringtoneStatusText),
                incomingRingtoneButton,
            },
        };
        Grid.SetRow(actionRow, 3);
        Grid.SetColumnSpan(actionRow, 2);
        fields.Children.Add(actionRow);
        return CreateControlRow(fields, bottomMargin: 10);
    }

    private Control CreateDspRssiRow()
    {
        TextBlock labelText = CreateControlLabel("RSSI");

        Border valueBadge = CreateIndicatorBadge(dspRssiText);
        valueBadge.HorizontalAlignment = HorizontalAlignment.Right;
        ToolTip.SetTip(valueBadge, "DSP MDI RSSI_RESULTS measurement byte");

        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        };

        Grid.SetColumn(labelText, 0);
        row.Children.Add(labelText);
        Grid.SetColumn(dspRssiSlider, 1);
        row.Children.Add(dspRssiSlider);
        Grid.SetColumn(valueBadge, 2);
        row.Children.Add(valueBadge);

        return CreateControlRow(row, bottomMargin: 10);
    }

    private Control CreateCcontAdcRow(string label, CcontAdcChannel channel)
    {
        ushort initialValue = CcontControlState.Normal.Get(channel);
        TextBlock valueText = CreateBadgeText();
        valueText.Text = FormatAdcValue(initialValue);
        valueText.Foreground = TextBrush;

        Slider slider = new()
        {
            Minimum = 0,
            Maximum = 0x3FF,
            Value = initialValue,
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, _) => OnCcontSliderChanged(channel, slider, valueText);

        CcontAdcControl control = new(slider, valueText);
        slider.AddHandler(PointerPressedEvent, (_, _) => control.Editing = true, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        slider.AddHandler(
            PointerReleasedEvent,
            (_, _) =>
            {
                control.Editing = false;
                OnCcontSliderChanged(channel, slider, valueText);
            },
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            true);
        slider.PointerCaptureLost += (_, _) =>
        {
            control.Editing = false;
            OnCcontSliderChanged(channel, slider, valueText);
        };

        ccontAdcControls[channel] = control;

        TextBlock labelText = CreateControlLabel(label);

        Border valueBadge = CreateIndicatorBadge(valueText);
        valueBadge.HorizontalAlignment = HorizontalAlignment.Right;

        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        };

        Grid.SetColumn(labelText, 0);
        row.Children.Add(labelText);
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        Grid.SetColumn(valueBadge, 2);
        row.Children.Add(valueBadge);

        return CreateControlRow(row, bottomMargin: 6);
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Focus();
        if (!runtimeStarted)
        {
            runtimeStarted = true;
            emulator.Start();
            wakuBridge?.Start();
        }
        UpdateFromCurrentState(force: true);
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
#if BROWSER
        DisposeBrowserVibration();
        BrowserCallMediaInterop.Dispose();
#endif
        ReleaseAllInputKeys();
        StopVibrationAnimation();
        emulator.FrameChanged -= OnEmulatorFrameChanged;
        emulator.AudioStateChanged -= OnEmulatorAudioStateChanged;
        emulator.AudioAnnouncementAvailable -= OnEmulatorAudioAnnouncementAvailable;
        emulator.CallTransitionAvailable -= OnEmulatorCallTransitionAvailable;
        emulator.StateChanged -= OnEmulatorStateChanged;
        emulator.TelemetryChanged -= OnEmulatorTelemetryChanged;
        emulator.LogAvailable -= OnEmulatorLogAvailable;
        if (wakuBridge is not null)
        {
            DetachBridgeFromEmulator(emulator);
            wakuBridge.CommandAvailable -= OnBridgeCommandAvailable;
            wakuBridge.StatusChanged -= OnBridgeStatusChanged;
        }
        if (profileManager is not null)
            profileManager.ProfileChanged -= OnProfileChanged;
        if (wakuDiagnostics is not null)
            wakuDiagnostics.DiagnosticsChanged -= OnWakuDiagnosticsChanged;
        resetFlashTimer.Stop();
        audio?.Dispose();
        emulator.Dispose();
        _ = DisposeProfileServicesAsync();
    }

    private async Task DisposeProfileServicesAsync()
    {
        try
        {
            if (wakuBridge is not null)
                await wakuBridge.DisposeAsync();
        }
        catch
        {
        }
        try
        {
            if (wakuTransport is IAsyncDisposable asyncTransport)
                await asyncTransport.DisposeAsync();
        }
        catch
        {
        }
        try
        {
            if (profileManager is not null)
                await profileManager.DisposeAsync();
        }
        catch
        {
        }
    }

    private void OnControlOverlayButtonClick(object? sender, RoutedEventArgs e)
    {
        if (controlOverlayButton is null || controlOverlayHost is null)
        {
            return;
        }

        bool open = controlOverlayButton.IsChecked == true;
        controlOverlayHost.IsVisible = open;
        controlOverlayButton.Content = open ? "×" : "☰";
        controlOverlayButton.Background = open ? BadgeOnBrush : BadgeNeutralBrush;
        controlOverlayButton.Foreground = open ? BadgeOnTextBrush : TextBrush;
        ToolTip.SetTip(controlOverlayButton, open ? "Close controls" : "Open controls");
        if (!open)
        {
            Focus();
        }
    }

    private void UpdateFromCurrentState(bool force = false)
    {
        Mad2PeripheralState state = emulator.PeripheralState;
        bool lcdBacklightOn = lcdBacklightOverride ?? state.LcdBacklightOn;
        bool keypadBacklightOn = keypadBacklightOverride ?? state.KeypadBacklightOn;

        lcd.SetBacklightOverrideWithoutRefresh(lcdBacklightOverride);
        InvalidateLcdIfChanged(lcdBacklightOn);
        UpdateRuntimeBadge();

        bool lcdResetFlashActive = IsResetFlashActive(lcdBacklightResetAtMilliseconds);
        bool keypadResetFlashActive = IsResetFlashActive(keypadBacklightResetAtMilliseconds);
        if (force ||
            state != lastPeripheralUiState ||
            lcdBacklightOn != lastLcdBacklightUiState ||
            keypadBacklightOn != lastKeypadBacklightUiState ||
            lcdResetFlashActive != lastLcdResetFlashUiState ||
            keypadResetFlashActive != lastKeypadResetFlashUiState)
        {
            UpdatePeripheralBadges(state, lcdBacklightOn, keypadBacklightOn, lcdResetFlashActive, keypadResetFlashActive);
            lastPeripheralUiState = state;
            lastLcdResetFlashUiState = lcdResetFlashActive;
            lastKeypadResetFlashUiState = keypadResetFlashActive;
        }

        if (force || lcdBacklightOn != lastLcdBacklightUiState || keypadBacklightOn != lastKeypadBacklightUiState)
        {
            UpdateLeds(lcdBacklightOn, keypadBacklightOn);
            lastLcdBacklightUiState = lcdBacklightOn;
            lastKeypadBacklightUiState = keypadBacklightOn;
        }

        GsmControlState gsmState = emulator.GsmState;
        if (force || gsmState != lastGsmUiState)
        {
            UpdateGsmPanel(gsmState);
            lastGsmUiState = gsmState;
        }

        DspRadioControlState dspRadioState = emulator.DspRadioState;
        if (force || dspRadioState != lastDspRadioUiState)
        {
            UpdateDspRadioPanel(dspRadioState);
            lastDspRadioUiState = dspRadioState;
        }

        CcontControlState ccontState = emulator.CcontState;
        if (force || ccontState != lastCcontUiState)
        {
            UpdateControlPanel(ccontState);
            lastCcontUiState = ccontState;
        }

        UpdateVibration(state);
    }

    private void OnButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is PhoneKeyFaceControl control)
        {
            PressPointerKey(control, control.Key, e);
        }
    }

    private void OnButtonReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is PhoneKeyFaceControl)
        {
            e.PreventGestureRecognition();
            ReleasePointerKeyForEvent(e.Pointer.Id, e.Pointer.Type, "button-release");
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnButtonCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (sender is PhoneKeyFaceControl)
        {
            ReleasePointerKeyForEvent(e.Pointer.Id, e.Pointer.Type, "button-capture-lost");
        }
    }

    private void OnPowerButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        PressPointerKey(powerButton, PhoneKey.Power, e);
    }

    private void OnPowerButtonReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.PreventGestureRecognition();
        ReleasePointerKeyForEvent(e.Pointer.Id, e.Pointer.Type, "power-release");
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnPowerButtonCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        ReleasePointerKeyForEvent(e.Pointer.Id, e.Pointer.Type, "power-capture-lost");
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        LogInput("lost-focus");
        ReleaseAllInputKeys();
    }

    private void OnAnyPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (ReleasePointerKeyForEvent(e.Pointer.Id, e.Pointer.Type, "root-release"))
        {
            e.PreventGestureRecognition();
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnAnyPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        ReleasePointerKeyForEvent(e.Pointer.Id, e.Pointer.Type, "root-capture-lost");
    }

    private void PressPointerKey(Control button, PhoneKey key, PointerPressedEventArgs e)
    {
        e.PreventGestureRecognition();
        long pointerId = e.Pointer.Id;

        if (inputState.TryGetPointerKey(pointerId, out PhoneKey existingKey) && existingKey != key)
        {
            LogInput($"pointer-press-reassign pointer={pointerId} from={existingKey} to={key}", force: true);
        }

        PhoneInputState.PressChange change = inputState.PressPointer(pointerId, key);
        if (change.SourceChanged)
        {
            pointerPressedAtMillisecondsByPointerId[pointerId] = Environment.TickCount64;
            pointerTypeByPointerId[pointerId] = e.Pointer.Type;
        }

        ApplyInputPressChange(change, key, tracePointerLatency: true);
        e.Pointer.Capture(button);
        LogInput(
            $"pointer-press pointer={pointerId} type={e.Pointer.Type} key={key} " +
            $"duplicate={!change.SourceChanged} wasActive={!change.KeyBecameActive}");
        e.Handled = true;
    }

    private void ApplyInputPressChange(
        PhoneInputState.PressChange change,
        PhoneKey key,
        bool tracePointerLatency)
    {
        if (change.PreviousKey is { } previousKey)
        {
            if (change.PreviousKeyBecameInactive)
            {
                emulator.SetKey(previousKey, false);
            }

            UpdateKeyVisual(previousKey);
        }

        if (change.KeyBecameActive)
        {
#if BROWSER
            int? latencyTraceId = tracePointerLatency
                ? BrowserInteractionLatencyBenchmark.BeginPointer(key)
                : null;
#endif
            emulator.SetKey(key, true);
#if BROWSER
            if (latencyTraceId is { } traceId)
            {
                BrowserInteractionLatencyBenchmark.MarkInputQueued(traceId);
            }
#endif
        }

        UpdateKeyVisual(key);
    }

    private bool ReleasePointerKeyForEvent(long pointerId, PointerType _, string reason)
    {
        return ReleasePointerKey(pointerId, reason);
    }

    private bool ReleasePointerKey(long pointerId, string reason)
    {
        PhoneInputState.ReleaseChange change = inputState.ReleasePointer(pointerId);
        if (!change.Found)
        {
            LogInput($"pointer-release-missing reason={reason} pointer={pointerId}");
            return false;
        }

        pointerPressedAtMillisecondsByPointerId.Remove(pointerId);
        pointerTypeByPointerId.Remove(pointerId);
        if (change.KeyBecameInactive)
        {
            emulator.SetKey(change.Key, false);
        }

        UpdateKeyVisual(change.Key);
        LogInput(
            $"pointer-release reason={reason} pointer={pointerId} key={change.Key} " +
            $"stillActive={IsInputKeyActive(change.Key)}");
        return true;
    }

    private void ReleaseAllInputKeys()
    {
        PhoneKey[] releasedKeys = inputState.ActiveKeys.ToArray();
        pointerPressedAtMillisecondsByPointerId.Clear();
        pointerTypeByPointerId.Clear();
        inputState.Clear();

        ReleaseAllEmulatorKeys();
        LogInput($"release-all keys={string.Join(',', releasedKeys)}");
    }

    private static void LogInput(string message, bool force = false)
    {
#if BROWSER
        if (force)
        {
            Console.WriteLine($"Noks browser input: event=\"{message}\"");
        }
#endif
    }

    private bool IsInputKeyActive(PhoneKey key)
        => inputState.IsActive(key);

    private void ReleaseAllEmulatorKeys()
    {
        foreach (PhoneKey key in Enum.GetValues<PhoneKey>())
        {
            emulator.SetKey(key, false);
            UpdateKeyVisual(key);
        }
    }

    private void UpdateKeyVisual(PhoneKey key)
    {
        bool pressed = IsInputKeyActive(key);
        if (phoneKeyControls.TryGetValue(key, out PhoneKeyFaceControl? control))
        {
            control.SetPressed(pressed);
        }

        if (key == PhoneKey.Power)
        {
            SetPowerButtonPressed(pressed);
            return;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsTextInputFocused(e.Source))
        {
            return;
        }

        if (MapKeyboard(e.Key, e.KeyModifiers) is { } key)
        {
            PhoneInputState.PressChange change = inputState.PressKeyboard((int)e.Key, key);
            ApplyInputPressChange(change, key, tracePointerLatency: false);
            LogInput(change.SourceChanged ? $"keyboard-down key={key}" : $"keyboard-repeat key={key}");
            e.Handled = true;
        }
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        PhoneInputState.ReleaseChange change = inputState.ReleaseKeyboard((int)e.Key);
        if (!change.Found)
        {
            if (!IsTextInputFocused(e.Source) && MapKeyboard(e.Key, e.KeyModifiers) is not null)
            {
                e.Handled = true;
            }
            return;
        }

        if (change.KeyBecameInactive)
        {
            emulator.SetKey(change.Key, false);
        }

        UpdateKeyVisual(change.Key);
        LogInput($"keyboard-up key={change.Key}");
        e.Handled = true;
    }

    private static PhoneKey? MapKeyboard(Key key, KeyModifiers modifiers)
        => key switch
        {
            Key.D8 when modifiers.HasFlag(KeyModifiers.Shift) => PhoneKey.Star,
            Key.D3 when modifiers.HasFlag(KeyModifiers.Shift) => PhoneKey.Hash,
            Key.D0 or Key.NumPad0 => PhoneKey.Digit0,
            Key.D1 or Key.NumPad1 => PhoneKey.Digit1,
            Key.D2 or Key.NumPad2 => PhoneKey.Digit2,
            Key.D3 or Key.NumPad3 => PhoneKey.Digit3,
            Key.D4 or Key.NumPad4 => PhoneKey.Digit4,
            Key.D5 or Key.NumPad5 => PhoneKey.Digit5,
            Key.D6 or Key.NumPad6 => PhoneKey.Digit6,
            Key.D7 or Key.NumPad7 => PhoneKey.Digit7,
            Key.D8 or Key.NumPad8 => PhoneKey.Digit8,
            Key.D9 or Key.NumPad9 => PhoneKey.Digit9,
            Key.Multiply => PhoneKey.Star,
            Key.OemPlus or Key.Add => PhoneKey.Hash,
            Key.Left => PhoneKey.Left,
            Key.Right => PhoneKey.Right,
            Key.Enter => PhoneKey.Main,
            Key.Escape => PhoneKey.Cancel,
            _ => null,
        };

    private static List<ScheduledPhoneKeyChange> ParseScheduledKeys(IReadOnlyList<string> args)
    {
        List<ScheduledPhoneKeyChange> changes = [];

        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] != "--key" || i + 1 >= args.Count)
            {
                continue;
            }

            AddScheduledKey(changes, args[++i]);
        }

        return changes;
    }

    internal static Dct3PhoneSettings ParsePhoneSettings(IReadOnlyList<string> args)
    {
        return PhoneSettingsParser.Parse(args);
    }

    private static void AddScheduledKey(List<ScheduledPhoneKeyChange> changes, string spec)
    {
        string[] at = spec.Split('@', 2);

        if (at.Length != 2)
        {
            return;
        }

        string[] timing = at[1].Split(':', 2);

        if (!long.TryParse(timing[0], out long step) || step < 0)
        {
            return;
        }

        long hold = 1_000_000;

        if (timing.Length > 1 && (!long.TryParse(timing[1], out hold) || hold <= 0))
        {
            return;
        }

        PhoneKey? key = ParsePhoneKeyName(at[0]);

        if (key is null || step > long.MaxValue - hold)
        {
            return;
        }

        changes.Add(new ScheduledPhoneKeyChange(step, key.Value, Pressed: true));
        changes.Add(new ScheduledPhoneKeyChange(step + hold, key.Value, Pressed: false));
    }

    private static PhoneKey? ParsePhoneKeyName(string name)
        => name.ToLowerInvariant() switch
        {
            "0" or "digit0" => PhoneKey.Digit0,
            "1" or "digit1" => PhoneKey.Digit1,
            "2" or "digit2" => PhoneKey.Digit2,
            "3" or "digit3" => PhoneKey.Digit3,
            "4" or "digit4" => PhoneKey.Digit4,
            "5" or "digit5" => PhoneKey.Digit5,
            "6" or "digit6" => PhoneKey.Digit6,
            "7" or "digit7" => PhoneKey.Digit7,
            "8" or "digit8" => PhoneKey.Digit8,
            "9" or "digit9" => PhoneKey.Digit9,
            "*" or "star" or "asterisk" => PhoneKey.Star,
            "#" or "hash" or "pound" => PhoneKey.Hash,
            "left" or "softleft" => PhoneKey.Left,
            "right" or "softright" => PhoneKey.Right,
            "menu" or "navi" or "ok" or "enter" => PhoneKey.Main,
            "c" or "cancel" or "back" or "del" => PhoneKey.Cancel,
            "power" => PhoneKey.Power,
            _ => null,
        };

    private static bool IsTextInputFocused(object? source)
        => source is TextBox;

    private void OnCcontSliderChanged(CcontAdcChannel channel, Slider slider, TextBlock valueText)
    {
        ushort value = ClampAdcValue(slider.Value);
        valueText.Text = FormatAdcValue(value);

        if (!syncingControlPanel)
        {
            emulator.SetCcontAdc(channel, value);
        }
    }

    private void OnDspRssiSliderChanged()
    {
        byte value = ClampDspRssiValue(dspRssiSlider.Value);
        dspRssiText.Text = FormatByteValue(value);

        if (!syncingControlPanel)
        {
            emulator.SetDspRssi(value);
        }
    }

    private void OnIncomingCallClick(object? sender, RoutedEventArgs e)
    {
        emulator.QueueIncomingCall(incomingNumberBox.Text ?? "");
    }

    private void OnIncomingSmsClick(object? sender, RoutedEventArgs e)
    {
        emulator.QueueIncomingSms(incomingNumberBox.Text ?? "", incomingSmsTextBox.Text ?? "");
    }

    private async void OnSaveUserNameClick(object? sender, RoutedEventArgs e)
    {
        if (profileManager is null)
            return;
        string requestedUserName = userNameBox.Text ?? "";
        try
        {
            await profileManager.UpdateUserNameAsync(requestedUserName);
        }
        catch (ArgumentException)
        {
            ToolTip.SetTip(userNameBox, "Use a name that fits the 16-byte Nokia EF_ADN phonebook field.");
        }
        catch
        {
            ToolTip.SetTip(userNameBox, "The user name was not saved. Try again.");
        }
        userNameBox.Text = profileManager.Profile.UserName;
    }

    private async void OnCopyNumberClick(object? sender, RoutedEventArgs e)
    {
        if (profileManager is null)
            return;
        string number = profileManager.Profile.FormattedPhoneNumber;
        try
        {
#if BROWSER
            await BrowserProfileInterop.CopyText(number);
#else
            await Task.CompletedTask;
#endif
        }
        catch
        {
            ToolTip.SetTip(copyNumberButton, "Copy is unavailable in this browser context.");
        }
    }

    private async void OnRecheckWakuClick(object? sender, RoutedEventArgs e)
    {
        if (wakuDiagnostics is null)
            return;
        recheckWakuButton.IsEnabled = false;
        wakuDiagnosticsSummaryText.Text = "Rechecking Waku peers and protocols…";
        try
        {
            await wakuDiagnostics.RefreshDiagnosticsAsync();
        }
        catch
        {
            ToolTip.SetTip(recheckWakuButton, "The Waku diagnostic recheck failed. See the latest error below.");
            UpdateWakuDiagnosticsUi();
        }
        finally
        {
            recheckWakuButton.IsEnabled = true;
        }
    }

    private async void OnCopyWakuDiagnosticsClick(object? sender, RoutedEventArgs e)
    {
        if (wakuDiagnostics is null)
            return;
        try
        {
#if BROWSER
            await BrowserProfileInterop.CopyText(wakuDiagnostics.DiagnosticsReport);
#else
            await Task.CompletedTask;
#endif
            copyWakuDiagnosticsButton.Content = "Copied";
        }
        catch
        {
            ToolTip.SetTip(copyWakuDiagnosticsButton, "Copy is unavailable in this browser context.");
        }
    }

    private void OnBackUpClick(object? sender, RoutedEventArgs e)
    {
        if (profileManager is null)
            return;
        restoringProfile = false;
        recoveryStatusText.Text = "Noks recovery phrase — do not enter into a cryptocurrency wallet.";
        recoveryPhraseBox.IsReadOnly = true;
        recoveryPhraseBox.Text = profileManager.Profile.CreateRecoveryPhrase();
        applyRecoveryButton.IsVisible = false;
        recoveryPanel.IsVisible = true;
    }

    private void OnRestoreClick(object? sender, RoutedEventArgs e)
    {
        if (profileManager is null)
            return;
        restoringProfile = true;
        recoveryStatusText.Text = "Noks recovery phrase — do not enter into a cryptocurrency wallet.";
        recoveryPhraseBox.IsReadOnly = false;
        recoveryPhraseBox.Text = "";
        applyRecoveryButton.IsVisible = true;
        recoveryPanel.IsVisible = true;
        recoveryPhraseBox.Focus();
    }

    private void OnExportWakuDataClick(object? sender, RoutedEventArgs e)
    {
        if (profileManager is null)
            return;
        try
        {
            string backup = WakuProfileBackupCodec.Serialize(profileManager.Profile);
            string fileName =
                $"noks-waku-{profileManager.Profile.PhoneNumber}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json";
#if BROWSER
            BrowserProfileInterop.DownloadJson(fileName, backup);
            SetDataManagementStatus("JSON DOWNLOADED", active: true);
#else
            SetDataManagementStatus("BROWSER ONLY", active: false);
#endif
        }
        catch (Exception ex)
        {
            SetDataManagementStatus("EXPORT FAILED", active: false);
            Console.WriteLine($"Noks Waku JSON export failed: {ex.Message}");
        }
    }

    private async void OnImportWakuDataClick(object? sender, RoutedEventArgs e)
    {
        if (profileManager is null)
            return;
#if BROWSER
        SetDataManagementButtonsEnabled(false);
        SetDataManagementStatus("CHOOSE JSON", active: true);
        bool reloading = false;
        try
        {
            string? backup = await BrowserProfileInterop.PickJsonFile();
            if (backup is null)
            {
                SetDataManagementStatus("IMPORT CANCELLED", active: true);
                return;
            }
            if (!WakuProfileBackupCodec.TryDeserialize(backup, out WakuProfile? imported) || imported is null)
            {
                SetDataManagementStatus("INVALID BACKUP", active: false);
                return;
            }
            using (imported)
            {
                if (!BrowserProfileInterop.ConfirmDataImport())
                {
                    SetDataManagementStatus("IMPORT CANCELLED", active: true);
                    return;
                }
                string profileJson = WakuProfileCodec.Serialize(imported);
                SetDataManagementStatus("RESTORING…", active: true);
                BrowserProfileInterop.StageDataReplacementAndReload(profileJson, clearAllProfiles: false);
                reloading = true;
            }
        }
        catch (Exception ex)
        {
            SetDataManagementStatus("IMPORT FAILED", active: false);
            Console.WriteLine($"Noks Waku JSON import failed: {ex.Message}");
        }
        finally
        {
            if (!reloading)
                SetDataManagementButtonsEnabled(true);
        }
#else
        await Task.CompletedTask;
        SetDataManagementStatus("BROWSER ONLY", active: false);
#endif
    }

    private async void OnResetAllDataClick(object? sender, RoutedEventArgs e)
    {
        if (profileManager is null)
            return;
#if BROWSER
        if (!BrowserProfileInterop.ConfirmFullReset())
            return;
        SetDataManagementButtonsEnabled(false);
        SetDataManagementStatus("RESETTING…", active: true);
        bool reloading = false;
        try
        {
            using WakuProfile replacement = WakuProfile.CreateNew();
            BrowserProfileInterop.StageDataReplacementAndReload(
                WakuProfileCodec.Serialize(replacement),
                clearAllProfiles: true);
            reloading = true;
        }
        catch (Exception ex)
        {
            SetDataManagementStatus("RESET FAILED", active: false);
            Console.WriteLine($"Noks full browser data reset failed: {ex.Message}");
        }
        finally
        {
            if (!reloading)
                SetDataManagementButtonsEnabled(true);
        }
#else
        await Task.CompletedTask;
        SetDataManagementStatus("BROWSER ONLY", active: false);
#endif
    }

    private void SetDataManagementButtonsEnabled(bool enabled)
    {
        exportWakuDataButton.IsEnabled = enabled;
        importWakuDataButton.IsEnabled = enabled;
        resetAllDataButton.IsEnabled = enabled;
    }

    private void SetDataManagementStatus(string text, bool active)
    {
        dataManagementStatusText.Text = text;
        dataManagementStatusText.Foreground = active ? MutedTextBrush : ErrorTextBrush;
    }

    private async void OnApplyRecoveryClick(object? sender, RoutedEventArgs e)
    {
        if (profileManager is null || !restoringProfile)
            return;
        applyRecoveryButton.IsEnabled = false;
        try
        {
            await profileManager.RestoreAsync(recoveryPhraseBox.Text ?? "");
            CloseRecoveryPanel();
        }
        catch (FormatException)
        {
            recoveryStatusText.Text =
                "Invalid Noks recovery phrase. Check all 24 words and the checksum. " +
                "Do not enter this phrase into a cryptocurrency wallet.";
        }
        catch
        {
            recoveryStatusText.Text =
                "The profile restore or save operation failed. Try again without leaving this page.";
        }
        finally
        {
            applyRecoveryButton.IsEnabled = true;
        }
    }

    private void OnCancelRecoveryClick(object? sender, RoutedEventArgs e) => CloseRecoveryPanel();

    private void CloseRecoveryPanel()
    {
        restoringProfile = false;
        recoveryPhraseBox.Text = "";
        recoveryPhraseBox.IsReadOnly = true;
        recoveryPanel.IsVisible = false;
    }

    private void OnProfileChanged(WakuProfile _) => Dispatcher.UIThread.Post(UpdateProfileUi);

    private void OnWakuDiagnosticsChanged(WakuTransportDiagnostics _) =>
        Dispatcher.UIThread.Post(UpdateWakuDiagnosticsUi);

    private void OnBridgeStatusChanged(WakuPhoneBridge source)
    {
        ApplyWakuNetworkStatus(emulator, source.Status);
        Dispatcher.UIThread.Post(UpdateProfileUi);
    }

    private static void ApplyWakuNetworkStatus(
        PhoneEmulator target,
        WakuPhoneBridgeStatus status) =>
        target.SetFacadeNetworkAvailable(status == WakuPhoneBridgeStatus.Online);

    private void UpdateProfileUi()
    {
        if (profileManager is null)
            return;
        WakuProfile profile = profileManager.Profile;
        bool updateUserName = !userNameBox.IsKeyboardFocusWithin;
        if (updateUserName && userNameBox.Text != profile.UserName)
        {
            userNameBox.Text = profile.UserName;
        }
        myNumberText.Text = profile.FormattedPhoneNumber;
        networkStatusText.Text = wakuBridge?.Status switch
        {
            WakuPhoneBridgeStatus.Online => "Online",
            WakuPhoneBridgeStatus.Connecting => "Connecting",
            _ => "Offline",
        };
        networkStatusText.Foreground = wakuBridge?.Status == WakuPhoneBridgeStatus.Online
            ? BadgeOnTextBrush
            : MutedTextBrush;
    }

    private void UpdateWakuDiagnosticsUi()
    {
        if (wakuDiagnostics is null)
            return;
        WakuTransportDiagnostics value = wakuDiagnostics.Diagnostics;
        string peers = value.PeerCount == 1 ? "1 peer" : $"{value.PeerCount} peers";
        wakuDiagnosticsSummaryText.Text = value.Phase switch
        {
            "starting" => $"Starting {value.Mode} Waku light node",
            "ready" =>
                $"{peers} · Push {YesNo(value.LightPushReady)} · Filter {YesNo(value.FilterReady)} · Store {YesNo(value.StoreReady)}",
            "error" => $"Waku startup failed · {value.LastError ?? "unknown error"}",
            "disposed" => "Waku transport stopped",
            _ => "Waiting for the Waku transport",
        };
        wakuDiagnosticsSummaryText.Foreground = value.Phase == "ready"
            ? BadgeOnBrush
            : value.Phase == "error"
                ? ErrorTextBrush
                : TextBrush;
        wakuDiagnosticsDetailText.Text =
            $"{value.TopicCount} shared topics · TX {value.PublishSuccesses}/{value.PublishAttempts} accepted" +
            $" ({value.PublishFailures} failed) · RX {value.LiveMessages} live ·" +
            $" Store {value.StoreRecords} records / {value.StoreQueries} queries · last {value.LastEvent}";

        StringBuilder log = new();
        foreach (WakuTransportPeerDiagnostic peer in value.Peers)
        {
            log.Append("PEER ")
                .Append(TruncateDiagnostic(peer.Id, 24))
                .Append(" · ")
                .Append(peer.Services.Count == 0 ? "other" : string.Join(", ", peer.Services))
                .Append(" · ")
                .AppendLine(peer.Address);
        }
        foreach (WakuTransportDiagnosticEvent item in value.RecentEvents.TakeLast(30).Reverse())
        {
            log.Append(item.At == default ? "--:--:--" : item.At.ToLocalTime().ToString("HH:mm:ss"))
                .Append(" · ")
                .Append(item.Direction)
                .Append(" · ")
                .Append(item.Event)
                .Append(' ')
                .AppendLine(TruncateDiagnostic(item.Details, 240));
        }
        wakuDiagnosticsLogBox.Text = log.Length == 0
            ? "No Waku transport events yet."
            : log.ToString().TrimEnd();
        copyWakuDiagnosticsButton.Content = "Copy diagnostics";
    }

    private static string YesNo(bool value) => value ? "Yes" : "No";

    private static string TruncateDiagnostic(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : string.Concat(value.AsSpan(0, maximumLength - 3), "...");

    private void AttachBridgeToEmulator(PhoneEmulator target)
    {
        target.NetworkRequestAvailable += OnEmulatorNetworkRequestAvailable;
        target.SimMutationAvailable += OnEmulatorSimMutationAvailable;
    }

    private void DetachBridgeFromEmulator(PhoneEmulator target)
    {
        target.NetworkRequestAvailable -= OnEmulatorNetworkRequestAvailable;
        target.SimMutationAvailable -= OnEmulatorSimMutationAvailable;
    }

    private void OnEmulatorNetworkRequestAvailable(PhoneEmulator source)
    {
        WakuBridgeIngress.DrainNetworkRequests(source, wakuBridge);
    }

    private void OnEmulatorSimMutationAvailable(PhoneEmulator source)
    {
        while (source.TryDequeueSimMutation(out SimMutation? mutation) && mutation is not null)
            wakuBridge?.TryEnqueue(mutation);
    }

    private void OnEmulatorCallTransitionAvailable(PhoneEmulator source)
    {
        while (source.TryDequeueCallTransition(out CallTransition? transition) && transition is not null)
        {
            if (transition.Kind is CallTransitionKind.Reject or CallTransitionKind.Hangup)
            {
                Guid callId = transition.RequestId;
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(emulator, source))
                        audio?.StopAnnouncement(callId);
                });
            }
            wakuBridge?.TryEnqueue(transition);
        }
    }

    private void OnBridgeCommandAvailable(WakuPhoneBridge source)
    {
        while (source.TryDequeueCommand(out WakuPhoneCommand? command) && command is not null)
        {
            PhoneEmulator target = emulator;
            switch (command.Kind)
            {
                case WakuPhoneCommandKind.ResolveNetworkRequest:
                    target.ResolveNetworkRequest(new ResolveNetworkRequest(command.RequestId, command.Decision));
                    break;
                case WakuPhoneCommandKind.QueueIncomingSmartMessage:
                    target.QueueIncomingSmartMessage(command.Address, command.DestinationPort, command.Payload.AsSpan());
                    break;
                case WakuPhoneCommandKind.QueueIncomingCall:
                    target.QueueIncomingCall(command.RequestId, command.Address);
                    break;
                case WakuPhoneCommandKind.QueueIncomingSms:
                    target.QueueIncomingSms(
                        command.Address,
                        command.Text,
                        DateTimeOffset.FromUnixTimeMilliseconds(command.IssuedAtUnixMilliseconds).ToLocalTime());
                    break;
                case WakuPhoneCommandKind.SetManagedOwnNumber:
                    target.SetManagedOwnNumber(command.Address);
                    break;
                case WakuPhoneCommandKind.BeginCallMedia:
#if BROWSER
                    Dispatcher.UIThread.Post(() => _ = BeginBrowserCallMediaAsync(command));
#else
                    // The desktop has no WebRTC audio path. The bridge immediately reports media readiness.
                    // Thus, call signaling (ringing, GSM CONNECT) continues without real voice audio.
                    wakuBridge?.TryEnqueue(WakuCallMediaEvent.State(
                        command.RequestId,
                        WakuCallMediaEventKind.Connected));
#endif
                    break;
                case WakuPhoneCommandKind.ActivateCallMedia:
#if BROWSER
                    Dispatcher.UIThread.Post(() => _ = ActivateBrowserCallMediaAsync(command.RequestId));
#endif
                    break;
                case WakuPhoneCommandKind.ApplyCallMediaSignal:
#if BROWSER
                    Dispatcher.UIThread.Post(() => _ = ApplyBrowserCallMediaSignalAsync(command));
#endif
                    break;
                case WakuPhoneCommandKind.EndCallMedia:
#if BROWSER
                    Dispatcher.UIThread.Post(() => _ = EndBrowserCallMediaAsync(command.RequestId));
#endif
                    break;
                case WakuPhoneCommandKind.ConnectNetworkCall:
                    target.ConnectNetworkCall(command.RequestId);
                    break;
                case WakuPhoneCommandKind.TerminateNetworkCall:
                    target.TerminateNetworkCall(command.RequestId);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command));
            }
        }
    }

#if BROWSER
    private void OnBrowserCallMediaEvent(string attemptIdText, int kindValue, string payloadBase64)
    {
        if (wakuBridge is null ||
            !Guid.TryParseExact(attemptIdText, "D", out Guid attemptId) ||
            kindValue < (int)WakuCallMediaEventKind.SdpOffer ||
            kindValue > (int)WakuCallMediaEventKind.Failed ||
            payloadBase64.Length > MaximumBrowserCallSignalBase64Length)
        {
            return;
        }

        WakuCallMediaEventKind kind = (WakuCallMediaEventKind)kindValue;
        if (kind is WakuCallMediaEventKind.Connected or WakuCallMediaEventKind.Failed)
        {
            wakuBridge.TryEnqueue(WakuCallMediaEvent.State(attemptId, kind));
            return;
        }

        try
        {
            byte[] payload = Convert.FromBase64String(payloadBase64);
            if (payload.Length is > 0 and <= WakuCallSignalCodec.MaximumSignalSize)
                wakuBridge.TryEnqueue(WakuCallMediaEvent.Signal(attemptId, kind, payload));
        }
        catch (FormatException)
        {
        }
    }

    private async Task BeginBrowserCallMediaAsync(WakuPhoneCommand command)
    {
        try
        {
            await BrowserCallMediaInterop.Begin(command.RequestId.ToString("D"), command.IsCaller);
        }
        catch
        {
            wakuBridge?.TryEnqueue(WakuCallMediaEvent.State(
                command.RequestId,
                WakuCallMediaEventKind.Failed));
        }
    }

    private async Task ApplyBrowserCallMediaSignalAsync(WakuPhoneCommand command)
    {
        try
        {
            await BrowserCallMediaInterop.Apply(
                command.RequestId.ToString("D"),
                (int)command.EventKind,
                Convert.ToBase64String(command.Payload.AsSpan()));
        }
        catch
        {
            wakuBridge?.TryEnqueue(WakuCallMediaEvent.State(
                command.RequestId,
                WakuCallMediaEventKind.Failed));
        }
    }

    private async Task ActivateBrowserCallMediaAsync(Guid attemptId)
    {
        try
        {
            await BrowserCallMediaInterop.Activate(attemptId.ToString("D"));
        }
        catch
        {
            wakuBridge?.TryEnqueue(WakuCallMediaEvent.State(
                attemptId,
                WakuCallMediaEventKind.Failed));
        }
    }

    private static async Task EndBrowserCallMediaAsync(Guid attemptId)
    {
        try
        {
            await BrowserCallMediaInterop.End(attemptId.ToString("D"));
        }
        catch
        {
        }
    }
#endif

    private void OnRingtoneNotationChanged(object? sender, TextChangedEventArgs e)
    {
        if (!NokiaSmartMessagingRingtone.TryParseRtttlMetadata(
            ringtoneNotationBox.Text ?? "",
            out NokiaSmartMessagingRingtone.RtttlMetadata metadata))
        {
            return;
        }

        ringtoneNameBox.Text = metadata.Title;
        ringtoneTempoBox.Text = metadata.BeatsPerMinute.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private void OnIncomingRingtoneClick(object? sender, RoutedEventArgs e)
    {
        string notation = ringtoneNotationBox.Text ?? "";
        bool isRtttl = NokiaSmartMessagingRingtone.IsRtttl(notation);
        int tempo = 63;
        if (!isRtttl &&
            (!int.TryParse(ringtoneTempoBox.Text?.Trim(), out tempo) || tempo <= 0))
        {
            SetRingtoneStatus("BAD BPM", active: false, "Tempo must be a positive whole number.");
            return;
        }

        try
        {
            byte[] payload = NokiaSmartMessagingRingtone.Encode(
                ringtoneNameBox.Text?.Trim() ?? "",
                tempo,
                notation);
            emulator.QueueIncomingSmartMessage(
                incomingNumberBox.Text ?? "",
                NokiaSmartMessagingRingtone.DestinationPort,
                payload);
            string format = isRtttl ? "RTTTL" : "COMPOSER";
            int smsPartCount = NokiaSmartMessagingRingtone.GetSmsPartCount(payload.Length);
            SetRingtoneStatus(
                smsPartCount == 1
                    ? $"QUEUED {payload.Length}B {format}"
                    : $"QUEUED {smsPartCount} SMS",
                active: true,
                $"{format} Smart Messaging ringtone queued for delivery " +
                $"({payload.Length} bytes across {smsPartCount} SMS part{(smsPartCount == 1 ? "" : "s")}).");
        }
        catch (ArgumentException ex)
        {
            SetRingtoneStatus("BAD TONE", active: false, ex.Message);
        }
    }

    private async void OnApplyPhoneSettingsClick(object? sender, RoutedEventArgs e)
    {
        string? simImsi = NormalizeSimImsi(simImsiBox.Text);
        if (simImsiBox.Text?.Trim().Length > 0 && simImsi is null)
        {
            SetPhoneSettingsStatus("BAD IMSI", active: false);
            return;
        }

        string networkName = NormalizeNetworkName(networkNameBox.Text);
        networkNameBox.Text = networkName;
        Dct3PhoneSettings settings = emulator.Settings with
        {
            SimImsi = simImsi,
            NetworkName = networkName,
            OwnPhoneNumber = profileManager?.Profile.PhoneNumber ?? emulator.Settings.EffectiveOwnPhoneNumber,
        };

        if (settings == emulator.Settings)
        {
            SetPhoneSettingsStatus("CURRENT", active: true);
            return;
        }

        await ApplyPhoneSettingsAsync(settings);
    }

    private async Task ApplyPhoneSettingsAsync(Dct3PhoneSettings settings)
    {
        SetPhoneSettingsStatus("APPLYING", active: false);
        applyPhoneSettingsButton.IsEnabled = false;

        try
        {
            ReleaseAllInputKeys();

            PhoneEmulator oldEmulator = emulator;
            PhoneEmulator newEmulator = await recreateEmulator(settings);
            oldEmulator.FrameChanged -= OnEmulatorFrameChanged;
            oldEmulator.AudioStateChanged -= OnEmulatorAudioStateChanged;
            oldEmulator.AudioAnnouncementAvailable -= OnEmulatorAudioAnnouncementAvailable;
            oldEmulator.CallTransitionAvailable -= OnEmulatorCallTransitionAvailable;
            oldEmulator.StateChanged -= OnEmulatorStateChanged;
            oldEmulator.TelemetryChanged -= OnEmulatorTelemetryChanged;
            oldEmulator.LogAvailable -= OnEmulatorLogAvailable;
            if (wakuBridge is not null)
                DetachBridgeFromEmulator(oldEmulator);
            emulator = newEmulator;
            newEmulator.FrameChanged += OnEmulatorFrameChanged;
            newEmulator.AudioStateChanged += OnEmulatorAudioStateChanged;
            newEmulator.AudioAnnouncementAvailable += OnEmulatorAudioAnnouncementAvailable;
            newEmulator.CallTransitionAvailable += OnEmulatorCallTransitionAvailable;
            newEmulator.StateChanged += OnEmulatorStateChanged;
            if (telemetryPanelFrame?.IsVisible == true)
            {
                newEmulator.TelemetryChanged += OnEmulatorTelemetryChanged;
            }

            if (logEnabled)
            {
                newEmulator.LogAvailable += OnEmulatorLogAvailable;
            }
            if (wakuBridge is not null)
                AttachBridgeToEmulator(newEmulator);

            if (wakuBridge is not null)
                ApplyWakuNetworkStatus(newEmulator, wakuBridge.Status);

            newEmulator.SetLoggingEnabled(logEnabled);
            lcd.Emulator = newEmulator;
            lastInvalidatedLcdDataWrites = -1;
            lastInvalidatedLcdBacklightOn = null;
            ResetUiStateCache();
            oldEmulator.Dispose();
            newEmulator.Start();
            UpdateFromCurrentState(force: true);
            OnEmulatorTelemetryChanged(newEmulator);
#if BROWSER
            BrowserSettingsInterop.ApplyPhoneSettings(settings.SimImsi ?? "", settings.EffectiveNetworkName);
#endif
            Console.WriteLine(
                $"Noks phone settings applied: sim={settings.SimImsi ?? "auto"} network=\"{settings.EffectiveNetworkName}\"");
            SetPhoneSettingsStatus("APPLIED", active: true);
        }
        catch (Exception ex)
        {
            SetPhoneSettingsStatus("FAILED", active: false);
            Console.WriteLine($"Noks phone settings apply failed: {ex.Message}");
        }
        finally
        {
            applyPhoneSettingsButton.IsEnabled = true;
        }
    }

    private void OnChargerPresentToggleClick(object? sender, RoutedEventArgs e)
    {
        if (syncingControlPanel)
        {
            return;
        }

        ushort value = chargerPresentToggle.IsChecked == true ? DefaultChargerVoltage : (ushort)0;
        SetLocalCcontAdcValue(CcontAdcChannel.ChargerVoltage, value);
        emulator.SetCcontAdc(CcontAdcChannel.ChargerVoltage, value);
    }

    private void OnTelemetryToggleClick(object? sender, RoutedEventArgs e)
    {
        bool visible = telemetryToggle.IsChecked == true;
        if (telemetryPanelFrame is not null)
        {
            telemetryPanelFrame.IsVisible = visible;
        }

        SetPanelToggle(telemetryToggle, "TEL", visible, visible ? "Hide telemetry" : "Show telemetry");
        if (visible)
        {
            emulator.TelemetryChanged += OnEmulatorTelemetryChanged;
            OnEmulatorTelemetryChanged(emulator);
        }
        else
        {
            emulator.TelemetryChanged -= OnEmulatorTelemetryChanged;
        }
    }

    private void OnPqcRendezvousToggleClick(object? sender, RoutedEventArgs e)
    {
        bool enabled = pqcRendezvousToggle.IsChecked == true;
        wakuBridge?.SetPostQuantumRendezvousEnabled(enabled);
        UpdatePqcRendezvousUi(wakuBridge?.PostQuantumRendezvousEnabled ?? enabled);
    }

    private void UpdatePqcRendezvousUi(bool enabled)
    {
        bool required = wakuBridge?.PostQuantumRendezvousRequired == true;
        pqcRendezvousText.Text = required ? "PQC ONLY" : enabled ? "PQC ALL" : "Classic";
        pqcRendezvousText.Foreground = enabled ? BadgeOnTextBrush : MutedTextBrush;
        SetPanelToggle(
            pqcRendezvousToggle,
            "PQC",
            enabled,
            required
                ? "PQC is enforced for Noks identities, rendezvous, contact cards, and direct packets: ML-DSA-65 + ML-KEM-768 + AES-256-GCM"
                : enabled
                ? "PQC key exchange is active for rendezvous and direct packets: ML-KEM-768 + AES-256-GCM. Rendezvous descriptors use ML-DSA and proof of work."
                : "Use classic X25519/ChaCha20-Poly1305 packets and rendezvous");
    }

    private void OnLogFilterChanged(object? sender, SelectionChangedEventArgs e) => RebuildVisibleLog();

    private void OnLogPauseClick(object? sender, RoutedEventArgs e)
    {
        logPaused = logPauseToggle.IsChecked == true;
        SetPanelToggle(logPauseToggle, "PAUSE", logPaused, logPaused ? "Resume log display" : "Pause log display");
        if (!logPaused)
        {
            RebuildVisibleLog();
        }
    }

    private void OnLogClearClick(object? sender, RoutedEventArgs e)
    {
        logHistory.Clear();
        visibleLogEntries.Clear();
    }

    private bool DrainEmulationLog()
    {
        if (!logEnabled)
        {
            return false;
        }

        bool followTail = IsLogAtBottom();
        int drained = 0;
        List<EmulationLogEntry> addedVisibleEntries = [];
        while (drained < 1_000 && emulator.TryDequeueLog(out EmulationLogEntry? entry))
        {
            drained++;
            if (entry is null)
            {
                continue;
            }

            logHistory.Add(entry);
            if (!logPaused && LogEntryMatchesFilter(entry))
            {
                addedVisibleEntries.Add(entry);
            }
        }

        if (logHistory.Count > MaximumLogEntries)
        {
            logHistory.RemoveRange(0, logHistory.Count - MaximumLogEntries);
        }

        if (!logPaused && logHistory.Count > 0 && visibleLogEntries.Count > 0)
        {
            long oldestRetainedSequence = logHistory[0].Sequence;
            int expiredVisibleEntries = 0;
            while (expiredVisibleEntries < visibleLogEntries.Count &&
                visibleLogEntries[expiredVisibleEntries].Sequence < oldestRetainedSequence)
            {
                expiredVisibleEntries++;
            }

            if (expiredVisibleEntries > 0)
            {
                visibleLogEntries.RemoveRange(0, expiredVisibleEntries);
            }
        }

        if (addedVisibleEntries.Count > 0)
        {
            visibleLogEntries.AddRange(addedVisibleEntries);
        }

        if (visibleLogEntries.Count > MaximumLogEntries)
        {
            visibleLogEntries.RemoveRange(0, visibleLogEntries.Count - MaximumLogEntries);
        }

        if (addedVisibleEntries.Count > 0 && followTail)
        {
            ScheduleLogTailScroll();
        }

        return drained == 1_000;
    }

    private void RebuildVisibleLog()
    {
        string filter = logFilterBox.SelectedItem as string ?? "ALL";
        List<EmulationLogEntry> filteredEntries = new(Math.Min(logHistory.Count, MaximumLogEntries));
        foreach (EmulationLogEntry entry in logHistory)
        {
            if (filter == "ALL" || string.Equals(filter, entry.Channel.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                filteredEntries.Add(entry);
            }
        }

        visibleLogEntries.Clear();
        visibleLogEntries.AddRange(filteredEntries);
        ScheduleLogTailScroll();
    }

    private bool LogEntryMatchesFilter(EmulationLogEntry entry)
    {
        string filter = logFilterBox.SelectedItem as string ?? "ALL";
        return filter == "ALL" || string.Equals(filter, entry.Channel.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private bool IsLogAtBottom()
    {
        ScrollViewer? scroll = ResolveLogScrollViewer();
        return scroll is null ||
            scroll.Extent.Height <= scroll.Viewport.Height ||
            scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 2;
    }

    private ScrollViewer? ResolveLogScrollViewer()
    {
        logScrollViewer ??= logList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        return logScrollViewer;
    }

    private void ScheduleLogTailScroll()
    {
        if (logTailScrollQueued || visibleLogEntries.Count == 0)
        {
            return;
        }

        logTailScrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            logTailScrollQueued = false;
            ScrollViewer? scroll = ResolveLogScrollViewer();
            if (scroll is not null)
            {
                scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
            }
        }, DispatcherPriority.Background);
    }

    private void OnResetCcontClick(object? sender, RoutedEventArgs e)
    {
        emulator.ResetCcontAdcInputs();
        UpdateControlPanel(CcontControlState.Normal);
    }

    private void OnAudioMuteToggleClick(object? sender, RoutedEventArgs e)
    {
        audioMuted = audioMuteToggle.IsChecked == true;
        SetPanelToggle(audioMuteToggle, "MUTE", audioMuted,
            audioMuted ? "Host audio muted" : "Mute host audio");
        UpdateAudio(emulator.AudioState);
    }

#if BROWSER
    private async void OnSpeakerReactivateClick(object? sender, RoutedEventArgs e)
    {
        speakerReactivateButton.Content = "SPEAKER...";
        ToolTip.SetTip(speakerReactivateButton, "Reactivating browser audio output");

        try
        {
            Task<bool> appAudioReady = BrowserAudioInterop.Reactivate();
            Task<bool> callAudioReady = BrowserCallMediaInterop.ReactivatePlayback();
            bool ready = await appAudioReady & await callAudioReady;
            speakerReactivateButton.Content = ready ? "SPEAKER ON" : "SPEAKER RETRY";
            ToolTip.SetTip(
                speakerReactivateButton,
                ready
                    ? "Browser speaker audio is active. If audio stops, press again."
                    : "The browser blocked one or more audio paths. Press to try again.");
        }
        catch
        {
            speakerReactivateButton.Content = "SPEAKER RETRY";
            ToolTip.SetTip(speakerReactivateButton, "Browser audio reactivation failed. Press to try again.");
        }
    }
#endif

    private void OnLcdBacklightToggleClick(object? sender, RoutedEventArgs e)
    {
        bool resetOverride = IsOverrideResetHold(lcdBacklightPressedAtMilliseconds);
        lcdBacklightOverride = resetOverride ? null : lcdBacklightToggle.IsChecked == true;
        lcdBacklightPressedAtMilliseconds = 0;
        lcdBacklightResetAtMilliseconds = resetOverride ? Environment.TickCount64 : 0;
        if (resetOverride)
        {
            resetFlashTimer.Stop();
            resetFlashTimer.Start();
        }

        UpdateFromCurrentState(force: true);
    }

    private void OnKeypadBacklightToggleClick(object? sender, RoutedEventArgs e)
    {
        bool resetOverride = IsOverrideResetHold(keypadBacklightPressedAtMilliseconds);
        keypadBacklightOverride = resetOverride ? null : keypadBacklightToggle.IsChecked == true;
        keypadBacklightPressedAtMilliseconds = 0;
        keypadBacklightResetAtMilliseconds = resetOverride ? Environment.TickCount64 : 0;
        if (resetOverride)
        {
            resetFlashTimer.Stop();
            resetFlashTimer.Start();
        }

        UpdateFromCurrentState(force: true);
    }

    private void OnLcdBacklightTogglePressed(object? sender, PointerPressedEventArgs e)
    {
        lcdBacklightPressedAtMilliseconds = Environment.TickCount64;
    }

    private void OnKeypadBacklightTogglePressed(object? sender, PointerPressedEventArgs e)
    {
        keypadBacklightPressedAtMilliseconds = Environment.TickCount64;
    }

    private void TryCreateAudio()
    {
        try
        {
            audio = PhoneAudio.Create();
            audio?.SetAnnouncementEndedHandler(OnAudioAnnouncementEnded);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Audio unavailable: {ex.Message}");
            audio = null;
        }
    }

    private void UpdateAudio(Dct3AudioState state)
    {
        if (audio is null)
        {
            return;
        }

        try
        {
            audio.Update(audioMuted ? Dct3AudioState.Off : state);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Audio stopped: {ex.Message}");
            audio.Dispose();
            audio = null;
        }
    }

    private void OnEmulatorAudioStateChanged(PhoneEmulator source)
    {
#if BROWSER
        BrowserInteractionLatencyBenchmark.MarkAudioEventRaised();
#endif
        if (Interlocked.Exchange(ref audioRefreshQueued, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref audioRefreshQueued, 0);
            if (ReferenceEquals(emulator, source))
            {
#if BROWSER
                BrowserInteractionLatencyBenchmark.MarkAudioUiDispatch();
#endif
                UpdateAudio(source.AudioState);
            }
        }, DispatcherPriority.Background);
    }

    private void OnEmulatorAudioAnnouncementAvailable(PhoneEmulator source)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(emulator, source))
            {
                return;
            }

            while (source.TryDequeueAudioAnnouncement(out CallAudioAnnouncement? announcement))
            {
                if (announcement is null)
                {
                    continue;
                }

                if (audioMuted || audio is null || !audio.SupportsAnnouncements)
                {
                    source.TerminateNetworkCall(announcement.CallId);
                    continue;
                }

                try
                {
                    audio.PlayAnnouncement(announcement);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Call announcement failed: {ex.Message}");
                    source.TerminateNetworkCall(announcement.CallId);
                }
            }
        }, DispatcherPriority.Background);
    }

    private void OnAudioAnnouncementEnded(Guid callId)
    {
        emulator.TerminateNetworkCall(callId);
    }

    private void OnEmulatorStateChanged(PhoneEmulator source) => QueueStateRefresh(source);

    private void QueueStateRefresh(PhoneEmulator source)
    {
        if (!ReferenceEquals(source, System.Threading.Volatile.Read(ref emulator)) ||
            Interlocked.Exchange(ref stateRefreshQueued, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref stateRefreshQueued, 0);
            if (ReferenceEquals(source, emulator))
            {
                UpdateFromCurrentState();
            }
        }, DispatcherPriority.Background);
    }

    private void OnEmulatorTelemetryChanged(PhoneEmulator source)
    {
        if (!ReferenceEquals(source, System.Threading.Volatile.Read(ref emulator)) ||
            telemetryPanelFrame?.IsVisible != true ||
            Interlocked.Exchange(ref telemetryRefreshQueued, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref telemetryRefreshQueued, 0);
            if (!ReferenceEquals(source, emulator) || telemetryPanelFrame?.IsVisible != true)
            {
                return;
            }

            PhoneTelemetryState state = source.Telemetry;
            if (state != lastTelemetryUiState)
            {
                UpdateTelemetryPanel(state);
                lastTelemetryUiState = state;
            }
        }, DispatcherPriority.Background);
    }

    private void OnEmulatorLogAvailable(PhoneEmulator source)
    {
        if (!logEnabled ||
            !ReferenceEquals(source, System.Threading.Volatile.Read(ref emulator)) ||
            Interlocked.Exchange(ref logRefreshQueued, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref logRefreshQueued, 0);
            if (logEnabled && ReferenceEquals(source, emulator) && DrainEmulationLog())
            {
                OnEmulatorLogAvailable(source);
            }
        }, DispatcherPriority.Background);
    }

    private void OnEmulatorFrameChanged(PhoneEmulator source)
    {
        if (!ReferenceEquals(source, System.Threading.Volatile.Read(ref emulator)) ||
            Interlocked.Exchange(ref lcdRefreshQueued, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(source, emulator))
            {
                Interlocked.Exchange(ref lcdRefreshQueued, 0);
                return;
            }

            Mad2PeripheralState state = emulator.PeripheralState;
            bool lcdBacklightOn = lcdBacklightOverride ?? state.LcdBacklightOn;
            if (!InvalidateLcdIfChanged(lcdBacklightOn))
            {
                Interlocked.Exchange(ref lcdRefreshQueued, 0);
            }
        });
    }

    private bool InvalidateLcdIfChanged(bool lcdBacklightOn, bool force = false)
    {
        long dataWrites = emulator.Frame.DataWrites;

        if (!force &&
            dataWrites == lastInvalidatedLcdDataWrites &&
            lastInvalidatedLcdBacklightOn == lcdBacklightOn)
        {
            return false;
        }

        lastInvalidatedLcdDataWrites = dataWrites;
        lastInvalidatedLcdBacklightOn = lcdBacklightOn;
        QueueLcdBitmapUpdate();
        return true;
    }

    private void QueueLcdBitmapUpdate()
    {
        if (lcdBitmapUpdateQueued)
        {
            return;
        }

        lcdBitmapUpdateQueued = true;
        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            FlushLcdBitmapUpdate();
            return;
        }

        topLevel.RequestAnimationFrame(_ => FlushLcdBitmapUpdate());
    }

    private void FlushLcdBitmapUpdate()
    {
        lcdBitmapUpdateQueued = false;
        lcd.UpdateBitmap();
        Interlocked.Exchange(ref lcdRefreshQueued, 0);
    }

    private void ResetUiStateCache()
    {
        lastPeripheralUiState = null;
        lastCcontUiState = null;
        lastGsmUiState = null;
        lastDspRadioUiState = null;
        lastTelemetryUiState = null;
        lastLcdBacklightUiState = null;
        lastKeypadBacklightUiState = null;
        lastLcdResetFlashUiState = null;
        lastKeypadResetFlashUiState = null;
    }

    private void UpdateRuntimeBadge()
    {
        string status = emulator.Status;
        double runtimeSeconds = emulator.Cycles / (double)Dct3Machine.CyclesPerSecond;
        EmulationPacing pacing = emulator.Pacing;
        runtimeText.Text = $"{ShortStatus(status)} {runtimeSeconds:F1}s";
        ToolTip.SetTip(runtimeBadge, $"{status}. Pacing {pacing.RateScale:F5}x. Drift {pacing.DriftMilliseconds:+0.0;-0.0;0.0} ms.");
        SetIndicatorBadge(runtimeBadge, runtimeText, true, neutral: true);
    }

    private void UpdatePeripheralBadges(
        Mad2PeripheralState state,
        bool lcdBacklightOn,
        bool keypadBacklightOn,
        bool lcdResetFlashActive,
        bool keypadResetFlashActive)
    {
        SetLightToggle(lcdBacklightToggle, "LCD", lcdBacklightOn, lcdBacklightOverride.HasValue, lcdResetFlashActive);
        SetLightToggle(keypadBacklightToggle, "KB", keypadBacklightOn, keypadBacklightOverride.HasValue, keypadResetFlashActive);
        vibrationText.Text = "VIB";
        ToolTip.SetTip(vibrationBadge, state.VibratorEnabled ? "Vibration on" : "Vibration off");
        SetIndicatorBadge(vibrationBadge, vibrationText, state.VibratorEnabled, neutral: false);
    }

    private void UpdateControlPanel(CcontControlState state)
    {
        syncingControlPanel = true;

        try
        {
            foreach ((CcontAdcChannel channel, CcontAdcControl control) in ccontAdcControls)
            {
                ushort value = state.Get(channel);

                if (control.Editing)
                {
                    control.ValueText.Text = FormatAdcValue(ClampAdcValue(control.Slider.Value));
                }
                else
                {
                    control.ValueText.Text = FormatAdcValue(value);

                    if (Math.Abs(control.Slider.Value - value) > 0.5)
                    {
                        control.Slider.Value = value;
                    }
                }
            }

            SetPanelToggle(chargerPresentToggle, "CHG", state.ChargerPresent, state.ChargerPresent ? "Charger detected" : "Charger absent");
            ccontPwmText.Text = state.ChargerPwmEnabled ? $"PWM {state.ChargerPwm:X2}" : "PWM";
            ToolTip.SetTip(ccontPwmBadge, state.ChargerPwmEnabled ? $"Charger PWM 0x{state.ChargerPwm:X2}" : "Charger PWM off");
            SetIndicatorBadge(ccontPwmBadge, ccontPwmText, state.ChargerPwmEnabled, neutral: false);
            UpdateFirmwareBatteryBadges(state);
        }
        finally
        {
            syncingControlPanel = false;
        }
    }

    private void UpdateFirmwareBatteryBadges(CcontControlState state)
    {
        firmwareBatteryText.Text = $"BAT {state.FirmwareBatteryPercent}";
        ToolTip.SetTip(
            firmwareBatteryBadge,
            $"Firmware battery percent 0x{state.FirmwareBatteryPercent:X2}, class 0x{state.FirmwareBatteryClass:X2}, flags 0x{state.FirmwareBatteryFlags:X2}, sample 0x{state.FirmwareBatterySample:X4}");
        SetIndicatorBadge(firmwareBatteryBadge, firmwareBatteryText, true, neutral: true);

        firmwarePowerStateText.Text = $"PWR {state.FirmwarePowerState:X2}";
        ToolTip.SetTip(firmwarePowerStateBadge, "Firmware charger/battery state byte at RAM 0x11FDDA");
        SetIndicatorBadge(firmwarePowerStateBadge, firmwarePowerStateText, true, neutral: true);

        firmwareThresholdText.Text = state.FirmwareBatteryThresholdsLoaded ? "BTBL" : "BTBL 0";
        ToolTip.SetTip(
            firmwareThresholdBadge,
            state.FirmwareBatteryThresholdsLoaded
                ? "Firmware battery threshold table at RAM 0x117270 is populated"
                : "Firmware battery threshold table at RAM 0x117270 is currently zero");
        SetIndicatorBadge(firmwareThresholdBadge, firmwareThresholdText, state.FirmwareBatteryThresholdsLoaded, neutral: false);
    }

    private void UpdateTelemetryPanel(PhoneTelemetryState state)
    {
        if (telemetryPanelFrame?.IsVisible != true)
        {
            return;
        }

        string powerReason = string.IsNullOrWhiteSpace(state.PowerOffReason) ? "-" : state.PowerOffReason;
        telemetryText.Text =
            $"steps {state.ExecutedSteps:N0}\n" +
            $"emu {state.EmulatedSeconds:F1}s  pc {state.Pc:X8}  cpsr {state.Cpsr:X8}\n" +
            $"und {state.UndefinedInstructions:N0}  last {state.LastUndefinedAddress:X8}:{state.LastUndefinedInstruction:X8}\n" +
            $"off {state.PoweredOff}  reason {powerReason}\n" +
            $"wd {state.WatchdogResets}  ccont-wd 0x{state.CcontWatchdog:X2}  pwr-key {state.PowerKeyHeld}\n" +
            $"ccont cmd 0x{state.CcontWatchdogCommand:X2}  arm {state.CcontWatchdogArmReloads:N0}  kick {state.CcontWatchdogKicks:N0}  dis {state.CcontWatchdogDisables:N0}  exp {state.CcontWatchdogExpires:N0}\n" +
            $"rtc {state.CcontRtcHour:00}:{state.CcontRtcMinute:00}:{state.CcontRtcSecond:00}  day {state.CcontRtcDay:00}  ctl 0x{state.CcontRtcControl:X2}  pend 0x{state.CcontRtcInterruptPending:X2}  mask 0x{state.CcontRtcInterruptMask:X2}\n" +
            $"idle hook {state.IdleYieldHookResolved}  now {state.AtIdleYieldLoop}  checks {state.IdleLoopChecks:N0}  waits {state.IdleYieldWaits:N0}\n" +
            $"held keys {state.HeldInputKeys}\n" +
            $"wall pauses {state.WallClockPauseCount:N0}  last {state.LastWallClockPauseMilliseconds:F0}ms\n" +
            $"vbat 0x{state.Ccont.BatteryVoltage:X3}  vchg 0x{state.Ccont.ChargerVoltage:X3}  ichg 0x{state.Ccont.ChargingCurrent:X3}\n" +
            $"fw pwr 0x{state.Ccont.FirmwarePowerState:X2}  bat 0x{state.Ccont.FirmwareBatteryPercent:X2}  class 0x{state.Ccont.FirmwareBatteryClass:X2}  flags 0x{state.Ccont.FirmwareBatteryFlags:X2}  sample 0x{state.Ccont.FirmwareBatterySample:X4}";
    }

    private void UpdateGsmPanel(GsmControlState state)
    {
        string label = state.Registered
            ? state.DedicatedChannelActive ? "DCH" : "REG"
            : "NO NET";

        if (state.PendingIncomingServices > 0)
        {
            label += $" P{Math.Min(state.PendingIncomingServices, 9)}";
        }

        gsmStateText.Text = label;
        ToolTip.SetTip(
            gsmStateBadge,
            $"Registered={state.Registered}; dedicated channel={state.DedicatedChannelActive}; pending incoming={state.PendingIncomingServices}");
        SetIndicatorBadge(gsmStateBadge, gsmStateText, state.Registered, neutral: !state.Registered);
    }

    private void UpdateDspRadioPanel(DspRadioControlState state)
    {
        syncingControlPanel = true;

        try
        {
            if (dspRssiEditing)
            {
                dspRssiText.Text = FormatByteValue(ClampDspRssiValue(dspRssiSlider.Value));
                return;
            }

            dspRssiText.Text = FormatByteValue(state.Rssi);

            if (Math.Abs(dspRssiSlider.Value - state.Rssi) > 0.5)
            {
                dspRssiSlider.Value = state.Rssi;
            }
        }
        finally
        {
            syncingControlPanel = false;
        }
    }

    private void UpdateLeds(bool lcdBacklightOn, bool keypadBacklightOn)
    {
        if (displayFrame is not null)
        {
            displayFrame.Background = lcdBacklightOn
                ? LcdControl.BackgroundOnBrush
                : LcdControl.BackgroundOffBrush;
        }
        foreach (PhoneKeyFaceControl control in phoneKeyControls.Values)
        {
            control.SetBacklight(lcdBacklightOn);
        }
        directionKeySurface.SetBacklight(lcdBacklightOn);
    }

    private static TextBlock CreateBadgeText()
        => new()
        {
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

    private static TextBlock CreateTelemetryText()
        => new()
        {
            Foreground = MutedTextBrush,
            FontFamily = EmbeddedFontFamily,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None,
            LineHeight = 14,
        };

    private Border CreateTelemetryPanel()
    {
        Border panel = new()
        {
            Background = BadgeNeutralBrush,
            BorderBrush = BadgeNeutralBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(8),
            IsVisible = false,
            Child = telemetryText,
        };

        telemetryPanelFrame = panel;
        return panel;
    }

    private ComboBox CreateLogFilterBox()
    {
        ComboBox box = new()
        {
            ItemsSource = new[] { "ALL", "FBUS", "TRACE", "MDI", "TASK", "MBUS", "HARDWARE" },
            SelectedIndex = 0,
            MinWidth = 92,
            Height = 26,
            FontSize = 10,
            Background = BadgeOffBrush,
            Foreground = TextBrush,
        };
        ToolTip.SetTip(box, "Filter captured bus, firmware, DSP, task, and hardware records");
        return box;
    }

    private ListBox CreateLogList()
    {
        ListBox list = new()
        {
            ItemsSource = visibleLogEntries,
            SelectionMode = SelectionMode.Multiple,
            Background = BadgeOffBrush,
            BorderBrush = BadgeNeutralBorderBrush,
            BorderThickness = new Thickness(1),
            Height = 260,
            ItemTemplate = new FuncDataTemplate<EmulationLogEntry>((entry, _) => new TextBox
            {
                Text = entry?.DisplayText ?? "",
                IsReadOnly = true,
                AcceptsReturn = false,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = EmbeddedFontFamily,
                FontSize = 10,
                Background = Brushes.Transparent,
                Foreground = TextBrush,
                BorderThickness = new Thickness(0),
                MinHeight = 0,
                Height = 18,
                Padding = new Thickness(3, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            }),
        };
        list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(ListBoxItem.PaddingProperty, new Thickness(0)),
                new Setter(ListBoxItem.MinHeightProperty, 0d),
                new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
            },
        });
        ToolTip.SetTip(list, "Rows are virtualized. Select text in a row to copy raw or decoded data.");
        return list;
    }

    private Control CreateLogPanel()
    {
        SetPanelToggle(logPauseToggle, "PAUSE", false, "Pause log display");
        Grid toolbar = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(0, 0, 0, 6),
        };
        Grid.SetColumn(logFilterBox, 0);
        toolbar.Children.Add(logFilterBox);
        Grid.SetColumn(logPauseToggle, 1);
        toolbar.Children.Add(logPauseToggle);
        Grid.SetColumn(logClearButton, 2);
        toolbar.Children.Add(logClearButton);

        return new Border
        {
            Background = BadgeNeutralBrush,
            BorderBrush = BadgeNeutralBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(8),
            Child = new StackPanel
            {
                Children = { toolbar, logList },
            },
        };
    }

    private Control CreateLogSection()
    {
        Control panel = CreateLogPanel();
        panel.IsVisible = false;
        SetPanelToggle(logVisibilityToggle, "LOG ▸", false, "Enable and show bus/debug logging");
        logVisibilityToggle.Click += (_, _) =>
        {
            logEnabled = logVisibilityToggle.IsChecked == true;
            panel.IsVisible = logEnabled;
            if (logEnabled)
            {
                emulator.LogAvailable += OnEmulatorLogAvailable;
            }
            else
            {
                emulator.LogAvailable -= OnEmulatorLogAvailable;
            }

            emulator.SetLoggingEnabled(logEnabled);
            SetPanelToggle(
                logVisibilityToggle,
                logEnabled ? "LOG ▾" : "LOG ▸",
                logEnabled,
                logEnabled ? "Disable and hide bus/debug logging" : "Enable and show bus/debug logging");
            if (logEnabled)
            {
                OnEmulatorLogAvailable(emulator);
            }
        };

        return new StackPanel
        {
            Children =
            {
                CreateSectionLabel("BUS / DEBUG LOG"),
                logVisibilityToggle,
                panel,
            },
        };
    }

    private static TextBlock CreateControlLabel(string label)
        => new()
        {
            Text = label,
            Foreground = MutedTextBrush,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

    private static Border CreateControlRow(Control child, double bottomMargin)
        => new()
        {
            Background = BadgeNeutralBrush,
            BorderBrush = BadgeNeutralBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(0, 0, 0, bottomMargin),
            Padding = new Thickness(8, 4),
            Child = child,
        };

    private static TextBox CreateCompactTextBox(string text)
        => new()
        {
            Text = text,
            Background = BadgeOffBrush,
            BorderBrush = BadgeNeutralBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Foreground = TextBrush,
            FontSize = 10,
            MinHeight = 22,
            Padding = new Thickness(6, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

    private void SetPhoneSettingsStatus(string text, bool active)
    {
        phoneSettingsStatusText.Text = text;
        phoneSettingsStatusText.Foreground = active ? BadgeOnTextBrush : MutedTextBrush;
    }

    private void SetRingtoneStatus(string text, bool active, string tooltip)
    {
        ringtoneStatusText.Text = text;
        ringtoneStatusText.Foreground = active ? BadgeOnTextBrush : MutedTextBrush;
        ToolTip.SetTip(ringtoneStatusText, tooltip);
    }

    private static string? NormalizeSimImsi(string? value)
    {
        string trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return null;
        }

        return trimmed.Length == 15 && trimmed.All(ch => ch is >= '0' and <= '9') ? trimmed : null;
    }

    private static string NormalizeNetworkName(string? value)
    {
        string trimmed = string.IsNullOrWhiteSpace(value)
            ? Dct3PhoneSettings.DefaultNetworkName
            : value.Trim();
        string sanitized = new(trimmed.Where(ch => ch is >= ' ' and <= '~').Take(16).ToArray());
        return sanitized.Length == 0 ? Dct3PhoneSettings.DefaultNetworkName : sanitized;
    }

    private static Border CreatePanelPill(string label, IBrush background, IBrush foreground)
    {
        TextBlock text = CreateBadgeText();
        text.Text = label;
        text.Foreground = foreground;

        return new Border
        {
            Background = background,
            BorderBrush = BadgeNeutralBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Margin = new Thickness(0, 0, 0, 10),
            MinHeight = 20,
            Padding = new Thickness(10, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = text,
        };
    }

    private static TextBlock CreateSectionLabel(string label)
        => new()
        {
            Text = label,
            Foreground = TextBrush,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(2, 0, 0, 6),
        };

    private static Border CreateIndicatorBadge(TextBlock text)
        => new()
        {
            Background = BadgeNeutralBrush,
            BorderBrush = BadgeNeutralBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Margin = new Thickness(2),
            MinHeight = 18,
            Padding = new Thickness(8, 1),
            Child = text,
        };

    private static ToggleButton CreateLightToggle()
        => new()
        {
            Background = BadgeOffBrush,
            BorderBrush = BadgeNeutralBorderBrush,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(999),
            Focusable = false,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = BadgeOffTextBrush,
            Margin = new Thickness(2),
            MinHeight = 18,
            MinWidth = 38,
            Padding = new Thickness(8, 1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

    private Slider CreateDspRssiSlider()
    {
        Slider slider = new()
        {
            Minimum = 0,
            Maximum = Dsp.DefaultRssiMeasurement,
            Value = DspRadioControlState.Default.Rssi,
            VerticalAlignment = VerticalAlignment.Center,
        };

        slider.ValueChanged += (_, _) => OnDspRssiSliderChanged();
        slider.AddHandler(PointerPressedEvent, (_, _) => dspRssiEditing = true, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        slider.AddHandler(
            PointerReleasedEvent,
            (_, _) =>
            {
                dspRssiEditing = false;
                OnDspRssiSliderChanged();
            },
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            true);
        slider.PointerCaptureLost += (_, _) =>
        {
            dspRssiEditing = false;
            OnDspRssiSliderChanged();
        };

        return slider;
    }

    private static Button CreatePillButton(string label)
        => new()
        {
            Content = label,
            Background = BadgeNeutralBrush,
            BorderBrush = BadgeNeutralBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Focusable = false,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = MutedTextBrush,
            Margin = new Thickness(2),
            MinHeight = 18,
            MinWidth = 42,
            Padding = new Thickness(8, 1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

    private static void SetIndicatorBadge(Border badge, TextBlock text, bool active, bool neutral)
    {
        badge.Background = neutral ? BadgeNeutralBrush : active ? BadgeOnBrush : BadgeOffBrush;
        badge.BorderBrush = BadgeNeutralBorderBrush;
        text.Foreground = neutral ? MutedTextBrush : active ? BadgeOnTextBrush : BadgeOffTextBrush;
    }

    private static void SetPanelToggle(ToggleButton toggle, string label, bool active, string tooltip)
    {
        toggle.Content = label;
        toggle.IsChecked = active;
        toggle.Background = active ? BadgeOnBrush : BadgeOffBrush;
        toggle.Foreground = active ? BadgeOnTextBrush : BadgeOffTextBrush;
        toggle.BorderBrush = BadgeNeutralBorderBrush;
        ToolTip.SetTip(toggle, tooltip);
    }

    private void SetPowerButtonPressed(bool pressed)
    {
        ToolTip.SetTip(powerButton, pressed ? "Power key held" : "Hold power key");
    }

    private static void SetLightToggle(ToggleButton toggle, string label, bool active, bool overridden, bool resetFlash)
    {
        toggle.Content = label;
        toggle.IsChecked = active;
        toggle.Background = active ? BadgeOnBrush : BadgeOffBrush;
        toggle.Foreground = active ? BadgeOnTextBrush : BadgeOffTextBrush;
        toggle.BorderBrush = resetFlash ? BadgeResetBorderBrush : overridden ? BadgeOverrideBorderBrush : BadgeNeutralBorderBrush;
        string mode = overridden ? "manual override" : "firmware control";
        ToolTip.SetTip(toggle, $"{label} backlight {(active ? "on" : "off")} - {mode}. Hold and release to return to firmware.");
    }

    private static bool IsOverrideResetHold(long pressedAtMilliseconds)
        => pressedAtMilliseconds > 0 && Environment.TickCount64 - pressedAtMilliseconds >= OverrideResetHoldMilliseconds;

    private static bool IsResetFlashActive(long resetAtMilliseconds)
        => resetAtMilliseconds > 0 && Environment.TickCount64 - resetAtMilliseconds < OverrideResetFlashMilliseconds;

    private static string ShortStatus(string status)
        => status switch
        {
            "Starting" => "START",
            "Booting" => "BOOT",
            "Running" => "RUN",
            "Powered off" => "OFF",
            _ => status.Length <= 8 ? status.ToUpperInvariant() : "STATUS",
        };

    private void SetLocalCcontAdcValue(CcontAdcChannel channel, ushort value)
    {
        if (!ccontAdcControls.TryGetValue(channel, out CcontAdcControl? control))
        {
            return;
        }

        syncingControlPanel = true;

        try
        {
            control.ValueText.Text = FormatAdcValue(value);
            control.Slider.Value = value;
        }
        finally
        {
            syncingControlPanel = false;
        }
    }

    private static ushort ClampAdcValue(double value)
        => (ushort)Math.Clamp((int)Math.Round(value), 0, 0x3FF);

    private static byte ClampDspRssiValue(double value)
        => (byte)Math.Clamp((int)Math.Round(value), 0, Dsp.DefaultRssiMeasurement);

    private static string FormatAdcValue(ushort value)
        => $"0x{Math.Min(value, (ushort)0x3FF):X3}";

    private static string FormatByteValue(byte value)
        => $"0x{value:X2}";

    private void UpdateVibration(Mad2PeripheralState state)
    {
        if (!state.VibratorEnabled)
        {
            StopVibrationAnimation();
#if BROWSER
            StopBrowserVibration();
#endif
            return;
        }

        StartVibrationAnimation();
#if BROWSER
        UpdateBrowserVibration(state);
        if (!browserVibrationTimer.IsEnabled)
        {
            browserVibrationTimer.Start();
        }
#endif
    }

    private void StartVibrationAnimation()
    {
        if (vibrationAnimationCancellation is not null)
        {
            return;
        }

        layoutRoot?.Classes.Add("vibrating");
        vibrationAnimationCancellation = new CancellationTokenSource();
        Animation animation = new()
        {
            Duration = TimeSpan.FromMilliseconds(72),
            IterationCount = IterationCount.Infinite,
            Children =
            {
                CreateVibrationKeyFrame(0.00, -4.0, -1.2),
                CreateVibrationKeyFrame(0.25, 4.0, 0.8),
                CreateVibrationKeyFrame(0.50, -3.0, 1.2),
                CreateVibrationKeyFrame(0.75, 3.0, -0.8),
                CreateVibrationKeyFrame(1.00, -4.0, -1.2),
            },
        };
        _ = RunVibrationAnimationAsync(animation, vibrationAnimationCancellation.Token);
    }

    private void StopVibrationAnimation()
    {
        if (vibrationAnimationCancellation is not null)
        {
            vibrationAnimationCancellation.Cancel();
            vibrationAnimationCancellation.Dispose();
            vibrationAnimationCancellation = null;
        }

        layoutRoot?.Classes.Remove("vibrating");
        shakeTransform.X = 0;
        shakeTransform.Y = 0;
    }

    private static KeyFrame CreateVibrationKeyFrame(double cue, double x, double y) => new()
    {
        Cue = new Cue(cue),
        Setters =
        {
            new Setter(TranslateTransform.XProperty, x),
            new Setter(TranslateTransform.YProperty, y),
        },
    };

    private async Task RunVibrationAnimationAsync(Animation animation, CancellationToken cancellationToken)
    {
        try
        {
            await animation.RunAsync(shakeTransform, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

#if BROWSER
    private void UpdateBrowserVibration(Mad2PeripheralState state)
    {
        int control = state.VibratorControl & 0x1F;
        long now = Environment.TickCount64;
        if (browserVibrationActive &&
            browserVibrationLastControl == control &&
            now < browserVibrationNextPulseAtMilliseconds)
        {
            return;
        }

        try
        {
            BrowserVibrationInterop.Update(enabled: true, control);
            browserVibrationActive = true;
            browserVibrationLastControl = control;
            browserVibrationNextPulseAtMilliseconds = now + BrowserVibrationPulseMilliseconds;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Browser vibration stopped: {ex.Message}");
            browserVibrationActive = false;
            browserVibrationLastControl = -1;
            browserVibrationNextPulseAtMilliseconds = 0;
        }
    }

    private void StopBrowserVibration()
    {
        browserVibrationTimer.Stop();
        if (!browserVibrationActive)
        {
            return;
        }

        try
        {
            BrowserVibrationInterop.Update(enabled: false, control: 0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Browser vibration stop failed: {ex.Message}");
        }

        browserVibrationActive = false;
        browserVibrationLastControl = -1;
        browserVibrationNextPulseAtMilliseconds = 0;
    }

    private void DisposeBrowserVibration()
    {
        browserVibrationTimer.Stop();
        try
        {
            BrowserVibrationInterop.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Browser vibration dispose failed: {ex.Message}");
        }

        browserVibrationActive = false;
        browserVibrationLastControl = -1;
        browserVibrationNextPulseAtMilliseconds = 0;
    }
#endif

    private sealed class CcontAdcControl(Slider slider, TextBlock valueText)
    {
        public Slider Slider { get; } = slider;

        public TextBlock ValueText { get; } = valueText;

        public bool Editing { get; set; }
    }
}
