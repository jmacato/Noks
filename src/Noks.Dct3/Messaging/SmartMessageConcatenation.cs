namespace Noks.Dct3.Messaging;

internal readonly record struct SmartMessageConcatenation(
    byte Reference,
    byte PartCount,
    byte PartNumber)
{
    public bool IsMultipart => PartCount > 1;
}
