namespace Noks.Cryptography;

public sealed record PqcRendezvousAlgorithmSuite(
    int ProtocolVersion,
    string SigningAlgorithm,
    string ChallengeAlgorithm,
    string SymmetricAlgorithm);
