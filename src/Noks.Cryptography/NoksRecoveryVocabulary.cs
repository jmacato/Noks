using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Noks.Cryptography;

public static class NoksRecoveryVocabulary
{
    public const int WordCount = 2048;
    public const string Sha256 = "b37b22c14f48ee8b4b8dfa91d590ae00bf96e04a759c9f0de5d054dae66318b6";
    private const string ResourceName = "Noks.Cryptography.noks-recovery-vocabulary-v1.txt";
    private static readonly Lazy<ReadOnlyCollection<string>> LoadedWords = new(LoadWords);
    private static readonly Lazy<IReadOnlyDictionary<string, int>> LoadedIndices = new(CreateIndices);

    public static IReadOnlyList<string> Words => LoadedWords.Value;

    internal static bool TryGetIndex(string word, out int index) =>
        LoadedIndices.Value.TryGetValue(word, out index);

    private static ReadOnlyCollection<string> LoadWords()
    {
        Assembly assembly = typeof(NoksRecoveryVocabulary).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName) ??
            throw new InvalidOperationException("The Noks recovery vocabulary resource is missing.");
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        byte[] bytes = copy.ToArray();
        try
        {
            string actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(actualHash, Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("The Noks recovery vocabulary hash does not match version 1.");

            string[] words = Encoding.UTF8.GetString(bytes)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length != WordCount ||
                words.Any(word => word.Length is < 4 or > 8 || word.Any(character => character is < 'a' or > 'z')) ||
                words.Distinct(StringComparer.Ordinal).Count() != WordCount ||
                words.Select(word => word[..4]).Distinct(StringComparer.Ordinal).Count() != WordCount ||
                !words.SequenceEqual(words.OrderBy(word => word, StringComparer.Ordinal), StringComparer.Ordinal))
            {
                throw new InvalidDataException("The Noks recovery vocabulary is malformed.");
            }

            return Array.AsReadOnly(words);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static IReadOnlyDictionary<string, int> CreateIndices()
    {
        Dictionary<string, int> indices = new(WordCount, StringComparer.Ordinal);
        for (int index = 0; index < Words.Count; index++)
            indices.Add(Words[index], index);
        return new ReadOnlyDictionary<string, int>(indices);
    }
}
