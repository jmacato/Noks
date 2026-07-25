using System.Security.Cryptography;
using System.Text;
using Noks.Cryptography;

namespace Noks.Cryptography.Tests;

public sealed class NoksRecoveryTests
{
    private const string VectorPhrase =
        "abacus amaretto knelt amaretto fading affluent busload cameo anew gigabyte effects bubbly " +
        "abruptly lullaby ethanol lanky barge robotics aliens debug ragged arise jawless useable";

    [Fact]
    public void EntropyRoundTripsThroughFixedTwentyFourWordVector()
    {
        byte[] entropy = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

        Assert.Equal(VectorPhrase, NoksRecoveryPhrase.Encode(entropy));
        Assert.Equal(entropy, NoksRecoveryPhrase.Decode(VectorPhrase));
        Assert.Equal(entropy, NoksRecoveryPhrase.Decode(VectorPhrase.ToUpperInvariant()));
    }

    [Fact]
    public void ChecksumChangeIsRejected()
    {
        string invalid = VectorPhrase[..VectorPhrase.LastIndexOf(' ')] + " zoom";

        Assert.False(NoksRecoveryPhrase.TryDecode(invalid, out byte[] entropy));
        Assert.Empty(entropy);
        Assert.Throws<FormatException>(() => NoksRecoveryPhrase.Decode(invalid));
    }

    [Fact]
    public void VocabularyMatchesCommittedGenerationRulesAndAllExclusions()
    {
        IReadOnlyList<string> words = NoksRecoveryVocabulary.Words;
        Assert.Equal(2048, words.Count);
        Assert.Equal(words.Order(StringComparer.Ordinal), words);
        Assert.Equal(2048, words.Select(word => word[..4]).Distinct(StringComparer.Ordinal).Count());
        Assert.All(words, word => Assert.Matches("^[a-z]{4,8}$", word));

        string output = string.Join('\n', words) + "\n";
        Assert.Equal(
            NoksRecoveryVocabulary.Sha256,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(output))));

        string root = FindRepositoryRoot();
        string sourceDirectory = Path.Combine(root, "tools", "recovery-vocabulary", "sources");
        HashSet<string> exclusions = Directory.GetFiles(sourceDirectory, "bip39-*.txt")
            .SelectMany(File.ReadLines)
            .Select(word => word.Trim())
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(words, exclusions.Contains);

        Dictionary<string, int> distribution = words
            .GroupBy(word => word[..2], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Assert.Equal(194, distribution.Count);
        Assert.Equal(1, distribution.Values.Min());
        Assert.Equal(19, distribution.Values.Max());
    }

    [Fact]
    public void Slip10MatchesPublishedSecp256k1HardenedVector()
    {
        byte[] seed = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");

        byte[] derived = Slip10.DeriveSecp256k1(seed, [0]);

        Assert.Equal(
            "edb2e14f9ee77d26dd93b4ecede8d16ed408ce149b6cd80b0715a2d911a0afea",
            Convert.ToHexStringLower(derived));
    }

    [Fact]
    public void ProfileHierarchyMatchesFixedVector()
    {
        byte[] entropy = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        Assert.Equal(
            "f393775273512d670b6ce167cf53c79b979d9dd4e8ad480bbbf3a6bce264ed16" +
            "0d2ff610a7c5bdcb7f2ff4a2fa72bac67285aaf3c7eb31c762185dabe6b0bcee",
            Convert.ToHexStringLower(WakuProfileKeys.DeriveProfileSeed(entropy)));
        Assert.Equal("orbit-jqqx", NoksUserName.GenerateInitial(entropy));

        using WakuProfileKeys keys = WakuProfileKeys.Create(entropy);
        Assert.Equal(
            "533cbcd99a9dd922f511303a7dccee92d9b3d9850be42a8e63c6f3a32ae5f6be",
            Convert.ToHexStringLower(keys.ContactCardPrivateKey.Span));
        Assert.Equal(
            "03866edcb6c3f1e961f6863ed86bcc3a564ca0b82aa4a448896422696cb72eab28",
            Convert.ToHexStringLower(keys.ContactCardPublicKey.Span));
        Assert.Equal(
            "a77141ef32910a38fff11896c3e98f1d40a2235d81bb3c21a30500b94a45784e",
            Convert.ToHexStringLower(keys.EnvelopePrivateKey.Span));
        Assert.Equal(
            "035e7d711607ca6d2de970efcf3b8d1c894d9178d17ed1613487eb67e4044072b3",
            Convert.ToHexStringLower(keys.EnvelopePublicKey.Span));
        Assert.Equal(
            "1b843ecbd2e81fd372e9efae3a8c3406058eaffc7d8913d55fe10fe6439bacfa",
            Convert.ToHexStringLower(keys.MailboxPrivateKey.Span));
        Assert.Equal(
            "e78b7685b7943651518c39ad994236aa40f8193fbac6894f850d242836d9e552",
            Convert.ToHexStringLower(keys.MailboxPublicKey.Span));
        Assert.Equal("_YJWepMBd00g9FF40gLVG6ZX", keys.StableContactId);
    }

    [Theory]
    [InlineData("abc", true)]
    [InlineData("abc-123", true)]
    [InlineData("ABC", true)]
    [InlineData("a--b", true)]
    [InlineData("-abc", true)]
    [InlineData("ab", true)]
    [InlineData("Mañana 🚀", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("bad\0name", false)]
    public void UserNamePolicyIsStable(string value, bool expected) =>
        Assert.Equal(expected, NoksUserName.IsValid(value));

    [Fact]
    public void WakuUserNamePolicyAllowsAtMost256UnicodeCharacters()
    {
        Assert.True(NoksUserName.IsValid(string.Concat(Enumerable.Repeat("🚀", 256))));
        Assert.False(NoksUserName.IsValid(string.Concat(Enumerable.Repeat("🚀", 257))));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Noks.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root was not found.");
    }
}
