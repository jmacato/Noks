using System.Threading;
using Noks.Cpu;
using Noks.Dct3.Audio;
using Noks.Dct3.Core;
using Noks.Dct3.Display;
using Noks.Dct3.Input;
using Noks.Dct3.Memory;
using Noks.Dct3.Radio;
using Noks.Dct3.Sim;

namespace Noks.Dct3.Peripherals;

public sealed class Mad2Io
{
    // This value keeps long APDU writes ahead of the firmware's one-tick SIML2 send watchdog.
    private const long SimByteCycles = 64;
    private const long SimResponseDelayCycles = 512;
    private const byte SimControlReset = 0x80;
    private const byte SimControlLegacyReset = 0x20;
    private const byte SimControlLegacyEnable = 0x10;
    private const byte PupControlVibratorEnable = 0x10;
    private const byte PupControlBuzzerEnable = 0x20;
    private const byte PupControlMbusReset = 0x08;
    private const byte MbusControlTransmitMode = 0x20;
    private const byte MbusControlReceiveMode = 0x40;
    private const byte MbusControlInitialize = 0x80;
    private const byte MbusStatusSendReady = 0x10;
    private const byte MbusStatusReceiveByteAvailable = 0x04;
    private const byte MbusStatusTxdReady = 0x40;
    private const byte MbusStatusTxdHigh = 0x80;
    private const ushort FiqMbusByteService = 1 << 2;
    private const ushort FiqMbusTimer = 1 << 3;
    private const byte InterruptControlFiqEnable = 0x01;
    private const byte InterruptControlIrqEnable = 0x04;
    private const byte GenIoExternalEepromSda = 0x01;
    private const byte GenIoExternalEepromScl = 0x08;
    private const byte GenIoKeyboardBacklight = 0x08;
    private const byte GenIoLedDriveEnable = 0x08;
    private const byte CtrlIo3LcdBacklight = 0x02;
    private const ushort Timer1InterruptCounter = 0x8000;
    private const long MbusTimerDelayCycles = 64;

    private readonly byte[] regs = new byte[0x100];
    private readonly Ccont ccont;
    private readonly Dct3KeyMatrix keyMatrix;
    private readonly Pcd8544 lcd;
    private readonly SimCard sim;
    private readonly I2cEeprom24C128? externalEeprom;
    private readonly byte powerKeyMask;
    private readonly SerialBytePort simUart = new(SimByteCycles);
    private readonly IDct3Trace? trace;
    private ushort fiqStatus;
    private ushort irqStatus;
    private ushort timer0Counter;
    private ushort timer1Counter;
    private byte powerPort;
    private int startupPowerKeyHeld = 1;
    private long simPersistenceVersion;
    private bool mbusTimerActive;
    private long mbusTimerNextCycle;
    private bool mbusInitialTimerKickAvailable;
    private ushort lastLoggedFiqEffectiveStatus = 0xFFFF;
    private ushort lastLoggedIrqStatus = 0xFFFF;
    private int lastLoggedFiqMask = -1;
    private int lastLoggedIrqMask = -1;
    private bool lastLoggedFiqEnabled;
    private bool lastLoggedIrqEnabled;
    private bool lastLoggedFiqLine;
    private bool lastLoggedIrqLine;
    private Mad2AudioState latchedAudioState = Mad2AudioState.Off;
    private int lastLoggedKeyData = -1;
    private int lastLoggedKeyGeneration = -1;

    internal Mad2Io(
        Ccont ccont,
        Dct3KeyMatrix keyMatrix,
        Pcd8544 lcd,
        SimCard sim,
        I2cEeprom24C128? externalEeprom,
        Dct3KeyMap keyMap,
        IDct3Trace? trace)
    {
        this.ccont = ccont;
        this.keyMatrix = keyMatrix;
        this.lcd = lcd;
        this.sim = sim;
        this.externalEeprom = externalEeprom;
        powerKeyMask = 0x02;
        this.trace = trace;
        ccont.InterruptRequested = AssertCcontIrq;
        if (MadStateTracingEnabled)
        {
            simUart.Trace = message => TraceMad($"sim uart {message} {SimTraceState()}");
        }
        simUart.ByteTransmitted = OnSimByteTransmitted;
        Reset();
    }

    public Arm7Tdmi? Cpu { get; set; }

    public Dsp? Dsp { get; set; }

    public Action<bool>? DspRunChanged { get; set; }

    public Action<byte>? Timer0DividerChanged { get; set; }

    public Action? RequestSoftwareReset { get; set; }

    public Action? PeripheralServiceScheduled { get; set; }

    public Func<long>? CycleSource { get; set; }

    public bool StartupPowerKeyHeld => Volatile.Read(ref startupPowerKeyHeld) != 0;

    public bool PowerKeyHeld
    {
        get => StartupPowerKeyHeld || keyMatrix.PowerKeyPressed;
        set => SetStartupPowerKeyHeld(value);
    }

