#nullable enable

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Noks.Cryptography;

public static class NoksUserName
{
    public const int MinimumLength = 1;
    public const int MaximumLength = 256;
    public const int MaximumUtf8Length = MaximumLength * 4;
    private const string SuffixAlphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
    private static readonly byte[] StreamKey = Encoding.UTF8.GetBytes("Noks user name v1");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] Nouns =
    [
        "acorn", "beacon", "birch", "breeze", "brook", "cedar", "cloud", "comet",
        "creek", "dawn", "dune", "ember", "fern", "field", "forest", "frost",
        "grove", "harbor", "lake", "leaf", "maple", "meadow", "moon", "orbit",
        "pebble", "pine", "plume", "reef", "river", "sky", "solar", "stone",
    ];
    public static string GenerateInitial(ReadOnlySpan<byte> entropy)
    {
        if (entropy.Length != NoksRecoveryPhrase.EntropySize)
            throw new ArgumentException("Initial user-name generation requires 32 bytes of recovery entropy.", nameof(entropy));

        using DeterministicStream stream = new(entropy);
        _ = stream.NextByte();
        string noun = Nouns[stream.NextByte() & 31];
        Span<char> suffix = stackalloc char[4];
        for (int index = 0; index < suffix.Length; index++)
            suffix[index] = SuffixAlphabet[stream.NextUnbiased(SuffixAlphabet.Length)];
        return string.Concat(noun, "-", new string(suffix));
    }

    public static string Normalize(string value) => (value ?? string.Empty).Trim();

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0)
            return false;
        try
        {
            if (StrictUtf8.GetByteCount(value) > MaximumUtf8Length)
                return false;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
        int characterCount = 0;
        foreach (Rune _ in value.EnumerateRunes())
        {
            if (++characterCount > MaximumLength)
                return false;
        }
        return characterCount >= MinimumLength;
    }

    private sealed class DeterministicStream : IDisposable
    {
        private readonly byte[] entropy;
        private byte[] block = [];
        private uint counter;
        private int offset;

        public DeterministicStream(ReadOnlySpan<byte> entropy)
        {
            this.entropy = entropy.ToArray();
        }

        public byte NextByte()
        {
            if (offset >= block.Length)
                Refill();
            return block[offset++];
        }

        public int NextUnbiased(int upperBound)
        {
            int limit = 256 - 256 % upperBound;
            while (true)
            {
                int value = NextByte();
                if (value < limit)
                    return value % upperBound;
            }
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(entropy);
            CryptographicOperations.ZeroMemory(block);
        }

        private void Refill()
        {
            CryptographicOperations.ZeroMemory(block);
            byte[] input = new byte[entropy.Length + 4];
            entropy.CopyTo(input, 0);
            BinaryPrimitives.WriteUInt32BigEndian(input.AsSpan(entropy.Length), counter++);
            try
            {
                block = HMACSHA512.HashData(StreamKey, input);
                offset = 0;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(input);
            }
        }
    }
}
