using Noks.Cryptography;

namespace Noks.Waku.Tests;

public sealed class RendezvousPayloadCodecTests
{
    [Fact]
    public void RequestCardResponseAndCorrelationRoundTrip()
    {
        byte[] entropy = Enumerable.Repeat((byte)0x42, NoksRecoveryPhrase.EntropySize).ToArray();
        using WakuProfileKeys keys = WakuProfileKeys.Create(entropy);
        DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
        byte[] card = ContactCardV2Codec.Encode(ContactCardV2Codec.CreateSigned(
            keys,
            "clear-forest-ab12",
            "1234567890123",
            now,
            now.AddMinutes(1)));
        Guid id = Guid.Parse("58d78570-e848-41ec-9707-29e9b744554c");

        byte[] requestBytes = RendezvousPayloadCodec.EncodeRequest(
            id,
            RendezvousRouteKind.Sms,
            "9876543210987",
            card);
        Assert.True(RendezvousPayloadCodec.TryDecodeRequest(requestBytes, out RendezvousRequestPayload? request));
        Assert.NotNull(request);
        Assert.Equal(id, request.RendezvousId);
        Assert.Equal(RendezvousRouteKind.Sms, request.RouteKind);
        Assert.Equal("9876543210987", request.TargetNumber);
        Assert.Equal(card, request.ContactCard.ToArray());

        byte[] responseBytes = RendezvousPayloadCodec.EncodeCardResponse(id, card);
        Assert.True(RendezvousPayloadCodec.TryDecodeCardResponse(responseBytes, out RendezvousCardResponsePayload? response));
        Assert.NotNull(response);
        Assert.Equal(id, response.RendezvousId);
        Assert.Equal(card, response.ContactCard.ToArray());

        byte[] correlationBytes = RendezvousPayloadCodec.EncodeCorrelation(id);
        Assert.True(RendezvousPayloadCodec.TryDecodeCorrelation(correlationBytes, out Guid correlation));
        Assert.Equal(id, correlation);
    }

    [Fact]
    public void MalformedPayloadsAreRejected()
    {
        Assert.False(RendezvousPayloadCodec.TryDecodeRequest([], out _));
        Assert.False(RendezvousPayloadCodec.TryDecodeCardResponse(new byte[24], out _));
        Assert.False(RendezvousPayloadCodec.TryDecodeCorrelation(new byte[24], out _));
    }
}
