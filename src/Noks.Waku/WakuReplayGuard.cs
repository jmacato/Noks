namespace Noks.Waku;

public sealed class WakuReplayGuard
{
    private static readonly TimeSpan MaximumFutureClockSkew = TimeSpan.FromMinutes(1);
    private readonly int maximumEntries;
    private readonly Dictionary<Guid, long> expirations = [];

    public WakuReplayGuard(int maximumEntries = 4096)
    {
        if (maximumEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        this.maximumEntries = maximumEntries;
    }

    public bool TryAccept(WakuApplicationMessage message, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(message);
        var nowMilliseconds = now.ToUnixTimeMilliseconds();
        if ((decimal)message.IssuedAtUnixMilliseconds - nowMilliseconds >
                (decimal)MaximumFutureClockSkew.TotalMilliseconds ||
            nowMilliseconds >= message.ExpiresAtUnixMilliseconds)
            return false;

        RemoveExpired(nowMilliseconds);
        if (!expirations.TryAdd(message.EventId, message.ExpiresAtUnixMilliseconds))
            return false;
        if (expirations.Count > maximumEntries)
        {
            expirations.Remove(message.EventId);
            return false;
        }

        return true;
    }

    private void RemoveExpired(long nowMilliseconds)
    {
        foreach (var eventId in expirations.Where(pair => pair.Value <= nowMilliseconds).Select(pair => pair.Key).ToArray())
            expirations.Remove(eventId);
    }
}
