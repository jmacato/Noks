namespace Noks.Waku;

public static class PulseLitePacketFactory
{
    public static WakuApplicationMessage CreateCoverMessage(
        DateTimeOffset issuedAt,
        ReadOnlySpan<byte> senderIdentityPublicKey,
        ReadOnlySpan<byte> recipientMailboxPublicKey)
    {
        var expiresAt = issuedAt + WakuEventPolicy.MaximumRealtimeLifetime;
        return new WakuApplicationMessage(
            Guid.NewGuid(),
            WakuEventKind.PulseCover,
            issuedAt.ToUnixTimeMilliseconds(),
            expiresAt.ToUnixTimeMilliseconds(),
            senderIdentityPublicKey,
            recipientMailboxPublicKey,
            []);
    }

    public static WakuPublishRequest CreatePublishRequest(
        ReadOnlyMemory<byte> encryptedPacket,
        PulseLiteSlot slot,
        bool ephemeral = true)
    {
        if (encryptedPacket.Length != PulseLiteEnvelopeCodec.PacketSize)
            throw new ArgumentException(
                $"A Pulse Lite packet must be exactly {PulseLiteEnvelopeCodec.PacketSize} bytes.",
                nameof(encryptedPacket));

        return new WakuPublishRequest(
            slot.ContentTopic,
            encryptedPacket,
            ephemeral,
            slot.ScheduledAtUnixMilliseconds);
    }
}
