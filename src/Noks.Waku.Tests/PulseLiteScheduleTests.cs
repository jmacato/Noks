namespace Noks.Waku.Tests;

public sealed class PulseLiteScheduleTests
{
    [Fact]
    public void SlotsStayInsideJitteredWindowsAndSelectSharedTopics()
    {
        var key = Enumerable.Range(0, PulseLiteSchedule.ScheduleKeySize).Select(value => (byte)value).ToArray();
        var schedule = new PulseLiteSchedule(key, intervalMilliseconds: 30_000, phaseMilliseconds: 7_321);
        var after = DateTimeOffset.FromUnixTimeMilliseconds(1_750_000_000_000);

        var slot = schedule.GetNextSlot(after);
        var windowStart = slot.Sequence * schedule.IntervalMilliseconds + schedule.PhaseMilliseconds;

        Assert.True(slot.ScheduledAtUnixMilliseconds > after.ToUnixTimeMilliseconds());
        Assert.InRange(
            slot.ScheduledAtUnixMilliseconds - windowStart,
            schedule.IntervalMilliseconds / 4,
            schedule.IntervalMilliseconds * 3 / 4 - 1);
        Assert.InRange(slot.Bucket, 0, WakuTopicProfile.BucketCount - 1);
        Assert.Equal(
            WakuTopicProfile.GetTopic(
                slot.Bucket,
                WakuTopicProfile.GetEpoch(DateTimeOffset.FromUnixTimeMilliseconds(slot.ScheduledAtUnixMilliseconds))),
            slot.ContentTopic);
        Assert.Equal(slot, schedule.GetSlot(slot.Sequence));
    }

    [Fact]
    public void RecipientDoesNotInfluenceScheduledTopic()
    {
        var key = Enumerable.Repeat((byte)0xA5, PulseLiteSchedule.ScheduleKeySize).ToArray();
        var schedule = new PulseLiteSchedule(key, 10_000, 123);
        var slot = schedule.GetSlot(42);
        var firstRecipient = Enumerable.Repeat((byte)1, 32).ToArray();
        var secondRecipient = Enumerable.Repeat((byte)2, 32).ToArray();

        var first = PulseLitePacketFactory.CreatePublishRequest(new byte[PulseLiteEnvelopeCodec.PacketSize], slot);
        var second = PulseLitePacketFactory.CreatePublishRequest(new byte[PulseLiteEnvelopeCodec.PacketSize], slot);

        Assert.NotEqual(firstRecipient, secondRecipient);
        Assert.Equal(first.ContentTopic, second.ContentTopic);
        Assert.True(first.Ephemeral);
        Assert.True(second.Ephemeral);
        Assert.Equal(slot.ScheduledAtUnixMilliseconds, first.TimestampUnixMilliseconds);

        var durable = PulseLitePacketFactory.CreatePublishRequest(
            new byte[PulseLiteEnvelopeCodec.PacketSize],
            slot,
            ephemeral: false);
        Assert.False(durable.Ephemeral);
        Assert.Equal(first.ContentTopic, durable.ContentTopic);
    }

    [Fact]
    public void FirstShiftedWindowIsNotSkippedNearTheUnixEpoch()
    {
        var schedule = new PulseLiteSchedule(new byte[PulseLiteSchedule.ScheduleKeySize], 30_000, 7_321);

        var slot = schedule.GetNextSlot(DateTimeOffset.FromUnixTimeMilliseconds(1_000));

        Assert.Equal(0, slot.Sequence);
        Assert.InRange(slot.ScheduledAtUnixMilliseconds, 14_821, 29_820);
    }

    [Fact]
    public void ConsecutiveGapsStayBetweenHalfAndOneAndAHalfIntervals()
    {
        var schedule = new PulseLiteSchedule(
            Enumerable.Repeat((byte)0x5A, PulseLiteSchedule.ScheduleKeySize).ToArray(),
            10_000,
            321);
        long totalGap = 0;
        var distinctGaps = new HashSet<long>();

        for (var sequence = 0; sequence < 1_000; sequence++)
        {
            var current = schedule.GetSlot(sequence);
            var next = schedule.GetSlot(sequence + 1);
            var gap = next.ScheduledAtUnixMilliseconds - current.ScheduledAtUnixMilliseconds;
            Assert.InRange(
                gap,
                5_001,
                14_999);
            totalGap += gap;
            distinctGaps.Add(gap);
        }

        Assert.InRange(totalGap / 1_000d, 9_995, 10_005);
        Assert.True(distinctGaps.Count > 500, "Keyed slot jitter unexpectedly collapsed into a regular cadence.");
    }

    [Theory]
    [InlineData(999)]
    [InlineData(86400001)]
    public void ScheduleRejectsUnsafeIntervals(int intervalMilliseconds)
    {
        var key = new byte[PulseLiteSchedule.ScheduleKeySize];
        Assert.Throws<ArgumentOutOfRangeException>(() => new PulseLiteSchedule(key, intervalMilliseconds, 0));
    }
}
