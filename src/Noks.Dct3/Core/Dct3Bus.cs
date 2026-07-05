using System.Buffers.Binary;
using Noks.Cpu;
using Noks.Dct3.Memory;
using Noks.Dct3.Peripherals;
using Noks.Dct3.Radio;

namespace Noks.Dct3.Core;

public sealed class Dct3Bus : IArm7Bus
{
    private readonly byte[] ram = new byte[0x80000];
    private readonly byte[] dspRam = new byte[0x1000];
    private readonly Mad2Io io;
    private readonly IntelFlash16 flash;
    private readonly IDct3Trace? trace;
    private const uint LegacyRfControlAddress = 0x600000;
    private const uint LegacyRfDataAddress = 0x600100;
    private const byte LegacyRfIdleData = 0x00;
    private const int LegacyRfMaxTracePayloadBytes = 24;
    private const int LegacyRfDecodedPayloadBytes = 12;
    private byte legacyRfControl;
    private byte legacyRfData;
    private long legacyRfAccesses;
    private long legacyRfCommand26Writes;
    private byte legacyRfActiveCommand;
    private bool legacyRfHasActiveCommand;
    private int legacyRfCommandSequence;
    private int legacyRfCommandPayloadLength;
    private int legacyRfCommandPayloadLoggedLength;
    private int legacyRfCommandDataReadCount;
    private int legacyRfCommandControlReadCount;
    private readonly byte[] legacyRfCommandPayload = new byte[LegacyRfMaxTracePayloadBytes];

    public Dct3Bus(Mad2Io io, IntelFlash16 flash, IDct3Trace? trace)
    {
        this.io = io;
        this.flash = flash;
        this.trace = trace;
    }

    public long Cycles { get; private set; }

    public Func<uint>? PcProbe { get; set; }

    public Func<uint>? LrProbe { get; set; }

    public uint WatchLow { get; set; } = 1;

    public uint WatchHigh { get; set; }

    public bool WatchReads { get; set; }

    public int WatchLimit { get; set; } = 200;

    private int watchHits;

    public byte[] Ram => ram;

    public byte[] DspSharedRam => dspRam;

    public Dsp? Dsp { get; set; }

    public Action<uint, uint, int>? DspSharedWrite { get; set; }

    public Action? DspHostInterrupt { get; set; }

    public void AdvanceTo(long cycles)
    {
        if (cycles > Cycles)
        {
            Cycles = cycles;
        }
    }

    public uint ReadWord(uint address, ArmAccess access) => Read(address & ~3u, 4, access);

    public uint ReadHalf(uint address, ArmAccess access) => Read(address & ~1u, 2, access);

    public uint ReadByte(uint address, ArmAccess access) => Read(address, 1, access);

    public void WriteWord(uint address, uint value, ArmAccess access) => Write(address & ~3u, value, 4);

    public void WriteHalf(uint address, ushort value, ArmAccess access) => Write(address & ~1u, value, 2);

    public void WriteByte(uint address, byte value, ArmAccess access) => Write(address, value, 1);

    public void Idle() => Cycles++;

    private uint Read(uint address, int size, ArmAccess access)
    {
        Cycles++;
        uint a = address & 0xFFFFFF;

        if (a < 0x100000)
        {
            uint w = a & ~0x80000u;

            switch (w >> 16)
            {
                case 0:
                    uint ramValue = ReadBackingBe(ram, w & 0xFFFF, size);
                    LogWatchRead(a, size, ramValue);
                    return ramValue;
                case 1:
                    uint dspValue = ReadBackingBe(dspRam, size == 1 ? w & 0xFFF : w & 0xFFE, size);
                    LogWatchRead(a, size, dspValue);
                    trace?.DspRam(false, w & 0xFFF, dspValue);
                    return dspValue;
                case 2:
                    uint ioValue = ReadIo(a & 0xFF, size);
                    LogWatchRead(a, size, ioValue);
                    return ioValue;
                case 3:
                    trace?.InterfaceAccess("DSPIF", false, a & 3, 0);
                    return 0;
                case 4:
                    trace?.InterfaceAccess("MCUIF", false, a & 3, 0);
                    return 0;
                default:
                    trace?.Unmapped(false, a, 0, size);
                    return 0;
            }
        }

        if (a < 0x180000)
        {
            uint ramValue = ReadBackingBe(ram, a - 0x100000, size);
            LogWatchRead(a, size, ramValue);
            return ramValue;
        }

        if (a is >= 0x200000 and < 0x600000)
        {
            uint offset = (a - 0x200000) & 0x1FFFFF;

            if (flash.InArrayMode)
            {
                return ReadBackingBe(flash.Data, offset, size);
            }

            return size switch
            {
                4 => (uint)((flash.ReadDevice(offset) << 16) | flash.ReadDevice(offset + 2)),
                2 => flash.ReadDevice(offset),
                _ => (offset & 1) == 0 ? (uint)(flash.ReadDevice(offset) >> 8) : (uint)(flash.ReadDevice(offset) & 0xFF),
            };
        }

        if (TryReadLegacyRfLatch(a, size, out uint legacyRfValue))
        {
            return legacyRfValue;
        }

        trace?.Unmapped(false, a, 0, size);
        return 0;
    }

