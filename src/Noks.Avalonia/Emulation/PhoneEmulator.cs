using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
#if BROWSER
using Avalonia.Threading;
#endif
using Noks.Dct3.Audio;
using Noks.Dct3.Core;
using Noks.Dct3.Display;
using Noks.Dct3.Input;
using Noks.Dct3.Messaging;
using Noks.Dct3.Peripherals;
using Noks.Dct3.Radio;
using Noks.Dct3.Sim;
using Noks.Dct3.State;
#if BROWSER
using Noks.AvaloniaApp.Browser;
#endif
using Noks.AvaloniaApp.Startup;
using Noks.Application.Input;
using Noks.Application.Persistence;

namespace Noks.AvaloniaApp.Emulation;

public sealed class PhoneEmulator : IDisposable
{
    private const bool PaceToRealTime = true;
    private static readonly TimeSpan ActiveBatchTarget = TimeSpan.FromMilliseconds(4);
    private static readonly TimeSpan CooperativeActiveBatchTarget = TimeSpan.FromMilliseconds(12);
    private const int InitialActiveBatchSteps = 1_024;
    private const int MinimumActiveBatchSteps = 64;
    private const int MaximumActiveBatchSteps = 262_144;
    private const int MaximumIdleAccelerationInstructions =
        (int)(Dct3Machine.CyclesPerSecond / 100 / 8 * 4);
    private const long MinimumOverdueIdleFastForwardCycles = Dct3Machine.CyclesPerSecond / 1000;
    private static readonly TimeSpan MinimumIdleYieldBlock = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan IdleYieldWaitCap = TimeSpan.FromMilliseconds(10);
    private static readonly long StatePublishIntervalTicks = Stopwatch.Frequency / 4;
    private static readonly long PersistenceSaveIntervalTicks = Stopwatch.Frequency;
    private const ushort SimTelecomDirectoryFileId = 0x7F10;
    private const ushort SimAdnFileId = 0x6F3A;
    private const ushort SimSmsFileId = 0x6F3C;
    private const long MinimumKeyHoldCycles = Dct3Machine.CyclesPerSecond / 12;
    private const long MinimumKeyReleaseCycles = Dct3Machine.CyclesPerSecond / 50;
    private const long StartupPowerMinimumHoldCycles = 3 * Dct3Machine.CyclesPerSecond;
    private const long StartupPowerMaximumHoldCycles = 4 * Dct3Machine.CyclesPerSecond;
    private const long StartupPowerReadyLcdWrites = 7 * Pcd8544.Width * Pcd8544.Height / 8;
    private readonly Func<byte[]> loadFlash;
    private readonly Func<byte[]?> loadExternalEeprom;
    private readonly Dct3PhoneSettings settings;
    private readonly Dct3KeyMap keyMap;
    private readonly Dct3KeyMatrix keyMatrix = new();
    private readonly ConcurrentQueue<KeyChange> keyChanges = new();
    private readonly ConcurrentQueue<CcontAdcChange> ccontAdcChanges = new();
    private readonly ConcurrentQueue<DspRadioChange> dspRadioChanges = new();
    private readonly ConcurrentQueue<FacadeNetworkChange> facadeNetworkChanges = new();
    private readonly ConcurrentQueue<GsmIncomingChange> gsmIncomingChanges = new();
    private readonly ConcurrentQueue<ResolveNetworkRequest> networkResolutionChanges = new();
    private readonly ConcurrentQueue<Guid> networkCallConnections = new();
    private readonly ConcurrentQueue<Guid> networkCallTerminations = new();
    private readonly ConcurrentQueue<string> managedOwnNumberChanges = new();
    private readonly ConcurrentQueue<OutgoingNetworkRequest> outgoingNetworkRequests = new();
    private readonly ConcurrentQueue<SimMutation> simMutations = new();
    private readonly ConcurrentQueue<CallTransition> callTransitions = new();
    private readonly ConcurrentQueue<CallAudioAnnouncement> callAudioAnnouncements = new();
    private readonly ConcurrentQueue<MemoryReadRequest> memoryReadRequests = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly AutoResetEvent inputChanged = new(false);
    private readonly List<ScheduledPhoneKeyChange> scheduledKeyChanges;
    private readonly Dictionary<PhoneKey, KeyTimingState> keyTimingStates = [];
    private readonly PhonePersistenceSession? persistence;
    private readonly object persistenceLock = new();
#if BROWSER && !BROWSER_THREADS
    private Task? browserRunTask;
#else
    private Thread? thread;
#endif
    private LcdFrame frame = LcdFrame.Empty;
    private Mad2PeripheralState peripheralState = Mad2PeripheralState.Off;
    private Dct3AudioState audioState = Dct3AudioState.Off;
    private EmulationPacing pacing = EmulationPacing.Initial;
    private CcontControlState ccontState = CcontControlState.Normal;
    private DspRadioControlState dspRadioState = DspRadioControlState.Default;
    private GsmControlState gsmState = GsmControlState.Default;
    private PhoneTelemetryState telemetry = PhoneTelemetryState.Empty;
    private string status = "Starting";
    private int nextScheduledKeyChange;
    private int pendingKeyTransitionCount;
    private int observedInputChangeGeneration;
    private int observedInputPressGeneration;
    private int loggingEnabled;
    private int networkNotificationPending;
    private int simMutationNotificationPending;
    private int immediatePersistenceSavePending;
    private int callTransitionNotificationPending;
    private int audioAnnouncementNotificationPending;
    private long lastPersistedPersistenceVersion = -1;
    private bool persistenceSaveInFlight;
    private long lastPersistenceSaveTimestamp;
    private Dct3PersistenceSnapshot? pendingPersistenceSnapshot;
    private bool invalidExecutionLogged;
    private EmulationTrace? emulationTrace;

    public PhoneEmulator(
        string flashPath,
        string? externalEepromPath = null,
        IEnumerable<ScheduledPhoneKeyChange>? scheduledKeyChanges = null,
        Dct3PhoneSettings? settings = null)
        : this(
            LoadFlashFile(flashPath),
            LoadOptionalFile(externalEepromPath),
            scheduledKeyChanges,
            settings: settings)
    {
    }

    public PhoneEmulator(
        byte[] flashImage,
        byte[]? externalEepromImage = null,
        IEnumerable<ScheduledPhoneKeyChange>? scheduledKeyChanges = null,
        PhonePersistenceSession? persistence = null,
        Dct3PhoneSettings? settings = null)
        : this(() => flashImage.ToArray(), () => externalEepromImage?.ToArray(), scheduledKeyChanges)
    {
        this.persistence = persistence;
        this.settings = settings ?? Dct3PhoneSettings.Default;
        keyMap = Dct3KeyMaps.Resolve(flashImage, this.settings);
    }

    private PhoneEmulator(
        Func<byte[]> loadFlash,
        Func<byte[]?>? loadExternalEeprom,
        IEnumerable<ScheduledPhoneKeyChange>? scheduledKeyChanges)
    {
        this.loadFlash = loadFlash;
        this.loadExternalEeprom = loadExternalEeprom ?? (() => null);
        this.scheduledKeyChanges = scheduledKeyChanges?.OrderBy(change => change.Step).ToList() ?? [];
        settings = Dct3PhoneSettings.Default;
        keyMap = Dct3KeyMap.Nokia3310;
    }

    public long ExecutedSteps { get; private set; }

    public long Cycles { get; private set; }

    public LcdFrame Frame => Volatile.Read(ref frame);

    public Mad2PeripheralState PeripheralState => Volatile.Read(ref peripheralState);

    public Dct3AudioState AudioState => Volatile.Read(ref audioState);

    public EmulationPacing Pacing => Volatile.Read(ref pacing);

    public CcontControlState CcontState => Volatile.Read(ref ccontState);

    public DspRadioControlState DspRadioState => Volatile.Read(ref dspRadioState);

