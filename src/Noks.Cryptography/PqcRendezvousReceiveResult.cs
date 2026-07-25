namespace Noks.Cryptography;

public sealed record PqcRendezvousReceiveResult(bool IsAccepted, string Reason, string? EventId, byte[]? Plaintext)
{
    internal static PqcRendezvousReceiveResult Accepted(string eventId, byte[] plaintext) =>
        new(true, "Accepted.", eventId, plaintext);

    internal static PqcRendezvousReceiveResult Rejected(string reason) =>
        new(false, reason, null, null);
}
