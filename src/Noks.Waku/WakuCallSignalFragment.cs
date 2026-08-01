namespace Noks.Waku;

public sealed record WakuCallSignalFragment(
    Guid AttemptId,
    Guid SignalId,
    ushort ChunkIndex,
    ushort ChunkCount,
    int TotalLength,
    ReadOnlyMemory<byte> Data);
