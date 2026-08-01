using System.Security.Cryptography;

namespace Noks.Waku;

public static class NoksTemporaryNumber
{
    public const int DigitCount = 13;

    public static string Generate()
    {
        Span<char> digits = stackalloc char[DigitCount];
        Span<byte> random = stackalloc byte[32];
        int written = 0;
        while (written < digits.Length)
        {
            RandomNumberGenerator.Fill(random);
            foreach (byte value in random)
            {
                // 250 is the largest multiple of ten below 256. This choice avoids bias.
                if (value >= 250)
                    continue;
                digits[written++] = (char)('0' + value % 10);
                if (written == digits.Length)
                    break;
            }
        }
        CryptographicOperations.ZeroMemory(random);
        return new string(digits);
    }

    public static bool IsCanonical(string? value) =>
        value is { Length: DigitCount } && value.All(character => character is >= '0' and <= '9');

    public static string Format(string value)
    {
        if (!IsCanonical(value))
            throw new FormatException($"A temporary Noks number must contain exactly {DigitCount} digits.");
        return $"{value[..3]}-{value.Substring(3, 3)}-{value.Substring(6, 3)}-{value[9..]}";
    }
}
