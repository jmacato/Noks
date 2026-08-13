using Noks.Waku;
using Noks.Dct3.Sim;

namespace Noks.Application;

public sealed class PhonebookContactIndex
{
    private const ushort TelecomDirectory = 0x7F10;
    private const ushort AdnFile = 0x6F3A;
    private readonly Dictionary<int, string> numbersByRecord = [];

    public int UsedRecordCount => numbersByRecord.Count;

    public bool IsFull => UsedRecordCount >= SimCard.OrdinaryAdnRecordCount;

    public bool ContainsNumber(string number) =>
        numbersByRecord.Values.Any(value => string.Equals(value, number, StringComparison.Ordinal));

    public PhonebookIndexUpdate Apply(SimMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (mutation.ParentFileId != TelecomDirectory || mutation.FileId != AdnFile)
            return new PhonebookIndexUpdate([], [], null);

        HashSet<string> before = numbersByRecord.Values.ToHashSet(StringComparer.Ordinal);
        string? writtenNumber = null;
        if (mutation.RecordNumber == 0)
        {
            Rebuild(mutation.NewValue.AsSpan());
        }
        else if (mutation.RecordNumber is >= 1 and <= SimCard.OrdinaryAdnRecordCount)
        {
            numbersByRecord.Remove(mutation.RecordNumber);
            if (TryDecodeCanonical(mutation.NewValue.AsSpan(), out string number))
            {
                numbersByRecord[mutation.RecordNumber] = number;
                writtenNumber = number;
            }
        }

        HashSet<string> after = numbersByRecord.Values.ToHashSet(StringComparer.Ordinal);
        string[] added = after.Except(before, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] removed = before.Except(after, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new PhonebookIndexUpdate(added, removed, writtenNumber);
    }

    private void Rebuild(ReadOnlySpan<byte> file)
    {
        if (file.Length != SimCard.AdnRecordCount * SimPhonebookCodec.RecordLength)
            return;
        numbersByRecord.Clear();
        for (int index = 0; index < SimCard.OrdinaryAdnRecordCount; index++)
        {
            if (TryDecodeCanonical(
                    file.Slice(index * SimPhonebookCodec.RecordLength, SimPhonebookCodec.RecordLength),
                    out string number))
            {
                numbersByRecord[index + 1] = number;
            }
        }
    }

    private static bool TryDecodeCanonical(ReadOnlySpan<byte> record, out string number)
    {
        number = string.Empty;
        return SimPhonebookCodec.TryDecode(record, out _, out string decoded) &&
            NoksTemporaryNumber.IsCanonical(decoded) &&
            (number = decoded).Length != 0;
    }
}
