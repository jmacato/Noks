namespace Noks.Cryptography;

public sealed record PqcRendezvousDescriptor(
    PqcRendezvousAlgorithmSuite AlgorithmSuite,
    string TemporaryId,
    byte[] DescriptorId,
    long Sequence,
    long ExpiresAtUnixMilliseconds,
    byte[] SigningPublicKey,
    byte[] ChallengePublicKey,
    byte[] ProofOfWorkSeed,
    int MinimumWorkBits,
    byte[] Signature);
