namespace Noks.Waku;

public readonly record struct WakuPublishResult(int AcceptedPeerCount)
{
    public bool AcceptedByServicePeer => AcceptedPeerCount > 0;
}
