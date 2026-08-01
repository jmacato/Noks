using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Noks.Waku;

public sealed class PulseLiteSchedule
{
    public const int ScheduleKeySize = 32;
    public const int DefaultIntervalMilliseconds = 30_000;
    public const int MinimumIntervalMilliseconds = 1_000;
    public const int MaximumIntervalMilliseconds = 86_400_000;

    private readonly byte[] scheduleKey;

    public PulseLiteSchedule(
        ReadOnlySpan<byte> scheduleKey,
        int intervalMilliseconds,
        int phaseMilliseconds)
    {
        if (scheduleKey.Length != ScheduleKeySize)
            throw new ArgumentException($"A {ScheduleKeySize}-byte schedule key is required.", nameof(scheduleKey));
        if (intervalMilliseconds is < MinimumIntervalMilliseconds or > MaximumIntervalMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds));
        if (phaseMilliseconds < 0 || phaseMilliseconds >= intervalMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(phaseMilliseconds));

        this.scheduleKey = scheduleKey.ToArray();
        IntervalMilliseconds = intervalMilliseconds;
        PhaseMilliseconds = phaseMilliseconds;
    }

    public ReadOnlyMemory<byte> ScheduleKey => scheduleKey;

    public int IntervalMilliseconds { get; }

    public int PhaseMilliseconds { get; }

    public static PulseLiteSchedule Create(int intervalMilliseconds = DefaultIntervalMilliseconds)
    {
        if (intervalMilliseconds is < MinimumIntervalMilliseconds or > MaximumIntervalMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds));

        var key = RandomNumberGenerator.GetBytes(ScheduleKeySize);
        try
        {
            return new PulseLiteSchedule(
                key,
                intervalMilliseconds,
                RandomNumberGenerator.GetInt32(intervalMilliseconds));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public PulseLiteSlot GetNextSlot(DateTimeOffset after)
    {
        var afterMilliseconds = after.ToUnixTimeMilliseconds();
        if (afterMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(after));

        var sequence = afterMilliseconds < PhaseMilliseconds
            ? 0
            : (afterMilliseconds - PhaseMilliseconds) / IntervalMilliseconds;
        var slot = GetSlot(sequence);
        return slot.ScheduledAtUnixMilliseconds > afterMilliseconds
            ? slot
            : GetSlot(checked(sequence + 1));
    }

    public PulseLiteSlot GetSlot(long sequence)
    {
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));

        Span<byte> input = stackalloc byte[sizeof(long) + 1];
        Span<byte> digest = stackalloc byte[32];
        BinaryPrimitives.WriteInt64BigEndian(input[1..], sequence);
        input[0] = 0x01;
        HMACSHA256.HashData(scheduleKey, input, digest);
        var bucket = digest[0] % WakuTopicProfile.BucketCount;
        input[0] = 0x02;
        HMACSHA256.HashData(scheduleKey, input, digest);
        var jitterRange = Math.Max(1, IntervalMilliseconds / 2);
        var jitter = IntervalMilliseconds / 4 +
                     BinaryPrimitives.ReadUInt32BigEndian(digest[1..]) % jitterRange;
        var scheduledAt = checked(sequence * IntervalMilliseconds + PhaseMilliseconds + jitter);
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(scheduledAt);
        return new PulseLiteSlot(
            sequence,
            scheduledAt,
            bucket,
            WakuTopicProfile.GetTopic(bucket, WakuTopicProfile.GetEpoch(timestamp)));
    }
}
