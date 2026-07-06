using System.Text;
using Noks.Dct3.Firmware;

namespace Noks.Dct3.Tests;

public sealed class FirmwareOperatorDatabaseTests
{
    [Fact]
    public void Parse_DecodesPhilippineOperatorBlock()
    {
        byte[] firmware = CreateFirmwareTable(
            "PH",
            "515",
            ("01", "Islacom"),
            ("02", "GLOBE"),
            ("03", "SMART Gold"));

        IReadOnlyList<FirmwareMobileOperator> operators = FirmwareOperatorDatabase.Parse(firmware);

        Assert.Collection(
            operators,
            item => Assert.Equal(new FirmwareMobileOperator("PH", "515", "01", "Islacom"), item),
            item => Assert.Equal(new FirmwareMobileOperator("PH", "515", "02", "GLOBE"), item),
            item => Assert.Equal(new FirmwareMobileOperator("PH", "515", "03", "SMART Gold"), item));
        Assert.Equal("51503", operators[2].Plmn);
    }

    [Fact]
    public void Parse_WithoutOperatorTable_ReturnsEmptyList()
    {
        Assert.Empty(FirmwareOperatorDatabase.Parse(new byte[256]));
    }

    [Fact]
    public void Parse_IgnoresMalformedOperatorRecord()
    {
        byte[] firmware = CreateFirmwareTable("PH", "515", ("01", "GLOBE"));
        int nameLengthOffset = firmware.AsSpan().IndexOf("GLOBE"u8) - 1;
        firmware[nameLengthOffset] = 0xFF;

        Assert.Empty(FirmwareOperatorDatabase.Parse(firmware));
    }

    private static byte[] CreateFirmwareTable(
        string countryTag,
        string mcc,
        params (string Mnc, string Name)[] operators)
    {
        List<byte> bytes = [.. "PLMNV9.00"u8.ToArray(), 0, 0, 0];
        int countryOffset = bytes.Count;
        bytes.AddRange("CNTR"u8.ToArray());
        bytes.AddRange(new byte[4]);
        bytes.AddRange(Encoding.ASCII.GetBytes(countryTag.PadRight(3, '\0')));
        bytes.Add(0);
        bytes.Add(EncodeBcd(mcc[0], mcc[1]));
        bytes.Add((byte)(0xF0 | (mcc[2] - '0')));
        bytes.Add((byte)operators.Length);
        bytes.Add(0);

        foreach ((string mnc, string name) in operators)
        {
            byte[] encodedName = Encoding.ASCII.GetBytes(name);
            bytes.Add(0);
            bytes.Add(EncodeBcd(mnc[0], mnc[1]));
            bytes.Add(0);
            bytes.Add((byte)encodedName.Length);
            bytes.AddRange(encodedName);
            while ((bytes.Count - countryOffset - 16) % 4 != 0)
            {
                bytes.Add(0);
            }
        }

        bytes.AddRange(new byte[32]);
        return bytes.ToArray();
    }

    private static byte EncodeBcd(char first, char second) =>
        (byte)((second - '0') << 4 | (first - '0'));
}
