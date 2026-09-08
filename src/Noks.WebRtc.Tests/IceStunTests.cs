using System.Net;
using System.Buffers.Binary;

namespace Noks.WebRtc.Tests;

public sealed class IceStunTests
{
    [Fact]
    public void Authenticated_binding_messages_verify_and_tampering_fails()
    {
        var packet = Stun.BuildSuccess(Enumerable.Range(0, 12).Select(x => (byte)x).ToArray(), new IPEndPoint(IPAddress.Loopback, 5000), "secret");
        Assert.True(Stun.TryParse(packet, out var parsed));
        Assert.True(Stun.VerifyIntegrity(parsed!, "secret"));
        packet[24] ^= 1;
        Assert.False(Stun.TryParse(packet, out _));
    }

    [Fact]
    public void Authenticated_binding_request_verifies()
    {
        var packet = Stun.BuildRequest(Enumerable.Range(0, 12).Select(x => (byte)x).ToArray(), "remote:local", "secret", true, 12, true);
        Assert.True(Stun.TryParse(packet, out var parsed));
        Assert.True(Stun.VerifyIntegrity(parsed!, "secret"));
    }

    [Fact]
    public void Authenticated_role_conflict_error_verifies()
    {
        var packet = Stun.BuildError(Enumerable.Repeat((byte)7, 12).ToArray(), 487, "secret");
        Assert.True(Stun.TryParse(packet, out var parsed));
        Assert.Equal(487, parsed!.ErrorCode);
        Assert.True(Stun.VerifyIntegrity(parsed, "secret"));
    }

    [Fact]
    public void Duplicate_required_attribute_is_rejected_before_it_can_be_authenticated()
    {
        var packet = Stun.BuildRequest(Enumerable.Range(0, 12).Select(x => (byte)x).ToArray(), "remote:local", "secret", true, 12, false);
        // The first attribute is USERNAME; turn it into a second PRIORITY attribute.
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), 0x0024);
        Assert.False(Stun.TryParse(packet, out _));
    }

    [Fact]
    public void Rfc5769_sample_request_accepts_optional_software_and_verifies_integrity()
    {
        var packet = Convert.FromHexString("000100582112A442B7E7A701BC34D686FA87DFAE802200105354554E207465737420636C69656E74002400046E0001FF80290008932FF9B151263B36000600096576746A3A68367659202020000800149AEAA70CBFD8CB56781EF2B5B2D3F249C1B571A280280004E57A3BCF");
        Assert.True(Stun.TryParse(packet, out var parsed));
        Assert.True(Stun.VerifyIntegrity(parsed!, "VOkJxbRl1RmTxUk/WvJxBt"));
    }

    [Fact]
    public void Attribute_after_message_integrity_is_rejected()
    {
        var packet = Stun.BuildRequest(Enumerable.Range(0, 12).Select(x => (byte)x).ToArray(), "remote:local", "secret", true, 12, false);
        Assert.True(Stun.TryParse(packet, out var parsed));
        // Replace the final FINGERPRINT header with a USE-CANDIDATE header. It is after MESSAGE-INTEGRITY.
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(parsed!.IntegrityOffset + 24), 0x0025);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(parsed.IntegrityOffset + 26), 0);
        Assert.False(Stun.TryParse(packet, out _));
    }
}
