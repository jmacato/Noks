using System.Diagnostics;
using Noks.Dct3.Audio;
using Noks.Dct3.Core;
using Noks.Dct3.Display;
using Noks.Dct3.Messaging;
using Noks.Dct3.Peripherals;
using Noks.Dct3.Radio;

namespace Noks.Cli;

public static class RunCommand
{
    public static int Run(string[] args)
    {
        string? path = null;
        long steps = 100_000_000;
        bool ioLog = false;
        bool ccontLog = false;
        bool dspLog = false;
        int logLimit = 200;
        bool lcdLog = false;
        int lcdLogLimit = 80;
        string? lcdPgm = null;
        string? flashOut = null;
        string? simImsi = null;
        bool accelerateIdle = false;
        bool deterministicTime = false;
        string? watch = null;
        bool watchReads = false;
        long watchAfter = 0;
        List<(uint addr, byte[] bytes)> patches = [];
        List<uint> probes = [];
        long probeAfter = 0;
        List<ScheduledKeyEvent> keyEvents = [];
        List<ScheduledAdcEvent> adcEvents = [];
        List<ScheduledDspRssiEvent> dspRssiEvents = [];
        List<ScheduledIncomingGsmEvent> incomingGsmEvents = [];
        long traceAfter = -1;
        long traceCount = 0;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--steps" when i + 1 < args.Length:
                    steps = long.Parse(args[++i]);
                    break;
                case "--iolog":
                    ioLog = true;
                    break;
                case "--ccontlog":
                    ccontLog = true;
                    break;
                case "--dsplog":
                    dspLog = true;
                    break;
                case "--log-limit" when i + 1 < args.Length:
                    logLimit = int.Parse(args[++i]);
                    break;
                case "--lcd-log":
                    lcdLog = true;
                    break;
                case "--lcd-log-limit" when i + 1 < args.Length:
                    lcdLogLimit = int.Parse(args[++i]);
                    if (lcdLogLimit < 0)
                    {
                        Console.Error.WriteLine("--lcd-log-limit expects a non-negative integer");
                        return 1;
                    }

                    break;
                case "--lcd-pgm" when i + 1 < args.Length:
                    lcdPgm = args[++i];
                    break;
                case "--flash-out" when i + 1 < args.Length:
                    flashOut = args[++i];
                    break;
                case "--sim-imsi" when i + 1 < args.Length:
                    simImsi = args[++i];
                    break;
                case "--accelerate-idle":
                    accelerateIdle = true;
                    break;
                case "--deterministic-time":
                    deterministicTime = true;
                    break;
                case "--watch" when i + 1 < args.Length:
                    watch = args[++i];
                    break;
                case "--watch-reads":
                    watchReads = true;
                    break;
                case "--watch-after" when i + 1 < args.Length:
                    watchAfter = long.Parse(args[++i]);
                    break;
                case "--key" when i + 1 < args.Length:
                    try
                    {
                        AddKeyEvents(keyEvents, args[++i]);
                    }
                    catch (ArgumentException ex)
                    {
                        Console.Error.WriteLine(ex.Message);
                        return 1;
                    }

                    break;
                case "--adc" when i + 1 < args.Length:
                    try
                    {
                        AddAdcEvent(adcEvents, args[++i]);
                    }
                    catch (ArgumentException ex)
                    {
                        Console.Error.WriteLine(ex.Message);
                        return 1;
                    }

                    break;
                case "--dsp-rssi" when i + 1 < args.Length:
                    try
                    {
                        AddDspRssiEvent(dspRssiEvents, args[++i]);
                    }
                    catch (ArgumentException ex)
                    {
                        Console.Error.WriteLine(ex.Message);
                        return 1;
                    }

                    break;
                case "--incoming-call" when i + 1 < args.Length:
                    try
                    {
                        AddIncomingCallEvent(incomingGsmEvents, args[++i]);
                    }
                    catch (ArgumentException ex)
                    {
                        Console.Error.WriteLine(ex.Message);
                        return 1;
                    }

                    break;
                case "--incoming-sms" when i + 1 < args.Length:
                    try
                    {
                        AddIncomingSmsEvent(incomingGsmEvents, args[++i]);
                    }
                    catch (ArgumentException ex)
                    {
                        Console.Error.WriteLine(ex.Message);
                        return 1;
                    }

                    break;
                case "--incoming-ringtone" when i + 1 < args.Length:
                    try
                    {
                        AddIncomingRingtoneEvent(incomingGsmEvents, args[++i]);
                    }
                    catch (ArgumentException ex)
                    {
                        Console.Error.WriteLine(ex.Message);
                        return 1;
                    }

                    break;
                case "--probe" when i + 1 < args.Length:
                    probes.Add(Convert.ToUInt32(args[++i], 16));
                    break;
                case "--probe-after" when i + 1 < args.Length:
                    probeAfter = long.Parse(args[++i]);
                    break;
                case "--patch" when i + 1 < args.Length:
                    string[] pp = args[++i].Split(':');
                    uint pa = Convert.ToUInt32(pp[0], 16);
                    byte[] pb = Enumerable.Range(0, pp[1].Length / 2).Select(k => Convert.ToByte(pp[1].Substring(k * 2, 2), 16)).ToArray();
                    patches.Add((pa, pb));
                    break;
                case "--trace-exec" when i + 1 < args.Length:
                    string[] te = args[++i].Split(':');
                    traceAfter = long.Parse(te[0]);
                    traceCount = long.Parse(te[1]);
                    break;
                default:
                    if (path is null && !args[i].StartsWith('-'))
                    {
                        path = args[i];
                    }
                    else
                    {
                        Console.Error.WriteLine($"unknown or incomplete option '{args[i]}'");
                        return 1;
                    }

                    break;
            }
        }

        if (path is null || !File.Exists(path))
        {
            Console.Error.WriteLine("usage: noks run <flash.fls> [--steps <n>] [--accelerate-idle] [--deterministic-time] [--iolog] [--ccontlog] [--dsplog] [--log-limit <n>] [--lcd-log] [--lcd-log-limit <n>] [--lcd-pgm <path>] [--flash-out <path>] [--sim-imsi <15 digits>] [--key <name@step[:hold]>] [--adc <name@step:value>] [--dsp-rssi <step:value>] [--incoming-call <step[:number]>] [--incoming-sms <step[:originator[:text]]>] [--incoming-ringtone <step[:originator]>] [--watch <hexaddr[:len]>] [--watch-after <step>] [--watch-reads] [--probe <addr>] [--probe-after <step>]");
            return 1;
        }

        if (simImsi is not null && (simImsi.Length != 15 || simImsi.Any(ch => ch < '0' || ch > '9')))
        {
            Console.Error.WriteLine("--sim-imsi expects exactly 15 decimal digits");
            return 1;
        }

        long executed = 0;
        long realSteps = 0;
        long skippedIdleInstructions = 0;
        long idleAccelerations = 0;
        ConsoleTrace trace = new(ioLog, ccontLog, dspLog, logLimit);
        byte[] flash = File.ReadAllBytes(path);

        foreach ((uint addr, byte[] bytes) in patches)
        {
            const uint flashBase = 0x200000;
            if (addr < flashBase || addr - flashBase > flash.Length - bytes.Length)
            {
                Console.Error.WriteLine($"--patch address {addr:X6} is outside the flash image");
                return 1;
            }

            uint off = addr - flashBase;
            bytes.CopyTo(flash, off);
            Console.WriteLine($"  [patch] {addr:X6} = {string.Join(' ', bytes.Select(b => $"{b:X2}"))}");
        }

        DateTimeOffset? fixedLocalTime = deterministicTime
            ? new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero)
            : null;
        Dct3Machine machine = new(
            flash,
            trace,
            simImsi,
            rtcStart: fixedLocalTime?.DateTime,
            networkLocalTimeProvider: fixedLocalTime.HasValue ? () => fixedLocalTime.Value : null);
        trace.PcProbe = () => machine.Cpu.GetGpr(15);
        trace.StepProbe = () => executed;
        machine.Bus.PcProbe = () => machine.Cpu.GetGpr(15);

        if (watch is not null)
        {
            string[] parts = watch.Split(':');
            uint watchAddr = Convert.ToUInt32(parts[0], 16);
            uint watchLen = parts.Length > 1 ? Convert.ToUInt32(parts[1], 16) : 4;
            if (watchAfter <= 0)
            {
                machine.Bus.WatchLow = watchAddr;
                machine.Bus.WatchHigh = watchAddr + watchLen;
            }

            machine.Bus.WatchReads = watchReads;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        Dictionary<uint, long> pcHistogram = [];
        uint[] pcRing = new uint[32];
        long lcdWrites = 0;
        long lastLcdStep = 0;
        bool lcdFrameDirty = false;
        LcdSnapshot? lastLcdLogSnapshot = null;
        int lcdLogFrames = 0;
        int lcdLogSuppressed = 0;

        uint[] probeAddrs = probes.Select(p => p + 4).ToArray();
        int probeLines = 0;
        long lastProbeStep = 0;
        keyEvents.Sort((a, b) => a.Step.CompareTo(b.Step));
        adcEvents.Sort((a, b) => a.Step.CompareTo(b.Step));
        dspRssiEvents.Sort((a, b) => a.Step.CompareTo(b.Step));
        incomingGsmEvents.Sort((a, b) => a.Step.CompareTo(b.Step));
        int nextKeyEvent = 0;
        int nextAdcEvent = 0;
        int nextDspRssiEvent = 0;
        int nextIncomingGsmEvent = 0;
        bool scheduledPowerKeyHeld = false;

        while (executed < steps)
        {
            if (watch is not null && watchAfter > 0 && executed == watchAfter)
            {
                string[] parts = watch.Split(':');
                uint watchAddr = Convert.ToUInt32(parts[0], 16);
                uint watchLen = parts.Length > 1 ? Convert.ToUInt32(parts[1], 16) : 4;
                machine.Bus.WatchLow = watchAddr;
                machine.Bus.WatchHigh = watchAddr + watchLen;
                Console.WriteLine($"  [event @{executed}] watch enabled {watchAddr:X6}:{watchLen:X}");
            }

            if (machine.Io.PowerKeyHeld && !scheduledPowerKeyHeld && machine.Bus.Cycles > 3 * Dct3Machine.CyclesPerSecond)
            {
                machine.Io.PowerKeyHeld = false;
                Console.WriteLine("  [event] power key released");
            }

            while (nextKeyEvent < keyEvents.Count && keyEvents[nextKeyEvent].Step <= executed)
            {
                ScheduledKeyEvent keyEvent = keyEvents[nextKeyEvent++];

                if (keyEvent.Binding.Power)
                {
                    scheduledPowerKeyHeld = keyEvent.Pressed;
                    machine.Io.PowerKeyHeld = keyEvent.Pressed;
                }
                else
                {
                    machine.Io.SetKey(keyEvent.Binding.Column, keyEvent.Binding.Bit, keyEvent.Pressed);
                }

                Console.WriteLine($"  [event @{executed}] key {keyEvent.Name} {(keyEvent.Pressed ? "down" : "up")}");
            }

            while (nextAdcEvent < adcEvents.Count && adcEvents[nextAdcEvent].Step <= executed)
            {
                ScheduledAdcEvent adcEvent = adcEvents[nextAdcEvent++];
                SetAdcInput(machine.AdcInputs, adcEvent.Channel, adcEvent.Value);
                machine.Ccont.AdcInputChanged(adcEvent.Channel);
                Console.WriteLine($"  [event @{executed}] adc ch{adcEvent.Channel} {adcEvent.Name}={adcEvent.Value:X3}");
            }

            while (nextDspRssiEvent < dspRssiEvents.Count && dspRssiEvents[nextDspRssiEvent].Step <= executed)
            {
                ScheduledDspRssiEvent rssiEvent = dspRssiEvents[nextDspRssiEvent++];
                machine.SetDspRssi(rssiEvent.Value);
                Console.WriteLine($"  [event @{executed}] dsp rssi={rssiEvent.Value:X2}");
            }

            while (nextIncomingGsmEvent < incomingGsmEvents.Count && incomingGsmEvents[nextIncomingGsmEvent].Step <= executed)
            {
                ScheduledIncomingGsmEvent gsmEvent = incomingGsmEvents[nextIncomingGsmEvent++];

                if (gsmEvent.Kind == IncomingGsmEventKind.Call)
                {
                    machine.QueueIncomingCall(gsmEvent.Address);
                    Console.WriteLine($"  [event @{executed}] incoming call from {DisplayIncomingValue(gsmEvent.Address)}");
                }
                else if (gsmEvent.Kind == IncomingGsmEventKind.Sms)
                {
                    machine.QueueIncomingSms(gsmEvent.Address, gsmEvent.Text);
                    Console.WriteLine($"  [event @{executed}] incoming SMS from {DisplayIncomingValue(gsmEvent.Address)} text=\"{DisplayIncomingValue(gsmEvent.Text)}\"");
                }
                else
                {
                    byte[] payload = NokiaSmartMessagingRingtone.EncodeDemoRingtone();
                    machine.QueueIncomingSmartMessage(
                        gsmEvent.Address,
                        NokiaSmartMessagingRingtone.DestinationPort,
                        payload);
                    Console.WriteLine(
                        $"  [event @{executed}] incoming Smart Messaging ringtone from {DisplayIncomingValue(gsmEvent.Address)} " +
                        $"title=\"{NokiaSmartMessagingRingtone.DemoRingtoneName}\" bytes={payload.Length}");
                }
            }

            if (accelerateIdle)
            {
                const int maximumIdleAccelerationInstructions =
                    (int)(Dct3Machine.CyclesPerSecond / 100 / 8 * 4);
                long nextHostEventStep = steps;
                if (nextKeyEvent < keyEvents.Count)
                {
                    nextHostEventStep = Math.Min(nextHostEventStep, keyEvents[nextKeyEvent].Step);
                }

                if (nextAdcEvent < adcEvents.Count)
                {
                    nextHostEventStep = Math.Min(nextHostEventStep, adcEvents[nextAdcEvent].Step);
                }

                if (nextDspRssiEvent < dspRssiEvents.Count)
                {
                    nextHostEventStep = Math.Min(nextHostEventStep, dspRssiEvents[nextDspRssiEvent].Step);
                }

                if (nextIncomingGsmEvent < incomingGsmEvents.Count)
                {
                    nextHostEventStep = Math.Min(nextHostEventStep, incomingGsmEvents[nextIncomingGsmEvent].Step);
                }

                if (watch is not null && watchAfter > executed)
                {
                    nextHostEventStep = Math.Min(nextHostEventStep, watchAfter);
                }

                if (probeAddrs.Length > 0)
                {
                    nextHostEventStep = probeAfter > executed
                        ? Math.Min(nextHostEventStep, probeAfter)
                        : executed;
                }

                if (traceAfter >= 0)
                {
                    nextHostEventStep = traceAfter > executed
                        ? Math.Min(nextHostEventStep, traceAfter)
                        : executed;
                }

                int maximumSkippedInstructions = (int)Math.Clamp(
                    nextHostEventStep - executed,
                    0,
                    maximumIdleAccelerationInstructions);
                int skipped = machine.AccelerateIdleSpin(maximumSkippedInstructions);
                if (skipped > 0)
                {
                    executed += skipped;
                    skippedIdleInstructions += skipped;
                    idleAccelerations++;
                    continue;
                }
            }

            uint pc = machine.Cpu.GetGpr(15);
            pcRing[executed & 31] = pc;

            if (traceCount > 0 && traceAfter >= 0 && executed >= traceAfter)
            {
                traceCount--;
                Console.WriteLine($"T {pc - 4:X6} {machine.Cpu.GetGpr(0):X} {machine.Cpu.GetGpr(1):X}");
            }

            if (probeAddrs.Length > 0 && executed >= probeAfter && Array.IndexOf(probeAddrs, pc) >= 0)
            {
                probeLines++;
                lastProbeStep = executed;

                if (probeLines <= 150 || probeLines % 20000 == 0)
                {
                    uint r0 = machine.Cpu.GetGpr(0);
                    uint r1 = machine.Cpu.GetGpr(1);
                    uint r4 = machine.Cpu.GetGpr(4);
                    uint r5 = machine.Cpu.GetGpr(5);
                    uint r6 = machine.Cpu.GetGpr(6);
                    string peek = "";

                    AppendRamPeek(machine.Bus.Ram, "r0", r0, ref peek);
                    AppendRamPeek(machine.Bus.Ram, "r1", r1, ref peek);
                    AppendRamPeek(machine.Bus.Ram, "r4", r4, ref peek);
                    AppendRamPeek(machine.Bus.Ram, "r5", r5, ref peek);
                    AppendRamPeek(machine.Bus.Ram, "r6", r6, ref peek);

                    Console.WriteLine($"  [probe @{executed}] {pc - 4:X6} r0={r0:X} r1={r1:X} r2={machine.Cpu.GetGpr(2):X} r3={machine.Cpu.GetGpr(3):X} r4={r4:X} r5={r5:X} r6={r6:X} r7={machine.Cpu.GetGpr(7):X} r9={machine.Cpu.GetGpr(9):X} sp={machine.Cpu.GetGpr(13):X} lr={machine.Cpu.GetGpr(14):X6}{peek}");
                }
            }

            if ((executed & 63) == 0)
            {
                pcHistogram[pc] = pcHistogram.GetValueOrDefault(pc) + 1;
            }

            machine.Step();
            executed++;
            realSteps++;

            if (machine.Lcd.DataWrites != lcdWrites)
            {
                lcdWrites = machine.Lcd.DataWrites;
                lastLcdStep = executed;
                lcdFrameDirty = true;
            }

            if (lcdLog && lcdFrameDirty && executed - lastLcdStep >= 50_000)
            {
                lcdFrameDirty = false;
                FlushLcdLog(machine.Lcd, executed, lcdLogLimit, ref lastLcdLogSnapshot, ref lcdLogFrames, ref lcdLogSuppressed);
            }

            if (machine.PoweredOff)
            {
                break;
            }
        }

        stopwatch.Stop();

        if (lcdLog && machine.Lcd.DataWrites > 0)
        {
            FlushLcdLog(machine.Lcd, executed, lcdLogLimit, ref lastLcdLogSnapshot, ref lcdLogFrames, ref lcdLogSuppressed);
        }

        Console.WriteLine();
        Console.WriteLine(
            $"executed {executed:N0} logical steps ({realSteps:N0} real, {skippedIdleInstructions:N0} idle-skipped in {idleAccelerations:N0} grants), " +
            $"{machine.Bus.Cycles:N0} cycles ({(double)machine.Bus.Cycles / Dct3Machine.CyclesPerSecond:F2}s emulated) in {stopwatch.Elapsed.TotalSeconds:F1}s host");
        Console.WriteLine($"pc={machine.Cpu.GetGpr(15):X8} cpsr={machine.Cpu.CpsrValue:X8} mode={machine.Cpu.CpsrValue & 0x1F:X2} thumb={(machine.Cpu.CpsrValue >> 5) & 1}");
        Console.WriteLine("regs: " + string.Join(' ', Enumerable.Range(0, 15).Select(i => $"r{i}={machine.Cpu.GetGpr(i):X8}")));
        if (probeAddrs.Length > 0)
        {
            Console.WriteLine($"probe hits={probeLines:N0} last at step {lastProbeStep:N0}");
        }

        Console.WriteLine($"io reads={trace.IoReads:N0} writes={trace.IoWrites:N0} unmapped={trace.UnmappedAccesses:N0}");
        Console.WriteLine($"io hot reads: {string.Join(' ', trace.IoReadCounts.OrderByDescending(p => p.Value).Take(24).Select(p => $"{p.Key:X2}:{p.Value}"))}");
        Console.WriteLine($"io hot writes: {string.Join(' ', trace.IoWriteCounts.OrderByDescending(p => p.Value).Take(24).Select(p => $"{p.Key:X2}:{p.Value}"))}");
        Console.WriteLine($"dsp ram reads={trace.DspReads:N0} writes={trace.DspWrites:N0}");

        if (trace.DspReadCounts.Count > 0)
        {
            string hot = string.Join(' ', trace.DspReadCounts.OrderByDescending(p => p.Value).Take(24).Select(p => $"{p.Key:X3}:{p.Value}"));
            Console.WriteLine($"dsp hot reads: {hot}");
            string hotW = string.Join(' ', trace.DspWriteCounts.Where(p => p.Key is < 0x200 or >= 0xE00).OrderByDescending(p => p.Value).Take(24).Select(p => $"{p.Key:X3}:{p.Value}"));
            Console.WriteLine($"dsp hot writes: {hotW}");
            Console.WriteLine($"dsp recent: {string.Join(' ', trace.DspRecent)}");
        }
        Console.WriteLine($"flash programs={machine.Flash.ProgramCount} erases={machine.Flash.EraseCount}");
        Console.WriteLine($"watchdog resets={machine.WatchdogResets} powered off={machine.PoweredOff}");
        Console.WriteLine($"lcd: power down={machine.Lcd.PowerDown} mode={machine.Lcd.DisplayMode} vop={machine.Lcd.Vop} cmds={machine.Lcd.CommandWrites:N0} data={machine.Lcd.DataWrites:N0} last write step={lastLcdStep:N0} hash={ComputeLcdHash(machine.Lcd):X8}");
        if (lcdLog)
        {
            Console.WriteLine($"lcd frame log: frames={lcdLogFrames:N0} suppressed={lcdLogSuppressed:N0}");
        }

        Mad2PeripheralState peripherals = machine.Io.PeripheralState;
        Console.WriteLine($"peripherals: vibra={(peripherals.VibratorEnabled ? "on" : "off")} vibra-ctl={peripherals.VibratorControl:X2} lcd-light={(peripherals.LcdBacklightOn ? "on" : "off")} keypad-light={(peripherals.KeypadBacklightOn ? "on" : "off")} led-drive={(peripherals.LedDriveEnabled ? "on" : "off")}");
        Mad2AudioState audio = machine.Io.AudioState;
        DspToneState dspTone = machine.DspState.ToneState;
        Console.WriteLine(
            $"audio: buzzer={(audio.BuzzerEnabled ? "on" : "off")} divider={audio.BuzzerDivider:X2} volume={audio.BuzzerVolume:X2} " +
            $"dsp-tone={(dspTone.Audible ? "on" : "off")} osc1={dspTone.Oscillator1Hz:F2}Hz " +
            $"osc2={dspTone.Oscillator2Hz:F2}Hz amp={dspTone.Amplitude:X4}");

        foreach (PeripheralWorkerMetrics metrics in Dct3Machine.GetPeripheralWorkerMetrics().Where(value => value.Enabled))
        {
            Console.WriteLine(
                $"worker {metrics.Peripheral}: queued={metrics.QueuedInvocations:N0} inline={metrics.InlineInvocations:N0} " +
                $"wakes={metrics.WorkerWakeups:N0} completed={metrics.CompletedInvocations:N0} " +
                $"sync-allocs={metrics.SynchronizationAllocations:N0} request-reply={metrics.RequestReplyTime.TotalMilliseconds:F1}ms");
        }

        Console.WriteLine();
        Console.WriteLine("recent pcs:");
        for (int i = 0; i < pcRing.Length; i++)
        {
            uint pc = pcRing[(executed + 1 + i) & 31];
            Console.Write($" {pc:X6}");
        }

        Console.WriteLine();

        Console.WriteLine();
        Console.WriteLine("hot pcs (sampled):");
        foreach ((uint pc, long count) in pcHistogram.OrderByDescending(p => p.Value).Take(10))
        {
            Console.WriteLine($"  {pc:X8}  {count:N0}");
        }

        if (trace.DspWrites > 0)
        {
            Console.WriteLine();
            Console.WriteLine("dsp shared ram 000-1FF:");
            byte[] shared = machine.Bus.DspSharedRam;
            for (int row = 0; row < 0x200; row += 16)
            {
                string hex = string.Join(' ', Enumerable.Range(0, 8).Select(i => $"{shared[row + i * 2]:X2}{shared[row + i * 2 + 1]:X2}"));
                Console.WriteLine($"  {row:X3}: {hex}");
            }
        }

        if (machine.Lcd.DataWrites > 0)
        {
            Console.WriteLine();
            PrintLcd(machine.Lcd);
        }

        if (lcdPgm is not null)
        {
            WritePgm(machine.Lcd, lcdPgm);
            Console.WriteLine($"lcd written to {lcdPgm}");
        }

        if (flashOut is not null)
        {
            File.WriteAllBytes(flashOut, machine.Flash.Data);
            Console.WriteLine($"flash written to {flashOut}");
        }

        return 0;
    }

    private static void AppendRamPeek(byte[] ram, string register, uint address, ref string peek)
    {
        if (address is < 0x100000 or >= 0x180000)
        {
            return;
        }

        int offset = (int)(address - 0x100000);
        int count = Math.Min(16, ram.Length - offset);
        if (count <= 0)
        {
            return;
        }

        peek += $" {register}=[" + string.Join(' ', Enumerable.Range(0, count).Select(i => $"{ram[offset + i]:X2}")) + "]";
    }

    private static void FlushLcdLog(Pcd8544 lcd, long step, int limit, ref LcdSnapshot? previous, ref int frames, ref int suppressed)
    {
        if (previous is not null &&
            previous.DisplayMode == lcd.DisplayMode &&
            previous.PowerDown == lcd.PowerDown &&
            lcd.Vram.SequenceEqual(previous.Vram))
        {
            return;
        }

        if (frames >= limit)
        {
            suppressed++;
            previous = new LcdSnapshot(lcd.Vram.ToArray(), lcd.DisplayMode, lcd.PowerDown);
            return;
        }

        frames++;
        previous = new LcdSnapshot(lcd.Vram.ToArray(), lcd.DisplayMode, lcd.PowerDown);
        Console.WriteLine();
        Console.WriteLine($"lcd frame #{frames} @step {step:N0} data={lcd.DataWrites:N0} mode={lcd.DisplayMode} power-down={lcd.PowerDown} hash={ComputeLcdHash(lcd):X8}");
        PrintLcd(lcd);
    }

    private static uint ComputeLcdHash(Pcd8544 lcd)
    {
        unchecked
        {
            uint hash = 2166136261;

            foreach (byte value in lcd.Vram)
            {
                hash = (hash ^ value) * 16777619;
            }

            hash = (hash ^ (byte)lcd.DisplayMode) * 16777619;
            hash = (hash ^ (lcd.PowerDown ? (byte)1 : (byte)0)) * 16777619;
            return hash;
        }
    }

    private static void PrintLcd(Pcd8544 lcd)
    {
        ReadOnlySpan<int> dotBits = [0, 1, 2, 6, 3, 4, 5, 7];
        int columns = Pcd8544.Width / 2;

        Console.WriteLine("+" + new string('-', columns) + "+");

        for (int cy = 0; cy < Pcd8544.Height; cy += 4)
        {
            char[] line = new char[columns];

            for (int cx = 0; cx < Pcd8544.Width; cx += 2)
            {
                int bits = 0;

                for (int dot = 0; dot < 8; dot++)
                {
                    int px = cx + dot / 4;
                    int py = cy + dot % 4;

                    if (lcd.GetPixel(px, py))
                    {
                        bits |= 1 << dotBits[dot];
                    }
                }

                line[cx / 2] = (char)(0x2800 + bits);
            }

            Console.WriteLine("|" + new string(line) + "|");
        }

        Console.WriteLine("+" + new string('-', columns) + "+");
    }

    private static void WritePgm(Pcd8544 lcd, string path)
    {
        using StreamWriter writer = new(path);
        writer.WriteLine("P2");
        writer.WriteLine($"{Pcd8544.Width} {Pcd8544.Height}");
        writer.WriteLine("255");

        for (int y = 0; y < Pcd8544.Height; y++)
        {
            for (int x = 0; x < Pcd8544.Width; x++)
            {
                writer.Write(lcd.GetPixel(x, y) ? "0 " : "255 ");
            }

            writer.WriteLine();
        }
    }

    private static void AddKeyEvents(List<ScheduledKeyEvent> events, string spec)
    {
        string[] at = spec.Split('@', 2);

        if (at.Length != 2)
        {
            throw new ArgumentException("The --key value is invalid. Use name@step[:hold].");
        }

        string[] timing = at[1].Split(':', 2);

        if (!long.TryParse(timing[0], out long step) || step < 0)
        {
            throw new ArgumentException("The --key step is invalid. Use a nonnegative number.");
        }

        long hold = 1_000_000;

        if (timing.Length > 1 && (!long.TryParse(timing[1], out hold) || hold <= 0))
        {
            throw new ArgumentException("The --key hold value is invalid. Use a positive number.");
        }

        if (step > long.MaxValue - hold)
        {
            throw new ArgumentException("The sum of the --key step and hold values is too large.");
        }

        KeyBinding binding = ParseKeyBinding(at[0]);
        events.Add(new ScheduledKeyEvent(step, at[0], binding, true));
        events.Add(new ScheduledKeyEvent(step + hold, at[0], binding, false));
    }

    private static void AddAdcEvent(List<ScheduledAdcEvent> events, string spec)
    {
        string[] at = spec.Split('@', 2);

        if (at.Length != 2)
        {
            throw new ArgumentException("The --adc value is invalid. Use name@step:value.");
        }

        string[] timing = at[1].Split(':', 2);

        if (timing.Length != 2 || !long.TryParse(timing[0], out long step) || step < 0)
        {
            throw new ArgumentException("The --adc step is invalid. Use a nonnegative number.");
        }

        ushort value = ParseAdcValue(timing[1]);
        int channel = ParseAdcChannel(at[0]);
        events.Add(new ScheduledAdcEvent(step, at[0], channel, value));
    }

    private static void AddDspRssiEvent(List<ScheduledDspRssiEvent> events, string spec)
    {
        string[] timing = spec.Split(':', 2);

        if (timing.Length != 2 || !long.TryParse(timing[0], out long step) || step < 0)
        {
            throw new ArgumentException("The --dsp-rssi value is invalid. Use step:value.");
        }

        byte value = ParseDspRssiValue(timing[1]);
        events.Add(new ScheduledDspRssiEvent(step, value));
    }

    private static void AddIncomingCallEvent(List<ScheduledIncomingGsmEvent> events, string spec)
    {
        string[] parts = spec.Split(':', 2);

        if (!long.TryParse(parts[0], out long step) || step < 0)
        {
            throw new ArgumentException("The --incoming-call value is invalid. Use step[:number].");
        }

        string number = parts.Length > 1 ? parts[1] : "";
        events.Add(new ScheduledIncomingGsmEvent(step, IncomingGsmEventKind.Call, number, ""));
    }

    private static void AddIncomingSmsEvent(List<ScheduledIncomingGsmEvent> events, string spec)
    {
        string[] parts = spec.Split(':', 3);

        if (!long.TryParse(parts[0], out long step) || step < 0)
        {
            throw new ArgumentException("The --incoming-sms value is invalid. Use step[:originator[:text]].");
        }

        string originator = parts.Length > 1 ? parts[1] : "";
        string text = parts.Length > 2 ? parts[2] : "";
        events.Add(new ScheduledIncomingGsmEvent(step, IncomingGsmEventKind.Sms, originator, text));
    }

    private static void AddIncomingRingtoneEvent(List<ScheduledIncomingGsmEvent> events, string spec)
    {
        string[] parts = spec.Split(':', 2);
        if (!long.TryParse(parts[0], out long step) || step < 0)
        {
            throw new ArgumentException("The --incoming-ringtone value is invalid. Use step[:originator].");
        }

        string originator = parts.Length > 1 ? parts[1] : "";
        events.Add(new ScheduledIncomingGsmEvent(step, IncomingGsmEventKind.Ringtone, originator, ""));
    }

    private static int ParseAdcChannel(string name)
    {
        string key = name.ToLowerInvariant();

        if (int.TryParse(key, out int channel))
        {
            if (channel is < 0 or > 7)
            {
                throw new ArgumentException("The raw --adc channel is invalid. Use a value from 0 through 7.");
            }

            return channel;
        }

        return key switch
        {
            "acc" or "accessory" => 0,
            "rssi" => 1,
            "vbat" or "battery" => 2,
            "bsi" or "type" => 3,
            "btemp" or "temp" => 4,
            "vchg" or "charger" => 5,
            "vcxo" => 6,
            "ichg" or "current" => 7,
            _ => throw new ArgumentException($"Unknown ADC channel: '{name}'."),
        };
    }

    private static ushort ParseAdcValue(string value)
    {
        int parsed = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt32(value[2..], 16)
            : Convert.ToInt32(value, 16);

        if (parsed is < 0 or > 0x3FF)
        {
            throw new ArgumentException("The --adc value is invalid. Use a 10-bit hexadecimal value.");
        }

        return (ushort)parsed;
    }

    private static byte ParseDspRssiValue(string value)
    {
        int parsed = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt32(value[2..], 16)
            : Convert.ToInt32(value, 16);

        if (parsed is < 0 or > Dsp.DefaultRssiMeasurement)
        {
            throw new ArgumentException($"The --dsp-rssi value is invalid. Use a hexadecimal byte from 00 through {Dsp.DefaultRssiMeasurement:X2}.");
        }

        return (byte)parsed;
    }

    private static void SetAdcInput(CcontAdcInputs inputs, int channel, ushort value)
    {
        inputs.Set(channel, value);
    }

    private static string DisplayIncomingValue(string value)
        => string.IsNullOrWhiteSpace(value) ? "default" : value;

    private static KeyBinding ParseKeyBinding(string name)
    {
        string key = name.ToLowerInvariant();

        if (key.Contains('.'))
        {
            string[] parts = key.Split('.', 2);

            if (!int.TryParse(parts[0], out int column) || !int.TryParse(parts[1], out int bit) || column is < 0 or > 4 || bit is < 0 or > 7)
            {
                throw new ArgumentException("The raw --key matrix value is invalid. Use column.bit with column 0-4 and bit 0-7.");
            }

            return new KeyBinding(column, bit, false);
        }

        return key switch
        {
            "up" => new KeyBinding(0, 1, false),
            "0" => new KeyBinding(0, 2, false),
            "c" or "clear" or "back" or "del" => new KeyBinding(0, 4, false),
            "down" => new KeyBinding(1, 1, false),
            "2" => new KeyBinding(1, 3, false),
            "1" => new KeyBinding(1, 4, false),
            "6" => new KeyBinding(2, 2, false),
            "5" => new KeyBinding(2, 3, false),
            "4" => new KeyBinding(2, 4, false),
            "9" => new KeyBinding(3, 2, false),
            "8" => new KeyBinding(3, 3, false),
            "7" => new KeyBinding(3, 4, false),
            "3" => new KeyBinding(4, 1, false),
            "#" or "hash" or "pound" => new KeyBinding(4, 2, false),
            "menu" or "navi" or "action1" or "enter" => new KeyBinding(4, 3, false),
            "*" or "star" or "asterisk" => new KeyBinding(4, 4, false),
            "power" => new KeyBinding(0, 0, true),
            _ => throw new ArgumentException($"Unknown key: '{name}'."),
        };
    }

    private readonly record struct KeyBinding(int Column, int Bit, bool Power);

    private readonly record struct ScheduledKeyEvent(long Step, string Name, KeyBinding Binding, bool Pressed);

    private readonly record struct ScheduledAdcEvent(long Step, string Name, int Channel, ushort Value);

    private readonly record struct ScheduledDspRssiEvent(long Step, byte Value);

    private readonly record struct ScheduledIncomingGsmEvent(long Step, IncomingGsmEventKind Kind, string Address, string Text);

    private enum IncomingGsmEventKind
    {
        Call,
        Sms,
        Ringtone,
    }

    private sealed record LcdSnapshot(byte[] Vram, int DisplayMode, bool PowerDown);
}
