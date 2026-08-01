using Noks.Cryptography;

namespace Noks.Waku.Tests;

public sealed class PqcRendezvousWireCodecTests
{
    [Fact]
    public void DescriptorChunkAndRequestRoundTripThroughFixedWakuRecords()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PqcRendezvousIdentity recipient = PqcRendezvousCrypto.CreateIdentity();
        PqcRendezvousDescriptor descriptor = PqcRendezvousCrypto.CreateDescriptor(
            recipient, "0917123456789", 1, now.AddHours(1), minimumWorkBits: 4);
        PqcRendezvousDescriptorChunk chunk = PqcRendezvousCrypto.CreateDescriptorChunks(descriptor)[0];

        byte[] descriptorPacket = PqcRendezvousWireCodec.EncodeDescriptorChunk(chunk);
        Assert.Equal(WakuEnvelopeCodec.EnvelopeSize, descriptorPacket.Length);
        PqcRendezvousDescriptorChunkRecord decodedChunk = Assert.IsType<PqcRendezvousDescriptorChunkRecord>(
            AssertRecord(descriptorPacket));
        Assert.Equal(chunk.DescriptorHash, decodedChunk.Chunk.DescriptorHash);
        Assert.Equal(chunk.Payload, decodedChunk.Chunk.Payload);

        PqcRendezvousRequest request = PqcRendezvousCrypto.CreateRequest(
            descriptor, "/noks/test/1", "signed-contact-card"u8).Request;
        byte[] requestPacket = PqcRendezvousWireCodec.EncodeRequest(request);
        Assert.Equal(PqcWakuEnvelopeCodec.EnvelopeSize, requestPacket.Length);
        PqcRendezvousRequestRecord decodedRequest = Assert.IsType<PqcRendezvousRequestRecord>(
            AssertRecord(requestPacket));
        Assert.Equal(request.TemporaryId, decodedRequest.Request.TemporaryId);
        Assert.Equal(request.DescriptorId, decodedRequest.Request.DescriptorId);
        Assert.Equal(request.Challenge, decodedRequest.Request.Challenge);
        Assert.Equal(request.Ciphertext, decodedRequest.Request.Ciphertext);
        Assert.True(PqcRendezvousCrypto.VerifyProofOfWork(descriptor, decodedRequest.Request));
    }

    private static PqcRendezvousWireRecord AssertRecord(byte[] packet)
    {
        Assert.True(PqcRendezvousWireCodec.TryDecode(packet, out PqcRendezvousWireRecord? record));
        return Assert.IsAssignableFrom<PqcRendezvousWireRecord>(record);
    }
}
