namespace Noks.Dct3.Radio;

internal static class RadioTraceFormat
{

    internal static string DumpPayload(ReadOnlySpan<byte> payload)
    {
        int count = Math.Min(payload.Length, 32);
        string dump = string.Join(' ', payload[..count].ToArray().Select(value => $"{value:X2}"));
        return payload.Length > count ? dump + " ..." : dump;
    }

    internal static string SanitizeTraceText(string value)
    {
        string text = new(value.Where(ch => ch is >= ' ' and <= '~').Take(32).ToArray());
        return text.Length == 0 ? "default" : text;
    }

}
