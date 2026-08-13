namespace Noks.Application;

public sealed record WakuPhoneBridgeOptions(
    TimeSpan PairingLifetime,
    TimeSpan SmsLifetime,
    TimeSpan StoreWindow,
    int MaximumPendingRoutes,
    int MaximumQueuedWork,
    int MaximumQueuedCommands)
{
    public TimeSpan CallMediaSetupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>This flag turns on the experimental Store-backed ML-DSA/ML-KEM rendezvous transport.</summary>
    public bool EnablePostQuantumRendezvous { get; init; }

    /// <summary>This flag prevents callers and UI controls from enabling the classical compatibility path.</summary>
    public bool RequirePostQuantumRendezvous { get; init; }

    public int PostQuantumMinimumWorkBits { get; init; } = 12;

    public static WakuPhoneBridgeOptions Default { get; } = new(
        TimeSpan.FromMinutes(2),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(6),
        MaximumPendingRoutes: 16,
        MaximumQueuedWork: 256,
        MaximumQueuedCommands: 128);
}
