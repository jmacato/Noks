namespace Noks.Dct3.Messaging;

internal static class SmartMessageSms
{
    public const int SinglePartPayloadCapacity = 133;
    public const int ConcatenatedPartPayloadCapacity = 128;
    public const int MaximumPartCount = 3;
    public const int MaximumConcatenatedPayloadLength =
        ConcatenatedPartPayloadCapacity * MaximumPartCount;

    public static int GetPartCount(int payloadLength)
    {
        if (payloadLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadLength), "A Smart Messaging payload cannot be empty.");
        }

        if (payloadLength <= SinglePartPayloadCapacity)
        {
            return 1;
        }

        int partCount = ((payloadLength - 1) / ConcatenatedPartPayloadCapacity) + 1;
        if (partCount > MaximumPartCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payloadLength),
                $"A Nokia Smart Messaging payload supports at most {MaximumPartCount} SMS parts " +
                $"({MaximumConcatenatedPayloadLength} encoded bytes).");
        }

        return partCount;
    }

    public static SmartMessagePart[] Split(ReadOnlySpan<byte> payload, byte reference)
    {
        int partCount = GetPartCount(payload.Length);
        if (partCount == 1)
        {
            return [new SmartMessagePart(payload.ToArray(), default)];
        }

        SmartMessagePart[] parts = new SmartMessagePart[partCount];
        for (int partIndex = 0; partIndex < partCount; partIndex++)
        {
            int offset = partIndex * ConcatenatedPartPayloadCapacity;
            int length = Math.Min(ConcatenatedPartPayloadCapacity, payload.Length - offset);
            SmartMessageConcatenation concatenation = new(
                reference,
                PartCount: (byte)partCount,
                PartNumber: (byte)(partIndex + 1));
            parts[partIndex] = new SmartMessagePart(payload.Slice(offset, length).ToArray(), concatenation);
        }

        return parts;
    }
}