    public byte Timer0Divider => regs[0x0F];

    public ushort Timer0Counter => timer0Counter;

    public ushort Timer1Counter => timer1Counter;

    public ushort Timer1InterruptCompare => Timer1InterruptCounter;

    public ushort EffectiveFiqStatusValue => EffectiveFiqStatus();

    public ushort IrqStatusValue => irqStatus;

    public byte FiqMaskRegister => regs[0x0A];

    public byte IrqMaskRegister => regs[0x0B];

    public byte InterruptControlRegister => regs[0x0C];

    public byte VisibleInterruptControlRegister => (byte)((regs[0x0C] & ~0x20) | ((irqStatus >> 3) & 0x20));

    public byte SimControlRegister => regs[0x39];

    public byte SimControlStatus => simUart.ControlStatus;

    public byte SimInterruptId => simUart.InterruptId;

    public int SimRxCount => simUart.RxCount;

    public int SimTxCount => simUart.TxCount;

    public long SimPersistenceVersion => Volatile.Read(ref simPersistenceVersion);

    public int Timer0TicksUntilCompare
    {
        get
        {
            int compare = (regs[0x12] << 8) | regs[0x13];
            int ticks = (compare - timer0Counter) & 0xFFFF;
            return ticks == 0 ? 0x10000 : ticks;
        }
    }

    public int Timer1TicksUntilInterrupt
    {
        get
        {
            int ticks = Timer1InterruptCounter - timer1Counter;
            return ticks <= 0 ? Timer1InterruptCounter : ticks;
        }
    }

    public bool Fiq8TimerEnabled => (regs[0x16] & 0x01) != 0;

    public ushort Timer0Compare => (ushort)((regs[0x12] << 8) | regs[0x13]);

    public byte PeekRegister(int offset) => regs[offset & 0xFF];

    public long NextSimWakeCycle => simUart.NextWakeCycle;

    public long NextMbusWakeCycle => mbusTimerActive ? mbusTimerNextCycle : long.MaxValue;

    public Mad2PeripheralState PeripheralState => new(
        (regs[0x15] & PupControlVibratorEnable) != 0,
        regs[0x1B],
        (regs[0x33] & CtrlIo3LcdBacklight) != 0,
        (regs[0x24] & GenIoLedDriveEnable) != 0 && (regs[0x20] & GenIoKeyboardBacklight) != 0,
        (regs[0x24] & GenIoLedDriveEnable) != 0);

    public Mad2AudioState AudioState => new(
        (regs[0x15] & PupControlBuzzerEnable) != 0,
        regs[0x1C],
        regs[0x1E]);

    public Mad2AudioState ConsumeAudioState()
    {
        Mad2AudioState current = AudioState;

        if (current.Audible)
        {
            return current;
        }

        Mad2AudioState latched = latchedAudioState;
        latchedAudioState = Mad2AudioState.Off;
        return latched.Audible ? latched : current;
    }

    public void Reset(bool startupPowerKeyHeld = true)
    {
        Array.Clear(regs);
        regs[0x01] = 0x01;
        regs[0x0C] = 0x0A;
        regs[0x15] = PupControlMbusReset;
        regs[0x19] = MbusStatusSendReady | MbusStatusTxdReady | MbusStatusTxdHigh;
        // External UIF inputs pull high when no device drives the line. The firmware's
        // ITC SIM-presence query reads these latches, separate from the SIM UART card-ready status.
        regs[0xF0] = 0xFF;
        regs[0xF1] = 0xFF;
        regs[0xF2] = 0xFF;
        regs[0xF3] = 0xFF;
        regs[0x03] = 0xFF;
        fiqStatus = 0;
        irqStatus = 0;
        timer0Counter = 0;
        timer1Counter = 0;
        powerPort = 0xFF;
        mbusTimerActive = false;
        mbusTimerNextCycle = long.MaxValue;
        mbusInitialTimerKickAvailable = false;
        ResetLineDecisionLog();
        ResetKeypadReadLog();
        Volatile.Write(ref this.startupPowerKeyHeld, startupPowerKeyHeld ? 1 : 0);
        latchedAudioState = Mad2AudioState.Off;
        InvokeSim(static target => target.Reset());
        simUart.Reset();
        externalEeprom?.ResetBus();
    }

    public SimFileOverlay[] CreateSimPersistenceOverlay() =>
        InvokeSim(static target => target.CreateOverlay());

    public void MarkWatchdogReset() => regs[0x01] |= 0x02;

    public void AssertDspIrq() => AssertIrq(4);

    public void AssertCcontIrq() => AssertIrq(2);

    public void AssertKeypadIrq() => AssertIrq(0);

    public void SetKey(int column, int bit, bool pressed) =>
        keyMatrix.SetKey(column, bit, pressed);

    public void AssertMdiFiq() => AssertFiq(0);

