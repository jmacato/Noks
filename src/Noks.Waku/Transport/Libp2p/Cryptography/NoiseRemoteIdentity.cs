namespace Noks.Waku.Transport.Libp2p.Cryptography;

internal sealed record NoiseRemoteIdentity(
    byte[] PublicKey,
    IReadOnlySet<string> StreamMuxers);
