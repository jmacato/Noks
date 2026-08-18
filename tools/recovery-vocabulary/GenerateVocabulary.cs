using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

const string EffSha256 = "addd35536511597a02fa0a9ff1e5284677b8883b83e986e43f15a3db996b903e";
const int VocabularySize = 2048;

Dictionary<string, string> bip39Sha256 = new(StringComparer.Ordinal)
{
    ["chinese_simplified"] = "5c5942792bd8340cb8b27cd592f1015edf56a8c5b26276ee18a482428e7c5726",
    ["chinese_traditional"] = "417b26b3d8500a4ae3d59717d7011952db6fc2fb84b807f3f94ac734e89c1b5f",
    ["czech"] = "7e80e161c3e93d9554c2efb78d4e3cebf8fc727e9c52e03b83b94406bdcc95fc",
    ["english"] = "2f5eed53a4727b4bf8880d8f3f199efc90e58503646d9ff8eff3a2ed3b24dbda",
    ["french"] = "ebc3959ab7801a1df6bac4fa7d970652f1df76b683cd2f4003c941c63d517e59",
    ["italian"] = "d392c49fdb700a24cd1fceb237c1f65dcc128f6b34a8aacb58b59384b5c648c2",
    ["japanese"] = "2eed0aef492291e061633d7ad8117f1a2b03eb80a29d0e4e3117ac2528d05ffd",
    ["korean"] = "9e95f86c167de88f450f0aaf89e87f6624a57f973c67b516e338e8e8b8897f60",
    ["portuguese"] = "2685e9c194c82ae67e10ba59d9ea5345a23dc093e92276fc5361f6667d79cd3f",
    ["spanish"] = "46846a5a0139d1e3cb77293e521c2865f7bcdb82c44e8d0a06a2cd0ecba48c0b",
};

string root = args.Length switch
{
    0 => Directory.GetCurrentDirectory(),
    2 when args[0] == "--root" => Path.GetFullPath(args[1]),
    _ => throw new ArgumentException("Usage: dotnet run tools/recovery-vocabulary/GenerateVocabulary.cs -- [--root <repository-root>]")
};

string toolDirectory = Path.Combine(root, "tools", "recovery-vocabulary");
string sourcesDirectory = Path.Combine(toolDirectory, "sources");
string effPath = Path.Combine(sourcesDirectory, "eff_large_wordlist_2016-07-18.txt");
VerifyHash(effPath, EffSha256);

HashSet<string> excluded = new(StringComparer.Ordinal);
foreach ((string language, string expectedHash) in bip39Sha256)
{
    string path = Path.Combine(sourcesDirectory, $"bip39-{language}.txt");
    VerifyHash(path, expectedHash);
    foreach (string word in File.ReadLines(path))
    {
        excluded.Add(word.Trim());
    }
}

Regex eligiblePattern = new("^[a-z]{4,8}$", RegexOptions.CultureInvariant);
IEnumerable<string> eligible = File.ReadLines(effPath)
    .Select(line => line.Split('\t', 2))
    .Where(parts => parts.Length == 2)
    .Select(parts => parts[1].Trim())
    .Where(word => eligiblePattern.IsMatch(word) && !excluded.Contains(word));

// A recovery word is unique after four characters. On a collision, keep the
// shorter word, then the first word in ordinal order.
List<string> uniqueFourCharacterPrefixes = eligible
    .GroupBy(word => word[..4], StringComparer.Ordinal)
    .Select(group => group.OrderBy(word => word.Length).ThenBy(word => word, StringComparer.Ordinal).First())
    .ToList();

// Each round takes one word from every two-character bucket. An empty bucket
// drops out of later rounds, so a common prefix cannot dominate and a rare
// prefix still contributes all its words.
List<Queue<string>> buckets = uniqueFourCharacterPrefixes
    .GroupBy(word => word[..2], StringComparer.Ordinal)
    .OrderBy(group => group.Key, StringComparer.Ordinal)
    .Select(group => new Queue<string>(group.OrderBy(word => word.Length).ThenBy(word => word, StringComparer.Ordinal)))
    .ToList();

List<string> selected = new(VocabularySize);
while (selected.Count < VocabularySize)
{
    bool madeProgress = false;
    foreach (Queue<string> bucket in buckets)
    {
        if (bucket.Count == 0)
        {
            continue;
        }

        selected.Add(bucket.Dequeue());
        madeProgress = true;
        if (selected.Count == VocabularySize)
        {
            break;
        }
    }

    if (!madeProgress)
    {
        throw new InvalidOperationException($"Only {selected.Count} eligible recovery words were available.");
    }
}

selected.Sort(StringComparer.Ordinal);
if (selected.Distinct(StringComparer.Ordinal).Count() != VocabularySize ||
    selected.Select(word => word[..4]).Distinct(StringComparer.Ordinal).Count() != VocabularySize)
{
    throw new InvalidOperationException("Generated vocabulary violates uniqueness requirements.");
}

string output = string.Join('\n', selected) + "\n";
string outputPath = Path.Combine(root, "src", "Noks.Cryptography", "Resources", "noks-recovery-vocabulary-v1.txt");
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllText(outputPath, output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

Dictionary<string, int> distribution = selected
    .GroupBy(word => word[..2], StringComparer.Ordinal)
    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
Console.WriteLine($"eligible-after-first-four-dedup={uniqueFourCharacterPrefixes.Count}");
Console.WriteLine($"selected={selected.Count}");
Console.WriteLine($"two-letter-buckets={distribution.Count}");
Console.WriteLine($"two-letter-min={distribution.Values.Min()}");
Console.WriteLine($"two-letter-max={distribution.Values.Max()}");
Console.WriteLine($"sha256={Hash(Encoding.UTF8.GetBytes(output))}");

static void VerifyHash(string path, string expected)
{
    string actual = Hash(File.ReadAllBytes(path));
    if (!string.Equals(actual, expected, StringComparison.Ordinal))
    {
        throw new InvalidDataException($"Source hash mismatch for {path}: expected {expected}, got {actual}.");
    }
}

static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(SHA256.HashData(value));