    public void SetStartupPowerKeyHeld(bool held)
    {
        int next = held ? 1 : 0;
        int previous = Interlocked.Exchange(ref startupPowerKeyHeld, next);
        if (previous != next)
        {
            AssertKeypadIrq();
        }
    }

    public byte Read(uint offset)
    {
        byte data = regs[offset];

        switch (offset)
        {
            case 0x00:
                data = 0x40;
                break;
            case 0x04:
                data = (byte)(timer1Counter >> 8);
                break;
            case 0x05:
                data = (byte)timer1Counter;
                break;
            case 0x06:
                data = (byte)(Timer1InterruptCounter >> 8);
                break;
            case 0x07:
                data = (byte)(Timer1InterruptCounter & 0xFF);
                break;
            case 0x08:
                data = (byte)EffectiveFiqStatus();
                break;
            case 0x09:
                data = (byte)irqStatus;
                break;
            case 0x0C:
                data = (byte)((data & ~0x20) | ((irqStatus >> 3) & 0x20));
                break;
            case 0x10:
                data = (byte)(timer0Counter >> 8);
                break;
            case 0x11:
                data = (byte)timer0Counter;
                break;
            case 0x16:
                data = (byte)((data & ~0x02) | ((EffectiveFiqStatus() >> 7) & 0x02));
                break;
            case 0x18:
                data &= unchecked((byte)~MbusControlInitialize);
                break;
            case 0x19:
                data = ReadMbusStatus();
                break;
            case 0x1A:
                data = ReadMbusData();
                break;
            case 0x20:
                data = ReadGenIoLines();
                break;
            case 0x2A:
                data = keyMatrix.ReadSelectedColumns(regs[0x28]);
                data &= powerPort;

                if (PowerKeyHeld)
                {
                    data &= (byte)~powerKeyMask;
                }

                TraceKeypadRead(regs[0x28], data);
                break;
            case 0x37:
                int rxBefore = simUart.RxCount;
                byte rxStatusBefore = simUart.ControlStatus;
                byte rxIrqBefore = simUart.InterruptId;
                data = simUart.ReadRx();
                TraceMad(
                    $"sim rx read {data:X2} rx={rxBefore}->{simUart.RxCount} " +
                    $"status={rxStatusBefore:X2}->{simUart.ControlStatus:X2} " +
                    $"irq={rxIrqBefore:X2}->{simUart.InterruptId:X2} {SimTraceState()}");
                ServiceSimInterrupt();
                break;
            case 0x38:
                data = simUart.InterruptId;
                TraceMad($"sim irq read {data:X2} {SimTraceState()}");
                break;
            case 0x39:
                byte registerBits = data;
                byte statusBits = simUart.ControlStatus;
                data = (byte)((data & unchecked((byte)~SerialBytePort.ControlStatusMask)) | statusBits);
                TraceMad(
                    $"sim ctl read reg={registerBits:X2} status={statusBits:X2} visible={data:X2} {SimTraceState()}");
                break;
            case 0x3C:
                data = (byte)Math.Min(simUart.RxCount, 0xFF);
                TraceMad($"sim rx count read {data:X2} {SimTraceState()}");
                break;
            case 0x3F:
                data = (byte)Math.Min(simUart.TxCount, 0xFF);
                TraceMad($"sim tx count read {data:X2} {SimTraceState()}");
                break;
            case 0x6C:
                data = ccont.Read();
                break;
            case 0x6D:
                data = 0x07;
                break;
        }

        if (MadStateTracingEnabled && ShouldLogMadRegister(offset))
        {
            TraceMad($"r {offset:X2}={data:X2}");
        }

        trace?.MadRead(offset, data);
        return data;
    }

