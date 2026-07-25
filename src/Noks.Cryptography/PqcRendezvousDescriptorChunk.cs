namespace Noks.Cryptography;

public sealed record PqcRendezvousDescriptorChunk(
    int ProtocolVersion,
    byte[] DescriptorHash,
    int Index,
    int Count,
    byte[] Payload);
