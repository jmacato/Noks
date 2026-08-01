namespace Noks.Waku.Transport.Libp2p.Wire;

internal sealed class ByteQueue
{
    private byte[] buffer;
    private int start;
    private int end;

    public ByteQueue(int initialCapacity = 4096)
    {
        buffer = new byte[initialCapacity];
    }

    public int Count => end - start;

    public ReadOnlySpan<byte> Span => buffer.AsSpan(start, Count);

    public void Append(ReadOnlySpan<byte> value)
    {
        EnsureCapacity(value.Length);
        value.CopyTo(buffer.AsSpan(end));
        end += value.Length;
    }

    public bool TryRead(int length, out byte[] value)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (Count < length)
        {
            value = [];
            return false;
        }

        value = buffer.AsSpan(start, length).ToArray();
        Consume(length);
        return true;
    }

    public void Consume(int length)
    {
        if ((uint)length > (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(length));

        start += length;
        if (start == end)
        {
            start = 0;
            end = 0;
        }
    }

    private void EnsureCapacity(int additionalLength)
    {
        int required = checked(Count + additionalLength);
        if (required <= buffer.Length - start)
        {
            if (end + additionalLength > buffer.Length)
            {
                buffer.AsSpan(start, Count).CopyTo(buffer);
                end = Count;
                start = 0;
            }

            return;
        }

        int capacity = buffer.Length;
        while (capacity < required)
            capacity = checked(capacity * 2);

        byte[] replacement = new byte[capacity];
        buffer.AsSpan(start, Count).CopyTo(replacement);
        end = Count;
        start = 0;
        buffer = replacement;
    }
}
