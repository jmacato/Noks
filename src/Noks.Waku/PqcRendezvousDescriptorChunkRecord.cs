using Noks.Cryptography;

namespace Noks.Waku;

public sealed record PqcRendezvousDescriptorChunkRecord(PqcRendezvousDescriptorChunk Chunk) : PqcRendezvousWireRecord;
