using Noks.Dct3.Messaging;
namespace Noks.Dct3.Peripherals;

public sealed class SerialBytePort
{
    public const byte RxReadyInterrupt = 0x40;
    public const byte TxCompleteInterrupt = 0x10;
    public const byte TxReadyStatus = 0x40;
    public const byte CardReadyStatus = 0x08;
    public const byte ReceiveCompleteStatus = 0x04;
    public const byte ControlStatusMask = TxReadyStatus | CardReadyStatus | ReceiveCompleteStatus;
    private const int TxReadyInterruptThreshold = 5;

    private readonly Queue<byte> rx = new();
    private readonly Queue<byte> tx = new();
    private readonly Queue<ScheduledRxByte> scheduledRx = new();
    private readonly long byteCycles;
    private long nextTxCycle;
    private bool txActive;
    private bool txReadyInterruptArmed;
    private bool rxCompletePending;
    private bool rxCompleteStatus;
    private byte interruptId;
    private bool interruptRequested;
    private bool enabled;

    public SerialBytePort(long byteCycles)
    {
        this.byteCycles = byteCycles;
    }

    public Action<byte, long>? ByteTransmitted { get; set; }

    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value)
            {
                return;
            }

            enabled = value;
            Trace?.Invoke(
                $"enabled {(value ? 1 : 0)} status={ControlStatus:X2} irq={interruptId:X2} " +
                $"rx={rx.Count} tx={tx.Count} pending={(rxCompletePending ? 1 : 0)} complete={(rxCompleteStatus ? 1 : 0)}");
        }
    }

    public int RxCount => rx.Count;

    public int TxCount => tx.Count;

    public int ScheduledRxCount => scheduledRx.Count;

    public byte InterruptId => interruptId;

    public bool RxCompletePending => rxCompletePending;

    public bool RxCompleteStatus => rxCompleteStatus;

    public Action<string>? Trace { get; set; }

    public bool HoldCardReadyWhenIdle { get; set; } = true;

    public byte ControlStatus =>
        (byte)(
            (Enabled ? TxReadyStatus : 0x00) |
            (CardReadyVisible ? CardReadyStatus : 0x00) |
            (rxCompleteStatus ? ReceiveCompleteStatus : 0x00));

    public long NextWakeCycle
    {
        get
        {
            long next = long.MaxValue;

            if (txActive)
            {
                next = Math.Min(next, nextTxCycle);
            }

            if (scheduledRx.Count != 0)
            {
                next = Math.Min(next, scheduledRx.Peek().ReadyCycle);
            }

            return next;
        }
    }

    public bool NeedsService(long cycles) => NextWakeCycle <= cycles;

    public bool ConsumeInterruptRequest()
    {
        bool requested = interruptRequested;
        interruptRequested = false;
        return requested;
    }

    public void Reset()
    {
        rx.Clear();
        tx.Clear();
        scheduledRx.Clear();
        nextTxCycle = 0;
        txActive = false;
        txReadyInterruptArmed = false;
        rxCompletePending = false;
        rxCompleteStatus = false;
        interruptId = 0;
        interruptRequested = false;
        enabled = false;
        Trace?.Invoke("reset");
    }

    public void ClearInterrupts(byte mask)
    {
        byte oldInterruptId = interruptId;
        interruptId &= (byte)~mask;
        if (oldInterruptId != interruptId)
        {
            Trace?.Invoke($"irq clear mask={mask:X2} {oldInterruptId:X2}->{interruptId:X2}");
        }
    }

    public void WriteTx(byte value, long cycles)
    {
        if (!Enabled)
        {
            Trace?.Invoke($"tx drop disabled value={value:X2}");
            return;
        }

        rxCompleteStatus = false;
        tx.Enqueue(value);
        txReadyInterruptArmed |= tx.Count > TxReadyInterruptThreshold;
        Trace?.Invoke(
            $"tx queue value={value:X2} tx={tx.Count} status={ControlStatus:X2} " +
            $"armed={(txReadyInterruptArmed ? 1 : 0)}");

        if (!txActive)
        {
            txActive = true;
            nextTxCycle = cycles + byteCycles;
            Trace?.Invoke($"tx start next={nextTxCycle}");
        }
    }

    public byte ReadRx()
    {
        if (rx.Count == 0)
        {
            Trace?.Invoke("rx read empty");
            return 0;
        }

        byte value = rx.Dequeue();

        if (rx.Count == 0)
        {
            interruptId &= unchecked((byte)~RxReadyInterrupt);

            if (rxCompletePending)
            {
                rxCompletePending = false;
                rxCompleteStatus = true;
                Trace?.Invoke($"rx complete latched value={value:X2}");
            }
        }

        Trace?.Invoke(
            $"rx read value={value:X2} rx={rx.Count} status={ControlStatus:X2} irq={interruptId:X2} " +
            $"pending={(rxCompletePending ? 1 : 0)} complete={(rxCompleteStatus ? 1 : 0)}");

        return value;
    }

    public void QueueRx(ReadOnlySpan<byte> data, bool complete, long cycles, long delayCycles)
    {
        long readyCycle = cycles + delayCycles;
        Trace?.Invoke(
            $"rx schedule len={data.Length} complete={(complete ? 1 : 0)} at={readyCycle} " +
            $"delay={delayCycles} queued={scheduledRx.Count}");

        for (int i = 0; i < data.Length; i++)
        {
            scheduledRx.Enqueue(new ScheduledRxByte(data[i], readyCycle + (byteCycles * i), complete && i == data.Length - 1));
        }
    }

    public void Tick(long cycles)
    {
        while (txActive && tx.Count != 0 && cycles >= nextTxCycle)
        {
            byte value = tx.Dequeue();
            ByteTransmitted?.Invoke(value, nextTxCycle);
            nextTxCycle += byteCycles;
            Trace?.Invoke($"tx sent value={value:X2} tx={tx.Count} next={nextTxCycle}");

            if (txReadyInterruptArmed && tx.Count <= TxReadyInterruptThreshold)
            {
                txReadyInterruptArmed = false;
                RequestInterrupt(TxCompleteInterrupt);
            }
        }

        if (txActive && tx.Count == 0)
        {
            txActive = false;
            RequestInterrupt(TxCompleteInterrupt);
        }

        while (scheduledRx.Count != 0 && cycles >= scheduledRx.Peek().ReadyCycle)
        {
            ScheduledRxByte value = scheduledRx.Dequeue();
            rx.Enqueue(value.Value);
            rxCompleteStatus = false;
            rxCompletePending |= value.Complete;
            Trace?.Invoke(
                $"rx ready value={value.Value:X2} rx={rx.Count} scheduled={scheduledRx.Count} " +
                $"pending={(rxCompletePending ? 1 : 0)} complete={(rxCompleteStatus ? 1 : 0)}");
            RequestInterrupt(RxReadyInterrupt);
        }
    }

    private void RequestInterrupt(byte bits)
    {
        byte oldInterruptId = interruptId;
        interruptId |= bits;
        interruptRequested = true;
        Trace?.Invoke($"irq request bits={bits:X2} {oldInterruptId:X2}->{interruptId:X2}");
    }

    private bool CardReadyVisible =>
        Enabled && (HoldCardReadyWhenIdle || txActive || tx.Count != 0 || rx.Count != 0 || scheduledRx.Count != 0 || rxCompletePending);

    private readonly record struct ScheduledRxByte(byte Value, long ReadyCycle, bool Complete);
}
