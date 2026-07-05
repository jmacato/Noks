using Noks.Dct3.Audio;
using Noks.Dct3.Messaging;
using Noks.Dct3.Sim;
using Noks.Dct3.State;

namespace Noks.Dct3.Radio;

internal static class GsmAlphabet
{
    internal static string DecodeGsm7(ReadOnlySpan<byte> packed, int septetCount)
    {
        System.Text.StringBuilder text = new(septetCount);
        bool extension = false;
        for (int index = 0; index < septetCount; index++)
        {
            int bitOffset = index * 7;
            int byteOffset = bitOffset / 8;
            int shift = bitOffset % 8;
            int septet = packed[byteOffset] >> shift;
            if (shift > 1 && byteOffset + 1 < packed.Length)
            {
                septet |= packed[byteOffset + 1] << (8 - shift);
            }

            septet &= 0x7F;
            if (extension)
            {
                text.Append(septet switch
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
                    _ => '\uFFFD',
                });
                extension = false;
            }
            else if (septet == 0x1B)
            {
                extension = true;
            }
            else
            {
                text.Append(DecodeGsmDefaultAlphabetCharacter(septet));
            }
        }

        if (extension)
        {
            text.Append('\uFFFD');
        }

        return text.ToString();
    }

    internal static char DecodeGsmDefaultAlphabetCharacter(int septet) => septet switch
    {
        0x00 => '@',
        0x01 => '£',
        0x02 => '$',
        0x03 => '¥',
        0x04 => 'è',
        0x05 => 'é',
        0x06 => 'ù',
        0x07 => 'ì',
        0x08 => 'ò',
        0x09 => 'Ç',
        0x0A => '\n',
        0x0B => 'Ø',
        0x0C => 'ø',
        0x0D => '\r',
        0x0E => 'Å',
        0x0F => 'å',
        0x10 => 'Δ',
        0x11 => '_',
        0x12 => 'Φ',
        0x13 => 'Γ',
        0x14 => 'Λ',
        0x15 => 'Ω',
        0x16 => 'Π',
        0x17 => 'Ψ',
        0x18 => 'Σ',
        0x19 => 'Θ',
        0x1A => 'Ξ',
        0x1C => 'Æ',
        0x1D => 'æ',
        0x1E => 'ß',
        0x1F => 'É',
        0x24 => '¤',
        0x40 => '¡',
        0x5B => 'Ä',
        0x5C => 'Ö',
        0x5D => 'Ñ',
        0x5E => 'Ü',
        0x5F => '§',
        0x60 => '¿',
        0x7B => 'ä',
        0x7C => 'ö',
        0x7D => 'ñ',
        0x7E => 'ü',
        0x7F => 'à',
        _ => (char)septet,
    };

    internal static byte[] PackGsm7(string text, out byte septetCount)
    {
        septetCount = (byte)Math.Min(text.Length, GsmProtocol.MaximumIncomingSmsTextSeptets);
        byte[] packed = new byte[(septetCount * 7 + 7) / 8];

        for (int index = 0; index < septetCount; index++)
        {
            byte septet = ToGsmDefaultAlphabetSeptet(text[index]);
            int bitOffset = index * 7;
            int byteOffset = bitOffset / 8;
            int shift = bitOffset % 8;
            packed[byteOffset] |= (byte)(septet << shift);

            if (shift > 1 && byteOffset + 1 < packed.Length)
            {
                packed[byteOffset + 1] |= (byte)(septet >> (8 - shift));
            }
        }

        return packed;
    }

    internal static byte ToGsmDefaultAlphabetSeptet(char value) =>
        value is >= ' ' and <= '~' ? (byte)(value & 0x7F) : (byte)0x20;

    internal static byte[] EncodeSemiOctets(string digits)
    {
        byte[] encoded = new byte[(digits.Length + 1) / 2];

        for (int index = 0; index < digits.Length; index++)
        {
            int nibble = digits[index] - '0';
            if ((index & 1) == 0)
            {
                encoded[index / 2] = (byte)nibble;
            }
            else
            {
                encoded[index / 2] |= (byte)(nibble << 4);
            }
        }

        if ((digits.Length & 1) != 0)
        {
            encoded[^1] |= 0xF0;
        }

        return encoded;
    }

    internal static bool TryDecodeSemiOctets(
        ReadOnlySpan<byte> encoded,
        int? digitCount,
        out string digits)
    {
        int expectedDigits = digitCount ?? encoded.Length * 2;
        Span<char> decoded = stackalloc char[Math.Min(expectedDigits, 20)];
        int count = 0;
        foreach (byte value in encoded)
        {
            int low = value & 0x0F;
            int high = value >> 4;
            if (low > 9 || count >= decoded.Length)
            {
                digits = "";
                return false;
            }

            decoded[count++] = (char)('0' + low);
            if (count >= expectedDigits || high == 0x0F && digitCount is null)
            {
                break;
            }

            if (high > 9 || count >= decoded.Length)
            {
                digits = "";
                return false;
            }

            decoded[count++] = (char)('0' + high);
        }

        if (count != expectedDigits && digitCount is not null || count == 0)
        {
            digits = "";
            return false;
        }

        digits = new string(decoded[..count]);
        return true;
    }

    internal static byte[] BuildBcdNumberContents(string digits, bool international = false)
    {
        byte[] semiOctets = EncodeSemiOctets(SanitizeDialableAddress(digits));
        byte[] contents = new byte[1 + semiOctets.Length];
        contents[0] = international ? (byte)0x91 : (byte)0x81;
        semiOctets.CopyTo(contents.AsSpan(1));
        return contents;
    }

    internal static byte EncodeTimestampSemiOctet(int value)
    {
        value = Math.Clamp(value, 0, 99);
        return (byte)(value / 10 | (value % 10 << 4));
    }

    internal static byte[] BuildTimestampAndTimeZone(DateTimeOffset localTime) =>
    [
        EncodeTimestampSemiOctet(localTime.Year % 100),
        EncodeTimestampSemiOctet(localTime.Month),
        EncodeTimestampSemiOctet(localTime.Day),
        EncodeTimestampSemiOctet(localTime.Hour),
        EncodeTimestampSemiOctet(localTime.Minute),
        EncodeTimestampSemiOctet(localTime.Second),
        EncodeTimeZone(localTime.Offset),
    ];

    internal static byte EncodeTimeZone(TimeSpan offset)
    {
        int quarters = (int)Math.Round(offset.TotalMinutes / 15, MidpointRounding.AwayFromZero);
        quarters = Math.Clamp(quarters, -99, 99);
        byte encoded = EncodeTimestampSemiOctet(Math.Abs(quarters));

        if (quarters < 0)
        {
            encoded |= 0x08;
        }

        return encoded;
    }


    internal static string SanitizeDialableAddress(string value)
    {
        string digits = new(value.Where(char.IsDigit).Take(20).ToArray());
        return digits.Length == 0 ? GsmProtocol.DefaultIncomingAddress : digits;
    }


    internal static string SanitizeSmsText(string value)
    {
        string text = new(value.Where(ch => ch is >= ' ' and <= '~').Take(GsmProtocol.MaximumIncomingSmsTextSeptets).ToArray());
        return text.Length == 0 ? GsmProtocol.DefaultIncomingSmsText : text;
    }
}
