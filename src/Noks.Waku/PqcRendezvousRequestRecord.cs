using Noks.Cryptography;

namespace Noks.Waku;

public sealed record PqcRendezvousRequestRecord(PqcRendezvousRequest Request) : PqcRendezvousWireRecord;
