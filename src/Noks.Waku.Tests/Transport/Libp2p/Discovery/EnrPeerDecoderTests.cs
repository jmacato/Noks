using Noks.Waku.Transport.Libp2p.Discovery;

namespace Noks.Waku.Tests.Transport.Libp2p.Discovery;

public sealed class EnrPeerDecoderTests
{
    private const string SandboxAmsterdam =
        "enr:-QESuED0qW1BCmF-oH_ARGPr97Nv767bl_43uoy70vrbah3EaCAdK3Q0iRQ6wkSTTpdrg_dU_NC2ydO8leSlRpBX4pxiAYJpZIJ2NIJpcIRA4VDAim11bHRpYWRkcnO4XAArNiZub2RlLTAxLmRvLWFtczMud2FrdS5zYW5kYm94LnN0YXR1cy5pbQZ2XwAtNiZub2RlLTAxLmRvLWFtczMud2FrdS5zYW5kYm94LnN0YXR1cy5pbQYfQN4DgnJzkwABCAAAAAEAAgADAAQABQAGAAeJc2VjcDI1NmsxoQOTd-h5owwj-cx7xrmbvQKU8CV3Fomfdvcv1MBc-67T5oN0Y3CCdl-DdWRwgiMohXdha3UyDw";

    [Fact]
    public void DecodesOfficialWakuEnrIntoBrowserPeer()
    {
        WakuPeer peer = EnrPeerDecoder.Decode(SandboxAmsterdam);

        Assert.Equal(
            "wss://node-01.do-ams3.waku.sandbox.status.im:8000/",
            peer.WebSocketUri.AbsoluteUri);
        Assert.Equal(
            "039377E879A30C23F9CC7BC6B99BBD0294F0257716899F76F72FD4C05CFBAED3E6",
            Convert.ToHexString(peer.IdentityPublicKey));
        Assert.StartsWith("16Uiu2", peer.PeerId, StringComparison.Ordinal);
    }

    [Fact]
    public void Base58PreservesIdentityMultihashLeadingZero()
    {
        Assert.Equal("1", Base58Btc.Encode([0]));
        Assert.Equal("12", Base58Btc.Encode([0, 1]));
        Assert.Equal("Cn8eVZg", Base58Btc.Encode("hello"u8));
    }
}
