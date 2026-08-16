using Noks.Dct3.Core;

namespace Noks.Cli;

public sealed class ConsoleTrace : IDct3Trace
{
    private readonly bool ioLog;
    private readonly bool ccontLog;
    private readonly bool dspLog;
    private readonly int limit;
    private int ioLines;
    private int ccontLines;
    private int lcdLines;
    private int dspLines;
    private int unmappedLines;

    public ConsoleTrace(bool ioLog, bool ccontLog, bool dspLog, int limit)
    {
        this.ioLog = ioLog;
        this.ccontLog = ccontLog;
        this.dspLog = dspLog;
        this.limit = limit;
    }

    public long DspReads { get; private set; }

    public long DspWrites { get; private set; }

    public Dictionary<uint, long> DspReadCounts { get; } = [];

    public Dictionary<uint, long> DspWriteCounts { get; } = [];

    public Queue<string> DspRecent { get; } = new();

    public Func<uint>? PcProbe { get; set; }

    public Func<long>? StepProbe { get; set; }

    public bool MadStateEnabled => false;

    public long IoReads { get; private set; }

    public long IoWrites { get; private set; }

    public Dictionary<uint, long> IoReadCounts { get; } = [];

    public Dictionary<uint, long> IoWriteCounts { get; } = [];

    public long UnmappedAccesses { get; private set; }

    public List<string> Events { get; } = [];

    public void MadRead(uint offset, byte value)
    {
        IoReads++;
        IoReadCounts[offset] = IoReadCounts.GetValueOrDefault(offset) + 1;

        if (ioLog && ioLines < limit)
        {
            ioLines++;
            Console.WriteLine($"  io r {offset:X2} = {value:X2}  {Mad2RegNames.Describe(offset)}");
        }
    }

    public void MadWrite(uint offset, byte value)
    {
        IoWrites++;
        IoWriteCounts[offset] = IoWriteCounts.GetValueOrDefault(offset) + 1;

        if (ioLog && ioLines < limit)
        {
            ioLines++;
            Console.WriteLine($"  io w {offset:X2} = {value:X2}  {Mad2RegNames.Describe(offset)}");
        }
    }

    public void MadState(string message)
    {
    }

    public void CcontRead(int reg, byte value)
    {
        if (ccontLog && ccontLines < limit)
        {
            ccontLines++;
            Console.WriteLine($"  ccont r {reg:X} = {value:X2}");
        }
    }

    public void CcontWrite(int reg, byte value)
    {
        if (ccontLog && ccontLines < limit)
        {
            ccontLines++;
            Console.WriteLine($"  ccont w {reg:X} = {value:X2}");
        }
    }

    public void LcdCommand(byte value)
    {
        if (ioLog && lcdLines < limit)
        {
            lcdLines++;
            Console.WriteLine($"  lcd cmd {value:X2}");
        }
    }

    public void LcdData(byte value, int x, int y, bool vertical)
    {
    }

    public void FlashCommand(string description)
    {
        Record($"flash: {description}");
    }

    public void InterfaceAccess(string block, bool write, uint offset, uint value)
    {
        if (dspLog && dspLines < limit)
        {
            dspLines++;
            Console.WriteLine(write ? $"  {block} w {offset:X} = {value:X8}" : $"  {block} r {offset:X}");
        }
    }

    public void DspRam(bool write, uint offset, uint value)
    {
        if (write)
        {
            DspWrites++;
            DspWriteCounts[offset] = DspWriteCounts.GetValueOrDefault(offset) + 1;
        }
        else
        {
            DspReads++;
            DspReadCounts[offset] = DspReadCounts.GetValueOrDefault(offset) + 1;
        }

        DspRecent.Enqueue($"{(write ? 'w' : 'r')} {offset:X3}={value:X4}@{PcProbe?.Invoke() ?? 0:X6}");

        if (DspRecent.Count > 32)
        {
            DspRecent.Dequeue();
        }

        if (dspLog && dspLines < limit)
        {
            dspLines++;
            Console.WriteLine($"  dspram {(write ? 'w' : 'r')} {offset:X3} = {value:X4}");
        }
    }

    public void Unmapped(bool write, uint address, uint value, int size)
    {
        UnmappedAccesses++;

        if (unmappedLines < 20)
        {
            unmappedLines++;
            Record(write ? $"unmapped write {address:X6} = {value:X8}" : $"unmapped read {address:X6}");
        }
    }

    public void Event(string message)
    {
        Record(message);
    }

    private void Record(string message)
    {
        if (Events.Count < 200)
        {
            Events.Add(message);
        }

        long step = StepProbe?.Invoke() ?? 0;
        Console.WriteLine($"  [event @{step}] {message}");
    }
}
