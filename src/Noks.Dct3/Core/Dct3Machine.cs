using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;
using Noks.Cpu;
using Noks.Dct3.Audio;
using Noks.Dct3.Display;
using Noks.Dct3.Firmware;
using Noks.Dct3.Input;
using Noks.Dct3.Memory;
using Noks.Dct3.Messaging;
using Noks.Dct3.Peripherals;
using Noks.Dct3.Radio;
using Noks.Dct3.Sim;
using Noks.Dct3.State;

namespace Noks.Dct3.Core;

public sealed class Dct3Machine
{
    public const uint FlashBase = 0x200000;
    public const uint EntryPoint = 0x200040;
    public const long CyclesPerSecond = 13_000_000;

    private const int DefaultDecodedSimLockOffset = 0x17B10;
    private const int V607SimLockCheckStateOffset = 0x10924;
    private const int V607SimLockCheckRoutineOffset = 0x18EE2; // 0x218EE2
    private const int V607SimLockCheckLiteralOffset = 0x19248; // 0x219248
    private const int V639FirmwareVersionOffset = 0x1FC;
    private const int V639FirmwareModelOffset = 0x20D;
    private const int DefaultRandomAccessReferenceTableOffset = 0x19F80;
    private const int RandomAccessReferenceTableLength = 0x18;
    private const uint RamBase = 0x100000;
    private const uint RamLimit = 0x180000;
    private const long Timer1Period = CyclesPerSecond / 1057;
    private const long Fiq8Period = CyclesPerSecond / 100;
    private const long WallTimerRefreshCycleInterval = 8192;

    private static readonly byte[] RandomAccessReferenceMatcherPrefix =
    [
        0xB5, 0xF0, 0x78, 0x43, 0x08, 0xD9, 0x06, 0x09,
        0x0E, 0x0D, 0x78, 0x82, 0x09, 0x51, 0x07, 0x5B,
        0x0E, 0x9B, 0x43, 0x19, 0x06, 0x09, 0x0E, 0x0E,
        0x06, 0xD1, 0x0E, 0xC9, 0x06, 0x09, 0x0E, 0x09,
        0x46, 0x8C,
    ];

    private readonly IDct3Trace? trace;
    private long timer0Next;
    private long timer1Next;
    private long fiq8Next;
    private long watchdogNext;
    private long wallTimerBaseCycles;
    private long wallTimerBaseTimestamp;
    private long wallTimerCachedCycles;
    private long wallTimerRefreshAfterBusCycles;
    private long wallTimerLastTimestamp;
    private readonly long wallClockCatchUpLimitTicks;
    private long timer0Period;
    private byte timer0Divider;
    private readonly Dct3TimerClock timerClock;
    private readonly Channel<Dct3PeripheralWorkItem> peripheralWork = Channel.CreateUnbounded<Dct3PeripheralWorkItem>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly Channel<DspEffect> dspEffects = Channel.CreateUnbounded<DspEffect>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
    private DspRuntimeState dspState = new(
        Dsp.DefaultRssiMeasurement,
        Registered: false,
        DedicatedChannelActive: false,
        PendingIncomingServices: 0,
        DspExecutionState.Stopped,
        DspToneState.Off);
    private DspToneState latchedDspToneState = DspToneState.Off;
    private int desiredFacadeNetworkAvailable = 1;
    private bool softwareResetPending;
    private int peripheralWorkPending;
    private long nextPeripheralServiceCycles;
    private long nextMachineServiceCycles;
    private readonly NitzClockRuntimeHook? nitzClockHook;
    private NitzClockHookState nitzClockHookState;
    private uint nitzClockOriginalStackPointer;
    private NitzClockDateTime nitzClockPendingDateTime;
    private bool nitzClockElapsedSourceSyncActive;
    private readonly uint longRingtoneBufferPatchTriggerPc;
    private uint activeLongRingtoneBufferPatchTriggerPc;
    private bool longRingtoneBufferPatchApplied;
    private readonly bool usesV607SimLockCheckLayout;
    private readonly bool usesTestNetworkSimLockProfile;
    private readonly int randomAccessReferenceTableOffset;
    private readonly int decodedSimLockOffset;

