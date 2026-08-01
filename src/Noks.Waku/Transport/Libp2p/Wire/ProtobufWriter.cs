using System.Buffers;
using System.Text;

namespace Noks.Waku.Transport.Libp2p.Wire;

internal sealed class ProtobufWriter
{
    private readonly ArrayBufferWriter<byte> output = new();

    public void WriteUInt32(int fieldNumber, uint value)
    {
        WriteTag(fieldNumber, 0);
        WriteVarint(value);
    }

    public void WriteUInt64(int fieldNumber, ulong value)
    {
        WriteTag(fieldNumber, 0);
        WriteVarint(value);
    }

    public void WriteSInt64(int fieldNumber, long value)
    {
        WriteTag(fieldNumber, 0);
        WriteVarint(unchecked((ulong)((value << 1) ^ (value >> 63))));
    }

    public void WriteBool(int fieldNumber, bool value) => WriteUInt32(fieldNumber, value ? 1u : 0u);

    public void WriteString(int fieldNumber, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteTag(fieldNumber, 2);
        int byteCount = Encoding.UTF8.GetByteCount(value);
        WriteVarint((ulong)byteCount);
        Span<byte> destination = output.GetSpan(byteCount);
        int written = Encoding.UTF8.GetBytes(value, destination);
        output.Advance(written);
    }

    public void WriteBytes(int fieldNumber, ReadOnlySpan<byte> value)
    {
        WriteTag(fieldNumber, 2);
        WriteVarint((ulong)value.Length);
        value.CopyTo(output.GetSpan(value.Length));
        output.Advance(value.Length);
    }

    public void WriteMessage(int fieldNumber, Action<ProtobufWriter> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ProtobufWriter nested = new();
        write(nested);
        WriteBytes(fieldNumber, nested.WrittenSpan);
    }

    public byte[] ToArray() => WrittenSpan.ToArray();

    public ReadOnlySpan<byte> WrittenSpan => output.WrittenSpan;

    private void WriteTag(int fieldNumber, int wireType)
    {
        if (fieldNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(fieldNumber));
        WriteVarint(checked((ulong)((fieldNumber << 3) | wireType)));
    }

    private void WriteVarint(ulong value)
    {
        Span<byte> destination = output.GetSpan(Libp2pVarint.MaximumEncodedLength);
        int written = Libp2pVarint.Write(destination, value);
        output.Advance(written);
    }
}
