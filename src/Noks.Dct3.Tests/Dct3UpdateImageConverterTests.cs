using Noks.Dct3.Display;

namespace Noks.Dct3.Tests;

public sealed class Dct3UpdateImageConverterTests
{
    [Fact]
    public void Convert_DecodesRecordsByDestinationAddress()
    {
        byte[] part =
        [
            0x0B, 0x20, 0x00, 0x00, 0x11, 0x00, 0x00, 0x04, 0xAE,
            0x01, 0x02, 0x03, 0x04,
            0x0B, 0x34, 0x00, 0x00, 0x22, 0x00, 0x00, 0x02, 0xF6,
            0x05, 0x06,
        ];

        byte[] flash = Dct3UpdateImageConverter.Convert(
            [new Dct3UpdateImagePart("test", part)],
            out Dct3UpdateImagePartSummary[] summaries);

        Assert.Equal([0x01, 0x02, 0x03, 0x04], flash.AsSpan(0, 4).ToArray());
        Assert.Equal([0x05, 0x06], flash.AsSpan(0x140000, 2).ToArray());
        Assert.Equal(0xFF, flash[0x1000]);
        Assert.Equal(new Dct3UpdateImagePartSummary("test", 2, 0x200000, 0x340002), summaries[0]);
    }

    [Fact]
    public void Convert_RejectsBadRecordMarker()
    {
        byte[] part = [0x00, 0x20, 0x00, 0x00, 0x11, 0x00, 0x00, 0x01, 0xAE, 0x01];

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() =>
            Dct3UpdateImageConverter.Convert([new Dct3UpdateImagePart("bad", part)], out _));

        Assert.Contains("Record marker 0x00 at 0x0 is invalid.", ex.Message);
    }

    [Fact]
    public void TryGetFirstRecordAddress_ReadsHeaderAddress()
    {
        byte[] header = [0x0B, 0x3D, 0x00, 0x00, 0x11, 0x00, 0x20, 0x00, 0xAE];

        bool found = Dct3UpdateImageConverter.TryGetFirstRecordAddress(header, out uint address);

        Assert.True(found);
        Assert.Equal(0x3D0000u, address);
    }
}
