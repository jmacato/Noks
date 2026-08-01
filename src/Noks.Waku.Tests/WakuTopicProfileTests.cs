namespace Noks.Waku.Tests;

public sealed class WakuTopicProfileTests
{
    [Fact]
    public void TopicDerivationMatchesFrozenVector()
    {
        var mailboxPublicKey = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

        Assert.Equal(3, WakuTopicProfile.GetMailboxBucket(mailboxPublicKey));
        Assert.Equal(
            "/b3069dd9cafc09fb/1/b66d6ebc42d088d2601ff025/proto",
            WakuTopicProfile.GetTopic(2, 12345));
    }

    [Fact]
    public void CoverSetsIncludeEveryBucketAndBoundaryEpoch()
    {
        var epoch = 12345L;
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(epoch * WakuTopicProfile.EpochDurationMilliseconds);

        var live = WakuTopicProfile.GetLiveCoverTopics(timestamp);
        var stored = WakuTopicProfile.GetStoreCoverTopics(timestamp, TimeSpan.FromHours(24));

        Assert.Equal(WakuTopicProfile.BucketCount, live.Count);
        Assert.Equal(5 * WakuTopicProfile.BucketCount, stored.Count);
        Assert.Equal(live, stored.Take(WakuTopicProfile.BucketCount));
        Assert.Equal(WakuTopicProfile.GetTopic(3, epoch - 4), stored[^1]);
    }
}
