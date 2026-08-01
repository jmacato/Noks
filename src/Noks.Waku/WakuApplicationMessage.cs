namespace Noks.Waku;

public sealed class WakuApplicationMessage
{
    public const int SenderRoutingKeySize = 33;
    public const int RecipientRoutingKeySize = 32;

    public WakuApplicationMessage(
        Guid eventId,
        WakuEventKind kind,
        long issuedAtUnixMilliseconds,
        long expiresAtUnixMilliseconds,
        ReadOnlySpan<byte> senderIdentityPublicKey,
        ReadOnlySpan<byte> recipientMailboxPublicKey,
        ReadOnlySpan<byte> payload)
    {
        if (eventId == Guid.Empty)
            throw new ArgumentException("An event identifier is required.", nameof(eventId));
        if (!WakuEventPolicy.IsKnown(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Waku event kind.");
        if (expiresAtUnixMilliseconds <= issuedAtUnixMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(expiresAtUnixMilliseconds), "Expiry must be after issue time.");
        var maximumLifetimeMilliseconds = (long)WakuEventPolicy.GetMaximumLifetime(kind).TotalMilliseconds;
        if ((decimal)expiresAtUnixMilliseconds - issuedAtUnixMilliseconds > maximumLifetimeMilliseconds)
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUnixMilliseconds),
                $"{kind} events cannot live longer than {WakuEventPolicy.GetMaximumLifetime(kind)}.");
        if (senderIdentityPublicKey.Length != SenderRoutingKeySize)
            throw new ArgumentException("A 33-byte sender routing identity is required.", nameof(senderIdentityPublicKey));
        if (recipientMailboxPublicKey.Length != RecipientRoutingKeySize)
            throw new ArgumentException("A 32-byte recipient routing identity is required.", nameof(recipientMailboxPublicKey));
        if (payload.Length > PqcWakuEnvelopeCodec.MaximumPayloadSize)
            throw new ArgumentException($"Payload cannot exceed {PqcWakuEnvelopeCodec.MaximumPayloadSize} bytes.", nameof(payload));

        EventId = eventId;
        Kind = kind;
        IssuedAtUnixMilliseconds = issuedAtUnixMilliseconds;
        ExpiresAtUnixMilliseconds = expiresAtUnixMilliseconds;
        SenderIdentityPublicKey = senderIdentityPublicKey.ToArray();
        RecipientMailboxPublicKey = recipientMailboxPublicKey.ToArray();
        Payload = payload.ToArray();
    }

    public Guid EventId { get; }

    public WakuEventKind Kind { get; }

    public WakuDeliveryClass DeliveryClass => WakuEventPolicy.GetDeliveryClass(Kind);

    public long IssuedAtUnixMilliseconds { get; }

    public long ExpiresAtUnixMilliseconds { get; }

    public ReadOnlyMemory<byte> SenderIdentityPublicKey { get; }

    public ReadOnlyMemory<byte> RecipientMailboxPublicKey { get; }

    public ReadOnlyMemory<byte> Payload { get; }

    public bool IsValidAt(DateTimeOffset instant) =>
        instant.ToUnixTimeMilliseconds() >= IssuedAtUnixMilliseconds &&
        instant.ToUnixTimeMilliseconds() < ExpiresAtUnixMilliseconds;
}
