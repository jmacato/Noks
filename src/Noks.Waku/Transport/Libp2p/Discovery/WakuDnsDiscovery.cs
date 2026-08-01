using System.Net.Http.Json;
using System.Text.Json;

namespace Noks.Waku.Transport.Libp2p.Discovery;

internal sealed class WakuDnsDiscovery
{
    private const string DnsEndpoint = "https://dns.google/resolve";
    private const string SandboxDomain = "sandbox.waku.nodes.status.im";
    private const string TestDomain = "test.waku.nodes.status.im";
    private const int MaximumTreeEntries = 64;

    private static readonly string[] FallbackEnrs =
    [
        "enr:-QESuED0qW1BCmF-oH_ARGPr97Nv767bl_43uoy70vrbah3EaCAdK3Q0iRQ6wkSTTpdrg_dU_NC2ydO8leSlRpBX4pxiAYJpZIJ2NIJpcIRA4VDAim11bHRpYWRkcnO4XAArNiZub2RlLTAxLmRvLWFtczMud2FrdS5zYW5kYm94LnN0YXR1cy5pbQZ2XwAtNiZub2RlLTAxLmRvLWFtczMud2FrdS5zYW5kYm94LnN0YXR1cy5pbQYfQN4DgnJzkwABCAAAAAEAAgADAAQABQAGAAeJc2VjcDI1NmsxoQOTd-h5owwj-cx7xrmbvQKU8CV3Fomfdvcv1MBc-67T5oN0Y3CCdl-DdWRwgiMohXdha3UyDw",
        "enr:-QEkuED9X80QF_jcN9gA2ZRhhmwVEeJnsg_Hyg7IFCTYnZD0BDI7a8HArE61NhJZFwygpHCWkgwSt2vqiABXkBxzIqZBAYJpZIJ2NIJpcIQiQlleim11bHRpYWRkcnO4bgA0Ni9ub2RlLTAxLmdjLXVzLWNlbnRyYWwxLWEud2FrdS5zYW5kYm94LnN0YXR1cy5pbQZ2XwA2Ni9ub2RlLTAxLmdjLXVzLWNlbnRyYWwxLWEud2FrdS5zYW5kYm94LnN0YXR1cy5pbQYfQN4DgnJzkwABCAAAAAEAAgADAAQABQAGAAeJc2VjcDI1NmsxoQPFAS8zz2cg1QQhxMaK8CzkGQ5wdHvPJcrgLzJGOiHpwYN0Y3CCdl-DdWRwgiMohXdha3UyDw",
        "enr:-QEkuEBfEzJm_kigJ2HoSS_RBFJYhKHocGdkhhBr6jSUAWjLdFPp6Pj1l4yiTQp7TGHyu1kC6FyaU573VN8klLsEm-XuAYJpZIJ2NIJpcIQI2SVcim11bHRpYWRkcnO4bgA0Ni9ub2RlLTAxLmFjLWNuLWhvbmdrb25nLWMud2FrdS5zYW5kYm94LnN0YXR1cy5pbQZ2XwA2Ni9ub2RlLTAxLmFjLWNuLWhvbmdrb25nLWMud2FrdS5zYW5kYm94LnN0YXR1cy5pbQYfQN4DgnJzkwABCAAAAAEAAgADAAQABQAGAAeJc2VjcDI1NmsxoQOwsS69tgD7u1K50r5-qG5hweuTwa0W26aYPnvivpNlrYN0Y3CCdl-DdWRwgiMohXdha3UyDw",
    ];

    private readonly HttpClient httpClient;

    public WakuDnsDiscovery(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? new HttpClient();
    }

    public async Task<IReadOnlyList<WakuPeer>> DiscoverAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, WakuPeer> peers = new(StringComparer.Ordinal);
        foreach (string domain in new[] { SandboxDomain, TestDomain })
        {
            try
            {
                await DiscoverTreeAsync(domain, peers, cancellationToken);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or JsonException or FormatException or TaskCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw;
            }
        }

        foreach (string enr in FallbackEnrs)
        {
            WakuPeer peer = EnrPeerDecoder.Decode(enr);
            peers.TryAdd(peer.PeerId, peer);
        }

        return peers.Values.ToArray();
    }

    private async Task DiscoverTreeAsync(
        string domain,
        Dictionary<string, WakuPeer> peers,
        CancellationToken cancellationToken)
    {
        string root = await ResolveSingleTxtAsync(domain, cancellationToken);
        string entry = ReadRootEntry(root);
        Queue<string> pending = new();
        pending.Enqueue(entry);
        HashSet<string> visited = new(StringComparer.Ordinal);

        while (pending.Count > 0 && visited.Count < MaximumTreeEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string subdomain = pending.Dequeue();
            if (!visited.Add(subdomain))
                continue;

            string record = await ResolveSingleTxtAsync($"{subdomain}.{domain}", cancellationToken);
            if (record.StartsWith("enrtree-branch:", StringComparison.Ordinal))
            {
                foreach (string branch in record["enrtree-branch:".Length..].Split(
                             ',',
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    pending.Enqueue(branch);
                }
            }
            else if (record.StartsWith("enr:", StringComparison.Ordinal))
            {
                WakuPeer peer = EnrPeerDecoder.Decode(record);
                peers.TryAdd(peer.PeerId, peer);
            }
        }
    }

    private async Task<string> ResolveSingleTxtAsync(string name, CancellationToken cancellationToken)
    {
        string url = $"{DnsEndpoint}?name={Uri.EscapeDataString(name)}&type=TXT";
        using HttpResponseMessage response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("Answer", out JsonElement answers))
            throw new FormatException($"DNS TXT response for {name} contains no answers.");

        foreach (JsonElement answer in answers.EnumerateArray())
        {
            if (answer.TryGetProperty("data", out JsonElement data) && data.GetString() is { Length: > 0 } value)
                return value.Trim('"').Replace("\" \"", "", StringComparison.Ordinal);
        }

        throw new FormatException($"DNS TXT response for {name} is empty.");
    }

    private static string ReadRootEntry(string root)
    {
        const string prefix = "enrtree-root:v1 ";
        if (!root.StartsWith(prefix, StringComparison.Ordinal))
            throw new FormatException("Invalid ENR tree root.");

        foreach (string part in root[prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("e=", StringComparison.Ordinal) && part.Length > 2)
                return part[2..];
        }

        throw new FormatException("ENR tree root does not identify an entry.");
    }
}