    private void Write(uint address, uint value, int size)
    {
        Cycles++;
        uint a = address & 0xFFFFFF;

        if (a >= WatchLow && a < WatchHigh && watchHits < WatchLimit)
        {
            watchHits++;
            trace?.Event($"watch w{size} {a:X6}={value:X8} {ProbeState()}");
        }

        if (a < 0x100000)
        {
            uint w = a & ~0x80000u;

            switch (w >> 16)
            {
                case 0:
                    WriteBackingBe(ram, w & 0xFFFF, value, size);
                    return;
                case 1:
                    WriteBackingBe(dspRam, size == 1 ? w & 0xFFF : w & 0xFFE, value, size);
                    trace?.DspRam(true, w & 0xFFF, value);
                    if (DspSharedWrite is not null)
                    {
                        DspSharedWrite(w & 0xFFF, value, size);
                    }
                    else
                    {
                        Dsp?.OnSharedWrite(w & 0xFFF, value, size);
                    }

                    return;
                case 2:
                    WriteIo(a & 0xFF, value, size);
                    return;
                case 3:
                    trace?.InterfaceAccess("DSPIF", true, a & 3, value);

                    if ((a & 3) == 0 && (value & 0x04) != 0)
                    {
                        if (DspHostInterrupt is not null)
                        {
                            DspHostInterrupt();
                        }
                        else
                        {
                            Dsp?.OnHostInterrupt();
                        }
                    }

                    return;
                case 4:
                    trace?.InterfaceAccess("MCUIF", true, a & 3, value);
                    return;
                default:
                    trace?.Unmapped(true, a, value, size);
                    return;
            }
        }

        if (a < 0x180000)
        {
            WriteBackingBe(ram, a - 0x100000, value, size);
            return;
        }

        if (a is >= 0x200000 and < 0x600000)
        {
            uint offset = (a - 0x200000) & 0x1FFFFF;

            switch (size)
            {
                case 4:
                    flash.WriteDevice(offset, (ushort)(value >> 16));
                    flash.WriteDevice(offset + 2, (ushort)value);
                    break;
                default:
                    flash.WriteDevice(offset, (ushort)value);
                    break;
            }

            return;
        }

        if (TryWriteLegacyRfLatch(a, value, size))
        {
            return;
        }

        trace?.Unmapped(true, a, value, size);
    }

    public void ResetWatchHits()
    {
        watchHits = 0;
    }

    public bool HasReadWatchAt(uint address) =>
        WatchReads && watchHits < WatchLimit && address >= WatchLow && address < WatchHigh;

    private static uint ReadBackingBe(byte[] backing, uint offset, int size)
    {
        return size switch
        {
            4 => BinaryPrimitives.ReadUInt32BigEndian(backing.AsSpan((int)offset, 4)),
            2 => BinaryPrimitives.ReadUInt16BigEndian(backing.AsSpan((int)offset, 2)),
            _ => backing[offset],
        };
    }

    private static void WriteBackingBe(byte[] backing, uint offset, uint value, int size)
    {
        switch (size)
        {
            case 4:
                BinaryPrimitives.WriteUInt32BigEndian(backing.AsSpan((int)offset, 4), value);
                break;
            case 2:
                BinaryPrimitives.WriteUInt16BigEndian(backing.AsSpan((int)offset, 2), (ushort)value);
                break;
            default:
                backing[offset] = (byte)value;
                break;
        }
    }

    private bool TryReadLegacyRfLatch(uint address, int size, out uint value)
    {
        value = 0;
        if (size != 1 || address is not (LegacyRfControlAddress or LegacyRfDataAddress))
        {
            return false;
        }

        value = address == LegacyRfControlAddress ? legacyRfControl : LegacyRfIdleData;
        TrackLegacyRfRead(address);
        TraceLegacyRfLatch(false, address, value);
        return true;
    }

