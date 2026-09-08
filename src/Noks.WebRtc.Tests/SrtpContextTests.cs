namespace Noks.WebRtc.Tests;

public sealed class SrtpContextTests
{
    [Fact]
    public void Rfc3711AppendixB3DerivesPublishedSessionKeys()
    {
        var key = Hex("E1F97A0D3E018BE0D64FA32C06DE4139");
        var salt = Hex("0EC675AD498AFEEBB6960B3AABE6");
        Assert.Equal(Hex("C61E7A93744F39EE10734AFE3FF7A087"), SrtpContext.DeriveForTest(key, salt, 0, 16));
        Assert.Equal(Hex("30CBBC08863D8C85D49DB34A9AE1"), SrtpContext.DeriveForTest(key, salt, 2, 14));
        Assert.Equal(Hex("CEBE321F6FF7716B6FD4AB49AF256A156D38BAA48F0A0ACF3C34E2359E6CDBCEE049646C43D9327AD175578EF72270986371C10C9A369AC2F94A8C5FBCDDDC256D6E919A48B610EF17C2041E474035766B68642C59BBFC2F34DB60DBDFB2"), SrtpContext.DeriveForTest(key, salt, 1, 94));
    }

    [Fact]
    public void Rfc3711AppendixB2GeneratesPublishedAesCmKeystream()
    {
        var stream = SrtpContext.KeystreamForTest(Hex("2B7E151628AED2A6ABF7158809CF4F3C"), Hex("F0F1F2F3F4F5F6F7F8F9FAFBFCFD"), 0, 0, 48);
        Assert.Equal(Hex("E03EAD0935C95E80E166B16DD92B4EB4D23513162B02D0F72A43A2FE4A5F97AB41E95B3BB0A2E8DD477901E4FCA894C0"), stream);
    }

    [Fact]
    public void RtpAuthenticatesBeforeStateUpdateAndRejectsReplay()
    {
        var key = Enumerable.Range(0, 16).Select(static i => (byte)i).ToArray(); var salt = Enumerable.Range(0, 14).Select(static i => (byte)(0xa0 + i)).ToArray();
        using var sender = new SrtpContext(key, salt); using var receiver = new SrtpContext(key, salt);
        var encrypted = sender.ProtectRtp(Rtp(7, 0x11223344, [1, 2, 3]));
        var tampered = encrypted.ToArray(); tampered[^1] ^= 1;
        Assert.False(receiver.TryUnprotectRtp(tampered, out _));
        Assert.True(receiver.TryUnprotectRtp(encrypted, out var clear));
        Assert.Equal(Rtp(7, 0x11223344, [1, 2, 3]), clear);
        Assert.False(receiver.TryUnprotectRtp(encrypted, out _));
    }

    [Fact]
    public void RtpSupportsExtensionsOutOfOrderAndRollover()
    {
        var key = new byte[16]; var salt = new byte[14];
        using var sender = new SrtpContext(key, salt); using var receiver = new SrtpContext(key, salt);
        var one = sender.ProtectRtp(Rtp(65534, 9, [1], extension: true));
        var two = sender.ProtectRtp(Rtp(65535, 9, [2]));
        var three = sender.ProtectRtp(Rtp(0, 9, [3]));
        Assert.True(receiver.TryUnprotectRtp(two, out _));
        Assert.True(receiver.TryUnprotectRtp(one, out _));
        Assert.True(receiver.TryUnprotectRtp(three, out var clear));
        Assert.Equal((byte)3, clear[^1]);
    }

    [Fact]
    public void SrtcpUsesEBitAuthenticationAndReplayProtection()
    {
        var key = Enumerable.Range(0, 16).Select(static i => (byte)i).ToArray(); var salt = Enumerable.Range(0, 14).Select(static i => (byte)(0xa0 + i)).ToArray();
        using var sender = new SrtpContext(key, salt); using var receiver = new SrtpContext(key, salt);
        var plain = Rtcp(0x11223344, [1, 2, 3, 4]); var encrypted = sender.ProtectRtcp(plain);
        Assert.NotEqual(plain[8], encrypted[8]); Assert.NotEqual(0u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(encrypted.AsSpan(encrypted.Length - 14, 4)) & 0x80000000);
        Assert.True(receiver.TryUnprotectRtcp(encrypted, out var clear)); Assert.Equal(plain, clear);
        Assert.False(receiver.TryUnprotectRtcp(encrypted, out _));
        var tampered = encrypted.ToArray(); tampered[^1] ^= 1; Assert.False(receiver.TryUnprotectRtcp(tampered, out _));
    }

