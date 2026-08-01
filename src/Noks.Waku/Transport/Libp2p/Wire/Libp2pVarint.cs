namespace Noks.Waku.Transport.Libp2p.Wire;

internal static class Libp2pVarint
{
    public const int MaximumEncodedLength = 10;

    public static int GetEncodedLength(ulong value)
    {
        int length = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            length++;
        }

        return length;
    }

    public static int Write(Span<byte> destination, ulong value)
    {
        int offset = 0;
        do
        {
            byte next = (byte)(value & 0x7f);
            value >>= 7;
            if (value != 0)
                next |= 0x80;
            destination[offset++] = next;
        }
        while (value != 0);

        return offset;
    }

    public static bool TryRead(ReadOnlySpan<byte> source, out ulong value, out int bytesRead)
    {
        value = 0;
        bytesRead = 0;

        for (int shift = 0; shift < 64 && bytesRead < source.Length; shift += 7)
        {
            byte next = source[bytesRead++];
            if (shift == 63 && (next & 0xfe) != 0)
                throw new FormatException("Unsigned varint exceeds 64 bits.");

            value |= (ulong)(next & 0x7f) << shift;
            if ((next & 0x80) == 0)
                return true;
        }

        if (bytesRead >= MaximumEncodedLength)
            throw new FormatException("Unsigned varint is too long.");

        value = 0;
        bytesRead = 0;
        return false;
    }

    public static byte[] Prefix(ReadOnlySpan<byte> payload)
    {
        int prefixLength = GetEncodedLength((ulong)payload.Length);
        byte[] result = new byte[prefixLength + payload.Length];
        Write(result, (ulong)payload.Length);
        payload.CopyTo(result.AsSpan(prefixLength));
        return result;
    }
}
