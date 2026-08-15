using Noks.Dct3.Sim;

namespace Noks.Application.Tests;

public sealed class PhonebookContactIndexTests
{
    [Fact]
    public void TracksOrdinaryRecordsDuplicatesAndFinalDeletion()
    {
        PhonebookContactIndex index = new();
        byte[] alice = SimPhonebookCodec.Encode("Alice", "1234567890123");
        PhonebookIndexUpdate first = index.Apply(Mutation(1, EmptyRecord(), alice));
        Assert.Equal(["1234567890123"], first.AddedNumbers);
        Assert.Equal("1234567890123", first.WrittenNumber);

        PhonebookIndexUpdate duplicate = index.Apply(Mutation(2, EmptyRecord(), alice));
        Assert.Empty(duplicate.AddedNumbers);
        Assert.Equal("1234567890123", duplicate.WrittenNumber);
        Assert.Equal(2, index.UsedRecordCount);

        PhonebookIndexUpdate deleteOne = index.Apply(Mutation(1, alice, EmptyRecord()));
        Assert.Empty(deleteOne.RemovedNumbers);
        Assert.True(index.ContainsNumber("1234567890123"));

        PhonebookIndexUpdate deleteFinal = index.Apply(Mutation(2, alice, EmptyRecord()));
        Assert.Equal(["1234567890123"], deleteFinal.RemovedNumbers);
        Assert.False(index.ContainsNumber("1234567890123"));
    }

    [Fact]
    public void RebuildCapsOrdinaryCapacityAndIgnoresManagedRecord()
    {
        byte[] file = Enumerable.Repeat(
            (byte)0xFF,
            SimCard.AdnRecordCount * SimPhonebookCodec.RecordLength).ToArray();
        for (int record = 1; record <= SimCard.OrdinaryAdnRecordCount; record++)
        {
            string number = record.ToString("D13");
            SimPhonebookCodec.Encode($"C{record}", number).CopyTo(
                file,
                (record - 1) * SimPhonebookCodec.RecordLength);
        }
        SimPhonebookCodec.Encode("My Number", "999999999999999").CopyTo(
            file,
            (SimCard.ManagedOwnNumberRecord - 1) * SimPhonebookCodec.RecordLength);

        PhonebookContactIndex index = new();
        index.Apply(new SimMutation(0x7F10, 0x6F3A, 0, new byte[file.Length], file, SimMutationOrigin.PersistenceRestore));

        Assert.True(index.IsFull);
        Assert.Equal(SimCard.OrdinaryAdnRecordCount, index.UsedRecordCount);
        Assert.False(index.ContainsNumber("999999999999999"));
    }

    [Fact]
    public void InternationalAndNonNoksNumbersNeverEnterPairingIndex()
    {
        PhonebookContactIndex index = new();
        index.Apply(Mutation(1, EmptyRecord(), SimPhonebookCodec.Encode("Intl", "+1234567890123")));
        index.Apply(Mutation(2, EmptyRecord(), SimPhonebookCodec.Encode("Short", "1234567")));
        Assert.Equal(0, index.UsedRecordCount);
    }

    private static SimMutation Mutation(int record, byte[] oldValue, byte[] newValue) =>
        new(0x7F10, 0x6F3A, record, oldValue, newValue, SimMutationOrigin.Firmware);

    private static byte[] EmptyRecord() =>
        Enumerable.Repeat((byte)0xFF, SimPhonebookCodec.RecordLength).ToArray();
}