    public GsmControlState GsmState => Volatile.Read(ref gsmState);

    public PhoneTelemetryState Telemetry => Volatile.Read(ref telemetry);

    public string Status => Volatile.Read(ref status);

    public Dct3PhoneSettings Settings => settings;

    public Dct3KeyMap KeyMap => keyMap;

    public int PendingKeyTransitions => Volatile.Read(ref pendingKeyTransitionCount);

    public event Action<PhoneEmulator>? FrameChanged;

    public event Action<PhoneEmulator>? AudioStateChanged;

    public event Action<PhoneEmulator>? StateChanged;

    public event Action<PhoneEmulator>? TelemetryChanged;

    public event Action<PhoneEmulator>? LogAvailable;

    public event Action<PhoneEmulator>? NetworkRequestAvailable;

    public event Action<PhoneEmulator>? SimMutationAvailable;

    public event Action<PhoneEmulator>? CallTransitionAvailable;

    public event Action<PhoneEmulator>? AudioAnnouncementAvailable;

    public bool TryDequeueLog(out EmulationLogEntry? entry)
    {
        entry = null;
        return Volatile.Read(ref emulationTrace) is { } trace && trace.TryDequeue(out entry);
    }

    public void SetLoggingEnabled(bool enabled)
    {
        Volatile.Write(ref loggingEnabled, enabled ? 1 : 0);
        Volatile.Read(ref emulationTrace)?.SetEnabled(enabled);
    }

    public void Start()
    {
#if BROWSER && !BROWSER_THREADS
        browserRunTask ??= RunAsync(cooperative: true, paceToRealTime: PaceToRealTime);
#else
        if (thread is not null)
        {
            return;
        }

        thread = new Thread(() => RunAsync(cooperative: false, paceToRealTime: PaceToRealTime).GetAwaiter().GetResult())
        {
            IsBackground = true,
            Name = "Noks phone emulator",
        };
        thread.Start();
#endif
    }

    public void SetKey(PhoneKey key, bool pressed)
    {
        keyChanges.Enqueue(new KeyChange(key, pressed));
        inputChanged.Set();
    }

    public void SetCcontAdc(CcontAdcChannel channel, ushort value)
    {
        ccontAdcChanges.Enqueue(new CcontAdcChange(channel, (ushort)Math.Min(value, (ushort)0x3FF), Reset: false));
    }

    public void ResetCcontAdcInputs()
    {
        ccontAdcChanges.Enqueue(new CcontAdcChange(default, 0, Reset: true));
    }

    public void SetDspRssi(byte value)
    {
        dspRadioChanges.Enqueue(new DspRadioChange(value));
        inputChanged.Set();
    }

    public void SetFacadeNetworkAvailable(bool available)
    {
        facadeNetworkChanges.Enqueue(new FacadeNetworkChange(available));
        inputChanged.Set();
    }

    public void QueueIncomingCall(string callingNumber)
    {
        QueueIncomingCall(Guid.NewGuid(), callingNumber);
    }