    private bool TryWriteLegacyRfLatch(uint address, uint value, int size)
    {
        if (size != 1 || address is not (LegacyRfControlAddress or LegacyRfDataAddress))
        {
            return false;
        }

        byte byteValue = (byte)value;
        byte previousControl = legacyRfControl;
        byte previousData = legacyRfData;
        if (address == LegacyRfControlAddress)
        {
            StartLegacyRfCommand(byteValue);
            legacyRfControl = byteValue;
        }
        else
        {
            legacyRfData = byteValue;
            AppendLegacyRfCommandData(byteValue);
        }

        TraceLegacyRfLatch(true, address, byteValue);
        TraceLegacyRfCommand26(address, byteValue, previousControl, previousData);
        return true;
    }

    private void TraceLegacyRfLatch(bool write, uint address, uint value)
    {
        legacyRfAccesses++;
        uint offset = address - LegacyRfControlAddress;
        trace?.InterfaceAccess("LEGACY600", write, offset, value);

        if (ShouldTraceLegacyRfAccess(legacyRfAccesses))
        {
            trace?.Event(
                $"legacy service latch {(write ? 'w' : 'r')} {offset:X3}={value:X2} " +
                $"ctrl={legacyRfControl:X2} data={legacyRfData:X2} count={legacyRfAccesses} {ProbeState()}");
        }
    }

    private static bool ShouldTraceLegacyRfAccess(long count) =>
        count <= 16 || (count & (count - 1)) == 0;

    private void StartLegacyRfCommand(byte command)
    {
        FlushLegacyRfCommand();
        legacyRfHasActiveCommand = true;
        legacyRfActiveCommand = command;
        legacyRfCommandSequence++;
        legacyRfCommandPayloadLength = 0;
        legacyRfCommandPayloadLoggedLength = 0;
        legacyRfCommandDataReadCount = 0;
        legacyRfCommandControlReadCount = 0;
    }

    private void AppendLegacyRfCommandData(byte value)
    {
        if (!legacyRfHasActiveCommand)
        {
            return;
        }

        if (legacyRfCommandPayloadLoggedLength < legacyRfCommandPayload.Length)
        {
            legacyRfCommandPayload[legacyRfCommandPayloadLoggedLength++] = value;
        }

        legacyRfCommandPayloadLength++;
    }

    private void TrackLegacyRfRead(uint address)
    {
        if (!legacyRfHasActiveCommand)
        {
            return;
        }

        if (address == LegacyRfControlAddress)
        {
            legacyRfCommandControlReadCount++;
        }
        else
        {
            legacyRfCommandDataReadCount++;
        }
    }

    private void FlushLegacyRfCommand()
    {
        if (!legacyRfHasActiveCommand)
        {
            return;
        }

        if (ShouldTraceLegacyRfCommand(legacyRfCommandSequence, legacyRfActiveCommand, legacyRfCommandPayloadLength))
        {
            string payload = legacyRfCommandPayloadLoggedLength == 0
                ? "-"
                : Convert.ToHexString(legacyRfCommandPayload.AsSpan(0, legacyRfCommandPayloadLoggedLength));
            string truncated = legacyRfCommandPayloadLength > legacyRfCommandPayloadLoggedLength
                ? $"+{legacyRfCommandPayloadLength - legacyRfCommandPayloadLoggedLength}"
                : "";
            uint pc = PcProbe?.Invoke() ?? 0;
            uint lr = LrProbe?.Invoke() ?? 0;
            trace?.Event(
                $"legacy service cmd {legacyRfActiveCommand:X2} seq={legacyRfCommandSequence} " +
                $"payload[{legacyRfCommandPayloadLength}]={payload}{truncated} " +
                $"reads(c={legacyRfCommandControlReadCount},d={legacyRfCommandDataReadCount}) " +
                $"site={DescribeLegacyRfCallsite(pc, lr)} " +
                $"{DescribeLegacyRfPayloadSummary(legacyRfActiveCommand)} " +
                $"pc={pc:X6} lr={lr:X6}");
        }

        legacyRfHasActiveCommand = false;
    }

    private static bool ShouldTraceLegacyRfCommand(int sequence, byte command, int payloadLength)
    {
        if (sequence <= 16 || (sequence & (sequence - 1)) == 0)
        {
            return true;
        }

        return command is
            0x03 or 0x04 or 0x06 or 0x08 or 0x09 or 0x0A or 0x0B or
            0x10 or 0x19 or 0x1A or 0x1B or 0x80 ||
            payloadLength >= 8;
    }

