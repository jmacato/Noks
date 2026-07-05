using System.Text;

namespace Noks.Dct3.Sim;

public static class SimPhonebookCodec
{
    public const int RecordLength = 30;
    public const int AlphaIdentifierLength = 16;
    public const int MaximumPhoneNumberDigits = 20;
    private const char Escape = '\u001B';
    private const string GsmDefaultAlphabet =
        "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞ\u001BÆæßÉ !\"#¤%&'()*+,-./" +
        "0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";

    public static byte[] Encode(string name, string phoneNumber)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(phoneNumber);

        string trimmedPhoneNumber = phoneNumber.Trim();
        bool international = trimmedPhoneNumber.StartsWith('+');
        if (trimmedPhoneNumber.IndexOf('+', international ? 1 : 0) >= 0)
        {
            throw new FormatException("A plus sign is valid only at the start of a phone number.");
        }

        string digits = NormalizeDigits(phoneNumber);
        if (digits.Length is < 1 or > MaximumPhoneNumberDigits)
        {
            throw new ArgumentOutOfRangeException(
                nameof(phoneNumber),
                $"A SIM phonebook number must contain 1-{MaximumPhoneNumberDigits} digits.");
        }

        if (!TryEncodeAlphaIdentifier(name, out byte[] alpha))
        {
            throw new ArgumentOutOfRangeException(
                nameof(name),
                $"A SIM phonebook name must contain 1-{AlphaIdentifierLength} bytes from the GSM default alphabet.");
        }

        byte[] record = new byte[RecordLength];
        Array.Fill(record, (byte)0xFF);
        alpha.CopyTo(record, 0);

        int bcdLength = (digits.Length + 1) / 2;
        record[16] = (byte)(bcdLength + 1);
        record[17] = international ? (byte)0x91 : (byte)0x81;
        for (int index = 0; index < digits.Length; index += 2)
        {
            int low = digits[index] - '0';
            int high = index + 1 < digits.Length ? digits[index + 1] - '0' : 0x0F;
            record[18 + (index / 2)] = (byte)(low | (high << 4));
        }