    public void Write(uint offset, byte value)
    {
        byte oldValue = regs[offset];
        regs[offset] = value;

        switch (offset)
        {
            case 0x01:
                if ((oldValue & 0x04) == 0 && (value & 0x04) != 0)
                {
                    RequestSoftwareReset?.Invoke();
                }

                break;
            case 0x02:
                trace?.Event((value & 0x01) != 0 ? "DSP run" : "DSP hold");
                if (DspRunChanged is not null)
                {
                    DspRunChanged((value & 0x01) != 0);
                }
                else
                {
                    Dsp?.SetRunning((value & 0x01) != 0);
                }

                break;
            case 0x08:
                AckFiq(value);
                break;
            case 0x09:
                AckIrq(value);
                break;
            case 0x0A:
                TraceMad(
                    $"fiq mask {oldValue:X2}->{value:X2} " +
                    $"mbus-byte={MaskState(value, FiqMbusByteService)} mbus-timer={MaskState(value, FiqMbusTimer)} " +
                    $"level={MbusLevelFiqStatus():X3}");
                UpdateLines();
                break;
            case 0x0B:
                UpdateLines();
                break;
            case 0x0C:
                AckIrq((ushort)((value << 3) & 0x100));
                break;
            case 0x0F:
                if (oldValue != value)
                {
                    Timer0DividerChanged?.Invoke(value);
                }

                break;
            case 0x16:
                AckFiq((ushort)((value << 7) & 0x100));
                break;
            case 0x18:
                regs[offset] = (byte)(value & unchecked((byte)~MbusControlInitialize));
                ServiceMbusControl(oldValue, regs[offset]);
                break;
            case 0x19:
                regs[offset] = (byte)((value & MbusStatusTxdHigh) | MbusStatusSendReady | MbusStatusTxdReady);
                UpdateLines();
                break;
            case 0x1A:
                ServiceMbusDataWrite(value);
                break;
            case 0x2C:
                ccont.Write(value);
                break;
            case 0x2D:
                ccont.BeginTransaction();
                TraceMad($"gensio start {value:X2}");
                break;
            case 0x36:
                TraceMad($"sim tx enqueue {value:X2} {SimTraceState()}");
                simUart.WriteTx(value, CycleSource?.Invoke() ?? 0);
                PeripheralServiceScheduled?.Invoke();
                ServiceSimInterrupt();
                break;
            case 0x38:
                TraceMad($"sim irq clear {value:X2} before={simUart.InterruptId:X2} {SimTraceState()}");
                simUart.ClearInterrupts(value);
                break;
            case 0x39:
                regs[offset] = (byte)(value & unchecked((byte)~SerialBytePort.ControlStatusMask));
                HandleSimControl(oldValue, value);
                break;
            case 0x2E:
                int lcdX = lcd.X;
                int lcdY = lcd.Y;
                bool lcdVertical = lcd.Vertical;
                lcd.WriteData(value);
                trace?.LcdData(value, lcdX, lcdY, lcdVertical);
                break;
            case 0x6E:
                lcd.WriteCommand(value);
                trace?.LcdCommand(value);
                break;
        }

        if (offset is 0x20 or 0x24)
        {
            UpdateExternalEepromLines();
        }

        if (offset is 0x15 or 0x1C or 0x1E)
        {
            LatchAudibleAudioState();
        }

        if (MadStateTracingEnabled && ShouldLogMadRegister(offset))
        {
            TraceMad($"w {offset:X2} req={value:X2} old={oldValue:X2} now={regs[offset]:X2}");
        }

        trace?.MadWrite(offset, value);
    }

    private void LatchAudibleAudioState()
    {
        Mad2AudioState current = AudioState;

        if (current.Audible)
        {
            latchedAudioState = current;
        }
    }

    private void TraceKeypadRead(byte select, byte data)
    {
        if (trace is null)
        {
            return;
        }

        int generation = keyMatrix.ChangeGeneration;
        bool activeRead = data != 0xFF || lastLoggedKeyData != 0xFF;
        bool changed =
            data != lastLoggedKeyData ||
            generation != lastLoggedKeyGeneration;

        if (!activeRead || !changed)
        {
            return;
        }

        lastLoggedKeyData = data;
        lastLoggedKeyGeneration = generation;

        trace.Event(
            $"keypad scan sel={select:X2} data={data:X2} gen={generation} pressgen={keyMatrix.PressGeneration} " +
            $"power={(PowerKeyHeld ? 1 : 0)} powerPort={powerPort:X2}");
    }

    private void ResetKeypadReadLog()
    {
        lastLoggedKeyData = -1;
        lastLoggedKeyGeneration = -1;
    }

    private static bool ShouldLogMadRegister(uint offset) =>
        offset is 0x08 or 0x09 or 0x0A or 0x0B or 0x0C or 0x15 or 0x16 or 0x18 or 0x19 or 0x1A
            or 0x2C or 0x2D or 0x6C or 0x6D
            or >= 0x36 and <= 0x3F;

    private static string MaskState(byte mask, ushort bit) => (mask & bit) == 0 ? "unmasked" : "masked";

    private string SimTraceState() =>
        $"sim=ctl{regs[0x39]:X2}/visible{VisibleSimControlValue():X2}/status{simUart.ControlStatus:X2}/irq{simUart.InterruptId:X2}/rx{simUart.RxCount}/tx{simUart.TxCount}/sched{simUart.ScheduledRxCount}/pend{(simUart.RxCompletePending ? 1 : 0)}/done{(simUart.RxCompleteStatus ? 1 : 0)}";

    private bool MadStateTracingEnabled => trace is { MadStateEnabled: true };

    private byte VisibleSimControlValue() =>
        (byte)((regs[0x39] & unchecked((byte)~SerialBytePort.ControlStatusMask)) | simUart.ControlStatus);