    public void QueueIncomingCall(Guid requestId, string callingNumber)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("An incoming call requires a non-empty request ID.", nameof(requestId));
        }

        gsmIncomingChanges.Enqueue(new GsmIncomingChange(
            GsmIncomingKind.Call,
            callingNumber,
            "",
            CorrelationId: requestId));
        inputChanged.Set();
    }

    public void QueueIncomingSms(string originator, string text)
    {
        QueueIncomingSms(originator, text, DateTimeOffset.Now);
    }

    public void QueueIncomingSms(string originator, string text, DateTimeOffset sentAt)
    {
        gsmIncomingChanges.Enqueue(new GsmIncomingChange(
            GsmIncomingKind.Sms,
            originator,
            text,
            SentAt: sentAt));
    }

    public void QueueIncomingSmartMessage(string originator, ushort destinationPort, ReadOnlySpan<byte> payload)
    {
        gsmIncomingChanges.Enqueue(new GsmIncomingChange(
            GsmIncomingKind.SmartMessage,
            originator,
            "",
            destinationPort,
            payload.ToArray()));
    }

    public void ResolveNetworkRequest(ResolveNetworkRequest resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        networkResolutionChanges.Enqueue(resolution);
        inputChanged.Set();
    }

    public void TerminateNetworkCall(Guid requestId)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A call termination requires a non-empty request ID.", nameof(requestId));
        }

        networkCallTerminations.Enqueue(requestId);
        inputChanged.Set();
    }

    public void ConnectNetworkCall(Guid requestId)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A call connection requires a non-empty request ID.", nameof(requestId));
        }

        networkCallConnections.Enqueue(requestId);
        inputChanged.Set();
    }

    public void SetManagedOwnNumber(string phoneNumber)
    {
        ArgumentNullException.ThrowIfNull(phoneNumber);
        managedOwnNumberChanges.Enqueue(phoneNumber);
        inputChanged.Set();
    }

    public bool TryDequeueOutgoingNetworkRequest(out OutgoingNetworkRequest? request) =>
        outgoingNetworkRequests.TryDequeue(out request);

    public bool TryDequeueSimMutation(out SimMutation? mutation) =>
        simMutations.TryDequeue(out mutation);

    public bool TryDequeueCallTransition(out CallTransition? transition) =>
        callTransitions.TryDequeue(out transition);

    public bool TryDequeueAudioAnnouncement(out CallAudioAnnouncement? announcement) =>
        callAudioAnnouncements.TryDequeue(out announcement);

    public Task<byte[]> ReadMemoryAsync(uint address, int length, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<byte[]> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        memoryReadRequests.Enqueue(new MemoryReadRequest(address, length, completion));
        inputChanged.Set();
        return completion.Task.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        cancellation.Cancel();
        inputChanged.Set();
#if !BROWSER
        thread?.Join(TimeSpan.FromSeconds(2));
#endif
        inputChanged.Dispose();
        cancellation.Dispose();
    }

    private async Task RunAsync(bool cooperative, bool paceToRealTime)
    {
        if (PackageExpiration.TryGetBlockMessage(out string expirationMessage))
        {
            PublishStatus(expirationMessage);
            return;
        }

        try
        {
            byte[] flash = loadFlash();
            byte[]? externalEeprom = loadExternalEeprom();
            Dct3Machine? machineForTrace = null;
            EmulationTrace trace = new(() => machineForTrace?.Bus.Cycles ?? 0);
            trace.EntriesAvailable += () => LogAvailable?.Invoke(this);
            trace.SetEnabled(Volatile.Read(ref loggingEnabled) != 0);
            Volatile.Write(ref emulationTrace, trace);
            Dct3Machine machine = new(
                flash,
                trace,
                externalEepromImage: externalEeprom,
                timerClock: Dct3TimerClock.CpuCycles,
#if BROWSER
                ccontWatchdogEnabled: false,
#else
                ccontWatchdogEnabled: true,
#endif
                wallClockCatchUpLimit: TimeSpan.FromMilliseconds(250),
                persistenceSnapshot: persistence?.InitialSnapshot,
                settings: settings,
                keyMatrix: keyMatrix,
                simMutation: EnqueueSimMutation);
            machineForTrace = machine;
            machine.OutgoingNetworkRequestSubmitted += request =>
            {
                EnqueueBounded(outgoingNetworkRequests, request, maximumCount: 16);
                Volatile.Write(ref networkNotificationPending, 1);
            };
            machine.CallTransitionCommitted += transition =>
            {
                EnqueueBounded(callTransitions, transition, maximumCount: 64);
                Volatile.Write(ref callTransitionNotificationPending, 1);
            };
            machine.CallAudioAnnouncementQueued += announcement =>
            {
                EnqueueBounded(callAudioAnnouncements, announcement, maximumCount: 8);
                Volatile.Write(ref audioAnnouncementNotificationPending, 1);
            };
            bool firstUndefinedInstructionLogged = false;
            machine.Cpu.UndefinedInstructionObserved += _ =>
            {
                if (firstUndefinedInstructionLogged)
                {
                    return;
                }

                firstUndefinedInstructionLogged = true;
                Console.Error.WriteLine(
                    $"Noks CPU first undefined: address={machine.Cpu.LastUndefinedInstructionAddress:X8} " +
                    $"instruction={machine.Cpu.LastUndefinedInstruction:X8} {FormatCpuFaultState(machine)}");
            };
            machine.Lcd.FrameCompleted += () => PublishFrame(machine.Lcd);
            machine.Lcd.DisplayStateChanged += () => PublishFrame(machine.Lcd);
            MarkPersistenceLoaded(machine);
            long startupPowerPressedAtCycles = machine.Bus.Cycles;
            RealTimePacer pacer = new();
            AdaptiveStepBatch activeBatch = new(
                cooperative ? CooperativeActiveBatchTarget : ActiveBatchTarget,
                InitialActiveBatchSteps,
                MinimumActiveBatchSteps,
                MaximumActiveBatchSteps);
            long idleLoopChecks = 0;
            long idleYieldWaits = 0;
            long idleSpinAccelerations = 0;
            long idleBlockedByPendingControls = 0;
            long idleBlockedByWatchdog = 0;
            long idleBlockedByMachineWait = 0;
            long lastStatusStep = 0;
            long nextStatePublishAt = Stopwatch.GetTimestamp();
            bool logDiagnostics = Environment.GetEnvironmentVariable("NOKS_EMU_DIAGNOSTICS") == "1";
            bool logMonitor = Environment.GetEnvironmentVariable("NOKS_EMU_MONITOR") == "1";
            bool disableIdleAcceleration =
                Environment.GetEnvironmentVariable("NOKS_EMU_DISABLE_IDLE_ACCELERATION") == "1";
            long lastDiagnosticsTimestamp = Stopwatch.GetTimestamp();
            long nextMonitorTimestamp = Stopwatch.GetTimestamp();
            long lastMonitorStep = 0;
            long lastMonitorCycle = 0;
            CpuHistoryEntry[] cpuHistory = logMonitor ? new CpuHistoryEntry[256] : [];
            int cpuHistoryNext = 0;
            bool cpuHistoryDumped = false;
            bool fiqSelfReturnDumped = false;
#if BROWSER
            long lastConsoleStep = 0;
#endif

            PublishStatus("Booting");
            PublishCcontState(machine);
            PublishDspStates(machine);
            StateChanged?.Invoke(this);

            while (!cancellation.IsCancellationRequested && !machine.PoweredOff)
            {
                ApplyScheduledKeys(machine);
                QueueInputKeyChanges();
                AdvanceKeyTransitions(machine);
                ApplyQueuedControls(machine);
                PublishPendingBridgeNotifications();
                ServiceInputChanges(machine);
                machine.RefreshWallClockTimers();

                int maximumSkippedInstructions = MaximumIdleAccelerationInstructions;
                if (pendingKeyTransitionCount > 0)
                {
                    maximumSkippedInstructions = 0;
                }

                if (nextScheduledKeyChange < scheduledKeyChanges.Count)
                {
                    long remaining = scheduledKeyChanges[nextScheduledKeyChange].Step - ExecutedSteps;
                    maximumSkippedInstructions = Math.Min(
                        maximumSkippedInstructions,
                        (int)Math.Clamp(remaining, 0, int.MaxValue));
                }

                int skippedIdleInstructions = disableIdleAcceleration
                    ? 0
                    : machine.AccelerateIdleSpin(maximumSkippedInstructions);
                bool idleSpinAccelerated = skippedIdleInstructions > 0;
                if (idleSpinAccelerated)
                {
                    ExecutedSteps += skippedIdleInstructions;
                    idleSpinAccelerations++;
                }

                bool atIdleYieldLoop = machine.IsAtIdleYieldLoop();
                bool idleYielded = false;
                IdleWaitBlockReason idleWaitBlockReason = IdleWaitBlockReason.None;
                if (!idleSpinAccelerated && cooperative)
                {
                    (idleYielded, idleWaitBlockReason) = await WaitForIdleYieldAsync(machine);
                }
                else if (!idleSpinAccelerated)
                {
                    idleYielded = WaitForIdleYield(machine, out idleWaitBlockReason);
                }

                if (atIdleYieldLoop)
                {
                    idleLoopChecks++;
                    switch (idleWaitBlockReason)
                    {
                        case IdleWaitBlockReason.PendingControls:
                            idleBlockedByPendingControls++;
                            break;
                        case IdleWaitBlockReason.WatchdogNearExpiry:
                            idleBlockedByWatchdog++;
                            break;
                        case IdleWaitBlockReason.MachineWaitUnavailable:
                            idleBlockedByMachineWait++;
                            break;
                    }
                }

                if (idleYielded)
                {
                    idleYieldWaits++;
                    pacer.Reanchor(machine.Bus.Cycles);
                }

                int stepsThisBatch = activeBatch.Steps;
                AdaptiveStepBatch? measuredBatch = activeBatch;
                if (idleYielded || idleSpinAccelerated)
                {
                    stepsThisBatch = machine.InterruptPending || HasFutureScheduledKeys()
                        ? 1
                        : 0;
                    measuredBatch = null;
                }
                else if (atIdleYieldLoop)
                {
                    // Move only to the canonical loop boundary. A large batch can finish at
                    // another instruction in this four-instruction loop and repeatedly miss
                    // the safe fast-forward point.
                    stepsThisBatch = 1;
                    measuredBatch = null;
                }

                if (ShouldReleaseStartupPower(machine, startupPowerPressedAtCycles))
                {
                    machine.Io.SetStartupPowerKeyHeld(false);
                }

                long batchStartedAt = Stopwatch.GetTimestamp();
                for (int i = 0; i < stepsThisBatch; i++)
                {
                    if (logMonitor)
                    {
                        CpuHistoryEntry pendingInstruction = new(
                            ExecutedSteps,
                            machine.Cpu.GetGpr(15),
                            machine.Cpu.CpsrValue,
                            machine.Cpu.GetPipelineOpcode(0),
                            machine.Cpu.GetGpr(13),
                            machine.Cpu.GetGpr(14),
                            machine.Cpu.GetGpr(0),
                            machine.Cpu.GetGpr(1),
                            machine.Cpu.GetGpr(2),
                            machine.Cpu.GetGpr(3),
                            machine.Cpu.GetGpr(11),
                            machine.Cpu.GetGpr(12));
                        cpuHistory[cpuHistoryNext] = pendingInstruction;
                        cpuHistoryNext = (cpuHistoryNext + 1) % cpuHistory.Length;

                        // The v4.18 scheduler returns from FIQ at fetch PC 0x2D5940.
                        // A return LR that points into the FIQ handler causes recursive context corruption.
                        // Capture the first occurrence. Do not capture later iterations of the short epilogue loop.
                        uint fiqSpsr = machine.Cpu.GetSpsrRaw(Noks.Cpu.ArmBank.Fiq);
                        uint fiqReturnPc = pendingInstruction.Lr - 4;
                        if (!fiqSelfReturnDumped &&
                            pendingInstruction.Pc == 0x002D5940 &&
                            (fiqSpsr & 0x20) == 0 &&
                            fiqReturnPc is >= 0x002D58F4 and <= 0x002D5940)
                        {
                            fiqSelfReturnDumped = true;
                            Console.Error.WriteLine(
                                $"Noks recursive FIQ return target={fiqReturnPc:X8} " +
                                $"step={pendingInstruction.Step} lr={pendingInstruction.Lr:X8} " +
                                $"spsr={fiqSpsr:X8} " +
                                $"fiqLine={machine.Cpu.FiqLine} irqLine={machine.Cpu.IrqLine} " +
                                $"fiq={machine.Io.EffectiveFiqStatusValue:X3}/{machine.Io.FiqMaskRegister:X2} " +
                                $"irq={machine.Io.IrqStatusValue:X3}/{machine.Io.IrqMaskRegister:X2} " +
                                $"ctl={machine.Io.InterruptControlRegister:X2}");
                            for (int historyOffset = 0; historyOffset < cpuHistory.Length; historyOffset++)
                            {
                                CpuHistoryEntry entry = cpuHistory[
                                    (cpuHistoryNext + historyOffset) % cpuHistory.Length];
                                Console.Error.WriteLine(
                                    $"  step={entry.Step} pc={entry.Pc:X8} cpsr={entry.Cpsr:X8} " +
                                    $"instruction={entry.Instruction:X8} sp={entry.Sp:X8} lr={entry.Lr:X8} " +
                                    $"r0={entry.R0:X8} r1={entry.R1:X8} r2={entry.R2:X8} r3={entry.R3:X8} " +
                                    $"r11={entry.R11:X8} r12={entry.R12:X8}");
                            }
                        }
                    }

                    machine.Step();
                    ExecutedSteps++;

                    uint steppedPc = machine.Cpu.GetGpr(15);
                    bool suspiciousHighFlash = steppedPc is >= 0x003F_0000 and < 0x0040_0010;
                    bool suspiciousThumbVector = steppedPc < 0x40 && (machine.Cpu.CpsrValue & 0x20) != 0;
                    bool suspiciousSystemVector = steppedPc < 0x20 &&
                        (machine.Cpu.CpsrValue & 0x1F) is Noks.Cpu.Arm7Tdmi.ModeUsr or Noks.Cpu.Arm7Tdmi.ModeSys;
                    if (logMonitor && !cpuHistoryDumped &&
                        (!IsMappedExecutionPc(steppedPc) || suspiciousHighFlash || suspiciousThumbVector ||
                            suspiciousSystemVector))
                    {
                        cpuHistoryDumped = true;
                        Console.Error.WriteLine(
                            suspiciousHighFlash
                                ? "Noks CPU execution history before entering high-flash data:"
                                : "Noks CPU execution history before escape:");
                        for (int historyOffset = 0; historyOffset < cpuHistory.Length; historyOffset++)
                        {
                            CpuHistoryEntry entry = cpuHistory[(cpuHistoryNext + historyOffset) % cpuHistory.Length];
                            Console.Error.WriteLine(
                                $"  step={entry.Step} pc={entry.Pc:X8} cpsr={entry.Cpsr:X8} " +
                                $"instruction={entry.Instruction:X8} sp={entry.Sp:X8} lr={entry.Lr:X8} " +
                                $"r0={entry.R0:X8} r1={entry.R1:X8} r2={entry.R2:X8} r3={entry.R3:X8} " +
                                $"r11={entry.R11:X8} r12={entry.R12:X8}");
                        }

                        Console.Error.WriteLine(
                            "Noks CPU first escaped mapped execution: " +
                            $"{FormatCpuFaultState(machine)}");
                    }
                }
                long batchFinishedAt = Stopwatch.GetTimestamp();
                measuredBatch?.Observe(stepsThisBatch, batchFinishedAt - batchStartedAt);
                PublishAudioState(new Dct3AudioState(
                    machine.Io.ConsumeAudioState(),
                    machine.ConsumeDspToneState()));
                LogExecutionFault(machine);
                FlushRequestedPersistenceSave(machine);

                if (cooperative)
                {
                    if (paceToRealTime)
                    {
                        await pacer.PaceAsync(machine.Bus.Cycles, cancellation.Token);
                    }
                    else
                    {
                        await Task.Yield();
                    }
                }
                else if (paceToRealTime)
                {
                    pacer.Pace(machine.Bus.Cycles, cancellation.Token);
                }
                else
                {
                    Thread.Yield();
                }
                long now = Stopwatch.GetTimestamp();

                if (now >= nextStatePublishAt)
                {
                    nextStatePublishAt = now + StatePublishIntervalTicks;
                    long previousRuntimeSecond = Cycles / Dct3Machine.CyclesPerSecond;
                    Mad2PeripheralState previousPeripheralState = Volatile.Read(ref peripheralState);
                    CcontControlState previousCcontState = Volatile.Read(ref ccontState);
                    DspRadioControlState previousDspRadioState = Volatile.Read(ref dspRadioState);
                    GsmControlState previousGsmState = Volatile.Read(ref gsmState);
                    Cycles = machine.Bus.Cycles;
                    Volatile.Write(ref pacing, pacer.State);
                    Volatile.Write(ref peripheralState, machine.Io.PeripheralState);
                    CcontControlState currentCcontState = PublishCcontState(machine);
                    PublishDspStates(machine);
                    PublishTelemetry(machine, currentCcontState, idleLoopChecks, idleYieldWaits);
                    SchedulePersistenceSave(machine);

                    if (previousRuntimeSecond != Cycles / Dct3Machine.CyclesPerSecond ||
                        previousPeripheralState != Volatile.Read(ref peripheralState) ||
                        previousCcontState != Volatile.Read(ref ccontState) ||
                        previousDspRadioState != Volatile.Read(ref dspRadioState) ||
                        previousGsmState != Volatile.Read(ref gsmState))
                    {
                        StateChanged?.Invoke(this);
                    }

                    if (ExecutedSteps - lastStatusStep >= 1_000_000)
                    {
                        lastStatusStep = ExecutedSteps;
                        PublishStatus(machine.Lcd.DataWrites > 0 ? "Running" : "Booting");
                    }
                }

#if BROWSER
                if (logDiagnostics && ExecutedSteps - lastConsoleStep >= 5_000_000)
                {
                    lastConsoleStep = ExecutedSteps;
                    CcontControlState currentCcontState = CcontControlState.From(machine.AdcInputs, machine.Ccont, machine.Bus);
                    Console.WriteLine(
                        $"Noks browser emulation: {TelemetryLine(machine, currentCcontState, idleLoopChecks, idleYieldWaits)} " +
                        $"activeBatch={activeBatch.Steps} lcdWrites={machine.Lcd.DataWrites}");
                }
#endif

                if (logDiagnostics && now - lastDiagnosticsTimestamp >= Stopwatch.Frequency * 5)
                {
                    lastDiagnosticsTimestamp = now;
                    CcontControlState currentCcontState = CcontControlState.From(machine.AdcInputs, machine.Ccont, machine.Bus);
                    Console.WriteLine(
                        $"Noks desktop emulation: {TelemetryLine(machine, currentCcontState, idleLoopChecks, idleYieldWaits)} " +
                        $"activeBatch={activeBatch.Steps} " +
                        $"idleSpinAccelerations={idleSpinAccelerations} " +
                        $"idleBlockPending={idleBlockedByPendingControls} idleBlockWatchdog={idleBlockedByWatchdog} idleBlockMachine={idleBlockedByMachineWait} " +
                        $"idleBlockReason={machine.DescribeIdleYieldWaitBlock(IdleYieldWaitCap)} interrupt={machine.InterruptPending} " +
                        $"lcdWrites={machine.Lcd.DataWrites}");
                }

                if (logMonitor && now >= nextMonitorTimestamp)
                {
                    nextMonitorTimestamp = now + Stopwatch.Frequency;
                    long stepDelta = ExecutedSteps - lastMonitorStep;
                    long cycleDelta = machine.Bus.Cycles - lastMonitorCycle;
                    lastMonitorStep = ExecutedSteps;
                    lastMonitorCycle = machine.Bus.Cycles;
                    Console.WriteLine(
                        $"Noks monitor: steps={ExecutedSteps} (+{stepDelta}) cycles={machine.Bus.Cycles} (+{cycleDelta}) " +
                        $"pc={machine.Cpu.GetGpr(15):X8} cpsr={machine.Cpu.CpsrValue:X8} status={Status} idle={machine.IsAtIdleYieldLoop()} " +
                        $"idleSpin={idleSpinAccelerations} idleWaits={idleYieldWaits} pendingKeys={pendingKeyTransitionCount} " +
                        $"heldKeys=\"{FormatPressedKeys()}\" irq={machine.InterruptPending} " +
                        $"audio={AudioState.Audible}:buzz={AudioState.Buzzer.BuzzerDivider:X2}:dsp={AudioState.DspTone.Oscillator1Hz:F0}/{AudioState.DspTone.Oscillator2Hz:F0} " +
                        $"gsm={machine.DspState.Registered}/{machine.DspState.DedicatedChannelActive} " +
                        $"simInt={machine.Io.SimInterruptId:X2} t0={machine.Io.Timer0Counter:X4}/{machine.Io.Timer0Compare:X4}:{machine.Io.Timer0Divider:X2} " +
                        $"t1={machine.Io.Timer1Counter:X4} fiq={machine.Io.EffectiveFiqStatusValue:X3}/{machine.Io.FiqMaskRegister:X2} " +
                        $"irq={machine.Io.IrqStatusValue:X3}/{machine.Io.IrqMaskRegister:X2} ctl={machine.Io.InterruptControlRegister:X2} " +
                        $"mbus={machine.Io.PeekRegister(0x18):X2}/{machine.Io.PeekRegister(0x19):X2} fiq8={machine.Io.Fiq8TimerEnabled} lcdWrites={machine.Lcd.DataWrites}");
                }
            }

            if (machine.PoweredOff)
            {
                PublishStatus("Powered off");
                CcontControlState currentCcontState = PublishCcontState(machine);
                PublishTelemetry(machine, currentCcontState, idleLoopChecks, idleYieldWaits);
                StateChanged?.Invoke(this);
                SchedulePersistenceSave(machine, force: true);
                Console.WriteLine($"Noks browser poweroff: {TelemetryLine(machine, currentCcontState, idleLoopChecks, idleYieldWaits)}");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PublishStatus(ex.Message);
        }
    }

    private static byte[] LoadFlashFile(string flashPath)
    {
        if (!File.Exists(flashPath))
        {
            throw new FileNotFoundException($"The flash file was not found: {flashPath}.", flashPath);
        }

        return File.ReadAllBytes(flashPath);
    }

    private static byte[]? LoadOptionalFile(string? path)
    {
        if (path is null)
        {
            return null;
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The file was not found: {path}.", path);
        }

        return File.ReadAllBytes(path);
    }

    private void MarkPersistenceLoaded(Dct3Machine machine)
    {
        lastPersistedPersistenceVersion = machine.PersistenceVersion;
        Volatile.Write(ref immediatePersistenceSavePending, 0);
    }

    private void EnqueueSimMutation(SimMutation mutation)
    {
        EnqueueBounded(simMutations, mutation, maximumCount: 4096);
        Volatile.Write(ref simMutationNotificationPending, 1);

        if (RequiresImmediatePersistenceSave(mutation))
        {
            Volatile.Write(ref immediatePersistenceSavePending, 1);
        }
    }

    private static bool RequiresImmediatePersistenceSave(SimMutation mutation) =>
        mutation.Origin != SimMutationOrigin.PersistenceRestore &&
        mutation.ParentFileId == SimTelecomDirectoryFileId &&
        mutation.FileId is SimAdnFileId or SimSmsFileId;

    private void FlushRequestedPersistenceSave(Dct3Machine machine)
    {
        if (Interlocked.Exchange(ref immediatePersistenceSavePending, 0) != 0)
        {
            SchedulePersistenceSave(machine, force: true);
        }
    }

    private void SchedulePersistenceSave(Dct3Machine machine, bool force = false)
    {
        if (persistence is null || machine.PersistenceVersion == lastPersistedPersistenceVersion)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (!force &&
            lastPersistenceSaveTimestamp != 0 &&
            now - lastPersistenceSaveTimestamp < PersistenceSaveIntervalTicks)
        {
            return;
        }

        Dct3PersistenceSnapshot snapshot = machine.CreatePersistenceSnapshot();
        long version = machine.PersistenceVersion;
        lastPersistedPersistenceVersion = version;
        lastPersistenceSaveTimestamp = now;

        bool startSave;
        lock (persistenceLock)
        {
            pendingPersistenceSnapshot = snapshot;
            startSave = !persistenceSaveInFlight;

            if (startSave)
            {
                persistenceSaveInFlight = true;
            }
        }

        if (startSave)
        {
            _ = DrainPersistenceSavesAsync();
        }
    }

    private async Task DrainPersistenceSavesAsync()
    {
        if (persistence is null)
        {
            return;
        }

        Dct3PersistenceSnapshot? failedSnapshot = null;
        try
        {
            while (true)
            {
                Dct3PersistenceSnapshot snapshot;

                lock (persistenceLock)
                {
                    if (pendingPersistenceSnapshot is null)
                    {
                        persistenceSaveInFlight = false;
                        return;
                    }

                    snapshot = pendingPersistenceSnapshot;
                    pendingPersistenceSnapshot = null;
                }

                failedSnapshot = snapshot;
                await SavePersistenceSnapshotAsync(snapshot).ConfigureAwait(false);
                failedSnapshot = null;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (failedSnapshot is not null)
            {
                lock (persistenceLock)
                    pendingPersistenceSnapshot ??= failedSnapshot;
            }
            Console.WriteLine($"Noks persistence save failed: {ex.Message}");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }
        finally
        {
            bool restart;
            lock (persistenceLock)
            {
                restart = pendingPersistenceSnapshot is not null && !cancellation.IsCancellationRequested;
                if (restart)
                {
                    persistenceSaveInFlight = true;
                }
                else
                {
                    persistenceSaveInFlight = false;
                }
            }

            if (restart)
            {
                _ = DrainPersistenceSavesAsync();
            }
        }
    }

    private Task SavePersistenceSnapshotAsync(Dct3PersistenceSnapshot snapshot)
    {
        if (persistence is null)
        {
            return Task.CompletedTask;
        }

#if BROWSER
        return Dispatcher.UIThread.InvokeAsync(
            async () => await persistence.Store.SaveAsync(persistence.Key, snapshot, cancellation.Token));
#else
        return persistence.Store.SaveAsync(persistence.Key, snapshot, cancellation.Token).AsTask();
#endif
    }

    private bool WaitForIdleYield(Dct3Machine machine, out IdleWaitBlockReason blockReason)
    {
        blockReason = IdleWaitBlockReason.None;
        machine.ServiceWallClockTimers();
        machine.ServicePendingPeripherals();
        ServiceInputChanges(machine);

        if (HasPendingControls())
        {
            blockReason = IdleWaitBlockReason.PendingControls;
            return false;
        }

        if (IsCcontWatchdogNearExpiry(machine))
        {
            blockReason = IdleWaitBlockReason.WatchdogNearExpiry;
            return false;
        }

        if (!machine.TryGetIdleYieldWait(out TimeSpan wait, IdleYieldWaitCap))
        {
            long advancedCycles = machine.FastForwardOverdueIdleToWallClock(MinimumOverdueIdleFastForwardCycles);
            if (advancedCycles > 0)
            {
                return true;
            }

            blockReason = IdleWaitBlockReason.MachineWaitUnavailable;
            return false;
        }

        TimeSpan boundedWait = wait < MinimumIdleYieldBlock ? MinimumIdleYieldBlock : wait;
        inputChanged.WaitOne(boundedWait);

        ServiceInputChanges(machine);
        if (!machine.InterruptPending)
        {
            machine.FastForwardIdleToWallClock();
        }

        return !cancellation.IsCancellationRequested;
    }

    private async ValueTask<(bool Yielded, IdleWaitBlockReason BlockReason)> WaitForIdleYieldAsync(
        Dct3Machine machine)
    {
        machine.ServiceWallClockTimers();
        machine.ServicePendingPeripherals();
        ServiceInputChanges(machine);

        if (HasPendingControls())
        {
            return (false, IdleWaitBlockReason.PendingControls);
        }

        if (IsCcontWatchdogNearExpiry(machine))
        {
            return (false, IdleWaitBlockReason.WatchdogNearExpiry);
        }

        if (!machine.TryGetIdleYieldWait(out TimeSpan wait, IdleYieldWaitCap))
        {
            long advancedCycles = machine.FastForwardOverdueIdleToWallClock(MinimumOverdueIdleFastForwardCycles);
            return advancedCycles > 0
                ? (true, IdleWaitBlockReason.None)
                : (false, IdleWaitBlockReason.MachineWaitUnavailable);
        }

        TimeSpan delay = wait < MinimumIdleYieldBlock ? MinimumIdleYieldBlock : wait;
        await Task.Delay(delay, cancellation.Token);
        ServiceInputChanges(machine);
        if (!machine.InterruptPending)
        {
            machine.FastForwardIdleToWallClock();
        }

        return (!cancellation.IsCancellationRequested, IdleWaitBlockReason.None);
    }

    private bool HasPendingControls() =>
        HasPendingInputChanges() ||
        !keyChanges.IsEmpty ||
        pendingKeyTransitionCount > 0 ||
        !ccontAdcChanges.IsEmpty ||
        !dspRadioChanges.IsEmpty ||
        !gsmIncomingChanges.IsEmpty ||
        !memoryReadRequests.IsEmpty ||
        (nextScheduledKeyChange < scheduledKeyChanges.Count &&
            scheduledKeyChanges[nextScheduledKeyChange].Step <= ExecutedSteps);

    private bool HasFutureScheduledKeys() =>
        nextScheduledKeyChange < scheduledKeyChanges.Count;

    private static bool IsCcontWatchdogNearExpiry(Dct3Machine machine) =>
        machine.CcontWatchdogEnabled && machine.Ccont.WatchdogValue is > 0 and <= 8;

    private void ApplyScheduledKeys(Dct3Machine machine)
    {
        while (nextScheduledKeyChange < scheduledKeyChanges.Count &&
            scheduledKeyChanges[nextScheduledKeyChange].Step <= ExecutedSteps)
        {
            ScheduledPhoneKeyChange change = scheduledKeyChanges[nextScheduledKeyChange++];
            QueueKeyTransition(new KeyChange(change.Key, change.Pressed));
        }
    }

    private void QueueInputKeyChanges()
    {
        while (keyChanges.TryDequeue(out KeyChange change))
        {
#if BROWSER
            if (change.Pressed)
            {
                BrowserInteractionLatencyBenchmark.MarkWorkerDequeued(change.Key);
            }
#endif
            QueueKeyTransition(change);
        }
    }

    private void QueueKeyTransition(KeyChange change)
    {
        if (!keyTimingStates.TryGetValue(change.Key, out KeyTimingState? state))
        {
            state = new KeyTimingState(change.Key);
            keyTimingStates.Add(change.Key, state);
        }

        if (state.RequestedPressed == change.Pressed)
        {
            return;
        }

        bool wasPending = state.HasPendingTransition;
        state.RequestedPressed = change.Pressed;
        if (change.Pressed && !state.OutputPressed)
        {
            state.PressPending = true;
        }

        UpdatePendingKeyTransitionCount(state, wasPending);
    }

    private void AdvanceKeyTransitions(Dct3Machine machine)
    {
        if (pendingKeyTransitionCount == 0)
        {
            return;
        }

        foreach (KeyTimingState state in keyTimingStates.Values)
        {
            AdvanceKeyTransitions(machine, state);
        }
    }

    private void AdvanceKeyTransitions(Dct3Machine machine, KeyTimingState state)
    {
        bool wasPending = state.HasPendingTransition;
        if (!state.OutputPressed)
        {
            if (!state.PressPending)
            {
                return;
            }

            if (state.OutputChanged &&
                machine.Bus.Cycles - state.OutputChangedAtCycles < MinimumKeyReleaseCycles)
            {
                return;
            }

            state.PressPending = false;
            state.OutputPressed = true;
            state.OutputChanged = true;
            state.OutputChangedAtCycles = machine.Bus.Cycles;
            SetMatrixKey(state.Key, pressed: true);
        }
        else
        {
            if (state.RequestedPressed ||
                machine.Bus.Cycles - state.OutputChangedAtCycles < MinimumKeyHoldCycles)
            {
                return;
            }

            state.OutputPressed = false;
            state.OutputChangedAtCycles = machine.Bus.Cycles;
            SetMatrixKey(state.Key, pressed: false);
        }

        UpdatePendingKeyTransitionCount(state, wasPending);
    }

    private void UpdatePendingKeyTransitionCount(KeyTimingState state, bool wasPending)
    {
        if (wasPending == state.HasPendingTransition)
        {
            return;
        }

        pendingKeyTransitionCount += state.HasPendingTransition ? 1 : -1;
    }

    private bool ShouldReleaseStartupPower(Dct3Machine machine, long startupPowerPressedAtCycles) =>
        machine.Io.StartupPowerKeyHeld &&
        !keyMatrix.PowerKeyPressed &&
        machine.Bus.Cycles >= startupPowerPressedAtCycles + StartupPowerMinimumHoldCycles &&
        (machine.Lcd.DataWrites >= StartupPowerReadyLcdWrites ||
            machine.Bus.Cycles >= startupPowerPressedAtCycles + StartupPowerMaximumHoldCycles);

    private void ApplyQueuedControls(Dct3Machine machine)
    {
        while (memoryReadRequests.TryDequeue(out MemoryReadRequest? request))
        {
            try
            {
                request.Completion.TrySetResult(machine.CreateRamSnapshot(request.Address, request.Length));
            }
            catch (Exception ex)
            {
                request.Completion.TrySetException(ex);
            }
        }

        while (ccontAdcChanges.TryDequeue(out CcontAdcChange change))
        {
            if (change.Reset)
            {
                CopyCcontAdcInputs(CcontAdcInputs.NormalBattery(), machine.AdcInputs);
                machine.Ccont.AdcInputsChanged();
            }
            else
            {
                SetCcontAdc(machine.AdcInputs, change.Channel, change.Value);
                machine.Ccont.AdcInputChanged((int)change.Channel);
            }
        }

        while (dspRadioChanges.TryDequeue(out DspRadioChange radioChange))
        {
            machine.SetDspRssi(radioChange.Rssi);
        }

        while (facadeNetworkChanges.TryDequeue(out FacadeNetworkChange networkChange))
        {
            machine.SetFacadeNetworkAvailable(networkChange.Available);
        }

        while (gsmIncomingChanges.TryDequeue(out GsmIncomingChange gsmChange))
        {
            if (gsmChange.Kind == GsmIncomingKind.Call)
            {
                machine.QueueIncomingCall(gsmChange.CorrelationId, gsmChange.Address);
            }
            else if (gsmChange.Kind == GsmIncomingKind.SmartMessage)
            {
                machine.QueueIncomingSmartMessage(gsmChange.Address, gsmChange.DestinationPort, gsmChange.Payload);
            }
            else
            {
                machine.QueueIncomingSms(gsmChange.Address, gsmChange.Text, gsmChange.SentAt);
            }
        }

        while (networkResolutionChanges.TryDequeue(out ResolveNetworkRequest? resolution))
        {
            machine.ResolveNetworkRequest(resolution);
        }

        while (networkCallConnections.TryDequeue(out Guid connectRequestId))
        {
            machine.ConnectNetworkCall(connectRequestId);
        }

        while (networkCallTerminations.TryDequeue(out Guid requestId))
        {
            machine.TerminateNetworkCall(requestId);
        }

        while (managedOwnNumberChanges.TryDequeue(out string? phoneNumber))
        {
            machine.SetManagedOwnNumber(phoneNumber);
        }
    }

    private void PublishPendingBridgeNotifications()
    {
        // A firmware batch can commit an EF_ADN write and submit a call before
        // control returns here. Publish the contact mutation first so the Waku
        // bridge indexes the saved number before it evaluates the route.
        if (Interlocked.Exchange(ref simMutationNotificationPending, 0) != 0)
        {
            SimMutationAvailable?.Invoke(this);
        }

        if (Interlocked.Exchange(ref networkNotificationPending, 0) != 0)
        {
            NetworkRequestAvailable?.Invoke(this);
        }

        if (Interlocked.Exchange(ref callTransitionNotificationPending, 0) != 0)
        {
            CallTransitionAvailable?.Invoke(this);
        }

        if (Interlocked.Exchange(ref audioAnnouncementNotificationPending, 0) != 0)
        {
            AudioAnnouncementAvailable?.Invoke(this);
        }
    }

    private static void EnqueueBounded<T>(ConcurrentQueue<T> queue, T item, int maximumCount)
    {
        queue.Enqueue(item);
        while (queue.Count > maximumCount)
        {
            queue.TryDequeue(out _);
        }
    }

    private void ServiceInputChanges(Dct3Machine machine)
    {
        (int changeGeneration, int pressGeneration) = keyMatrix.Generations;
        if (changeGeneration == observedInputChangeGeneration &&
            pressGeneration == observedInputPressGeneration)
        {
            return;
        }

        if (pressGeneration != observedInputPressGeneration)
        {
#if BROWSER
            BrowserInteractionLatencyBenchmark.MarkKeypadInterrupt();
#endif
            machine.Io.AssertKeypadIrq();
            observedInputPressGeneration = pressGeneration;
        }

        observedInputChangeGeneration = changeGeneration;
    }

    private bool HasPendingInputChanges()
    {
        (int changeGeneration, int pressGeneration) = keyMatrix.Generations;
        return changeGeneration != observedInputChangeGeneration ||
            pressGeneration != observedInputPressGeneration;
    }

    private bool SetMatrixKey(PhoneKey key, bool pressed)
    {
        Dct3KeyBinding binding = Dct3KeyMaps.GetBinding(ToDct3Key(key), keyMap);

        bool changed = binding.Power
            ? keyMatrix.SetPowerKey(pressed)
            : keyMatrix.SetKey(binding.Column, binding.Row, pressed);
#if BROWSER
        if (changed && pressed)
        {
            BrowserInteractionLatencyBenchmark.MarkMatrixApplied(key);
        }
#endif
        return changed;
    }

    private bool IsMatrixKeyPressed(PhoneKey key)
    {
        Dct3KeyBinding binding = Dct3KeyMaps.GetBinding(ToDct3Key(key), keyMap);

        return binding.Power
            ? keyMatrix.PowerKeyPressed
            : keyMatrix.IsKeyPressed(binding.Column, binding.Row);
    }

    private void PublishFrame(Pcd8544 lcd)
    {
        LcdFrame next = new(lcd.Vram.ToArray(), lcd.DisplayMode, lcd.PowerDown, lcd.DataWrites);
        Volatile.Write(ref frame, next);
        FrameChanged?.Invoke(this);
    }

    private void PublishAudioState(Dct3AudioState state)
    {
        Dct3AudioState previous = Volatile.Read(ref audioState);
        if (state == previous)
        {
            return;
        }

#if BROWSER
        if (state.Audible && !previous.Audible)
        {
            BrowserInteractionLatencyBenchmark.MarkAudioStatePublished();
        }
#endif
        Volatile.Write(ref audioState, state);
        AudioStateChanged?.Invoke(this);
    }

    private CcontControlState PublishCcontState(Dct3Machine machine)
    {
        CcontControlState state = CcontControlState.From(machine.AdcInputs, machine.Ccont, machine.Bus);
        Volatile.Write(ref ccontState, state);
        return state;
    }

    private void PublishDspStates(Dct3Machine machine)
    {
        DspRuntimeState state = machine.DspState;
        Volatile.Write(ref dspRadioState, DspRadioControlState.From(state));
        Volatile.Write(ref gsmState, GsmControlState.From(state));
    }

    private void PublishTelemetry(
        Dct3Machine machine,
        CcontControlState currentCcontState,
        long idleLoopChecks,
        long idleYieldWaits)
    {
        CcontRtcState rtcState = machine.Ccont.RtcState;
        PhoneTelemetryState state = new(
            ExecutedSteps,
            machine.Bus.Cycles,
            machine.Bus.Cycles / (double)Dct3Machine.CyclesPerSecond,
            machine.Cpu.GetGpr(15),
            machine.Cpu.CpsrValue,
            machine.Cpu.UndefinedInstructionCount,
            machine.Cpu.LastUndefinedInstructionAddress,
            machine.Cpu.LastUndefinedInstruction,
            machine.Io.SimControlRegister,
            machine.Io.SimControlStatus,
            machine.Io.SimInterruptId,
            machine.Io.SimRxCount,
            machine.Io.SimTxCount,
            machine.PoweredOff,
            machine.Ccont.LastPowerOffReason,
            machine.WatchdogResets,
            machine.Ccont.WatchdogValue,
            machine.Ccont.LastWatchdogCommand,
            machine.Ccont.WatchdogArmReloads,
            machine.Ccont.WatchdogKicks,
            machine.Ccont.WatchdogDisables,
            machine.Ccont.WatchdogExpires,
            rtcState.Control,
            rtcState.InterruptPending,
            rtcState.InterruptMask,
            rtcState.Second,
            rtcState.Minute,
            rtcState.Hour,
            rtcState.Day,
            machine.Io.PowerKeyHeld,
            machine.IdleYieldHook.HasValue,
            machine.IsAtIdleYieldLoop(),
            idleLoopChecks,
            idleYieldWaits,
            machine.WallClockPauseCount,
            machine.LastWallClockPauseMilliseconds,
            FormatPressedKeys(),
            currentCcontState);
        Volatile.Write(ref telemetry, state);
        TelemetryChanged?.Invoke(this);
    }

    private void PublishStatus(string value)
    {
        if (string.Equals(Volatile.Read(ref status), value, StringComparison.Ordinal))
        {
            return;
        }

        Volatile.Write(ref status, value);
        StateChanged?.Invoke(this);
    }

    private void LogExecutionFault(Dct3Machine machine)
    {
        uint pc = machine.Cpu.GetGpr(15);
        if (!invalidExecutionLogged && !IsMappedExecutionPc(pc))
        {
            invalidExecutionLogged = true;
            Console.Error.WriteLine($"Noks CPU escaped mapped execution: {FormatCpuFaultState(machine)}");
        }
    }

    private static bool IsMappedExecutionPc(uint pc) =>
        pc < 0x0018_0010 || pc is >= 0x0020_0000 and < 0x0040_0010;

    private static string FormatCpuFaultState(Dct3Machine machine)
    {
        string registers = string.Join(
            ' ',
            Enumerable.Range(0, 16).Select(index => $"r{index}={machine.Cpu.GetGpr(index):X8}"));
        return $"cycles={machine.Bus.Cycles} cpsr={machine.Cpu.CpsrValue:X8} " +
            $"pipe0={machine.Cpu.GetPipelineOpcode(0):X8} pipe1={machine.Cpu.GetPipelineOpcode(1):X8} {registers}";
    }

    private string TelemetryLine(
        Dct3Machine machine,
        CcontControlState currentCcontState,
        long idleLoopChecks,
        long idleYieldWaits)
    {
        CcontRtcState rtcState = machine.Ccont.RtcState;
        return $"steps={ExecutedSteps} cycles={machine.Bus.Cycles} emu={machine.Bus.Cycles / (double)Dct3Machine.CyclesPerSecond:F1}s " +
            $"pc={machine.Cpu.GetGpr(15):X8} cpsr={machine.Cpu.CpsrValue:X8} poweredOff={machine.PoweredOff} " +
            $"powerReason=\"{machine.Ccont.LastPowerOffReason}\" watchdogs={machine.WatchdogResets} ccontWd={machine.Ccont.WatchdogValue:X2} " +
            $"ccontWdEn={machine.CcontWatchdogEnabled} " +
            $"ccontCmd={machine.Ccont.LastWatchdogCommand:X2} ccontArm={machine.Ccont.WatchdogArmReloads} ccontKick={machine.Ccont.WatchdogKicks} ccontDis={machine.Ccont.WatchdogDisables} ccontExp={machine.Ccont.WatchdogExpires} " +
            $"rtcCtl={rtcState.Control:X2} rtcPend={rtcState.InterruptPending:X2} rtcMask={rtcState.InterruptMask:X2} rtc={rtcState.Hour:00}:{rtcState.Minute:00}:{rtcState.Second:00} rtcDay={rtcState.Day:00} " +
            $"gsmReg={machine.DspState.Registered} gsmDedicated={machine.DspState.DedicatedChannelActive} gsmPending={machine.DspState.PendingIncomingServices} " +
            $"pwrKey={machine.Io.PowerKeyHeld} idleHook={machine.IdleYieldHook.HasValue} idleNow={machine.IsAtIdleYieldLoop()} " +
            $"idleChecks={idleLoopChecks} idleWaits={idleYieldWaits} heldKeys=\"{FormatPressedKeys()}\" wallPauses={machine.WallClockPauseCount} wallPauseLastMs={machine.LastWallClockPauseMilliseconds:F0} " +
            $"vbat={currentCcontState.BatteryVoltage:X3} vchg={currentCcontState.ChargerVoltage:X3} ichg={currentCcontState.ChargingCurrent:X3} " +
            $"fwPwr={currentCcontState.FirmwarePowerState:X2} fwBat={currentCcontState.FirmwareBatteryPercent:X2} fwClass={currentCcontState.FirmwareBatteryClass:X2} fwFlags={currentCcontState.FirmwareBatteryFlags:X2} fwSample={currentCcontState.FirmwareBatterySample:X4}";
    }

    private string FormatPressedKeys() =>
        Enum.GetValues<PhoneKey>().Where(IsMatrixKeyPressed).OrderBy(key => key.ToString()).ToArray() is { Length: > 0 } keys
            ? string.Join(",", keys)
            : "-";

    private static void SetCcontAdc(CcontAdcInputs inputs, CcontAdcChannel channel, ushort value)
    {
        inputs.Set((int)channel, value);
    }

    private static void CopyCcontAdcInputs(CcontAdcInputs source, CcontAdcInputs target)
    {
        target.CopyFrom(source);
    }

    private static Dct3Key ToDct3Key(PhoneKey key) => key switch
    {
        PhoneKey.Power => Dct3Key.Power,
        PhoneKey.Digit0 => Dct3Key.Digit0,
        PhoneKey.Digit1 => Dct3Key.Digit1,
        PhoneKey.Digit2 => Dct3Key.Digit2,
        PhoneKey.Digit3 => Dct3Key.Digit3,
        PhoneKey.Digit4 => Dct3Key.Digit4,
        PhoneKey.Digit5 => Dct3Key.Digit5,
        PhoneKey.Digit6 => Dct3Key.Digit6,
        PhoneKey.Digit7 => Dct3Key.Digit7,
        PhoneKey.Digit8 => Dct3Key.Digit8,
        PhoneKey.Digit9 => Dct3Key.Digit9,
        PhoneKey.Star => Dct3Key.Star,
        PhoneKey.Hash => Dct3Key.Hash,
        PhoneKey.Left => Dct3Key.Up,
        PhoneKey.Right => Dct3Key.Down,
        PhoneKey.Main => Dct3Key.Main,
        PhoneKey.Cancel => Dct3Key.Clear,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    public sealed record LcdFrame(byte[] Vram, int DisplayMode, bool PowerDown, long DataWrites)
    {
        public static LcdFrame Empty { get; } = new(new byte[Pcd8544.Width * Pcd8544.Height / 8], 0, true, 0);

        public bool GetPixel(int x, int y)
        {
            bool bit = ((Vram[y / 8 * Pcd8544.Width + x] >> (y % 8)) & 1) != 0;
            return DisplayMode == 3 ? !bit : bit;
        }
    }

    private readonly record struct CcontAdcChange(CcontAdcChannel Channel, ushort Value, bool Reset);

    private readonly record struct KeyChange(PhoneKey Key, bool Pressed);

    private readonly record struct CpuHistoryEntry(
        long Step,
        uint Pc,
        uint Cpsr,
        uint Instruction,
        uint Sp,
        uint Lr,
        uint R0,
        uint R1,
        uint R2,
        uint R3,
        uint R11,
        uint R12);

    private sealed record MemoryReadRequest(
        uint Address,
        int Length,
        TaskCompletionSource<byte[]> Completion);

    private sealed class KeyTimingState(PhoneKey key)
    {
        public PhoneKey Key { get; } = key;

        public bool RequestedPressed { get; set; }

        public bool PressPending { get; set; }

        public bool OutputPressed { get; set; }

        public bool OutputChanged { get; set; }

        public long OutputChangedAtCycles { get; set; }

        public bool HasPendingTransition =>
            OutputPressed ? !RequestedPressed : PressPending;
    }

    private readonly record struct DspRadioChange(byte Rssi);

    private readonly record struct FacadeNetworkChange(bool Available);

    private readonly record struct GsmIncomingChange(
        GsmIncomingKind Kind,
        string Address,
        string Text,
        ushort DestinationPort = 0,
        byte[]? Data = null,
        Guid CorrelationId = default,
        DateTimeOffset SentAt = default)
    {
        public byte[] Payload => Data ?? [];
    }

    private enum GsmIncomingKind
    {
        Call,
        Sms,
        SmartMessage,
    }

    private enum IdleWaitBlockReason
    {
        None,
        PendingControls,
        WatchdogNearExpiry,
        MachineWaitUnavailable,
    }

    private sealed class AdaptiveStepBatch
    {
        private const double ThroughputSmoothing = 0.2;
        private const double BatchAdjustment = 0.5;
        private readonly double targetTicks;
        private readonly int minimumSteps;
        private readonly int maximumSteps;
        private double stepsPerTick;

        public AdaptiveStepBatch(TimeSpan target, int initialSteps, int minimumSteps, int maximumSteps)
        {
            targetTicks = Math.Max(1.0, target.TotalSeconds * Stopwatch.Frequency);
            this.minimumSteps = minimumSteps;
            this.maximumSteps = maximumSteps;
            Steps = Math.Clamp(initialSteps, minimumSteps, maximumSteps);
        }

        public int Steps { get; private set; }

        public void Observe(int completedSteps, long elapsedTicks)
        {
            if (completedSteps <= 0 || elapsedTicks <= 0)
            {
                return;
            }

            double observedStepsPerTick = completedSteps / (double)elapsedTicks;
            if (stepsPerTick == 0.0)
            {
                stepsPerTick = observedStepsPerTick;
            }
            else
            {
                double minimumObservation = stepsPerTick / 4.0;
                double maximumObservation = stepsPerTick * 4.0;
                double boundedObservation = Math.Clamp(
                    observedStepsPerTick,
                    minimumObservation,
                    maximumObservation);
                stepsPerTick += (boundedObservation - stepsPerTick) * ThroughputSmoothing;
            }

            int desiredSteps = (int)Math.Clamp(
                Math.Round(stepsPerTick * targetTicks),
                minimumSteps,
                maximumSteps);
            int boundedDesiredSteps = Math.Clamp(
                desiredSteps,
                Math.Max(minimumSteps, Steps / 2),
                Math.Min(maximumSteps, Steps * 2));
            Steps = Math.Clamp(
                (int)Math.Round(Steps + (boundedDesiredSteps - Steps) * BatchAdjustment),
                minimumSteps,
                maximumSteps);
        }
    }
}
