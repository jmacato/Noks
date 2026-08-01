namespace Noks.Waku.Transport.Libp2p.Discovery;

internal static class Rlp
{
    public static IReadOnlyList<byte[]> DecodeFlatList(ReadOnlySpan<byte> encoded)
    {
        ReadOnlySpan<byte> payload = ReadItem(encoded, out bool isList, out int consumed);
        if (!isList || consumed != encoded.Length)
            throw new FormatException("Expected one complete RLP list.");

        List<byte[]> values = [];
        int offset = 0;
        while (offset < payload.Length)
        {
            ReadOnlySpan<byte> value = ReadItem(payload[offset..], out bool nested, out int itemLength);
            if (nested)
                throw new FormatException("Nested RLP lists are not supported in ENR records.");
            values.Add(value.ToArray());
            offset += itemLength;
        }

        return values;
    }

    private static ReadOnlySpan<byte> ReadItem(
        ReadOnlySpan<byte> encoded,
        out bool isList,
        out int consumed)
    {
        if (encoded.IsEmpty)
            throw new FormatException("Truncated RLP item.");

        byte prefix = encoded[0];
        if (prefix <= 0x7f)
        {
            isList = false;
            consumed = 1;
            return encoded[..1];
        }

        if (prefix <= 0xb7)
            return ReadPayload(encoded, prefix - 0x80, 1, false, out isList, out consumed);
        if (prefix <= 0xbf)
            return ReadLongPayload(encoded, prefix - 0xb7, false, out isList, out consumed);
        if (prefix <= 0xf7)
            return ReadPayload(encoded, prefix - 0xc0, 1, true, out isList, out consumed);
        return ReadLongPayload(encoded, prefix - 0xf7, true, out isList, out consumed);
    }

    private static ReadOnlySpan<byte> ReadLongPayload(
        ReadOnlySpan<byte> encoded,
        int lengthOfLength,
        bool list,
        out bool isList,
        out int consumed)
    {
        if (lengthOfLength is < 1 or > 4 || encoded.Length < 1 + lengthOfLength)
            throw new FormatException("Invalid RLP length prefix.");
        if (encoded[1] == 0)
            throw new FormatException("RLP length contains a leading zero.");

        int length = 0;
        for (int index = 0; index < lengthOfLength; index++)
            length = checked((length << 8) | encoded[1 + index]);
        if (length < 56)
            throw new FormatException("Non-canonical long RLP length.");

        return ReadPayload(encoded, length, 1 + lengthOfLength, list, out isList, out consumed);
    }

    private static ReadOnlySpan<byte> ReadPayload(
        ReadOnlySpan<byte> encoded,
        int length,
        int prefixLength,
        bool list,
        out bool isList,
        out int consumed)
    {
        consumed = checked(prefixLength + length);
        if (encoded.Length < consumed)
            throw new FormatException("Truncated RLP payload.");

        isList = list;
        return encoded.Slice(prefixLength, length);
    }
}