    private void TraceMad(string message)
    {
        if (trace is not { MadStateEnabled: true })
        {
            return;
        }

        ushort effectiveFiqStatus = EffectiveFiqStatus();
        int fiqMask = CurrentFiqMask();
        int irqMask = CurrentIrqMask();
        string fiqLine = Cpu is null ? "-" : (Cpu.FiqLine ? "1" : "0");
        string irqLine = Cpu is null ? "-" : (Cpu.IrqLine ? "1" : "0");
        string cpsr = Cpu is null ? "--------" : $"{Cpu.CpsrValue:X8}";
        int timer0Compare = (regs[0x12] << 8) | regs[0x13];
        string fiqState = effectiveFiqStatus == fiqStatus
            ? $"{fiqStatus:X3}/{fiqMask:X3}"
            : $"{fiqStatus:X3}->{effectiveFiqStatus:X3}/{fiqMask:X3}";

        trace.MadState(
            $"{message} fiq={fiqState} irq={irqStatus:X3}/{irqMask:X3} " +
            $"ctl={regs[0x0C]:X2} mbus={regs[0x18]:X2}/{regs[0x19]:X2}/{regs[0x1A]:X2} " +
            $"t0={timer0Counter:X4}/{timer0Compare:X4}/{regs[0x0F]:X2} t1={timer1Counter:X4} f8={regs[0x16]:X2} " +
            $"mbus-flags=t{(mbusTimerActive ? 1 : 0)}i{(mbusInitialTimerKickAvailable ? 1 : 0)} " +
            $"line=F{fiqLine}I{irqLine} cpsr={cpsr}");
    }

    private int CurrentFiqMask() => regs[0x0A] | ((regs[0x16] & 0x04) != 0 ? 0x100 : 0);

    private int CurrentIrqMask() => regs[0x0B] | ((regs[0x0C] & 0x40) != 0 ? 0x100 : 0);

    private void ServiceMbusTimerAfterFiqAck(ushort mask)
    {
        if ((mask & FiqMbusTimer) == 0)
        {
            return;
        }

        if ((regs[0x0A] & FiqMbusTimer) != 0)
        {
            TraceMad($"mbus timer ack ignored masked fiqmask={regs[0x0A]:X2}");
            return;
        }

        bool receiveMode = (regs[0x18] & MbusControlReceiveMode) != 0;
        bool transmitMode = (regs[0x18] & MbusControlTransmitMode) != 0;
        ushort levelFiq = MbusLevelFiqStatus();

        if (!receiveMode)
        {
            TraceMad($"mbus timer ack ignored rx=0 tx={(transmitMode ? 1 : 0)} level={levelFiq:X3}");
            return;
        }

        bool idleReceiveTimerKick = !transmitMode && levelFiq == 0 && (fiqStatus & FiqMbusTimer) == 0;
        if (!mbusInitialTimerKickAvailable && !idleReceiveTimerKick)
        {
            TraceMad($"mbus timer ack ignored no-initial-kick tx={(transmitMode ? 1 : 0)} level={levelFiq:X3}");
            return;
        }

        string reason = mbusInitialTimerKickAvailable ? "initial" : "idle-rx";
        mbusInitialTimerKickAvailable = false;
        mbusTimerActive = true;
        mbusTimerNextCycle = (CycleSource?.Invoke() ?? 0) + MbusTimerDelayCycles;
        PeripheralServiceScheduled?.Invoke();
        TraceMad(
            $"mbus timer scheduled after ack next={mbusTimerNextCycle} reason={reason} " +
            $"tx={(transmitMode ? 1 : 0)} level={levelFiq:X3}");
    }

    private byte ReadGenIoLines()
    {
        byte data = regs[0x20];
        if (externalEeprom is null)
        {
            return data;
        }

        bool sclHigh = MasterGenIoLineHigh(GenIoExternalEepromScl);
        bool sdaHigh = MasterGenIoLineHigh(GenIoExternalEepromSda) && !externalEeprom.DrivesSdaLow;

        data &= unchecked((byte)~(GenIoExternalEepromScl | GenIoExternalEepromSda));
        if (sclHigh)
        {
            data |= GenIoExternalEepromScl;
        }

        if (sdaHigh)
        {
            data |= GenIoExternalEepromSda;
        }

        return data;
    }

    private void UpdateExternalEepromLines()
    {
        externalEeprom?.Observe(
            MasterGenIoLineHigh(GenIoExternalEepromScl),
            MasterGenIoLineHigh(GenIoExternalEepromSda));
    }

    private bool MasterGenIoLineHigh(byte mask) =>
        (regs[0x24] & mask) == 0 || (regs[0x20] & mask) != 0;

    private byte ReadMbusStatus() =>
        (byte)(regs[0x19] | MbusStatusSendReady | MbusStatusTxdReady);

    private byte ReadMbusData()
    {
        byte oldStatus = regs[0x19];
        byte data = regs[0x1A];
        regs[0x19] &= unchecked((byte)~MbusStatusReceiveByteAvailable);
        UpdateLines();
        if ((oldStatus & MbusStatusReceiveByteAvailable) != 0)
        {
            trace?.MbusByte(transmitted: false, data);
        }
        TraceMad($"mbus data r {data:X2} status {oldStatus:X2}->{regs[0x19]:X2}");
        return data;
    }

