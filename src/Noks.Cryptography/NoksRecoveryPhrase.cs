#nullable enable

using System;
using System.Security.Cryptography;

namespace Noks.Cryptography;

public static class NoksRecoveryPhrase
{
    public const int EntropySize = 32;
    public const int WordCount = 24;

    public static byte[] GenerateEntropy() => RandomNumberGenerator.GetBytes(EntropySize);

    public static string Generate(out byte[] entropy)
    {
        entropy = GenerateEntropy();
        return Encode(entropy);
    }

    public static string Encode(ReadOnlySpan<byte> entropy)
    {
        RequireEntropy(entropy);
        Span<byte> checksum = stackalloc byte[32];
        SHA256.HashData(entropy, checksum);
        Span<byte> encoded = stackalloc byte[EntropySize + 1];
        entropy.CopyTo(encoded);
        encoded[^1] = checksum[0];

        string[] words = new string[WordCount];
        for (int word = 0; word < WordCount; word++)
        {
            int index = 0;
            int firstBit = word * 11;
            for (int bit = 0; bit < 11; bit++)
            {
                int absoluteBit = firstBit + bit;
                index = (index << 1) | ((encoded[absoluteBit / 8] >> (7 - absoluteBit % 8)) & 1);
            }
            words[word] = NoksRecoveryVocabulary.Words[index];
        }

        CryptographicOperations.ZeroMemory(checksum);
        CryptographicOperations.ZeroMemory(encoded);
        return string.Join(' ', words);
    }

    public static byte[] Decode(string phrase)
    {
        if (!TryDecode(phrase, out byte[] entropy))
            throw new FormatException("The Noks recovery phrase is invalid or has a checksum error.");
        return entropy;
    }

    public static bool TryDecode(string? phrase, out byte[] entropy)
    {
        entropy = [];
        if (string.IsNullOrWhiteSpace(phrase))
            return false;

        string[] words = phrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length != WordCount)
            return false;

        Span<byte> encoded = stackalloc byte[EntropySize + 1];
        Span<byte> checksum = stackalloc byte[32];
        try
        {
            for (int word = 0; word < words.Length; word++)
            {
                string normalized = words[word].ToLowerInvariant();
                if (!NoksRecoveryVocabulary.TryGetIndex(normalized, out int index))
                    return false;

                int firstBit = word * 11;
                for (int bit = 0; bit < 11; bit++)
                {
                    if ((index & (1 << (10 - bit))) != 0)
                    {
                        int absoluteBit = firstBit + bit;
                        encoded[absoluteBit / 8] |= (byte)(1 << (7 - absoluteBit % 8));
                    }
                }
            }

            SHA256.HashData(encoded[..EntropySize], checksum);
            bool valid = CryptographicOperations.FixedTimeEquals(encoded[EntropySize..], checksum[..1]);
            if (valid)
                entropy = encoded[..EntropySize].ToArray();
            return valid;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
            CryptographicOperations.ZeroMemory(checksum);
        }
    }

    private static void RequireEntropy(ReadOnlySpan<byte> entropy)
    {
        if (entropy.Length != EntropySize)
            throw new ArgumentException($"Noks recovery entropy must contain exactly {EntropySize} bytes.", nameof(entropy));
    }
}
