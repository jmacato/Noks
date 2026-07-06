using Noks.Dct3.Core;
using Noks.Dct3.Sim;
using Noks.Dct3.State;
namespace Noks.Dct3.Tests;

public sealed class SimCardTests
{
    private const int SmsStorageRecordLength = 176;
    private const int SmsStorageRecordCount = byte.MaxValue;

    [Fact]
    public void AnswerToReset_UsesConfiguredAtr()
    {
        byte[] atr = SimCard.CreateLegacyDirectAnswerToReset();
        SimCard sim = new(null, answerToReset: atr);

        Assert.Equal(atr, sim.AnswerToReset().ToArray());
    }

    [Fact]
    public void ServiceProviderName_UsesConfiguredOperatorName()
    {
        SimCard sim = new(null, serviceProviderName: "Alice Mobile");

        byte[] serviceProviderName = ReadServiceProviderName(sim);
        byte[] cphsOperatorName = ReadTransparentFile(sim, 0x6F14, 20);

        Assert.Equal(0x00, serviceProviderName[0]);
        Assert.Equal("Alice Mobile", System.Text.Encoding.ASCII.GetString(serviceProviderName, 1, 12));
        Assert.All(serviceProviderName[13..], value => Assert.Equal(0xFF, value));
        Assert.Equal("Alice Mobile", System.Text.Encoding.ASCII.GetString(cphsOperatorName, 0, 12));
        Assert.All(cphsOperatorName[12..], value => Assert.Equal(0xFF, value));
    }

    [Fact]
    public void ServiceProviderName_IsBlankByDefault()
    {
        SimCard sim = new(null);

        byte[] serviceProviderName = ReadServiceProviderName(sim);
        byte[] cphsOperatorName = ReadTransparentFile(sim, 0x6F14, 20);

        Assert.Equal(0x00, serviceProviderName[0]);
        Assert.All(serviceProviderName[1..], value => Assert.Equal(0xFF, value));
        Assert.All(cphsOperatorName, value => Assert.Equal(0xFF, value));
    }

    [Fact]
    public void MachineSettings_ConfigureSimServiceProviderName()
    {
        Dct3Machine machine = new(
            new byte[0x200000],
            settings: new Dct3PhoneSettings(NetworkName: "Test Operator"));

        byte[] serviceProviderName = ReadServiceProviderName(machine.Sim);

        Assert.Equal("Test Operator", System.Text.Encoding.ASCII.GetString(serviceProviderName, 1, 13));
    }

    [Fact]
    public void PhonebookCodec_RoundTripsLocalAndInternationalNumbers()
    {
        byte[] local = SimPhonebookCodec.Encode("Alice", "12345678 901-2345");
        byte[] international = SimPhonebookCodec.Encode("Bob", "+63 917-555-0123");

        Assert.True(SimPhonebookCodec.TryDecode(local, out string localName, out string localNumber));
        Assert.Equal("Alice", localName);
        Assert.Equal("123456789012345", localNumber);
        Assert.True(SimPhonebookCodec.TryDecode(international, out string internationalName, out string internationalNumber));
        Assert.Equal("Bob", internationalName);
        Assert.Equal("+639175550123", internationalNumber);
    }