    private void ServiceMbusControl(byte oldValue, byte value)
    {
        bool oldReceiveMode = (oldValue & MbusControlReceiveMode) != 0;
        bool receiveMode = (value & MbusControlReceiveMode) != 0;
        bool oldTransmitMode = (oldValue & MbusControlTransmitMode) != 0;
        bool transmitMode = (value & MbusControlTransmitMode) != 0;
        byte oldStatus = regs[0x19];

        TraceMad(
            $"mbus ctl {oldValue:X2}->{value:X2} " +
            $"rx={(oldReceiveMode ? 1 : 0)}->{(receiveMode ? 1 : 0)} tx={(oldTransmitMode ? 1 : 0)}->{(transmitMode ? 1 : 0)} " +
            $"init={(((value & MbusControlInitialize) != 0) ? 1 : 0)}");

        if ((oldValue & MbusControlInitialize) == 0 && (value & MbusControlInitialize) != 0)
        {
            regs[0x18] = (byte)(value & unchecked((byte)~MbusControlInitialize));
            TraceMad("mbus init complete");
        }

        if (receiveMode)
        {
            regs[0x19] |= MbusStatusSendReady | MbusStatusTxdReady;
            if (!oldReceiveMode)
            {
                mbusInitialTimerKickAvailable = true;
            }

            TraceMad(
                $"mbus rx mode ready transition={(!oldReceiveMode ? 1 : 0)} " +
                $"initial-kick={(mbusInitialTimerKickAvailable ? 1 : 0)} status {oldStatus:X2}->{regs[0x19]:X2}");
        }

        if (transmitMode)
        {
            bool oldTimerActive = mbusTimerActive;
            regs[0x19] |= MbusStatusSendReady | MbusStatusTxdReady;
            mbusTimerActive = true;
            PeripheralServiceScheduled?.Invoke();
            TraceMad(
                $"mbus tx mode ready transition={(!oldTransmitMode ? 1 : 0)} " +
                $"timer={(oldTimerActive ? 1 : 0)}->{(mbusTimerActive ? 1 : 0)} status {oldStatus:X2}->{regs[0x19]:X2}");
        }
        else if (oldTransmitMode)
        {
            bool oldTimerActive = mbusTimerActive;
            mbusTimerActive = false;
            TraceMad($"mbus tx mode clear timer={(oldTimerActive ? 1 : 0)}->{(mbusTimerActive ? 1 : 0)}");
        }

        UpdateLines();
    }

    private void ServiceMbusDataWrite(byte value)
    {
        byte oldStatus = regs[0x19];
        regs[0x1A] = value;
        regs[0x19] |= MbusStatusSendReady | MbusStatusTxdReady;
        UpdateLines();
        trace?.MbusByte(transmitted: true, value);
        TraceMad($"mbus data w {value:X2} status {oldStatus:X2}->{regs[0x19]:X2}");
    }

    public void TickTimer0()
    {
        timer0Counter++;
        int compare = (regs[0x12] << 8) | regs[0x13];
        int ticksUntilCompare = (compare - timer0Counter) & 0xFFFF;

        if (MadStateTracingEnabled && ticksUntilCompare <= 8)
        {
            TraceMad($"tick timer0 counter={timer0Counter:X4} compare={compare:X4} until={ticksUntilCompare:X4}");
        }

        if (timer0Counter == (ushort)compare)
        {
            AssertFiq(4);
        }
    }

    public void TickTimer1()
    {
        timer1Counter++;

        if (timer1Counter == Timer1InterruptCounter)
        {
            AssertFiq(5);
            timer1Counter = 0;
        }
    }

    public void TickFiq8()
    {
        if ((regs[0x16] & 0x01) != 0)
        {
            AssertFiq(8);
        }
    }

    public void TickSim(long cycles)
    {
        int rxBefore = simUart.RxCount;
        int txBefore = simUart.TxCount;
        byte statusBefore = simUart.ControlStatus;
        byte irqBefore = simUart.InterruptId;
        simUart.Tick(cycles);
        if (rxBefore != simUart.RxCount ||
            txBefore != simUart.TxCount ||
            statusBefore != simUart.ControlStatus ||
            irqBefore != simUart.InterruptId)
        {
            TraceMad(
                $"sim tick cycles={cycles} rx={rxBefore}->{simUart.RxCount} tx={txBefore}->{simUart.TxCount} " +
                $"status={statusBefore:X2}->{simUart.ControlStatus:X2} irq={irqBefore:X2}->{simUart.InterruptId:X2} {SimTraceState()}");
        }

        ServiceSimInterrupt();
    }

