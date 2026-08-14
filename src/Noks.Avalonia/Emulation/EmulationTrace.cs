using System.Collections.Concurrent;
using Noks.Dct3.Core;
using Noks.AvaloniaApp.Diagnostics;

namespace Noks.AvaloniaApp.Emulation;

internal sealed class EmulationTrace : IDct3Trace
{
    private const int MaximumPendingEntries = 10_000;
    private readonly ConcurrentQueue<EmulationLogEntry> pending = new();
    private readonly Func<long> cycles;
    private long sequence;
    private int enabled;
    private int pendingCount;

    public EmulationTrace(Func<long> cycles)
    {
        this.cycles = cycles;
    }

    public event Action? EntriesAvailable;

    public bool MadStateEnabled => false;

    public void SetEnabled(bool value)
    {
        Volatile.Write(ref enabled, value ? 1 : 0);
        if (value)
        {
            return;
        }

        while (pending.TryDequeue(out _))
        {
            Interlocked.Decrement(ref pendingCount);
        }
    }

    public bool TryDequeue(out EmulationLogEntry? entry)
    {
        if (!pending.TryDequeue(out entry))
        {
            return false;
        }

        Interlocked.Decrement(ref pendingCount);
        return true;
    }

    public void FbusFrame(bool transmitted, ReadOnlySpan<byte> frame)
    {
        if (Volatile.Read(ref enabled) != 0)
        {
            Add(EmulationLogChannel.Fbus, FbusDecoder.Describe(transmitted, frame));
        }
    }

    public void MbusByte(bool transmitted, byte value)
    {
        if (Volatile.Read(ref enabled) != 0)
        {
            Add(EmulationLogChannel.Mbus, $"{(transmitted ? "TX" : "RX")} {value:X2}");
        }
    }

    public void Event(string message)
    {
        if (Volatile.Read(ref enabled) == 0)
        {
            return;
        }

        EmulationLogChannel channel = message.StartsWith("DSP MDI", StringComparison.Ordinal)
            ? EmulationLogChannel.Mdi
            : message.Contains("queue", StringComparison.OrdinalIgnoreCase) ||
              message.Contains("task", StringComparison.OrdinalIgnoreCase)
                ? EmulationLogChannel.Task
                : message.StartsWith("DSP ", StringComparison.Ordinal)
                    ? EmulationLogChannel.Mdi
                    : message.StartsWith("CCONT ", StringComparison.Ordinal) ||
                      message.StartsWith("firmware patch:", StringComparison.Ordinal)
                        ? EmulationLogChannel.Hardware
                        : EmulationLogChannel.Trace;
        Add(channel, message);
    }

    public void FlashCommand(string description)
    {
        if (Volatile.Read(ref enabled) != 0)
        {
            Add(EmulationLogChannel.Hardware, $"FLASH {description}");
        }
    }

    public void Unmapped(bool write, uint address, uint value, int size)
    {
        if (Volatile.Read(ref enabled) != 0)
        {
            Add(EmulationLogChannel.Hardware,
                write ? $"unmapped W{size * 8} {address:X6}={value:X8}" : $"unmapped R{size * 8} {address:X6}");
        }
    }

    public void MadRead(uint offset, byte value) { }
    public void MadWrite(uint offset, byte value) { }
    public void MadState(string message) { }
    public void CcontRead(int reg, byte value) { }
    public void CcontWrite(int reg, byte value) { }
    public void LcdCommand(byte value) { }
    public void LcdData(byte value, int x, int y, bool vertical) { }
    public void InterfaceAccess(string block, bool write, uint offset, uint value) { }
    public void DspRam(bool write, uint offset, uint value) { }

    private void Add(EmulationLogChannel channel, string text)
    {
        if (Volatile.Read(ref enabled) == 0)
        {
            return;
        }

        int count = Interlocked.Increment(ref pendingCount);
        if (count > MaximumPendingEntries)
        {
            Interlocked.Decrement(ref pendingCount);
            return;
        }

        long currentCycles = Math.Max(0, cycles());
        pending.Enqueue(new EmulationLogEntry(
            Interlocked.Increment(ref sequence),
            TimeSpan.FromSeconds((double)currentCycles / Dct3Machine.CyclesPerSecond),
            channel,
            text));
        EntriesAvailable?.Invoke();
    }
}
