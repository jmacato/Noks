using Noks.Dct3.Messaging;
namespace Noks.Dct3.Tests;

public sealed class NokiaSmartMessagingRingtoneTests
{
    private const string DemoRingtonePayload =
        "024A3A613D919551BD29BDE4040A249647154154164184184164154134114114134154154136132000";

    [Fact]
    public void EncodeDemoRingtone_MatchesSmartMessagingGoldenPayload()
    {
        byte[] payload = NokiaSmartMessagingRingtone.EncodeDemoRingtone();

        Assert.Equal(41, payload.Length);
        Assert.Equal(DemoRingtonePayload, Convert.ToHexString(payload));
    }

    [Fact]
    public void Encode_AcceptsComposerStyleUnicodeRest()
    {
        byte[] asciiRest = NokiaSmartMessagingRingtone.Encode("Tone", 140, "8c2 8- 8g1");
        byte[] unicodeRest = NokiaSmartMessagingRingtone.Encode("Tone", 140, "8c2 8‐ 8g1");

        Assert.Equal(asciiRest, unicodeRest);
    }

    [Fact]
    public void Encode_RtttlDemoRingtoneMatchesComposerNotation()
    {
        const string rtttl = "OdeToJoy:d=4,o=5,b=125:e,e,f,g,g,f,e,d,c,c,d,e,e,8d,2d";

        byte[] payload = NokiaSmartMessagingRingtone.Encode("Ignored", 25, rtttl);

        Assert.Equal(DemoRingtonePayload, Convert.ToHexString(payload));
    }

    [Fact]
    public void EncodeRtttl_SharpAndDottedNoteMatchGammuEncoder()
    {
        byte[] payload = NokiaSmartMessagingRingtone.EncodeRtttl("Test:d=4,o=6,b=125:c,8d#.,16p,2a5");

        Assert.Equal("024A3A515195CDD0040A10A64711414690824D1000", Convert.ToHexString(payload));
    }

    [Fact]
    public void TryParseRtttlMetadata_ReturnsEmbeddedTitleAndDefaults()
    {
        bool parsed = NokiaSmartMessagingRingtone.TryParseRtttlMetadata(
            "Parsed:d=8,o=6,b=180:c,p,g",
            out NokiaSmartMessagingRingtone.RtttlMetadata metadata);

        Assert.True(parsed);
        Assert.Equal("Parsed", metadata.Title);
        Assert.Equal(8, metadata.DefaultDuration);
        Assert.Equal(6, metadata.DefaultOctave);
        Assert.Equal(180, metadata.BeatsPerMinute);
    }

    [Fact]
    public void EncodeRtttl_LargeTuneUsesThreeSmsParts()
    {
        string notes = string.Join(',', Enumerable.Repeat("c", 200));

        byte[] payload = NokiaSmartMessagingRingtone.EncodeRtttl($"Long:d=8,o=5,b=140:{notes}");

        Assert.Equal(3, NokiaSmartMessagingRingtone.GetSmsPartCount(payload.Length));
    }

    [Fact]
    public void EncodeRtttl_RejectsPayloadBeyondThreeSmsParts()
    {
        string notes = string.Join(',', Enumerable.Repeat("c", 252));

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => NokiaSmartMessagingRingtone.EncodeRtttl($"Long:d=8,o=5,b=140:{notes}"));

        Assert.Contains("at most 3 SMS parts", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EncodeRtttl_RejectsCommandCountThatCannotFitHeader()
    {
        string notes = string.Join(',', Enumerable.Repeat("c", 253));

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => NokiaSmartMessagingRingtone.EncodeRtttl($"TooLong:d=8,o=5,b=140:{notes}"));

        Assert.Contains("too many encoded commands", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EncodeRtttl_ManyScaleChangesFailWithFormatErrorInsteadOfBufferOverflow()
    {
        string notes = string.Join(
            ',',
            Enumerable.Range(0, 255).Select(index => index % 2 == 0 ? "c4" : "c7"));

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => NokiaSmartMessagingRingtone.EncodeRtttl($"Scales:d=8,o=5,b=140:{notes}"));

        Assert.Contains("too many encoded commands", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
