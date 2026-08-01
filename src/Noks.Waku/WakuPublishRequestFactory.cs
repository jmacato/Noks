namespace Noks.Waku;

public static class WakuPublishRequestFactory
{
    public static WakuPublishRequest Create(
        WakuApplicationMessage message,
        ReadOnlyMemory<byte> encryptedEnvelope,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (encryptedEnvelope.Length is not
            (WakuEnvelopeCodec.EnvelopeSize or PqcWakuEnvelopeCodec.EnvelopeSize))
        {
            throw new ArgumentException(
                $"A Waku envelope must be exactly {WakuEnvelopeCodec.EnvelopeSize} or {PqcWakuEnvelopeCodec.EnvelopeSize} bytes.",
                nameof(encryptedEnvelope));
        }

        return new WakuPublishRequest(
            WakuTopicProfile.GetSendTopic(message.RecipientMailboxPublicKey.Span, timestamp),
            encryptedEnvelope,
            WakuEventPolicy.ShouldPublishEphemeral(message.Kind),
            timestamp.ToUnixTimeMilliseconds());
    }
}
