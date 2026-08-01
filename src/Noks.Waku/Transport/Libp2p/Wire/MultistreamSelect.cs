using System.Text;

namespace Noks.Waku.Transport.Libp2p.Wire;

internal static class MultistreamSelect
{
    public const string Protocol = "/multistream/1.0.0";
    public const string NotAvailable = "na";

    public static byte[] Encode(string protocol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        byte[] line = Encoding.UTF8.GetBytes($"{protocol}\n");
        return Libp2pVarint.Prefix(line);
    }

    public static bool TryDecode(ByteQueue input, out string protocol)
    {
        protocol = "";
        if (!Libp2pVarint.TryRead(input.Span, out ulong lengthValue, out int prefixLength))
            return false;

        int length = checked((int)lengthValue);
        if (length is < 1 or > 1024)
            throw new FormatException("Invalid multistream-select line length.");
        if (input.Count < prefixLength + length)
            return false;

        ReadOnlySpan<byte> line = input.Span.Slice(prefixLength, length);
        if (line[^1] != (byte)'\n')
            throw new FormatException("Multistream-select line is not newline terminated.");

        protocol = Encoding.UTF8.GetString(line[..^1]);
        input.Consume(prefixLength + length);
        return true;
    }
}
