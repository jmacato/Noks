using System.Text;

namespace Noks.Waku.Transport.Libp2p.Discovery;

internal static class Base58Btc
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static string Encode(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            return "";

        int zeroCount = 0;
        while (zeroCount < value.Length && value[zeroCount] == 0)
            zeroCount++;

        byte[] digits = new byte[checked(value.Length * 138 / 100 + 1)];
        int digitCount = 0;
        for (int index = zeroCount; index < value.Length; index++)
        {
            int carry = value[index];
            int position = 0;
            for (; position < digitCount; position++)
            {
                carry += digits[position] << 8;
                digits[position] = (byte)(carry % 58);
                carry /= 58;
            }

            while (carry != 0)
            {
                digits[digitCount++] = (byte)(carry % 58);
                carry /= 58;
            }
        }

        StringBuilder result = new(zeroCount + digitCount);
        result.Append(Alphabet[0], zeroCount);
        for (int index = digitCount - 1; index >= 0; index--)
            result.Append(Alphabet[digits[index]]);
        return result.ToString();
    }
}
