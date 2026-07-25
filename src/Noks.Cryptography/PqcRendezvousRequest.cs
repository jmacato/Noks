namespace Noks.Cryptography;

public sealed record PqcRendezvousRequest(
    string TemporaryId,
    byte[] DescriptorId,
    long DescriptorSequence,
    long DescriptorExpiresAtUnixMilliseconds,
    string ContentTopic,
    byte[] Challenge,
    byte[] AesNonce,
    byte[] Ciphertext,
    byte[] AuthenticationTag,
    ulong ProofOfWorkNonce);
