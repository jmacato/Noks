using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Noks.Waku.Transport.Protocols;

internal static class WakuSharding
{
    public const int ClusterId = 1;
    public const int ShardCount = 8;

    public static string GetPubsubTopic(string contentTopic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentTopic);
        string[] parts = contentTopic.Split('/');
        if (parts.Length is < 5 or > 6)
            throw new ArgumentException($"Invalid Waku content topic '{contentTopic}'.", nameof(contentTopic));

        int fieldOffset;
        if (parts.Length == 6)
        {
            if (!int.TryParse(parts[1], out int generation) || generation != 0)
                throw new ArgumentException($"Unsupported Waku content topic generation in '{contentTopic}'.", nameof(contentTopic));
            fieldOffset = 2;
        }
        else
        {
            fieldOffset = 1;
        }

        string application = parts[fieldOffset];
        string version = parts[fieldOffset + 1];
        if (string.IsNullOrEmpty(application) || string.IsNullOrEmpty(version) ||
            string.IsNullOrEmpty(parts[fieldOffset + 2]) || string.IsNullOrEmpty(parts[fieldOffset + 3]))
        {
            throw new ArgumentException($"Invalid Waku content topic '{contentTopic}'.", nameof(contentTopic));
        }

        byte[] routingKey = Encoding.UTF8.GetBytes(application + version);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(routingKey, digest);
        ulong suffix = BinaryPrimitives.ReadUInt64BigEndian(digest[^8..]);
        int shard = (int)(suffix % ShardCount);
        return $"/waku/2/rs/{ClusterId}/{shard}";
    }

    public static IReadOnlyDictionary<string, string[]> GroupByPubsubTopic(
        IEnumerable<string> contentTopics)
    {
        Dictionary<string, List<string>> groups = new(StringComparer.Ordinal);
        foreach (string contentTopic in contentTopics.Distinct(StringComparer.Ordinal))
        {
            string pubsubTopic = GetPubsubTopic(contentTopic);
            if (!groups.TryGetValue(pubsubTopic, out List<string>? values))
            {
                values = [];
                groups.Add(pubsubTopic, values);
            }
            values.Add(contentTopic);
        }

        return groups.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray(),
            StringComparer.Ordinal);
    }
}
