using Noks.Dct3.Core;
namespace Noks.Dct3.Display;

public static class Dct3UpdateImageConverter
{
    public const uint FlashBase = Dct3Machine.FlashBase;
    public const uint FlashEnd = 0x400000;
    public const int FlashSize = (int)(FlashEnd - FlashBase);

    public static byte[] Convert(IEnumerable<Dct3UpdateImagePart> parts, out Dct3UpdateImagePartSummary[] summaries)
    {
        ArgumentNullException.ThrowIfNull(parts);

        byte[] flash = new byte[FlashSize];
        Array.Fill(flash, (byte)0xFF);
        List<Dct3UpdateImagePartSummary> summaryList = [];

        foreach (Dct3UpdateImagePart part in parts)
        {
            if (part.Data is null)
            {
                throw new ArgumentException($"{part.Name}: The part data is null.", nameof(parts));
            }

            summaryList.Add(ConvertPart(flash, part.Name, part.Data));
        }

        summaries = summaryList.ToArray();
        return flash;
    }

    public static bool TryGetFirstRecordAddress(ReadOnlySpan<byte> data, out uint address)
    {
        address = 0;
        if (data.Length < 9 || data[0] != 0x0B)
        {
            return false;
        }

        address = ReadAddress(data);
        return true;
    }

    private static Dct3UpdateImagePartSummary ConvertPart(byte[] flash, string name, ReadOnlySpan<byte> data)
    {
        int pos = 0;
        int records = 0;
        uint startAddress = uint.MaxValue;
        uint endAddress = 0;

        while (pos < data.Length)
        {
            if (pos + 9 > data.Length)
            {
                throw new InvalidDataException($"{name}: The record header at 0x{pos:X} is truncated.");
            }

            ReadOnlySpan<byte> header = data.Slice(pos, 9);
            if (header[0] != 0x0B)
            {
                throw new InvalidDataException($"{name}: Record marker 0x{header[0]:X2} at 0x{pos:X} is invalid.");
            }

            uint address = ReadAddress(header);
            int length = ReadLength(header);
            int payloadStart = pos + 9;
            int payloadEnd = payloadStart + length;

            if (payloadEnd < payloadStart || payloadEnd > data.Length)
            {
                throw new InvalidDataException($"{name}: The record at 0x{pos:X} exceeds the file with length 0x{length:X}.");
            }

            ulong writeEnd = (ulong)address + (uint)length;
            if (address < FlashBase || writeEnd > FlashEnd)
            {
                throw new InvalidDataException($"{name}: The record at 0x{pos:X} writes outside flash: 0x{address:X6}-0x{writeEnd:X6}.");
            }

            int flashOffset = (int)(address - FlashBase);
            data.Slice(payloadStart, length).CopyTo(flash.AsSpan(flashOffset, length));
            records++;
            startAddress = Math.Min(startAddress, address);
            endAddress = Math.Max(endAddress, (uint)writeEnd);
            pos = payloadEnd;
        }

        if (records == 0)
        {
            throw new InvalidDataException($"{name}: No records were found.");
        }

        return new Dct3UpdateImagePartSummary(name, records, startAddress, endAddress);
    }

    private static uint ReadAddress(ReadOnlySpan<byte> header) =>
        ((uint)header[1] << 16) | ((uint)header[2] << 8) | header[3];

    private static int ReadLength(ReadOnlySpan<byte> header) =>
        (header[5] << 16) | (header[6] << 8) | header[7];
}
