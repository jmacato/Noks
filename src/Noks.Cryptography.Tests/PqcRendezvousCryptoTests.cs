using System.Text;
using Noks.Cryptography;

namespace Noks.Cryptography.Tests;

public sealed class PqcRendezvousCryptoTests
{
    [Fact]
    public void OfflineRendezvousUsesPqcDescriptorChallengeAndAes256Gcm()
    {
        PqcRendezvousIdentity recipient = PqcRendezvousCrypto.CreateIdentity();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PqcRendezvousDescriptor descriptor = PqcRendezvousCrypto.CreateDescriptor(
            recipient,
            "0917123456789",
            sequence: 1,
            expiresAt: now.AddMinutes(10),
            minimumWorkBits: 10);

        Assert.True(PqcRendezvousCrypto.VerifyDescriptor(descriptor, now));
        Assert.Equal(PqcRendezvousCrypto.SigningAlgorithm, "ML-DSA-65");
        Assert.Equal(PqcRendezvousCrypto.ChallengeAlgorithm, "ML-KEM-768");
        Assert.Equal(PqcRendezvousCrypto.SymmetricAlgorithm, "AES-256-GCM");

        PqcRendezvousDescriptorChunk[] chunks = PqcRendezvousCrypto.CreateDescriptorChunks(descriptor);
        Assert.True(chunks.Length > 1);
        Assert.True(PqcRendezvousCrypto.TryReassembleDescriptor(chunks.Reverse(), now, out var reassembled));
        Assert.Equal(PqcRendezvousCrypto.DefaultAlgorithmSuite, reassembled.AlgorithmSuite);
        Assert.Equal(descriptor.TemporaryId, reassembled.TemporaryId);
        Assert.Equal(descriptor.Signature, reassembled.Signature);
        Assert.False(PqcRendezvousCrypto.TryReassembleDescriptor(chunks[..^1], now, out _));

        byte[] message = Encoding.UTF8.GetBytes("Contact request queued while recipient is offline.");
        PqcRendezvousOutbound outbound = PqcRendezvousCrypto.CreateRequest(
            reassembled,
            "/noks/rendezvous/2026-07",
            message);

        Assert.True(outbound.ProofOfWorkAttempts > 0);
        Assert.True(PqcRendezvousCrypto.VerifyProofOfWork(reassembled, outbound.Request));

        var received = new HashSet<string>(StringComparer.Ordinal);
        PqcRendezvousReceiveResult accepted = PqcRendezvousCrypto.TryReceive(
            recipient,
            reassembled,
            outbound.Request,
            received,
            now.AddMinutes(1));

        Assert.True(accepted.IsAccepted, accepted.Reason);
        Assert.Equal(message, accepted.Plaintext);
        Assert.NotNull(accepted.EventId);

        PqcRendezvousReceiveResult replay = PqcRendezvousCrypto.TryReceive(
            recipient,
            reassembled,
            outbound.Request,
            received,
            now.AddMinutes(1));
        Assert.False(replay.IsAccepted);
        Assert.Equal("Duplicate request.", replay.Reason);
    }

    [Fact]
    public void PacketTamperingInvalidatesProofBeforeMlKemOrAesWork()
    {
        PqcRendezvousIdentity recipient = PqcRendezvousCrypto.CreateIdentity();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PqcRendezvousDescriptor descriptor = PqcRendezvousCrypto.CreateDescriptor(
            recipient,
            "0917987654321",
            sequence: 7,
            expiresAt: now.AddMinutes(10),
            minimumWorkBits: 9);
        PqcRendezvousOutbound outbound = PqcRendezvousCrypto.CreateRequest(
            descriptor,
            "/noks/rendezvous/2026-07",
            "one packet"u8);

        byte[] alteredCiphertext = (byte[])outbound.Request.Ciphertext.Clone();
        alteredCiphertext[0] ^= 0x01;
        PqcRendezvousRequest altered = outbound.Request with { Ciphertext = alteredCiphertext };

        Assert.False(PqcRendezvousCrypto.VerifyProofOfWork(descriptor, altered));
        PqcRendezvousReceiveResult rejected = PqcRendezvousCrypto.TryReceive(
            recipient,
            descriptor,
            altered,
            new HashSet<string>(StringComparer.Ordinal),
            now);
        Assert.False(rejected.IsAccepted);
        Assert.Equal("Proof of work is invalid.", rejected.Reason);
    }
}