    public void TickMbusTimer(long cycles)
    {
        if (!mbusTimerActive || cycles < mbusTimerNextCycle)
        {
            return;
        }

        mbusTimerActive = false;
        mbusTimerNextCycle = long.MaxValue;

        bool receiveMode = (regs[0x18] & MbusControlReceiveMode) != 0;
        bool transmitMode = (regs[0x18] & MbusControlTransmitMode) != 0;
        ushort levelFiq = MbusLevelFiqStatus();
        if (!receiveMode)
        {
            TraceMad($"mbus timer expired ignored rx=0 tx={(transmitMode ? 1 : 0)} level={levelFiq:X3}");
            return;
        }

        ushort oldStatus = fiqStatus;
        fiqStatus |= FiqMbusTimer;
        if (oldStatus != fiqStatus)
        {
            TraceMad($"mbus timer ready {oldStatus:X3}->{fiqStatus:X3} tx={(transmitMode ? 1 : 0)} level={levelFiq:X3}");
        }
        else
        {
            TraceMad($"mbus timer ready unchanged {fiqStatus:X3} tx={(transmitMode ? 1 : 0)} level={levelFiq:X3}");
        }

        UpdateLines();
    }

    public bool SimNeedsService(long cycles) => simUart.NeedsService(cycles);

    public bool TickWatchdogSecond()
    {
        if (regs[0x03] == 0xFF)
        {
            return false;
        }

        regs[0x03]--;
        return regs[0x03] == 0;
    }

    private void HandleSimControl(byte oldValue, byte value)
    {
        TraceMad($"sim ctl {oldValue:X2}->{value:X2} {SimTraceState()}");

        bool resetEdge = (oldValue & SimControlReset) == 0 && (value & SimControlReset) != 0;
        bool legacyResetEdge =
            (oldValue & SimControlLegacyReset) == 0 &&
            (value & SimControlLegacyReset) != 0 &&
            (value & SimControlLegacyEnable) != 0;

        if (resetEdge)
        {
            QueueSimAtr("reset");
            PeripheralServiceScheduled?.Invoke();
        }
        else if (legacyResetEdge)
        {
            QueueSimAtr("legacy reset");
            PeripheralServiceScheduled?.Invoke();
        }
    }

    private void EnableSimUart(string reason)
    {
        simUart.Reset();
        simUart.Enabled = true;
        TraceMad($"sim {reason} cycles={CycleSource?.Invoke() ?? 0} {SimTraceState()}");
        ServiceSimInterrupt();
    }

    private void QueueSimAtr(string reason)
    {
        long cycles = CycleSource?.Invoke() ?? 0;
        simUart.Reset();
        simUart.Enabled = true;
        byte[] answerToReset = InvokeSim(static target => target.AnswerToReset().ToArray());
        simUart.QueueRx(answerToReset, true, cycles, 0);
        TraceMad($"sim {reason}/ATR queued cycles={cycles} {SimTraceState()}");
        ServiceSimInterrupt();
    }

    private void ServiceSimInterrupt()
    {
        if (simUart.ConsumeInterruptRequest())
        {
            TraceMad($"sim interrupt request {SimTraceState()}");
            AssertFiq(6);
        }
    }

    private void OnSimByteTransmitted(byte value, long cycles)
    {
        TraceMad($"sim tx sent {value:X2} cycles={cycles} {SimTraceState()}");
        SimCardResponse? response = InvokeSim(target => target.Transmit(value));

        if (response.HasValue)
        {
            simUart.QueueRx(response.Value.Data, response.Value.Complete, cycles, SimResponseDelayCycles);
            TraceMad($"sim response queued len={response.Value.Data.Length} complete={(response.Value.Complete ? 1 : 0)} {SimTraceState()}");
        }
    }

    private void AssertFiq(int num)
    {
        ushort oldStatus = fiqStatus;
        fiqStatus |= (ushort)(1 << num);
        if (oldStatus != fiqStatus)
        {
            TraceMad($"assert fiq{num} {oldStatus:X3}->{fiqStatus:X3}");
        }
        else
        {
            TraceMad($"assert fiq{num} unchanged {fiqStatus:X3}");
        }

        UpdateLines();
    }

    private void InvokeSim(Action<SimCard> action) =>
        InvokeSim(
            target =>
            {
                action(target);
                return true;
            });

    private TResult InvokeSim<TResult>(Func<SimCard, TResult> action)
    {
        TResult result = action(sim);
        Volatile.Write(ref simPersistenceVersion, sim.PersistenceVersion);
        return result;
    }

    private void AssertIrq(int num)
    {
        ushort oldStatus = irqStatus;
        irqStatus |= (ushort)(1 << num);
        if (oldStatus != irqStatus)
        {
            TraceMad($"assert irq{num} {oldStatus:X3}->{irqStatus:X3}");
        }
        else
        {
            TraceMad($"assert irq{num} unchanged {irqStatus:X3}");
        }

        UpdateLines();
    }

    private void AckFiq(ushort mask)
    {
        ushort oldStatus = fiqStatus;
        ushort oldEffectiveStatus = EffectiveFiqStatus();
        fiqStatus &= (ushort)~mask;
        ushort effectiveStatus = EffectiveFiqStatus();
        if (mask != 0 || oldStatus != fiqStatus)
        {
            TraceMad($"ack fiq mask={mask:X3} {oldStatus:X3}->{fiqStatus:X3} effective={oldEffectiveStatus:X3}->{effectiveStatus:X3}");
        }

        ServiceMbusTimerAfterFiqAck(mask);
        UpdateLines();
    }

