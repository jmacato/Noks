namespace Noks.Waku.Transport.Libp2p.Discovery;

internal sealed record WakuPeer(
    Uri WebSocketUri,
    string PeerId,
    byte[] IdentityPublicKey,
    string Enr);