    private void TraceLegacyRfCommand26(
        uint address,
        byte value,
        byte previousControl,
        byte previousData)
    {
        if (address != LegacyRfControlAddress || value != 0x26)
        {
            return;
        }

        legacyRfCommand26Writes++;
        if (!ShouldTraceLegacyRfAccess(legacyRfCommand26Writes))
        {
            return;
        }

        trace?.Event(
            $"legacy service cmd 26 data={legacyRfData:X2} prev-ctrl={previousControl:X2} " +
            $"prev-data={previousData:X2} count={legacyRfCommand26Writes} {ProbeState()}");
    }

    private string DescribeLegacyRfPayloadSummary(byte command)
    {
        if (legacyRfCommandPayloadLoggedLength == 0)
        {
            return "service=empty";
        }

        Span<byte> payload = legacyRfCommandPayload.AsSpan(0, Math.Min(legacyRfCommandPayloadLoggedLength, LegacyRfDecodedPayloadBytes));
        return command switch
        {
            0x04 => DescribeLegacyRfWordPayload(payload, "svc04"),
            0x08 => DescribeLegacyRfWordPayload(payload, "svc08"),
            0x0B => DescribeLegacyRfWordPayload(payload, "svc0B"),
            0x10 => DescribeLegacyRfWordPayload(payload, "svc10"),
            0x19 => DescribeLegacyRfWordPayload(payload, "svc19"),
            0x1A => DescribeLegacyRfWordPayload(payload, "svc1A"),
            0x1B => DescribeLegacyRfWordPayload(payload, "svc1B"),
            0x26 => DescribeLegacyRfWordPayload(payload, "svc26"),
            0x06 => DescribeLegacyRfWordPayload(payload, "svc06"),
            0x03 => DescribeLegacyRfWordPayload(payload, "svc03"),
            0x09 => DescribeLegacyRfWordPayload(payload, "svc09"),
            0x0A => DescribeLegacyRfWordPayload(payload, "svc0A"),
            0x80 => DescribeLegacyRfWordPayload(payload, "svc80"),
            _ => $"service=bytes{payload.Length}:{Convert.ToHexString(payload)}"
        };
    }

    private static string DescribeLegacyRfWordPayload(ReadOnlySpan<byte> payload, string tag)
    {
        Span<char> buffer = stackalloc char[160];
        int written = 0;

        written += WriteText(buffer[written..], tag);
        written += WriteText(buffer[written..], "=");
        for (int i = 0; i < payload.Length; i += 2)
        {
            if (i != 0)
            {
                written += WriteText(buffer[written..], "/");
            }

            ushort word = i + 1 < payload.Length
                ? BinaryPrimitives.ReadUInt16BigEndian(payload[i..(i + 2)])
                : (ushort)(payload[i] << 8);
            written += WriteHex4(buffer[written..], word);
        }

        return new string(buffer[..written]);
    }

    private static string DescribeLegacyRfCallsite(uint pc, uint lr)
    {
        uint site = pc != 0 ? pc : lr;
        return site switch
        {
            0x2697CC => "timer-schedule-2697cc",
            0x26999A => "timer-remaining-26999a",
            0x269A14 => "timer-expiry-269a14",
            0x26A3CA => "event-post-26a3ca",
            0x26AB46 => "event-worker-26ab46",
            0x26A410 => "event-post-commit-26a410",
            0x26AB92 => "event-worker-post-26ab92",
            _ => $"service-site-{site:X6}"
        };
    }

    private static int WriteText(Span<char> destination, string text)
    {
        text.AsSpan().CopyTo(destination);
        return text.Length;
    }

    private static int WriteHex4(Span<char> destination, ushort value)
    {
        value.TryFormat(destination, out int charsWritten, "X4");
        return charsWritten;
    }

    private void LogWatchRead(uint address, int size, uint value)
    {
        if (!WatchReads || watchHits >= WatchLimit || address < WatchLow || address >= WatchHigh)
        {
            return;
        }

        watchHits++;
        trace?.Event($"watch r{size} {address:X6}={value:X8} {ProbeState()}");
    }

    private string ProbeState() => $"pc={PcProbe?.Invoke() ?? 0:X6} lr={LrProbe?.Invoke() ?? 0:X6}";

    private uint ReadIo(uint offset, int size)
    {
        uint result = 0;

        for (int i = 0; i < size; i++)
        {
            result = (result << 8) | io.Read((offset + (uint)i) & 0xFF);
        }

        return result;
    }

    private void WriteIo(uint offset, uint value, int size)
    {
        for (int i = 0; i < size; i++)
        {
            io.Write((offset + (uint)i) & 0xFF, (byte)(value >> ((size - 1 - i) * 8)));
        }
    }
}
