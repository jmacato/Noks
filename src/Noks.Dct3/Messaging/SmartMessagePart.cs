namespace Noks.Dct3.Messaging;

internal readonly record struct SmartMessagePart(
    byte[] Payload,
    SmartMessageConcatenation Concatenation);