    private void AckIrq(ushort mask)
    {
        ushort oldStatus = irqStatus;
        irqStatus &= (ushort)~mask;
        if (mask != 0 || oldStatus != irqStatus)
        {
            TraceMad($"ack irq mask={mask:X3} {oldStatus:X3}->{irqStatus:X3}");
        }

        UpdateLines();
    }

    private void UpdateLines()
    {
        int fiqMask = CurrentFiqMask();
        int irqMask = CurrentIrqMask();
        ushort effectiveFiqStatus = EffectiveFiqStatus();
        bool fiqEnabled = (regs[0x0C] & InterruptControlFiqEnable) != 0;
        bool irqEnabled = (regs[0x0C] & InterruptControlIrqEnable) != 0;
        bool fiqLine = fiqEnabled && (effectiveFiqStatus & ~fiqMask) != 0;
        bool irqLine = irqEnabled && (irqStatus & ~irqMask) != 0;

        TraceLineDecisions(effectiveFiqStatus, fiqMask, fiqEnabled, fiqLine, irqStatus, irqMask, irqEnabled, irqLine);
        SetFiqLine(fiqLine);
        SetIrqLine(irqLine);
    }

    private void ResetLineDecisionLog()
    {
        lastLoggedFiqEffectiveStatus = 0xFFFF;
        lastLoggedIrqStatus = 0xFFFF;
        lastLoggedFiqMask = -1;
        lastLoggedIrqMask = -1;
        lastLoggedFiqEnabled = false;
        lastLoggedIrqEnabled = false;
        lastLoggedFiqLine = false;
        lastLoggedIrqLine = false;
    }

    private void TraceLineDecisions(
        ushort effectiveFiqStatus,
        int fiqMask,
        bool fiqEnabled,
        bool fiqLine,
        ushort currentIrqStatus,
        int irqMask,
        bool irqEnabled,
        bool irqLine)
    {
        if (effectiveFiqStatus != lastLoggedFiqEffectiveStatus ||
            fiqMask != lastLoggedFiqMask ||
            fiqEnabled != lastLoggedFiqEnabled ||
            fiqLine != lastLoggedFiqLine)
        {
            lastLoggedFiqEffectiveStatus = effectiveFiqStatus;
            lastLoggedFiqMask = fiqMask;
            lastLoggedFiqEnabled = fiqEnabled;
            lastLoggedFiqLine = fiqLine;
            TraceMad(
                $"fiq decision en={(fiqEnabled ? 1 : 0)} pending={effectiveFiqStatus:X3} " +
                $"mask={fiqMask:X3} unmasked={(effectiveFiqStatus & ~fiqMask):X3} line={(fiqLine ? 1 : 0)}");
        }

        if (currentIrqStatus != lastLoggedIrqStatus ||
            irqMask != lastLoggedIrqMask ||
            irqEnabled != lastLoggedIrqEnabled ||
            irqLine != lastLoggedIrqLine)
        {
            lastLoggedIrqStatus = currentIrqStatus;
            lastLoggedIrqMask = irqMask;
            lastLoggedIrqEnabled = irqEnabled;
            lastLoggedIrqLine = irqLine;
            TraceMad(
                $"irq decision en={(irqEnabled ? 1 : 0)} pending={currentIrqStatus:X3} " +
                $"mask={irqMask:X3} unmasked={(currentIrqStatus & ~irqMask):X3} line={(irqLine ? 1 : 0)}");
        }
    }

    private ushort EffectiveFiqStatus() => (ushort)(fiqStatus | MbusLevelFiqStatus());

    private ushort MbusLevelFiqStatus()
    {
        bool receiveMode = (regs[0x18] & MbusControlReceiveMode) != 0;
        bool transmitMode = (regs[0x18] & MbusControlTransmitMode) != 0;
        byte status = regs[0x19];

        bool receiveReady = receiveMode && (status & (MbusStatusReceiveByteAvailable | 0x20)) != 0;
        bool transmitReady = transmitMode && (status & MbusStatusSendReady) != 0;
        return receiveReady || transmitReady ? FiqMbusByteService : (ushort)0;
    }

    private void SetFiqLine(bool state)
    {
        if (Cpu is not null)
        {
            bool oldState = Cpu.FiqLine;
            Cpu.FiqLine = state;
            if (oldState != state)
            {
                TraceMad($"line fiq={(state ? 1 : 0)}");
            }
        }
    }

    private void SetIrqLine(bool state)
    {
        if (Cpu is not null)
        {
            bool oldState = Cpu.IrqLine;
            Cpu.IrqLine = state;
            if (oldState != state)
            {
                TraceMad($"line irq={(state ? 1 : 0)}");
            }
        }
    }
}
