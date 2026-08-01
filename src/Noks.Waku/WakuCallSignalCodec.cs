using System.Buffers.Binary;

namespace Noks.Waku;

public static class WakuCallSignalCodec
{
    public const int HeaderSize = 44;
    public const int MaximumChunkDataSize = WakuEnvelopeCodec.MaximumPayloadSize - HeaderSize;
    public const int MaximumSignalSize = 256 * 1024;

    private static ReadOnlySpan<byte> Magic => "NCS1"u8;

    public static IReadOnlyList<byte[]> EncodeFragments(
        Guid attemptId,
        Guid signalId,
        ReadOnlySpan<byte> data)
    {
        if (attemptId == Guid.Empty)
            throw new ArgumentException("A call attempt identifier is required.", nameof(attemptId));
        if (signalId == Guid.Empty)
            throw new ArgumentException("A signal identifier is required.", nameof(signalId));
        if (data.Length > MaximumSignalSize)
            throw new ArgumentException($"A call signal cannot exceed {MaximumSignalSize} bytes.", nameof(data));

        var chunkCount = Math.Max(1, (data.Length + MaximumChunkDataSize - 1) / MaximumChunkDataSize);
        var output = new byte[chunkCount][];
        for (var index = 0; index < chunkCount; index++)
        {
            var offset = index * MaximumChunkDataSize;
            var length = Math.Min(MaximumChunkDataSize, data.Length - offset);
            var encoded = new byte[HeaderSize + length];
            Magic.CopyTo(encoded);
            attemptId.TryWriteBytes(encoded.AsSpan(4, 16), bigEndian: true, out _);
            signalId.TryWriteBytes(encoded.AsSpan(20, 16), bigEndian: true, out _);
            BinaryPrimitives.WriteUInt16BigEndian(encoded.AsSpan(36, 2), checked((ushort)index));
            BinaryPrimitives.WriteUInt16BigEndian(encoded.AsSpan(38, 2), checked((ushort)chunkCount));
            BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(40, 4), data.Length);
            data.Slice(offset, length).CopyTo(encoded.AsSpan(HeaderSize));
            output[index] = encoded;
        }

        return output;
    }

    public static bool TryDecode(ReadOnlySpan<byte> encoded, out WakuCallSignalFragment? fragment)
    {
        fragment = null;
        if (encoded.Length < HeaderSize || !encoded[..4].SequenceEqual(Magic))
            return false;

        var attemptId = new Guid(encoded.Slice(4, 16), bigEndian: true);
        var signalId = new Guid(encoded.Slice(20, 16), bigEndian: true);
        var chunkIndex = BinaryPrimitives.ReadUInt16BigEndian(encoded.Slice(36, 2));
        var chunkCount = BinaryPrimitives.ReadUInt16BigEndian(encoded.Slice(38, 2));
        var totalLength = BinaryPrimitives.ReadInt32BigEndian(encoded.Slice(40, 4));
        if (attemptId == Guid.Empty || signalId == Guid.Empty || chunkCount == 0 ||
            chunkIndex >= chunkCount || totalLength < 0 || totalLength > MaximumSignalSize ||
            encoded.Length - HeaderSize > MaximumChunkDataSize)
            return false;

        var expectedChunkCount = Math.Max(1, (totalLength + MaximumChunkDataSize - 1) / MaximumChunkDataSize);
        var expectedLength = chunkIndex + 1 == chunkCount
            ? totalLength - (chunkCount - 1) * MaximumChunkDataSize
            : MaximumChunkDataSize;
        if (chunkCount != expectedChunkCount || expectedLength < 0 || encoded.Length - HeaderSize != expectedLength)
            return false;

        fragment = new WakuCallSignalFragment(
            attemptId,
            signalId,
            chunkIndex,
            chunkCount,
            totalLength,
            encoded[HeaderSize..].ToArray());
        return true;
    }

    public static bool TryReassemble(
        IEnumerable<WakuCallSignalFragment> fragments,
        out byte[]? data)
    {
        ArgumentNullException.ThrowIfNull(fragments);
        data = null;
        var ordered = fragments.OrderBy(fragment => fragment.ChunkIndex).ToArray();
        if (ordered.Length == 0)
            return false;

        var first = ordered[0];
        if (ordered.Length != first.ChunkCount)
            return false;
        var output = new byte[first.TotalLength];
        var offset = 0;
        for (var index = 0; index < ordered.Length; index++)
        {
            var fragment = ordered[index];
            if (fragment.AttemptId != first.AttemptId || fragment.SignalId != first.SignalId ||
                fragment.ChunkIndex != index || fragment.ChunkCount != first.ChunkCount ||
                fragment.TotalLength != first.TotalLength || offset + fragment.Data.Length > output.Length)
                return false;
            fragment.Data.Span.CopyTo(output.AsSpan(offset));
            offset += fragment.Data.Length;
        }

        if (offset != output.Length)
            return false;
        data = output;
        return true;
    }
}
