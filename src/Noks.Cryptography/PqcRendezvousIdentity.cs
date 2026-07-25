using Org.BouncyCastle.Crypto.Parameters;

namespace Noks.Cryptography;

public sealed class PqcRendezvousIdentity
{
    internal PqcRendezvousIdentity(
        MLDsaPrivateKeyParameters signingPrivateKey,
        MLKemPrivateKeyParameters challengePrivateKey)
    {
        SigningPrivateKey = signingPrivateKey;
        ChallengePrivateKey = challengePrivateKey;
        SigningPublicKey = signingPrivateKey.GetPublicKey().GetEncoded();
        ChallengePublicKey = challengePrivateKey.GetPublicKeyEncoded();
    }

    internal MLDsaPrivateKeyParameters SigningPrivateKey { get; }

    internal MLKemPrivateKeyParameters ChallengePrivateKey { get; }

    public byte[] SigningPublicKey { get; }

    public byte[] ChallengePublicKey { get; }
}