    public Dct3Machine(
        byte[] flashImage,
        IDct3Trace? trace = null,
        string? simImsi = null,
        byte[]? externalEepromImage = null,
        bool externalEepromLog = false,
        Dct3TimerClock timerClock = Dct3TimerClock.CpuCycles,
        bool ccontWatchdogEnabled = true,
        TimeSpan? wallClockCatchUpLimit = null,
        Dct3PersistenceSnapshot? persistenceSnapshot = null,
        Dct3PhoneSettings? settings = null,
        Dct3KeyMatrix? keyMatrix = null,
        DateTime? rtcStart = null,
        Func<DateTimeOffset>? networkLocalTimeProvider = null,
        Action<SimMutation>? simMutation = null)
    {
        this.trace = trace;
        this.timerClock = timerClock;
        wallClockCatchUpLimitTicks =
            wallClockCatchUpLimit is { } limit && limit > TimeSpan.Zero
                ? Math.Max(1, (long)Math.Ceiling(limit.TotalSeconds * Stopwatch.Frequency))
                : 0;
        settings ??= Dct3PhoneSettings.Default;
        Flash = new IntelFlash16(flashImage, 0x200000, trace);
        Dct3KeyMap keyMap = Dct3KeyMaps.Resolve(Flash.Data, settings);
        randomAccessReferenceTableOffset = ResolveRandomAccessReferenceTableOffset(Flash.Data);
        decodedSimLockOffset = ResolveDecodedSimLockOffset(Flash.Data);
        if (Dct3FirmwareRuntimeHooks.TryResolveNitzClockHook(Flash.Data, out NitzClockRuntimeHook resolvedNitzClockHook))
        {
            nitzClockHook = resolvedNitzClockHook;
        }

        if (Dct3FirmwareRuntimeHooks.TryResolveIdleYieldHook(Flash.Data, out IdleYieldRuntimeHook resolvedIdleYieldHook))
        {
            IdleYieldHook = resolvedIdleYieldHook;
            trace?.Event(
                $"firmware hook: idle yield loop {resolvedIdleYieldHook.LoopStartAddress:X6}-{resolvedIdleYieldHook.LoopEndAddress:X6} " +
                $"flag={resolvedIdleYieldHook.AliveFlagAddress:X6} fiq-clear={resolvedIdleYieldHook.FiqClearAddress:X6}");
        }

        if (Dct3FirmwarePatches.TryResolveV418LongRingtoneBufferPatch(
                Flash.Data,
                out uint resolvedLongRingtoneBufferPatchTriggerPc))
        {
            longRingtoneBufferPatchTriggerPc = resolvedLongRingtoneBufferPatchTriggerPc;
        }

        usesV607SimLockCheckLayout = LooksLikeV607SimLockCheckLayout();
        usesTestNetworkSimLockProfile = usesV607SimLockCheckLayout || LooksLikeV639Nhm5Firmware();
        Dct3FirmwarePatches.ApplyNhm5RussianLanguagePmmRepair(Flash.Data, trace);
        Dct3FirmwarePatches.ApplyStaleMaintenanceStatePmmRepair(Flash.Data, trace);
        if (usesV607SimLockCheckLayout)
        {
            Dct3FirmwarePatches.ApplyV607AutomaticKeyguardPmmRepair(Flash.Data, trace);
        }

        Flash.CapturePersistenceBaseline();
        AdcInputs = CcontAdcInputs.NormalBattery();
        Ccont = new Ccont(AdcInputs, trace, rtcStart);
        Ccont.WatchdogExpirationEnabled = ccontWatchdogEnabled;
        KeyMatrix = keyMatrix ?? new Dct3KeyMatrix();
        Lcd = new Pcd8544();
        ExternalEeprom = externalEepromImage is null
            ? null
            : new I2cEeprom24C128(
                externalEepromImage,
                externalEepromLog ? message => trace?.Event(message) : null);
        Sim = new SimCard(
            trace,
            ResolveSimImsi(simImsi ?? settings.SimImsi),
            serviceProviderName: settings.EffectiveNetworkName,
            ownPhoneNumber: settings.EffectiveOwnPhoneNumber);
        Sim.MutationCommitted += mutation =>
        {
            simMutation?.Invoke(mutation);
            SimMutationCommitted?.Invoke(mutation);
        };
        if (persistenceSnapshot?.Version == Dct3PersistenceSnapshot.CurrentVersion)
        {
            Sim.ApplyOverlay(persistenceSnapshot.SimFiles);
        }

        Io = new Mad2Io(
            Ccont,
            KeyMatrix,
            Lcd,
            Sim,
            ExternalEeprom,
            keyMap,
            trace);
        Io.RequestSoftwareReset = () => softwareResetPending = true;
        Bus = new Dct3Bus(Io, Flash, trace);
        Io.CycleSource = () => CurrentPeripheralCycles();
        Io.PeripheralServiceScheduled = SchedulePeripheralService;
        Dsp = new Dsp(
            Bus.DspSharedRam,
            trace,
            Sim.Imsi,
            networkName: settings.EffectiveNetworkName,
            networkLocalTimeProvider: networkLocalTimeProvider,
            outgoingNetworkRequest: request => DispatchDspEffect(
                new DspEffect(DspEffectKind.PublishOutgoingNetworkRequest, NetworkRequest: request)),
            callTransition: transition => DispatchDspEffect(
                new DspEffect(DspEffectKind.PublishCallTransition, CallTransition: transition)),
            callAudioAnnouncement: announcement => DispatchDspEffect(
                new DspEffect(DspEffectKind.PublishCallAudioAnnouncement, AudioAnnouncement: announcement)));
        Dsp.RaiseIrq4 = () => DispatchDspEffect(new DspEffect(DspEffectKind.Irq4));
        Dsp.RaiseFiq0 = () => DispatchDspEffect(new DspEffect(DspEffectKind.Fiq0));
        Dsp.PublishDecodedSimLock = () => DispatchDspEffect(new DspEffect(DspEffectKind.PublishDecodedSimLock));
        Dsp.PublishRandomAccessReference = (requestReference, t1Prime, t3, t2) => DispatchDspEffect(
            new DspEffect(DspEffectKind.PublishRandomAccessReference, requestReference, t1Prime, t3, t2));
        Bus.Dsp = Dsp;
        Bus.DspSharedWrite = (offset, value, size) =>
        {
            if (!Dsp.ObservesSharedWrite(offset, value, size))
            {
                return;
            }

            InvokeDsp(target =>
            {
                target.SyncCycle(CurrentPeripheralCycles());
                target.CaptureArmContext();
                target.OnSharedWrite(offset, value, size);
            });
            SchedulePeripheralService();
        };
        Bus.DspHostInterrupt = () =>
        {
            if (DspState.ExecutionState == DspExecutionState.Stopped ||
                !Dsp.ObservesHostInterrupt(Bus.DspSharedRam))
            {
                return;
            }

            InvokeDsp(target =>
            {
                target.SyncCycle(CurrentPeripheralCycles());
                target.CaptureArmContext();
                target.OnHostInterrupt();
            });
            SchedulePeripheralService();
        };
        Io.Dsp = Dsp;
        Io.DspRunChanged = run =>
        {
            InvokeDsp(target =>
            {
                target.SyncCycle(CurrentPeripheralCycles());
                target.CaptureArmContext();
                bool starting = run && !target.IsRunning;
                target.SetFacadeNetworkAvailable(DesiredFacadeNetworkAvailable);
                target.SetRunning(run);
                if (starting)
                {
                    target.ReapplyFacadeNetworkAvailability();
                }
            });
            SchedulePeripheralService();
        };
        Io.Timer0DividerChanged = OnTimer0DividerChanged;
        Cpu = new Arm7Tdmi(Bus);
        Dsp.ArmContextProvider = FormatDspArmContext;
        Io.Cpu = Cpu;
        Reset();
    }

    public Arm7Tdmi Cpu { get; }

    public Dct3Bus Bus { get; }

    private string FormatDspArmContext()
    {
        uint pc = Cpu.GetGpr(15);
        uint instructionAddress = pc >= 4u ? pc - 4u : pc;
        return $"arm-pc={instructionAddress:X6} arm-rawpc={pc:X6} arm-lr={Cpu.GetGpr(14):X6} cycles={Bus.Cycles}";
    }

    public Mad2Io Io { get; }

    public Dct3KeyMatrix KeyMatrix { get; }

    public Ccont Ccont { get; }

    public CcontAdcInputs AdcInputs { get; }

    public Pcd8544 Lcd { get; }

    internal SimCard Sim { get; }

    internal I2cEeprom24C128? ExternalEeprom { get; }

    public IntelFlash16 Flash { get; }

    internal Dsp Dsp { get; }

    public DspRuntimeState DspState => Volatile.Read(ref dspState);

    public DspToneState ConsumeDspToneState()
    {
        DspToneState current = DspState.ToneState;

        if (current.Audible)
        {
            return current;
        }

        DspToneState latched = Interlocked.Exchange(ref latchedDspToneState, DspToneState.Off);
        return latched.Audible ? latched : current;
    }

    public event Action<SimMutation>? SimMutationCommitted;

    public event Action<OutgoingNetworkRequest>? OutgoingNetworkRequestSubmitted;

    public event Action<CallTransition>? CallTransitionCommitted;

    public event Action<CallAudioAnnouncement>? CallAudioAnnouncementQueued;

    public IdleYieldRuntimeHook? IdleYieldHook { get; }

    internal int RandomAccessReferenceTableOffset => randomAccessReferenceTableOffset;

    internal int DecodedSimLockOffset => decodedSimLockOffset;

    public int WatchdogResets { get; private set; }

    public int WallClockPauseCount { get; private set; }

    public double LastWallClockPauseMilliseconds { get; private set; }

    public bool PoweredOff => Ccont.PowerOffRequested;

    public bool CcontWatchdogEnabled => Ccont.WatchdogExpirationEnabled;

    public bool InterruptPending => Cpu.FiqLine || Cpu.IrqLine;

    public static PeripheralWorkerMetrics[] GetPeripheralWorkerMetrics() =>
    [
        PeripheralChannel<Dsp>.Metrics,
    ];

    public long PersistenceVersion => Io.SimPersistenceVersion;

    public byte[]? ExternalEepromData => ExternalEeprom?.Data;

    public byte[] CreateRamSnapshot(uint address, int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        ulong end = (ulong)address + (uint)length;
        if (address < RamBase || end > RamLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(address),
                $"RAM snapshot must stay within 0x{RamBase:X6}-0x{RamLimit - 1:X6}.");
        }

