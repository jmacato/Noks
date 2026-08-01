using System.Globalization;
using System.Security.Cryptography;
using System.Text;
namespace Noks.Waku;

public static class WakuTopicProfile
{
    public const long EpochDurationMilliseconds = 21_600_000;
    public const int BucketCount = 4;
    public const string PublicTopicSeed = "e97b24e993fc3d88c6d69dbfbf9b93f49b40ebbb5bbdce21f297cf8fd314cb12";

    public static long GetEpoch(DateTimeOffset timestamp) =>
        Math.DivRem(timestamp.ToUnixTimeMilliseconds(), EpochDurationMilliseconds, out _);

    public static int GetMailboxBucket(ReadOnlySpan<byte> mailboxPublicKey)
    {
        if (mailboxPublicKey.Length != WakuApplicationMessage.RecipientRoutingKeySize)
            throw new ArgumentException("A 32-byte mailbox routing identity is required.", nameof(mailboxPublicKey));

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(mailboxPublicKey, digest);
        return digest[0] % BucketCount;
    }

    public static string GetTopic(int bucket, long epoch)
    {
        if ((uint)bucket >= BucketCount)
            throw new ArgumentOutOfRangeException(nameof(bucket));
        if (epoch < 0)
            throw new ArgumentOutOfRangeException(nameof(epoch));

        var namespaceDigest = SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{PublicTopicSeed}:namespace:{epoch}")));
        var suffixDigest = SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{PublicTopicSeed}:{epoch}:{bucket}")));
        return $"/{Convert.ToHexString(namespaceDigest.AsSpan(0, 8)).ToLowerInvariant()}/1/" +
               $"{Convert.ToHexString(suffixDigest.AsSpan(0, 12)).ToLowerInvariant()}/proto";
    }

    public static string GetSendTopic(ReadOnlySpan<byte> recipientMailboxPublicKey, DateTimeOffset timestamp) =>
        GetTopic(GetMailboxBucket(recipientMailboxPublicKey), GetEpoch(timestamp));

    public static IReadOnlyList<string> GetLiveCoverTopics(DateTimeOffset timestamp)
    {
        var epoch = GetEpoch(timestamp);
        var topics = new string[BucketCount];
        for (var bucket = 0; bucket < BucketCount; bucket++)
            topics[bucket] = GetTopic(bucket, epoch);
        return topics;
    }

    public static IReadOnlyList<string> GetStoreCoverTopics(DateTimeOffset timestamp, TimeSpan deliveryWindow)
    {
        if (deliveryWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(deliveryWindow));

        var epochCount = checked((int)Math.Ceiling(deliveryWindow.TotalMilliseconds / EpochDurationMilliseconds)) + 1;
        var currentEpoch = GetEpoch(timestamp);
        var topics = new string[epochCount * BucketCount];
        var index = 0;
        for (var age = 0; age < epochCount; age++)
        {
            var epoch = currentEpoch - age;
            if (epoch < 0)
                break;
            for (var bucket = 0; bucket < BucketCount; bucket++)
                topics[index++] = GetTopic(bucket, epoch);
        }

        return index == topics.Length ? topics : topics[..index];
    }
}
