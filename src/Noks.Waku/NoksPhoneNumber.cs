namespace Noks.Waku;

public static class NoksPhoneNumber
{
    public const int MinimumDigits = 3;
    public const int MaximumDigits = 20;

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        Span<char> digits = stackalloc char[MaximumDigits];
        int count = 0;
        foreach (char character in value)
        {
            if (character is >= '0' and <= '9')
            {
                if (count == digits.Length)
                    return false;
                digits[count++] = character;
            }
            else if (character is not ('+' or ' ' or '-' or '(' or ')'))
            {
                return false;
            }
        }

        if (count < MinimumDigits)
            return false;
        normalized = new string(digits[..count]);
        return true;
    }

    public static string Normalize(string value)
    {
        if (!TryNormalize(value, out string normalized))
            throw new FormatException("Phone number must contain 3-20 decimal digits.");
        return normalized;
    }
}