    [Fact]
    public void PhonebookCodec_AcceptsTwentyDigitsAndRejectsTwentyOne()
    {
        const string maximum = "12345678901234567890";
        byte[] record = SimPhonebookCodec.Encode("Maximum", maximum);

        Assert.True(SimPhonebookCodec.TryDecode(record, out _, out string decoded));
        Assert.Equal(maximum, decoded);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SimPhonebookCodec.Encode("Too long", $"{maximum}1"));
    }

    [Fact]
    public void PhonebookCodec_UsesEfAdnAlphabetAndSixteenByteLimit()
    {
        byte[] record = SimPhonebookCodec.Encode("Alice @_£", "1234567890123");

        Assert.Equal([0x41, 0x6C, 0x69, 0x63, 0x65, 0x20, 0x00, 0x11, 0x01], record[..9]);
        Assert.True(SimPhonebookCodec.TryDecode(record, out string name, out _));
        Assert.Equal("Alice @_£", name);
        Assert.True(SimPhonebookCodec.IsValidAlphaIdentifier("Mixed Case 123"));
        Assert.False(SimPhonebookCodec.IsValidAlphaIdentifier("abcdefghijklmnopq"));
    }

    [Theory]
    [InlineData("lively-orbit-jqqx", "lively-orbit-...")]
    [InlineData("Mañana 🚀 calling", "Mañana _ calling")]
    [InlineData("Local Name", "Local Name")]
    public void PhonebookAlias_ReplacesUnsupportedCharactersAndUsesTrailingDots(
        string remoteName,
        string expected) =>
        Assert.Equal(expected, SimPhonebookCodec.CreateAlphaIdentifierAlias(remoteName));

    [Fact]
    public void Select_AdnAdvertisesStockMaximumAndManagedLastRecord()
    {
        SimCard sim = new(null, ownPhoneNumber: "123456789012345");
        SelectAdn(sim);

        byte[] selectResponse = SendApdu(sim, 0xA0, 0xC0, 0x00, 0x00, 0x0F);
        int advertisedSize = SimPhonebookCodec.RecordLength * SimCard.AdnRecordCount;
        Assert.Equal((byte)(advertisedSize >> 8), selectResponse[3]);
        Assert.Equal((byte)advertisedSize, selectResponse[4]);
        Assert.Equal(0x01, selectResponse[14]);
        Assert.Equal(SimPhonebookCodec.RecordLength, selectResponse[15]);

        byte[] record = ReadSelectedRecord(sim, SimCard.ManagedOwnNumberRecord);
        Assert.True(SimPhonebookCodec.TryDecode(record, out string name, out string number));
        Assert.Equal("My Number", name);
        Assert.Equal("123456789012345", number);
    }

    [Fact]
    public void Adn_RecordsOneThrough249RemainOrdinaryAndManagedRecordRepairsFirmwareWrites()
    {
        SimCard sim = new(null, ownPhoneNumber: "123456789012345");
        List<SimMutation> mutations = [];
        sim.MutationCommitted += mutations.Add;
        SelectAdn(sim);

        byte[] ordinary = SimPhonebookCodec.Encode("Contact", "5550001");
        for (int recordNumber = 1; recordNumber <= SimCard.OrdinaryAdnRecordCount; recordNumber++)
        {
            Assert.Equal(
                [0xDC, 0x90, 0x00],
                SendApdu(sim, [0xA0, 0xDC, (byte)recordNumber, 0x04, SimPhonebookCodec.RecordLength, .. ordinary]));
        }

        Assert.All(
            Enumerable.Range(1, SimCard.OrdinaryAdnRecordCount),
            recordNumber => Assert.Equal(ordinary, ReadSelectedRecord(sim, recordNumber)));

        byte[] attempted = SimPhonebookCodec.Encode("Overwrite", "9999999");
        Assert.Equal(
            [0xDC, 0x90, 0x00],
            SendApdu(sim, [0xA0, 0xDC, (byte)SimCard.ManagedOwnNumberRecord, 0x04, SimPhonebookCodec.RecordLength, .. attempted]));

        byte[] managed = ReadSelectedRecord(sim, SimCard.ManagedOwnNumberRecord);
        Assert.True(SimPhonebookCodec.TryDecode(managed, out string name, out string number));
        Assert.Equal("My Number", name);
        Assert.Equal("123456789012345", number);
        Assert.Equal(SimMutationOrigin.Firmware, mutations[^2].Origin);
        Assert.Equal(SimMutationOrigin.ManagedRepair, mutations[^1].Origin);
        Assert.Equal(SimCard.ManagedOwnNumberRecord, mutations[^1].RecordNumber);

        byte[] free = Enumerable.Repeat((byte)0xFF, SimPhonebookCodec.RecordLength).ToArray();
        SendApdu(sim, [0xA0, 0xDC, 0x2A, 0x04, SimPhonebookCodec.RecordLength, .. free]);
        Assert.Equal(free, ReadSelectedRecord(sim, 42));
        Assert.Equal(SimMutationOrigin.Firmware, mutations[^1].Origin);
        Assert.Equal(42, mutations[^1].RecordNumber);
    }

    [Fact]
    public void ManagedOwnNumber_UpdatesAdnAndMsisdnTogether()
    {
        SimCard sim = new(null);
        List<SimMutation> mutations = [];
        sim.MutationCommitted += mutations.Add;

        sim.SetManagedOwnNumber("98765432 109-8765");

        SelectAdn(sim);
        AssertRecordNumber(ReadSelectedRecord(sim, SimCard.ManagedOwnNumberRecord), "987654321098765");
        SelectMsisdn(sim);
        AssertRecordNumber(ReadSelectedRecord(sim, 1), "987654321098765");
        Assert.Collection(
            mutations,
            mutation =>
            {
                Assert.Equal(0x6F3A, mutation.FileId);
                Assert.Equal(SimMutationOrigin.Host, mutation.Origin);
            },
            mutation =>
            {
                Assert.Equal(0x6F40, mutation.FileId);
                Assert.Equal(SimMutationOrigin.Host, mutation.Origin);
            });
    }

    [Fact]
    public void ApplyOverlay_CannotReplaceManagedOwnNumberRecords()
    {
        byte[] replacement = SimPhonebookCodec.Encode("Not Mine", "2222222");
        byte[] adn = Enumerable.Repeat(
            (byte)0xFF,
            SimPhonebookCodec.RecordLength * SimCard.AdnRecordCount).ToArray();
        replacement.CopyTo(
            adn,
            (SimCard.ManagedOwnNumberRecord - 1) * SimPhonebookCodec.RecordLength);

        SimCard restored = new(null, ownPhoneNumber: "333333333333333");
        restored.ApplyOverlay(
        [
            new SimFileOverlay(0x7F10, 0x6F3A, adn),
            new SimFileOverlay(0x7F10, 0x6F40, replacement),
        ]);

        SelectAdn(restored);
        AssertRecordNumber(ReadSelectedRecord(restored, SimCard.ManagedOwnNumberRecord), "333333333333333");
        SelectMsisdn(restored);
        AssertRecordNumber(ReadSelectedRecord(restored, 1), "333333333333333");
    }

    [Fact]
    public void Select_SmsStorage_AdvertisesMaximumAddressableRecords()
    {
        SimCard sim = new(null);
        SelectSmsStorage(sim);

        byte[] response = SendApdu(sim, 0xA0, 0xC0, 0x00, 0x00, 0x0F);

        Assert.Equal(0xC0, response[0]);
        Assert.Equal(0x90, response[^2]);
        Assert.Equal(0x00, response[^1]);

        ReadOnlySpan<byte> selectResponse = response.AsSpan(1, 15);
        int advertisedSize = SmsStorageRecordLength * SmsStorageRecordCount;
        Assert.Equal((byte)(advertisedSize >> 8), selectResponse[2]);
        Assert.Equal((byte)advertisedSize, selectResponse[3]);
        Assert.Equal(0x01, selectResponse[13]);
        Assert.Equal(SmsStorageRecordLength, selectResponse[14]);
    }

    [Fact]
    public void ReadRecord_SmsStorage_ReturnsFreeRecords()
    {
        SimCard sim = new(null);
        SelectSmsStorage(sim);

        byte[] response = SendApdu(sim, 0xA0, 0xB2, 0x01, 0x04, 0xB0);

        Assert.Equal(0xB2, response[0]);
        Assert.Equal(0x90, response[^2]);
        Assert.Equal(0x00, response[^1]);

        ReadOnlySpan<byte> record = response.AsSpan(1, SmsStorageRecordLength);
        Assert.Equal(0x00, record[0]);
        Assert.True(record[1..].ToArray().All(value => value == 0xFF));
    }

    [Fact]
    public void ReadRecord_SmsStorage_ReturnsLastAddressableFreeRecord()
    {
        SimCard sim = new(null);
        SelectSmsStorage(sim);

        byte[] response = SendApdu(sim, 0xA0, 0xB2, 0xFF, 0x04, 0xB0);

        Assert.Equal(0xB2, response[0]);
        Assert.Equal(0x90, response[^2]);
        Assert.Equal(0x00, response[^1]);

        ReadOnlySpan<byte> record = response.AsSpan(1, SmsStorageRecordLength);
        Assert.Equal(0x00, record[0]);
        Assert.True(record[1..].ToArray().All(value => value == 0xFF));
    }

    [Fact]
    public void UpdateRecord_SmsStorage_AcceptsSingleProcedureByteAndPersistsRecord()
    {
        SimCard sim = new(null);
        SelectSmsStorage(sim);
        byte[] record = Enumerable.Repeat((byte)0xFF, SmsStorageRecordLength).ToArray();
        record[0] = 0x03;
        record[1] = 0x06;
        record[2] = 0x91;
        record[3] = 0x21;
        record[4] = 0x43;

        byte[] response = SendApdu(sim, [0xA0, 0xDC, 0x01, 0x04, 0xB0, .. record]);

        Assert.Equal([0xDC, 0x90, 0x00], response);

        byte[] readResponse = SendApdu(sim, 0xA0, 0xB2, 0x01, 0x04, 0xB0);
        Assert.Equal(record, readResponse.AsSpan(1, SmsStorageRecordLength).ToArray());
    }

    [Fact]
    public void ReadRecord_SmsParameters_ReturnsDefaultServiceCentreAddress()
    {
        SimCard sim = new(null);
        SendApdu(sim, 0xA0, 0xA4, 0x00, 0x00, 0x02, 0x7F, 0x10);
        SendApdu(sim, 0xA0, 0xA4, 0x00, 0x00, 0x02, 0x6F, 0x42);

        byte[] response = SendApdu(sim, 0xA0, 0xB2, 0x01, 0x04, 0x2C);

        Assert.Equal(0xB2, response[0]);
        Assert.Equal(0x90, response[^2]);
        Assert.Equal(0x00, response[^1]);

        ReadOnlySpan<byte> record = response.AsSpan(1, 44);
        Assert.Equal(0xFD, record[16]);
        Assert.Equal([0x06, 0x91, 0x21, 0x43, 0x65, 0x87, 0x09, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], record.Slice(29, 12).ToArray());
        Assert.True(record.Slice(17, 12).ToArray().All(value => value == 0xFF));
    }

    private static byte[] SendApdu(SimCard sim, params byte[] bytes)
    {
        List<byte> responseBytes = [];

        foreach (byte value in bytes)
        {
            SimCardResponse? response = sim.Transmit(value);

            if (response is not null)
            {
                responseBytes.AddRange(response.Value.Data);
            }
        }

        return responseBytes.ToArray();
    }

    private static void SelectSmsStorage(SimCard sim)
    {
        SendApdu(sim, 0xA0, 0xA4, 0x00, 0x00, 0x02, 0x7F, 0x10);
        SendApdu(sim, 0xA0, 0xA4, 0x00, 0x00, 0x02, 0x6F, 0x3C);
    }

    private static void SelectAdn(SimCard sim) => SelectTelecomFile(sim, 0x6F3A);

    private static void SelectMsisdn(SimCard sim) => SelectTelecomFile(sim, 0x6F40);

    private static void SelectTelecomFile(SimCard sim, ushort fileId)
    {
        SendApdu(sim, 0xA0, 0xA4, 0x00, 0x00, 0x02, 0x7F, 0x10);
        SendApdu(sim, 0xA0, 0xA4, 0x00, 0x00, 0x02, (byte)(fileId >> 8), (byte)fileId);
    }

    private static byte[] ReadSelectedRecord(SimCard sim, int recordNumber)
    {
        byte[] response = SendApdu(
            sim,
            0xA0,
            0xB2,
            (byte)recordNumber,
            0x04,
            SimPhonebookCodec.RecordLength);
        Assert.Equal(0xB2, response[0]);
        Assert.Equal([0x90, 0x00], response[^2..]);
        return response[1..^2];
    }

    private static void AssertRecordNumber(byte[] record, string expectedNumber)
    {
        Assert.True(SimPhonebookCodec.TryDecode(record, out string name, out string number));
        Assert.Equal("My Number", name);
        Assert.Equal(expectedNumber, number);
    }

    private static byte[] ReadServiceProviderName(SimCard sim)
        => ReadTransparentFile(sim, 0x6F46, 17);

    private static byte[] ReadTransparentFile(SimCard sim, ushort fileId, int length)
    {
        SendApdu(sim, 0xA0, 0xA4, 0x00, 0x00, 0x02, 0x7F, 0x20);
        SendApdu(sim, 0xA0, 0xA4, 0x00, 0x00, 0x02, (byte)(fileId >> 8), (byte)fileId);
        byte[] response = SendApdu(sim, 0xA0, 0xB0, 0x00, 0x00, (byte)length);

        Assert.Equal(0xB0, response[0]);
        Assert.Equal(0x90, response[^2]);
        Assert.Equal(0x00, response[^1]);
        return response[1..^2];
    }
}
