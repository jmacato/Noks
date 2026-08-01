using Noks.Waku.Transport.Libp2p.Wire;

namespace Noks.Waku.Transport.Protocols;

internal static class WakuMetadata
{
    public const string Protocol = "/vac/waku/metadata/1.0.0";
    public const uint ClusterId = 1;

    public static byte[] EncodeLightClient()
    {
        ProtobufWriter response = new();
        response.WriteUInt32(1, ClusterId);
        return response.ToArray();
    }

    public static WakuMetadataResponse Decode(ReadOnlySpan<byte> encoded)
    {
        uint? clusterId = null;
        List<uint> shards = [];
        ProtobufReader reader = new(encoded);
        while (reader.TryReadTag(out int field, out int wireType))
        {
            if (field == 1 && wireType == 0)
            {
                clusterId = reader.ReadUInt32();
            }
            else if (field == 2 && wireType == 0)
            {
                shards.Add(reader.ReadUInt32());
            }
            else if (field == 3 && wireType == 2)
            {
                DecodePackedShards(reader.ReadBytes(), shards);
            }
            else
            {
                reader.Skip(wireType);
            }
        }

        return new WakuMetadataResponse(clusterId, shards.ToArray());
    }

    private static void DecodePackedShards(ReadOnlySpan<byte> encoded, List<uint> shards)
    {
        int offset = 0;
        while (offset < encoded.Length)
        {
            if (!Libp2pVarint.TryRead(encoded[offset..], out ulong value, out int length))
                throw new FormatException("Waku Metadata contains a truncated shard.");
            shards.Add(checked((uint)value));
            offset += length;
        }
    }
}