        return Bus.Ram.AsSpan((int)(address - RamBase), length).ToArray();
    }

    public Dct3PersistenceSnapshot CreatePersistenceSnapshot() =>
        new(
            Dct3PersistenceSnapshot.CurrentVersion,
            [],
            Io.CreateSimPersistenceOverlay());

    public bool IsAtIdleYieldLoop()
    {
        if (IdleYieldHook is not { } hook)
        {
            return false;
        }

        uint pc = Cpu.GetGpr(15);
        bool interruptsEnabled = (Cpu.CpsrValue & ((1u << 7) | (1u << 6))) == 0;
        return interruptsEnabled && pc >= hook.LoopFetchStartAddress && pc <= hook.LoopFetchEndAddress;
    }

    public int AccelerateIdleSpin(int maximumInstructions = int.MaxValue)
    {
        const int instructionsPerLoop = 4;
        const int cyclesPerLoop = 8;
        const uint fixedPointFlags = 1u << 29;
        const uint conditionFlags = 0xF000_0000;

        if (maximumInstructions < instructionsPerLoop ||
            timerClock != Dct3TimerClock.CpuCycles ||
            IdleYieldHook is not { } hook ||
            Cpu.GetGpr(15) != hook.LoopFetchStartAddress ||
            InterruptPending ||
            !Cpu.IrqAcceptanceEnabled ||
            !Cpu.FiqAcceptanceEnabled ||
            (Cpu.CpsrValue & ((1u << 7) | (1u << 6))) != 0 ||
            (Cpu.CpsrValue & conditionFlags) != fixedPointFlags ||
            Cpu.GetGpr(6) != hook.AliveFlagAddress ||
            Cpu.GetPipelineOpcode(0) != 0x7830 ||
            Cpu.GetPipelineOpcode(1) != 0x2800 ||
            Cpu.PipelineAccess != (ArmAccess.Code | ArmAccess.Sequential) ||
            softwareResetPending ||
            nitzClockHookState != NitzClockHookState.None ||
            activeLongRingtoneBufferPatchTriggerPc != 0 ||
            Bus.HasReadWatchAt(hook.AliveFlagAddress) ||
            Volatile.Read(ref peripheralWorkPending) != 0)
        {
            return 0;
        }

        byte alive = ReadReadableRamByte(hook.AliveFlagAddress);
        if (alive == 0 || Cpu.GetGpr(0) != alive)
        {
            return 0;
        }

        long currentCycles = Bus.Cycles;
        if (currentCycles >= Volatile.Read(ref nextMachineServiceCycles) ||
            NextIdleWakeCpuCycles() <= currentCycles)
        {
            ServiceMachine(currentCycles);
            if (InterruptPending)
            {
                return 0;
            }
        }

        long cyclesAvailable = NextIdleWakeCpuCycles() - Bus.Cycles - 1;
        if (cyclesAvailable < cyclesPerLoop)
        {
            return 0;
        }

        long loops = Math.Min(cyclesAvailable / cyclesPerLoop, maximumInstructions / instructionsPerLoop);
        loops = Math.Min(loops, int.MaxValue / instructionsPerLoop);
        if (loops <= 0)
        {
            return 0;
        }

        Bus.AdvanceTo(Bus.Cycles + loops * cyclesPerLoop);
        ServiceMachine(Bus.Cycles);
        return (int)(loops * instructionsPerLoop);
    }

    public void ServiceWallClockTimers()
    {
        if (timerClock == Dct3TimerClock.WallClock)
        {
            ServiceDueTimers(CurrentTimerCycles(forceRefresh: true));
            RescheduleMachineService(Bus.Cycles);
        }
    }

    public void ServicePendingPeripherals()
    {
        ServicePeripherals(CurrentPeripheralCycles(forceRefresh: true));
        RescheduleMachineService(Bus.Cycles);
    }

    public long FastForwardIdleToWallClock()
    {
        if (timerClock != Dct3TimerClock.WallClock)
        {
            return 0;
        }

        long before = Bus.Cycles;
        long cycles = CurrentTimerCycles(forceRefresh: true);
        Bus.AdvanceTo(cycles);
        wallTimerRefreshAfterBusCycles = Bus.Cycles + WallTimerRefreshCycleInterval;
        ServiceDueTimers(cycles);
        ServicePeripherals(Bus.Cycles);
        RescheduleMachineService(Bus.Cycles);
        return Bus.Cycles - before;
    }

    public long FastForwardOverdueIdleToWallClock(long minimumAdvanceCycles)
    {
        if (timerClock != Dct3TimerClock.WallClock ||
            Volatile.Read(ref peripheralWorkPending) != 0 ||
            InterruptPending ||
            !IsAtIdleYieldLoop())
        {
            return 0;
        }

        long timerCycles = CurrentTimerCycles(forceRefresh: true);
        if (CyclesUntilNextIdleWake(timerCycles) > 0)
        {
            return 0;
        }

        if (timerCycles - Bus.Cycles < minimumAdvanceCycles)
        {
            return 0;
        }

        return FastForwardIdleToWallClock();
    }

    public void QueueIncomingCall(string callingNumber) =>
        QueueIncomingCall(Guid.NewGuid(), callingNumber);

    public void QueueIncomingCall(Guid requestId, string callingNumber) =>
        PostPeripheralWork(Dct3PeripheralWorkItem.QueueIncomingCall(requestId, callingNumber));

    public void QueueIncomingSms(string originator, string text) =>
        QueueIncomingSms(originator, text, default);

    public void QueueIncomingSms(string originator, string text, DateTimeOffset sentAt) =>
        PostPeripheralWork(Dct3PeripheralWorkItem.QueueIncomingSms(originator, text, sentAt));

    public void QueueIncomingSmartMessage(string originator, ushort destinationPort, ReadOnlySpan<byte> payload) =>
        PostPeripheralWork(Dct3PeripheralWorkItem.QueueIncomingSmartMessage(originator, destinationPort, payload));

    public void SetManagedOwnNumber(string phoneNumber) =>
        PostPeripheralWork(Dct3PeripheralWorkItem.SetManagedOwnNumber(phoneNumber));

    public void ResolveNetworkRequest(ResolveNetworkRequest resolution) =>
        PostPeripheralWork(Dct3PeripheralWorkItem.ResolveNetworkRequest(resolution));

    public void ConnectNetworkCall(Guid requestId) =>
        PostPeripheralWork(Dct3PeripheralWorkItem.ConnectNetworkCall(requestId));

    public void TerminateNetworkCall(Guid requestId) =>
        PostPeripheralWork(Dct3PeripheralWorkItem.TerminateNetworkCall(requestId));

    public void SetDspRssi(byte measurement) =>
        PostPeripheralWork(Dct3PeripheralWorkItem.SetDspRssi(measurement));

    public void SetFacadeNetworkAvailable(bool available)
    {
        Volatile.Write(ref desiredFacadeNetworkAvailable, available ? 1 : 0);
        PostPeripheralWork(Dct3PeripheralWorkItem.SetFacadeNetworkAvailable(available));
    }

    private bool DesiredFacadeNetworkAvailable =>
        Volatile.Read(ref desiredFacadeNetworkAvailable) != 0;

    public bool TryGetIdleYieldWait(out TimeSpan wait, TimeSpan maxWait)
    {
        wait = default;

        if (timerClock != Dct3TimerClock.WallClock ||
            Volatile.Read(ref peripheralWorkPending) != 0 ||
            InterruptPending ||
            !IsAtIdleYieldLoop() ||
            maxWait <= TimeSpan.Zero)
        {
            return false;
        }

        long timerCycles = CurrentTimerCycles(forceRefresh: true);
        long cyclesUntilNextTimer = CyclesUntilNextIdleWake(timerCycles);
        if (cyclesUntilNextTimer <= 0)
        {
            return false;
        }

        double seconds = cyclesUntilNextTimer / (double)CyclesPerSecond;
        TimeSpan requestedWait = TimeSpan.FromSeconds(seconds);
        wait = requestedWait <= maxWait ? requestedWait : maxWait;
        return wait > TimeSpan.Zero;
    }

    public string DescribeIdleYieldWaitBlock(TimeSpan maxWait)
    {
        if (timerClock != Dct3TimerClock.WallClock)
        {
            return "timer-clock";
        }

        if (Volatile.Read(ref peripheralWorkPending) != 0)
        {
            return "peripheral-work";
        }

        if (InterruptPending)
        {
            return "interrupt";
        }

        if (!IsAtIdleYieldLoop())
        {
            return "not-idle-hook";
        }

        if (maxWait <= TimeSpan.Zero)
        {
            return "max-wait";
        }

        long timerCycles = CurrentTimerCycles(forceRefresh: true);
        long cyclesUntilNextTimer = CyclesUntilNextIdleWake(timerCycles);
        if (cyclesUntilNextTimer <= 0)
        {
            return $"wake-due:{cyclesUntilNextTimer}";
        }

        double milliseconds = cyclesUntilNextTimer * 1000.0 / CyclesPerSecond;
        return $"available:{milliseconds:F3}ms";
    }

    public void Reset()
    {
        softwareResetPending = false;
        nitzClockHookState = NitzClockHookState.None;
        nitzClockOriginalStackPointer = 0;
        nitzClockElapsedSourceSyncActive = false;
        Io.Reset(startupPowerKeyHeld: true);
        Ccont.Reset();
        Lcd.Reset();
        InvokeDsp(target =>
        {
            target.Reset();
            target.SetFacadeNetworkAvailable(DesiredFacadeNetworkAvailable);
        });
        Interlocked.Exchange(ref latchedDspToneState, DspToneState.Off);
        Cpu.Reset();
        ResetPeripheralScheduler();
        JumpToEntryPoint();
        RescheduleTimers();
    }

    public void RefreshWallClockTimers()
    {
        if (timerClock == Dct3TimerClock.WallClock)
        {
            _ = CurrentTimerCycles(forceRefresh: true);
        }
    }

    public void Step()
    {
        HandleFirmwareRuntimeHooks();
        Cpu.Step();

        if (softwareResetPending)
        {
            SoftwareReset();
            return;
        }

        long cycles = Bus.Cycles;
        if (cycles < Volatile.Read(ref nextMachineServiceCycles))
        {
            return;
        }

        ServiceMachine(cycles);
    }

    private void ServiceMachine(long cycles)
    {
        ServiceDueTimers(CurrentTimerCycles());
        ServicePeripherals(CurrentPeripheralCycles());
        RescheduleMachineService(cycles);
    }

    private void ServiceDueTimers(long timerCycles)
    {
        while (timerCycles >= timer0Next)
        {
            Io.TickTimer0();
            timer0Next += timer0Period;
        }

        while (timerCycles >= timer1Next)
        {
            Io.TickTimer1();
            timer1Next += Timer1Period;
        }

        while (timerCycles >= fiq8Next)
        {
            Io.TickFiq8();
            fiq8Next += Fiq8Period;
        }

        while (timerCycles >= watchdogNext)
        {
            TickWatchdogs();
            watchdogNext += CyclesPerSecond;
        }
    }

    private void JumpToEntryPoint()
    {
        Cpu.SetGpr(15, EntryPoint);
        uint op0 = Bus.ReadWord(EntryPoint, ArmAccess.Code | ArmAccess.Nonsequential);
        uint op1 = Bus.ReadWord(EntryPoint + 4, ArmAccess.Code | ArmAccess.Sequential);
        Cpu.PrimePipeline(op0, op1, ArmAccess.Code | ArmAccess.Sequential);
        Cpu.SetGpr(15, EntryPoint + 8);
    }

    private void RescheduleTimers()
    {
        long cycles = CurrentTimerCycles(resetWallClock: true);
        timer0Divider = Io.Timer0Divider;
        timer0Period = Timer0PeriodForDivider(timer0Divider);
        timer0Next = cycles + timer0Period;
        timer1Next = cycles + Timer1Period;
        fiq8Next = cycles + Fiq8Period;
        watchdogNext = cycles + CyclesPerSecond;
        RescheduleMachineService(Bus.Cycles);
    }

    private void ResetPeripheralScheduler()
    {
        while (peripheralWork.Reader.TryRead(out _))
        {
        }

        Volatile.Write(ref peripheralWorkPending, 0);
        nextPeripheralServiceCycles = 0;
        Volatile.Write(ref nextMachineServiceCycles, 0);
    }

    private void PostPeripheralWork(Dct3PeripheralWorkItem item)
    {
        if (!peripheralWork.Writer.TryWrite(item))
        {
            throw new InvalidOperationException("Peripheral work queue rejected a message.");
        }

        Volatile.Write(ref peripheralWorkPending, 1);
        Volatile.Write(ref nextMachineServiceCycles, 0);
    }

    private void SchedulePeripheralService()
    {
        nextPeripheralServiceCycles = 0;
        Volatile.Write(ref nextMachineServiceCycles, 0);
    }

    private void ServicePeripherals(long cycles)
    {
        if (Volatile.Read(ref peripheralWorkPending) != 0)
        {
            DrainPeripheralWork(cycles);
            return;
        }

        if (cycles >= nextPeripheralServiceCycles)
        {
            ServiceScheduledPeripherals(cycles);
        }
    }

    private void DrainPeripheralWork(long cycles)
    {
        InvokeDsp(target => target.SyncCycle(cycles));

        while (true)
        {
            while (peripheralWork.Reader.TryRead(out Dct3PeripheralWorkItem item))
            {
                switch (item.Kind)
                {
                    case Dct3PeripheralWorkKind.SetDspRssi:
                        InvokeDsp(target => target.SetRssiMeasurement(item.Measurement));
                        break;
                    case Dct3PeripheralWorkKind.SetFacadeNetworkAvailable:
                        InvokeDsp(target => target.SetFacadeNetworkAvailable(DesiredFacadeNetworkAvailable));
                        break;
                    case Dct3PeripheralWorkKind.QueueIncomingCall:
                        InvokeDsp(target => target.QueueIncomingCall(item.CorrelationId, item.Address));
                        break;
                    case Dct3PeripheralWorkKind.QueueIncomingSms:
                        InvokeDsp(target => target.QueueIncomingSms(item.Address, item.Text, item.SentAt));
                        break;
                    case Dct3PeripheralWorkKind.QueueIncomingSmartMessage:
                        if (item.DestinationPort == NokiaSmartMessagingRingtone.DestinationPort &&
                            longRingtoneBufferPatchTriggerPc != 0 &&
                            !longRingtoneBufferPatchApplied)
                        {
                            activeLongRingtoneBufferPatchTriggerPc = longRingtoneBufferPatchTriggerPc;
                        }

                        InvokeDsp(target => target.QueueIncomingSmartMessage(
                            item.Address,
                            item.DestinationPort,
                            item.Payload));
                        break;
                    case Dct3PeripheralWorkKind.SetManagedOwnNumber:
                        Sim.SetManagedOwnNumber(item.Address);
                        break;
                    case Dct3PeripheralWorkKind.ResolveNetworkRequest:
                        InvokeDsp(target => target.ResolveNetworkRequest(item.NetworkResolution!));
                        break;
                    case Dct3PeripheralWorkKind.ConnectNetworkCall:
                        InvokeDsp(target => target.ConnectNetworkCall(item.CorrelationId));
                        break;
                    case Dct3PeripheralWorkKind.TerminateNetworkCall:
                        InvokeDsp(target => target.TerminateNetworkCall(item.CorrelationId));
                        break;
                }
            }

            Volatile.Write(ref peripheralWorkPending, 0);
            if (!peripheralWork.Reader.TryPeek(out _))
            {
                break;
            }

            Volatile.Write(ref peripheralWorkPending, 1);
        }

        ServiceScheduledPeripherals(cycles);
    }

    private void ServiceScheduledPeripherals(long cycles)
    {
        long nextDspWakeCycle = QueryDsp(target =>
        {
            if (target.NeedsService(cycles))
            {
                target.AdvanceTo(cycles);
            }

            return target.NextWakeCycle(cycles);
        });

        if (Io.SimNeedsService(cycles))
        {
            Io.TickSim(cycles);
        }

        if (cycles >= Io.NextMbusWakeCycle)
        {
            Io.TickMbusTimer(cycles);
        }

        nextPeripheralServiceCycles = Math.Min(Math.Min(nextDspWakeCycle, Io.NextSimWakeCycle), Io.NextMbusWakeCycle);
    }

    private void OnTimer0DividerChanged(byte divider)
    {
        timer0Divider = divider;
        timer0Period = Timer0PeriodForDivider(divider);
        Volatile.Write(ref nextMachineServiceCycles, 0);
    }

    private void RescheduleMachineService(long cycles)
    {
        long nextServiceCycles;
        if (timerClock == Dct3TimerClock.CpuCycles)
        {
            nextServiceCycles = Math.Min(NextTimerTickCycle(), nextPeripheralServiceCycles);
        }
        else
        {
            long peripheralCycles = CurrentPeripheralCycles();
            long peripheralWaitCycles = nextPeripheralServiceCycles - peripheralCycles;
            long peripheralBusDeadline = peripheralWaitCycles <= 0
                ? cycles + 1
                : cycles + peripheralWaitCycles;
            nextServiceCycles = Math.Min(wallTimerRefreshAfterBusCycles, peripheralBusDeadline);
        }

        Volatile.Write(ref nextMachineServiceCycles, nextServiceCycles <= cycles ? cycles + 1 : nextServiceCycles);
    }

    private long NextTimerTickCycle()
    {
        long nextFiq8 = Io.Fiq8TimerEnabled ? fiq8Next : long.MaxValue;
        return Math.Min(Math.Min(timer0Next, timer1Next), Math.Min(nextFiq8, watchdogNext));
    }

    private static long Timer0PeriodForDivider(byte divider) =>
        CyclesPerSecond * (divider + 1) / 33055;

    private long NextIdleWakeTimerCycles()
    {
        long nextTimer0Interrupt = timer0Next + (Io.Timer0TicksUntilCompare - 1L) * timer0Period;
        long nextTimer1Interrupt = timer1Next + (Io.Timer1TicksUntilInterrupt - 1L) * Timer1Period;
        long nextFiq8Interrupt = Io.Fiq8TimerEnabled ? fiq8Next : long.MaxValue;
        return Math.Min(Math.Min(nextTimer0Interrupt, nextTimer1Interrupt), Math.Min(nextFiq8Interrupt, watchdogNext));
    }

    private long NextIdleWakeCpuCycles() =>
        Math.Min(NextIdleWakeTimerCycles(), nextPeripheralServiceCycles);

    private long CyclesUntilNextIdleWake(long timerCycles)
    {
        long timerWaitCycles = NextIdleWakeTimerCycles() - timerCycles;
        long peripheralWaitCycles = nextPeripheralServiceCycles - CurrentPeripheralCycles();
        return Math.Min(timerWaitCycles, peripheralWaitCycles);
    }

    private long CurrentPeripheralCycles(bool forceRefresh = false) =>
        timerClock == Dct3TimerClock.WallClock
            ? CurrentTimerCycles(forceRefresh: forceRefresh)
            : Bus.Cycles;

    private long CurrentTimerCycles(bool resetWallClock = false, bool forceRefresh = false)
    {
        if (timerClock == Dct3TimerClock.CpuCycles)
        {
            return Bus.Cycles;
        }

        long nowTicks = Stopwatch.GetTimestamp();
        if (resetWallClock || wallTimerBaseTimestamp == 0)
        {
            return ReanchorWallClock(nowTicks);
        }

        long ticksSinceLastObservation = nowTicks - wallTimerLastTimestamp;
        if (wallClockCatchUpLimitTicks > 0 && ticksSinceLastObservation > wallClockCatchUpLimitTicks)
        {
            WallClockPauseCount++;
            LastWallClockPauseMilliseconds = ticksSinceLastObservation * 1000.0 / Stopwatch.Frequency;
            return ReanchorWallClock(nowTicks);
        }

        if (!forceRefresh && Bus.Cycles < wallTimerRefreshAfterBusCycles)
        {
            wallTimerLastTimestamp = nowTicks;
            return wallTimerCachedCycles;
        }

        long elapsedTicks = nowTicks - wallTimerBaseTimestamp;
        wallTimerCachedCycles = wallTimerBaseCycles + (long)(elapsedTicks * (double)CyclesPerSecond / Stopwatch.Frequency);
        wallTimerRefreshAfterBusCycles = Bus.Cycles + WallTimerRefreshCycleInterval;
        wallTimerLastTimestamp = nowTicks;
        return wallTimerCachedCycles;
    }

    private long ReanchorWallClock(long timestamp)
    {
        wallTimerBaseCycles = Bus.Cycles;
        wallTimerBaseTimestamp = timestamp;
        wallTimerCachedCycles = wallTimerBaseCycles;
        wallTimerRefreshAfterBusCycles = Bus.Cycles + WallTimerRefreshCycleInterval;
        wallTimerLastTimestamp = timestamp;
        return wallTimerCachedCycles;
    }

    private void TickWatchdogs()
    {
        bool ccontExpired = Ccont.TickSecond();
        SyncNitzClockElapsedSourceClockState();
        bool madExpired = Io.TickWatchdogSecond();

        if (!ccontExpired && !madExpired)
        {
            return;
        }

        trace?.Event(madExpired ? "MAD2 watchdog reset" : "CCONT watchdog reset");
        WatchdogResets++;
        nitzClockHookState = NitzClockHookState.None;
        nitzClockOriginalStackPointer = 0;
        nitzClockElapsedSourceSyncActive = false;
        Io.Reset(startupPowerKeyHeld: Io.StartupPowerKeyHeld);
        Ccont.Reset();
        InvokeDsp(target =>
        {
            target.Reset();
            target.SetFacadeNetworkAvailable(DesiredFacadeNetworkAvailable);
        });
        Interlocked.Exchange(ref latchedDspToneState, DspToneState.Off);
        Cpu.Reset();

        if (madExpired)
        {
            Io.MarkWatchdogReset();
        }

        JumpToEntryPoint();
        RescheduleTimers();
    }

    private void SoftwareReset()
    {
        trace?.Event("MAD2 software reset");
        softwareResetPending = false;
        nitzClockHookState = NitzClockHookState.None;
        nitzClockOriginalStackPointer = 0;
        nitzClockElapsedSourceSyncActive = false;
        Io.Reset(startupPowerKeyHeld: Io.StartupPowerKeyHeld);
        InvokeDsp(target =>
        {
            target.Reset();
            target.SetFacadeNetworkAvailable(DesiredFacadeNetworkAvailable);
        });
        Interlocked.Exchange(ref latchedDspToneState, DspToneState.Off);
        Cpu.Reset();
        JumpToEntryPoint();
        RescheduleTimers();
    }

    private void HandleFirmwareRuntimeHooks()
    {
        HandleLongRingtoneBufferPatch();
        HandleNitzClockHook();
    }

    private void HandleLongRingtoneBufferPatch()
    {
        uint triggerPc = activeLongRingtoneBufferPatchTriggerPc;
        if (triggerPc == 0 || Cpu.GetGpr(15) != triggerPc)
        {
            return;
        }

        activeLongRingtoneBufferPatchTriggerPc = 0;
        longRingtoneBufferPatchApplied = true;
        _ = Dct3FirmwarePatches.ApplyV418LongRingtoneBufferPatch(Flash.Data, trace);
    }

    private void HandleNitzClockHook()
    {
        if (nitzClockHook is not { } hook)
        {
            return;
        }

        uint pc = Cpu.GetGpr(15);
        if (nitzClockElapsedSourceSyncActive &&
            pc == hook.CcontElapsedSourceReturnAddress + 4u)
        {
            Cpu.SetGpr(0, CurrentCcontRtcElapsedSeconds());
        }

        if (pc != hook.IgnoredMessageHandlerAddress + 4u)
        {
            return;
        }

        switch (nitzClockHookState)
        {
            case NitzClockHookState.None:
                TryStartNitzClockHook(hook);
                break;
            case NitzClockHookState.WaitingForCalcTimestampReturn:
                nitzClockHookState = NitzClockHookState.WaitingForSetTimestampReturn;
                Cpu.SetGpr(14, hook.IgnoredMessageHandlerAddress | 1u);
                BranchToThumb(hook.SetTimestampAddress);
                break;
            case NitzClockHookState.WaitingForSetTimestampReturn:
                SyncNitzClockElapsedSource(hook, nitzClockPendingDateTime);
                Cpu.SetGpr(13, nitzClockOriginalStackPointer);
                nitzClockOriginalStackPointer = 0;
                nitzClockPendingDateTime = default;
                nitzClockHookState = NitzClockHookState.None;
                trace?.Event("firmware hook: NITZ clock apply complete");
                break;
        }
    }

    private static bool IsPcNearThumbRange(uint pc, uint startAddress, uint byteLength) =>
        pc >= startAddress && pc <= startAddress + byteLength + 8u;

    private ushort ReadRamUInt16BigEndian(uint address) =>
        IsRamRange(address, 2)
            ? BinaryPrimitives.ReadUInt16BigEndian(Bus.Ram.AsSpan((int)(address - RamBase), 2))
            : (ushort)0;

    private uint ReadRamUInt32BigEndian(uint address) =>
        IsRamRange(address, 4)
            ? BinaryPrimitives.ReadUInt32BigEndian(Bus.Ram.AsSpan((int)(address - RamBase), 4))
            : 0;

    private byte ReadRamByte(uint address) =>
        IsRamRange(address, 1)
            ? Bus.Ram[(int)(address - RamBase)]
            : (byte)0;

    private ushort ReadReadableRamUInt16BigEndian(uint address) =>
        TryGetReadableRamOffset(address, 2, out int offset)
            ? BinaryPrimitives.ReadUInt16BigEndian(Bus.Ram.AsSpan(offset, 2))
            : (ushort)0;

    private uint ReadReadableRamUInt32BigEndian(uint address) =>
        TryGetReadableRamOffset(address, 4, out int offset)
            ? BinaryPrimitives.ReadUInt32BigEndian(Bus.Ram.AsSpan(offset, 4))
            : 0;

    private byte ReadReadableRamByte(uint address) =>
        TryGetReadableRamOffset(address, 1, out int offset)
            ? Bus.Ram[offset]
            : (byte)0;

    private string DescribeRamSpan(uint address, int length)
    {
        if (!IsRamRange(address, length))
        {
            return $"{address:X8}:-";
        }

        return $"{address:X8}:{Convert.ToHexString(Bus.Ram.AsSpan((int)(address - RamBase), length))}";
    }

    private string DescribeReadableRamSpan(uint address, int length)
    {
        if (!TryGetReadableRamOffset(address, length, out int offset))
        {
            return $"{address:X8}:-";
        }

        return $"{address:X8}:{Convert.ToHexString(Bus.Ram.AsSpan(offset, length))}";
    }

    private void TryStartNitzClockHook(NitzClockRuntimeHook hook)
    {
        uint message = Cpu.GetGpr(4);
        if (!IsRamRange(message, 8) ||
            Bus.Ram[(int)(message - RamBase) + 6] != 0x06 ||
            Bus.Ram[(int)(message - RamBase) + 7] != 0xC1)
        {
            return;
        }

        uint argumentBlock = Cpu.GetGpr(5);
        if (!IsRamRange(argumentBlock, 12))
        {
            return;
        }

        uint timestampBuffer = BinaryPrimitives.ReadUInt32BigEndian(Bus.Ram.AsSpan((int)(argumentBlock - RamBase), 4));
        if (!IsRamRange(timestampBuffer, 6))
        {
            return;
        }

        Span<byte> firmwareDateTimeStruct = stackalloc byte[8];
        if (!Dct3FirmwareRuntimeHooks.TryDecodeNitzDateTimeStruct(
            Bus.Ram.AsSpan((int)(timestampBuffer - RamBase), 6),
            firmwareDateTimeStruct,
            out NitzClockDateTime dateTime))
        {
            return;
        }

        uint originalStackPointer = Cpu.GetGpr(13);
        uint scratchAddress = originalStackPointer - 8u;
        if (!IsRamRange(scratchAddress, 8))
        {
            return;
        }

        firmwareDateTimeStruct.CopyTo(Bus.Ram.AsSpan((int)(scratchAddress - RamBase), 8));
        nitzClockOriginalStackPointer = originalStackPointer;
        nitzClockPendingDateTime = dateTime;
        nitzClockHookState = NitzClockHookState.WaitingForCalcTimestampReturn;

        Cpu.SetGpr(13, scratchAddress);
        Cpu.SetGpr(0, hook.DateTimeZeroAddress);
        Cpu.SetGpr(1, scratchAddress);
        Cpu.SetGpr(14, hook.IgnoredMessageHandlerAddress | 1u);
        BranchToThumb(hook.CalcTimestampAddress);

        trace?.Event(
            $"firmware hook: NITZ clock {dateTime.Year:D4}-{dateTime.Month:D2}-{dateTime.Day:D2} " +
            $"{dateTime.Hour:D2}:{dateTime.Minute:D2}:{dateTime.Second:D2} " +
            $"via {hook.CalcTimestampAddress:X6}->{hook.SetTimestampAddress:X6}");
    }

    private void SyncNitzClockElapsedSource(NitzClockRuntimeHook hook, NitzClockDateTime dateTime)
    {
        Ccont.SetRtcTime(dateTime.Hour, dateTime.Minute, dateTime.Second, day: 0);
        nitzClockElapsedSourceSyncActive = true;
        SyncNitzClockElapsedSourceClockState(hook);

        trace?.Event(
            $"firmware hook: NITZ CCONT elapsed sync {dateTime.Hour:D2}:{dateTime.Minute:D2}:{dateTime.Second:D2} " +
            $"clock-state={hook.ClockStateAddress:X6} elapsed-return={hook.CcontElapsedSourceReturnAddress:X6}");
    }

    private void SyncNitzClockElapsedSourceClockState()
    {
        if (!nitzClockElapsedSourceSyncActive || nitzClockHook is not { } hook)
        {
            return;
        }

        SyncNitzClockElapsedSourceClockState(hook);
    }

    private void SyncNitzClockElapsedSourceClockState(NitzClockRuntimeHook hook)
    {
        if (!IsRamRange(hook.ClockStateAddress + 8u, 4))
        {
            return;
        }

        BinaryPrimitives.WriteUInt32BigEndian(
            Bus.Ram.AsSpan((int)(hook.ClockStateAddress + 8u - RamBase), 4),
            CurrentCcontRtcElapsedSeconds());
    }

    private uint CurrentCcontRtcElapsedSeconds()
    {
        CcontRtcState rtcState = Ccont.RtcState;
        return (uint)(rtcState.Day * 86_400 + rtcState.Hour * 3_600 + rtcState.Minute * 60 + rtcState.Second);
    }

    private void BranchToThumb(uint address)
    {
        address &= ~1u;
        Cpu.SetGpr(15, address);
        uint op0 = Bus.ReadHalf(address, ArmAccess.Code | ArmAccess.Nonsequential);
        uint op1 = Bus.ReadHalf(address + 2, ArmAccess.Code | ArmAccess.Sequential);
        Cpu.PrimePipeline(op0, op1, ArmAccess.Code | ArmAccess.Sequential);
        Cpu.SetGpr(15, address + 4);
    }

    private static bool IsRamRange(uint address, int length) =>
        length >= 0 &&
        length <= (int)(RamLimit - RamBase) &&
        address >= RamBase &&
        address - RamBase <= (RamLimit - RamBase) - (uint)length;

    private static bool IsReadableRamRange(uint address, int length) =>
        TryGetReadableRamOffset(address, length, out _);

    private static bool TryGetReadableRamOffset(uint address, int length, out int offset)
    {
        offset = 0;
        if (length < 0)
        {
            return false;
        }

        uint relative;
        if (address < RamBase)
        {
            relative = address & 0xFFFFu;
        }
        else
        {
            relative = address - RamBase;
        }

        uint ramLength = (uint)(RamLimit - RamBase);
        if (relative > ramLength || length > ramLength - relative)
        {
            return false;
        }

        offset = (int)relative;
        return true;
    }

    private void PublishDecodedSimLock()
    {
        Span<byte> table = Bus.Ram.AsSpan(decodedSimLockOffset, 0x18 * 5);
        table.Clear();

        // The decoder produces five SIM-lock records. Wildcard pattern bytes match all values.
        // Clear enable and status bits make the firmware treat each lock class as unlocked.
        // Thus, the firmware does not examine stale FF values from initialized RAM.
        for (int offset = 0; offset < table.Length; offset += 0x18)
        {
            table.Slice(offset, 8).Fill(0xFF);
            table[offset + 0x0C] = 0xFF;
            table[offset + 0x0D] = 0xFF;
            table[offset + 0x0E] = 0xFF;
            table[offset + 0x0F] = 0xFF;
            table[offset + 0x14] = 0xFF;
            table[offset + 0x15] = 0xFF;
        }

        trace?.Event("DSP decoded SIM lock table published");

        if (usesV607SimLockCheckLayout)
        {
            // v6.07 checks this separate SIM-lock state byte after local 0x17 completes.
            Bus.Ram[V607SimLockCheckStateOffset] = 0x02;
            trace?.Event("DSP v6.07 SIM-lock check state published");
        }
    }

    private string ResolveSimImsi(string? requestedImsi)
    {
        if (requestedImsi is not null)
        {
            return requestedImsi;
        }

        if (usesTestNetworkSimLockProfile)
        {
            trace?.Event($"SIM IMSI defaulted to {SimCard.DefaultTestNetworkImsi} for firmware SIM-lock profile");
            return SimCard.DefaultTestNetworkImsi;
        }

        return SimCard.DefaultImsi;
    }

    private bool LooksLikeV607SimLockCheckLayout() =>
        FlashHasBytes(V607SimLockCheckRoutineOffset, [
            0x48, 0xD9, 0x78, 0x02, 0x2A, 0x00, 0xD1, 0x08,
            0x46, 0x59, 0x78, 0x09, 0x29, 0x00, 0xD0, 0x49,
        ]) &&
        FlashHasBytes(V607SimLockCheckLiteralOffset, [
            0x00, 0x11, 0x09, 0x24,
            0x00, 0x10, 0xA6, 0xE4,
        ]);

    private bool LooksLikeV639Nhm5Firmware() =>
        FlashHasBytes(V639FirmwareVersionOffset, [
            0x56, 0x20, 0x30, 0x36, 0x2E, 0x33, 0x39,
        ]) &&
        FlashHasBytes(V639FirmwareModelOffset, [
            0x4E, 0x48, 0x4D, 0x2D, 0x35,
        ]);

    private bool FlashHasBytes(int offset, ReadOnlySpan<byte> expected) =>
        offset >= 0 &&
        expected.Length <= Flash.Data.Length - offset &&
        Flash.Data.AsSpan(offset, expected.Length).SequenceEqual(expected);

    private static int ResolveDecodedSimLockOffset(byte[] flash)
    {
        if (TryFindDecodedSimLockOffset(flash, out int offset))
        {
            return offset;
        }

        return DefaultDecodedSimLockOffset;
    }

    internal static bool TryFindDecodedSimLockOffset(ReadOnlySpan<byte> flash, out int offset)
    {
        ReadOnlySpan<byte> initialRecord =
        [
            0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF,
        ];

        int searchStart = 0;
        while (searchStart <= flash.Length - initialRecord.Length)
        {
            int relative = flash[searchStart..].IndexOf(initialRecord);
            if (relative < 0)
            {
                break;
            }

            int dataOffset = searchStart + relative;
            if (dataOffset >= 8 &&
                flash[dataOffset - 8] == 0x00 &&
                flash[dataOffset - 7] == 0x00 &&
                flash[dataOffset - 6] == 0x00 &&
                flash[dataOffset - 5] == initialRecord.Length)
            {
                uint tableAddress = BinaryPrimitives.ReadUInt32BigEndian(flash[(dataOffset - 4)..]);
                if (tableAddress >= RamBase &&
                    tableAddress + 0x18u * 5u <= RamLimit)
                {
                    offset = (int)(tableAddress - RamBase);
                    return true;
                }
            }

            searchStart = dataOffset + 1;
        }

        offset = 0;
        return false;
    }

    private static int ResolveRandomAccessReferenceTableOffset(byte[] flash)
    {
        if (TryFindRandomAccessReferenceTableOffset(flash, out int offset))
        {
            return offset;
        }

        return DefaultRandomAccessReferenceTableOffset;
    }

    internal static bool TryFindRandomAccessReferenceTableOffset(ReadOnlySpan<byte> flash, out int offset)
    {
        int searchStart = 0;

        while (searchStart <= flash.Length - RandomAccessReferenceMatcherPrefix.Length - 2)
        {
            int relative = flash[searchStart..].IndexOf(RandomAccessReferenceMatcherPrefix);
            if (relative < 0)
            {
                break;
            }

            int matchOffset = searchStart + relative;
            int ldrOffset = matchOffset + RandomAccessReferenceMatcherPrefix.Length;
            ushort ldr = BinaryPrimitives.ReadUInt16BigEndian(flash[ldrOffset..]);

            if ((ldr & 0xF800) == 0x4800 && ((ldr >> 8) & 0x07) == 0x07)
            {
                uint instructionAddress = FlashBase + (uint)ldrOffset;
                uint pcRelativeBase = (instructionAddress + 4) & ~3u;
                uint literalAddress = pcRelativeBase + (uint)(ldr & 0x00FF) * 4u;

                if (literalAddress >= FlashBase &&
                    literalAddress + 4u <= FlashBase + flash.Length)
                {
                    int literalOffset = (int)(literalAddress - FlashBase);
                    uint tableAddress = BinaryPrimitives.ReadUInt32BigEndian(flash[literalOffset..]);

                    if (tableAddress >= RamBase &&
                        tableAddress + RandomAccessReferenceTableLength <= RamLimit)
                    {
                        offset = (int)(tableAddress - RamBase);
                        return true;
                    }
                }
            }

            searchStart = matchOffset + 1;
        }

        offset = 0;
        return false;
    }

    private void PublishRandomAccessReference(byte requestReference, byte t1Prime, byte t3, byte t2)
    {
        Span<byte> table = Bus.Ram.AsSpan(randomAccessReferenceTableOffset, RandomAccessReferenceTableLength);
        table.Clear();
        table[0] = 0x01;
        table[1] = requestReference;
        table[2] = t1Prime;
        table[3] = t3;
        table[4] = t2;

        trace?.Event($"DSP RACH reference recorded RA={requestReference:X2} T1'={t1Prime} T3={t3} T2={t2}");
    }

    private void InvokeDsp(Action<Dsp> action)
    {
        DspRuntimeState state = PeripheralChannel<Dsp>.Invoke(
            Dsp,
            target =>
            {
                action(target);
                return CaptureDspState(target);
            });
        PublishDspRuntimeState(state);
        CommitDspEffects();
    }

    private TResult QueryDsp<TResult>(Func<Dsp, TResult> query)
    {
        DspInvocation<TResult> invocation = PeripheralChannel<Dsp>.Invoke(
            Dsp,
            target => new DspInvocation<TResult>(query(target), CaptureDspState(target)));
        PublishDspRuntimeState(invocation.State);
        CommitDspEffects();
        return invocation.Result;
    }

    private void PublishDspRuntimeState(DspRuntimeState state)
    {
        Volatile.Write(ref dspState, state);

        if (state.ToneState.Audible)
        {
            Volatile.Write(ref latchedDspToneState, state.ToneState);
        }
    }

    private static DspRuntimeState CaptureDspState(Dsp target) =>
        new(
            target.RssiMeasurement,
            target.RegisteredOnFacadeNetwork,
            target.DedicatedChannelActive,
            target.PendingIncomingServiceCount,
            target.ExecutionState,
            target.ToneState);

    private void PostDspEffect(DspEffect effect)
    {
        if (!dspEffects.Writer.TryWrite(effect))
        {
            throw new InvalidOperationException("DSP effect channel rejected a message.");
        }
    }

    private void DispatchDspEffect(DspEffect effect)
    {
        if (PeripheralChannel<Dsp>.IsWorkerThread)
        {
            PostDspEffect(effect);
        }
        else
        {
            ApplyDspEffect(effect);
        }
    }

    private void CommitDspEffects()
    {
        while (dspEffects.Reader.TryRead(out DspEffect effect))
        {
            ApplyDspEffect(effect);
        }
    }

    private void ApplyDspEffect(DspEffect effect)
    {
        switch (effect.Kind)
        {
            case DspEffectKind.Irq4:
                Io.AssertDspIrq();
                break;
            case DspEffectKind.Fiq0:
                Io.AssertMdiFiq();
                break;
            case DspEffectKind.PublishDecodedSimLock:
                PublishDecodedSimLock();
                break;
            case DspEffectKind.PublishRandomAccessReference:
                PublishRandomAccessReference(effect.Value0, effect.Value1, effect.Value2, effect.Value3);
                break;
            case DspEffectKind.PublishOutgoingNetworkRequest:
                OutgoingNetworkRequestSubmitted?.Invoke(effect.NetworkRequest!);
                break;
            case DspEffectKind.PublishCallTransition:
                CallTransitionCommitted?.Invoke(effect.CallTransition!);
                break;
            case DspEffectKind.PublishCallAudioAnnouncement:
                CallAudioAnnouncementQueued?.Invoke(effect.AudioAnnouncement!);
                break;
        }
    }

    private enum NitzClockHookState
    {
        None,
        WaitingForCalcTimestampReturn,
        WaitingForSetTimestampReturn,
    }

    private readonly record struct Dct3PeripheralWorkItem(
        Dct3PeripheralWorkKind Kind,
        byte Measurement = 0,
        string Address = "",
        string Text = "",
        ushort DestinationPort = 0,
        byte[]? Data = null,
        ResolveNetworkRequest? NetworkResolution = null,
        Guid CorrelationId = default,
        bool NetworkAvailable = true,
        DateTimeOffset SentAt = default)
    {
        public byte[] Payload => Data ?? [];

        public static Dct3PeripheralWorkItem SetDspRssi(byte measurement) =>
            new(Dct3PeripheralWorkKind.SetDspRssi, Measurement: measurement);

        public static Dct3PeripheralWorkItem SetFacadeNetworkAvailable(bool available) =>
            new(Dct3PeripheralWorkKind.SetFacadeNetworkAvailable, NetworkAvailable: available);

        public static Dct3PeripheralWorkItem QueueIncomingCall(Guid requestId, string address) =>
            new(
                Dct3PeripheralWorkKind.QueueIncomingCall,
                Address: address,
                CorrelationId: requestId);

        public static Dct3PeripheralWorkItem QueueIncomingSms(
            string address,
            string text,
            DateTimeOffset sentAt) =>
            new(Dct3PeripheralWorkKind.QueueIncomingSms, Address: address, Text: text, SentAt: sentAt);

        public static Dct3PeripheralWorkItem QueueIncomingSmartMessage(
            string address,
            ushort destinationPort,
            ReadOnlySpan<byte> payload) =>
            new(
                Dct3PeripheralWorkKind.QueueIncomingSmartMessage,
                Address: address,
                DestinationPort: destinationPort,
                Data: payload.ToArray());

        public static Dct3PeripheralWorkItem SetManagedOwnNumber(string phoneNumber) =>
            new(Dct3PeripheralWorkKind.SetManagedOwnNumber, Address: phoneNumber);

        public static Dct3PeripheralWorkItem ResolveNetworkRequest(ResolveNetworkRequest resolution) =>
            new(Dct3PeripheralWorkKind.ResolveNetworkRequest, NetworkResolution: resolution);

        public static Dct3PeripheralWorkItem ConnectNetworkCall(Guid requestId) =>
            new(Dct3PeripheralWorkKind.ConnectNetworkCall, CorrelationId: requestId);

        public static Dct3PeripheralWorkItem TerminateNetworkCall(Guid requestId) =>
            new(Dct3PeripheralWorkKind.TerminateNetworkCall, CorrelationId: requestId);
    }

    private readonly record struct DspEffect(
        DspEffectKind Kind,
        byte Value0 = 0,
        byte Value1 = 0,
        byte Value2 = 0,
        byte Value3 = 0,
        OutgoingNetworkRequest? NetworkRequest = null,
        CallTransition? CallTransition = null,
        CallAudioAnnouncement? AudioAnnouncement = null);

    private readonly record struct DspInvocation<TResult>(TResult Result, DspRuntimeState State);

    private enum DspEffectKind
    {
        Irq4,
        Fiq0,
        PublishDecodedSimLock,
        PublishRandomAccessReference,
        PublishOutgoingNetworkRequest,
        PublishCallTransition,
        PublishCallAudioAnnouncement,
    }

    private enum Dct3PeripheralWorkKind
    {
        SetDspRssi,
        SetFacadeNetworkAvailable,
        QueueIncomingCall,
        QueueIncomingSms,
        QueueIncomingSmartMessage,
        SetManagedOwnNumber,
        ResolveNetworkRequest,
        ConnectNetworkCall,
        TerminateNetworkCall,
    }
}
