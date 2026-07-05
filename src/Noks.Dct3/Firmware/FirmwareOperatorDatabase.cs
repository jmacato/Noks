using System.Text;

namespace Noks.Dct3.Firmware;

public static class FirmwareOperatorDatabase
{
    private static ReadOnlySpan<byte> TableMarker => "PLMNV9.00"u8;
    private static ReadOnlySpan<byte> CountryMarker => "CNTR"u8;

    public static IReadOnlyList<FirmwareMobileOperator> Parse(ReadOnlySpan<byte> firmware)
    {
        int tableOffset = firmware.IndexOf(TableMarker);
        if (tableOffset < 0)
        {
            return [];
        }

        List<FirmwareMobileOperator> operators = [];
        int cursor = tableOffset + TableMarker.Length;
        int tableEnd = Math.Min(firmware.Length, tableOffset + 0x10000);

        while (cursor + 16 <= tableEnd)
        {
            int relativeCountryOffset = firmware[cursor..tableEnd].IndexOf(CountryMarker);
            if (relativeCountryOffset < 0)
            {
                break;
            }

            int countryOffset = cursor + relativeCountryOffset;
            if (countryOffset + 16 > tableEnd ||
                !TryDecodeMcc(firmware[countryOffset + 12], firmware[countryOffset + 13], out string mcc))
            {
                cursor = countryOffset + CountryMarker.Length;
                continue;
            }

            string countryTag = Encoding.ASCII
                .GetString(firmware.Slice(countryOffset + 8, 3))
                .TrimEnd('\0', ' ');
            int operatorCount = firmware[countryOffset + 14];
            int entryOffset = countryOffset + 16;

            for (int i = 0; i < operatorCount; i++)
            {
                if (entryOffset + 4 > tableEnd)
                {
                    return operators;
                }

                int nameLength = firmware[entryOffset + 3];
                int paddedNameLength = (nameLength + 3) & ~3;
                if (nameLength is <= 0 or > 32 || entryOffset + 4 + paddedNameLength > tableEnd ||
                    !TryDecodeMnc(firmware[entryOffset + 1], out string mnc))
                {
                    break;
                }

                string name = Encoding.ASCII
                    .GetString(firmware.Slice(entryOffset + 4, nameLength))
                    .TrimEnd('\0', ' ');
                if (name.Length > 0 && name.All(ch => ch is >= ' ' and <= '~'))
                {
                    operators.Add(new FirmwareMobileOperator(countryTag, mcc, mnc, name));
                }

                entryOffset += 4 + paddedNameLength;
            }

            cursor = Math.Max(entryOffset, countryOffset + CountryMarker.Length);
        }

        return operators;
    }

    private static bool TryDecodeMcc(byte first, byte second, out string mcc)
    {
        int digit1 = first & 0x0F;
        int digit2 = first >> 4;
        int digit3 = second & 0x0F;
        if (digit1 > 9 || digit2 > 9 || digit3 > 9)
        {
            mcc = "";
            return false;
        }

        mcc = string.Create(3, (digit1, digit2, digit3), static (chars, digits) =>
        {
            chars[0] = (char)('0' + digits.digit1);
            chars[1] = (char)('0' + digits.digit2);
            chars[2] = (char)('0' + digits.digit3);
        });
        return true;
    }

    private static bool TryDecodeMnc(byte value, out string mnc)
    {
        int digit1 = value & 0x0F;
        int digit2 = value >> 4;
        if (digit1 > 9 || digit2 > 9)
        {
            mnc = "";
            return false;
        }

        mnc = string.Create(2, (digit1, digit2), static (chars, digits) =>
        {
            chars[0] = (char)('0' + digits.digit1);
            chars[1] = (char)('0' + digits.digit2);
        });
        return true;
    }
}
