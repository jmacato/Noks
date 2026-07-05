using System.Collections.Immutable;

namespace Noks.Dct3.Sim;

public sealed record SimMutation
{
    public SimMutation(
        ushort parentFileId,
        ushort fileId,
        int recordNumber,
        ReadOnlySpan<byte> oldValue,
        ReadOnlySpan<byte> newValue,
        SimMutationOrigin origin)
    {
        ParentFileId = parentFileId;
        FileId = fileId;
        RecordNumber = recordNumber;
        OldValue = ImmutableArray.Create(oldValue.ToArray());
        NewValue = ImmutableArray.Create(newValue.ToArray());
        Origin = origin;
    }

    public ushort ParentFileId { get; }

    public ushort FileId { get; }

    public int RecordNumber { get; }

    public ImmutableArray<byte> OldValue { get; }

    public ImmutableArray<byte> NewValue { get; }

    public SimMutationOrigin Origin { get; }
}