    [Fact]
    public void PionAesCmHmacSha1SrtcpVectorDecrypts()
    {
        // Pion SRTP v3.0.12 srtcp_test.go, AES_128_CM_HMAC_SHA1_80 case (MIT).
        var key = Hex("FDA62595D7F6926F7D9C024CC9209F34"); var salt = Hex("A9651985540B47BE2F27A8B88123");
        var encrypted = Hex("80C8000666EF91FFCD34C578B28BE16BC509D577E4CE5F208021BD667465E95F49E5F5C0684EE56A78077546ED90F6DC9DEF3BDFF279A9D88000000160C0AEB56F40880E28BA");
        var expected = Hex("80C8000666EF91FFDF4880DD61A62ED3D8BCDEBE000000090000160481CA000666EF91FF0110526E5435436D4A687A7965744178772B0000");
        using var context = new SrtpContext(key, salt);
        Assert.True(context.TryUnprotectRtcp(encrypted, out var clear)); Assert.Equal(expected, clear);
    }

    [Fact]
    public void InvalidPacketsAndDisposedContextAreSafe()
    {
        using var context = new SrtpContext(new byte[16], new byte[14]);
        for (var length = 0; length < 32; length++) { Assert.False(context.TryUnprotectRtp(new byte[length], out _)); Assert.False(context.TryUnprotectRtcp(new byte[length], out _)); }
        var oversized = new byte[1_048_576 + 23]; oversized[0] = 0x80; oversized[1] = 200; oversized[3] = 1;
        Assert.False(context.TryUnprotectRtp(oversized, out _)); Assert.False(context.TryUnprotectRtcp(oversized, out _));
        context.Dispose(); Assert.Throws<ObjectDisposedException>(() => context.ProtectRtp(Rtp(1, 1, [1])));
    }

    [Fact]
    public void Ssrc_capacity_fails_closed_without_forgotten_replay_state()
    {
        var key = new byte[16]; var salt = new byte[14];
        using var sender = new SrtpContext(key, salt); using var receiver = new SrtpContext(key, salt);
        byte[] first = sender.ProtectRtp(Rtp(1, 1, [1]));
        Assert.True(receiver.TryUnprotectRtp(first, out _));
        for (uint ssrc = 2; ssrc <= 64; ssrc++)
        {
            byte[] packet = sender.ProtectRtp(Rtp(1, ssrc, [1]));
            Assert.True(receiver.TryUnprotectRtp(packet, out _));
        }
        using var overflowSender = new SrtpContext(key, salt);
        byte[] overflow = overflowSender.ProtectRtp(Rtp(1, 65, [1]));
        Assert.False(receiver.TryUnprotectRtp(overflow, out _));
        Assert.False(receiver.TryUnprotectRtp(first, out _));
        Assert.Throws<InvalidOperationException>(() => sender.ProtectRtp(Rtp(1, 66, [1])));
        Assert.Throws<InvalidOperationException>(() => sender.ProtectRtp(Rtp(1, 1, [1])));
    }

    private static byte[] Rtp(ushort sequence, uint ssrc, byte[] payload, bool extension = false)
    {
        var header = extension ? 20 : 12; var p = new byte[header + payload.Length]; p[0] = (byte)(0x80 | (extension ? 0x10 : 0)); p[1] = 0; System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), sequence); System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), ssrc);
        if (extension) { p[12] = 0xbe; p[13] = 0xde; p[15] = 1; }
        payload.CopyTo(p, header); return p;
    }
    private static byte[] Rtcp(uint ssrc, byte[] payload) { var p = new byte[8 + payload.Length]; p[0] = 0x80; p[1] = 200; System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)(p.Length / 4 - 1)); System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), ssrc); payload.CopyTo(p, 8); return p; }
    private static byte[] Hex(string value) => Convert.FromHexString(value);
}
