using System.Security.Cryptography;

namespace Noks.Waku;

internal static class HkdfSha256
{
    private const int HashSize = 32;

    public static void DeriveKey(
        ReadOnlySpan<byte> inputKeyMaterial,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> info,
        Span<byte> output)
    {
        if (output.Length > 255 * HashSize)
            throw new ArgumentException("HKDF output is too long.", nameof(output));

        Span<byte> pseudoRandomKey = stackalloc byte[HashSize];
        HMACSHA256.HashData(salt, inputKeyMaterial, pseudoRandomKey);

        var previous = Array.Empty<byte>();
        var offset = 0;
        byte counter = 1;
        try
        {
            while (offset < output.Length)
            {
                var input = new byte[previous.Length + info.Length + 1];
                previous.CopyTo(input, 0);
                info.CopyTo(input.AsSpan(previous.Length));
                input[^1] = counter;

                var block = HMACSHA256.HashData(pseudoRandomKey, input);
                CryptographicOperations.ZeroMemory(input);
                if (previous.Length != 0)
                    CryptographicOperations.ZeroMemory(previous);
                previous = block;

                var count = Math.Min(HashSize, output.Length - offset);
                block.AsSpan(0, count).CopyTo(output[offset..]);
                offset += count;
                counter++;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pseudoRandomKey);
            if (previous.Length != 0)
                CryptographicOperations.ZeroMemory(previous);
        }
    }
}