        return record;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> record,
        out string name,
        out string normalizedPhoneNumber)
    {
        name = "";
        normalizedPhoneNumber = "";
        if (record.Length != RecordLength || record[16] is 0 or 0xFF)
        {
            return false;
        }

        int alphaLength = record[..AlphaIdentifierLength].IndexOf((byte)0xFF);
        if (alphaLength < 0)
        {
            alphaLength = AlphaIdentifierLength;
        }

        if (!TryDecodeAlphaIdentifier(record[..alphaLength], out name))
            return false;

        int bcdLength = record[16] - 1;
        if (bcdLength is < 1 or > 10 || 18 + bcdLength > record.Length)
        {
            name = "";
            return false;
        }

        StringBuilder digits = new(bcdLength * 2 + 1);
        if ((record[17] & 0x70) == 0x10)
        {
            digits.Append('+');
        }

        for (int index = 0; index < bcdLength; index++)
        {
            byte value = record[18 + index];
            int low = value & 0x0F;
            int high = value >> 4;
            if (low > 9 || high > 9 && high != 0x0F)
            {
                name = "";
                normalizedPhoneNumber = "";
                return false;
            }

            digits.Append((char)('0' + low));
            if (high != 0x0F)
            {
                digits.Append((char)('0' + high));
            }
        }

        normalizedPhoneNumber = digits.ToString();
        return normalizedPhoneNumber.Length > (normalizedPhoneNumber[0] == '+' ? 1 : 0);
    }

    public static string NormalizeDigits(string phoneNumber)
    {
        ArgumentNullException.ThrowIfNull(phoneNumber);
        StringBuilder digits = new(phoneNumber.Length);
        foreach (char value in phoneNumber)
        {
            if (value is >= '0' and <= '9')
            {
                digits.Append(value);
            }
            else if (value is not ('+' or ' ' or '-' or '(' or ')' or '.'))
            {
                throw new FormatException("Phone numbers can contain only digits and common display separators.");
            }
        }

        return digits.ToString();
    }

    public static bool IsValidAlphaIdentifier(string? name) =>
        TryEncodeAlphaIdentifier(name, out _);

    public static string CreateAlphaIdentifierAlias(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A phonebook alias cannot be empty.", nameof(name));

        StringBuilder projected = new();
        foreach (Rune rune in name.EnumerateRunes())
        {
            char value = rune.IsBmp && !char.IsControl((char)rune.Value) &&
                         TryGetGsmBytes((char)rune.Value, out _, out _)
                ? (char)rune.Value
                : '_';
            projected.Append(value);
        }

        string candidate = projected.ToString();
        if (TryEncodeAlphaIdentifier(candidate, out _))
            return candidate;

        StringBuilder shortened = new();
        int byteCount = 0;
        foreach (char value in candidate)
        {
            _ = TryGetGsmBytes(value, out _, out int encodedLength);
            if (byteCount + encodedLength > AlphaIdentifierLength - 3)
                break;
            shortened.Append(value);
            byteCount += encodedLength;
        }
        shortened.Append("...");
        return shortened.ToString();
    }

    public static bool TryEncodeAlphaIdentifier(string? name, out byte[] encoded)
    {
        encoded = [];
        if (string.IsNullOrWhiteSpace(name))
            return false;

        List<byte> bytes = new(name.Length);
        foreach (char value in name)
        {
            if (char.IsControl(value) || !TryGetGsmBytes(value, out ushort packed, out int encodedLength) ||
                bytes.Count + encodedLength > AlphaIdentifierLength)
            {
                return false;
            }
            if (encodedLength == 2)
                bytes.Add((byte)(packed >> 8));
            bytes.Add((byte)packed);
        }
        encoded = bytes.ToArray();
        return true;
    }

    private static bool TryDecodeAlphaIdentifier(ReadOnlySpan<byte> encoded, out string value)
    {
        StringBuilder decoded = new(encoded.Length);
        for (int index = 0; index < encoded.Length; index++)
        {
            byte current = encoded[index];
            if (current == Escape)
            {
                if (++index >= encoded.Length || !TryDecodeExtension(encoded[index], out char extension))
                {
                    value = "";
                    return false;
                }
                decoded.Append(extension);
                continue;
            }
            if (current >= GsmDefaultAlphabet.Length)
            {
                value = "";
                return false;
            }
            decoded.Append(GsmDefaultAlphabet[current]);
        }
        value = decoded.ToString().TrimEnd(' ');
        return value.Length > 0;
    }

    private static bool TryGetGsmBytes(char value, out ushort packed, out int encodedLength)
    {
        int baseIndex = GsmDefaultAlphabet.IndexOf(value, StringComparison.Ordinal);
        if (baseIndex >= 0 && baseIndex != Escape)
        {
            packed = (byte)baseIndex;
            encodedLength = 1;
            return true;
        }
        byte extension = value switch
        {
            '\f' => 0x0A,
            '^' => 0x14,
            '{' => 0x28,
            '}' => 0x29,
            '\\' => 0x2F,
            '[' => 0x3C,
            '~' => 0x3D,
            ']' => 0x3E,
            '|' => 0x40,
            '€' => 0x65,
            _ => byte.MaxValue,
        };
        if (extension == byte.MaxValue)
        {
            packed = 0;
            encodedLength = 0;
            return false;
        }
        packed = (ushort)(Escape << 8 | extension);
        encodedLength = 2;
        return true;
    }

    private static bool TryDecodeExtension(byte value, out char decoded)
    {
        decoded = value switch
        {
            0x0A => '\f',
            0x14 => '^',
            0x28 => '{',
            0x29 => '}',
            0x2F => '\\',
            0x3C => '[',
            0x3D => '~',
            0x3E => ']',
            0x40 => '|',
            0x65 => '€',
            _ => '\0',
        };
        return decoded != '\0';
    }
}
