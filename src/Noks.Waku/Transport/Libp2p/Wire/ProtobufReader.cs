using System.Text;

namespace Noks.Waku.Transport.Libp2p.Wire;

internal ref struct ProtobufReader
{
    private readonly ReadOnlySpan<byte> input;
    private int offset;

    public ProtobufReader(ReadOnlySpan<byte> input)
    {
        this.input = input;
    }

    public bool End => offset == input.Length;

    public bool TryReadTag(out int fieldNumber, out int wireType)
    {
        if (End)
        {
            fieldNumber = 0;
            wireType = 0;
            return false;
        }

        ulong tag = ReadVarint();
        fieldNumber = checked((int)(tag >> 3));
        wireType = (int)(tag & 7);
        if (fieldNumber == 0)
            throw new FormatException("Protobuf field number cannot be zero.");
        return true;
    }

    public ulong ReadUInt64() => ReadVarint();

    public uint ReadUInt32() => checked((uint)ReadVarint());

    public long ReadSInt64()
    {
        ulong value = ReadVarint();
        return unchecked((long)((value >> 1) ^ (ulong)-(long)(value & 1)));
    }

    public bool ReadBool() => ReadVarint() != 0;

    public byte[] ReadBytes()
    {
        int length = checked((int)ReadVarint());
        EnsureAvailable(length);
        byte[] result = input.Slice(offset, length).ToArray();
        offset += length;
        return result;
    }

    public string ReadString() => Encoding.UTF8.GetString(ReadBytes());

    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0:
                _ = ReadVarint();
                break;
            case 1:
                EnsureAvailable(8);
                offset += 8;
                break;
            case 2:
                int length = checked((int)ReadVarint());
                EnsureAvailable(length);
                offset += length;
                break;
            case 5:
                EnsureAvailable(4);
                offset += 4;
                break;
            default:
                throw new FormatException($"Unsupported protobuf wire type {wireType}.");
        }
    }

    private ulong ReadVarint()
    {
        if (!Libp2pVarint.TryRead(input[offset..], out ulong value, out int bytesRead))
            throw new FormatException("Truncated protobuf varint.");
        offset += bytesRead;
        return value;
    }

    private void EnsureAvailable(int length)
    {
        if (length < 0 || length > input.Length - offset)
            throw new FormatException("Truncated protobuf field.");
    }
}
