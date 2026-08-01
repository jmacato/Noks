namespace Noks.Waku.Transport.Protocols;

internal sealed record WakuMetadataResponse(uint? ClusterId, IReadOnlyList<uint> Shards);
