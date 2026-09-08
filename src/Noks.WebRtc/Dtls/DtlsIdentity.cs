using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Org.BouncyCastle.Utilities.Encoders;
using Org.BouncyCastle.X509;
using System.Security.Cryptography;

namespace Noks.WebRtc;

/// <summary>A short-lived, self-signed ECDSA identity used by a WebRTC DTLS association.</summary>
public sealed class DtlsIdentity : IDisposable
{
    private readonly AsymmetricCipherKeyPair _keyPair;
    private readonly Certificate _certificate;
    private bool _disposed;

    public DtlsIdentity()
    {
        var random = new SecureRandom();
        var generator = new ECKeyPairGenerator();
        generator.Init(new ECKeyGenerationParameters(X9ObjectIdentifiers.Prime256v1, random));
        _keyPair = generator.GenerateKeyPair();

        var certificateGenerator = new X509V3CertificateGenerator();
        var subject = new Org.BouncyCastle.Asn1.X509.X509Name("CN=Noks WebRTC");
        certificateGenerator.SetSerialNumber(BigInteger.ProbablePrime(120, random));
        certificateGenerator.SetIssuerDN(subject);
        certificateGenerator.SetSubjectDN(subject);
        certificateGenerator.SetNotBefore(DateTime.UtcNow.AddMinutes(-5));
        certificateGenerator.SetNotAfter(DateTime.UtcNow.AddDays(7));
        certificateGenerator.SetPublicKey(_keyPair.Public);
        var certificate = certificateGenerator.Generate(new Asn1SignatureFactory("SHA256WITHECDSA", _keyPair.Private, random));
        CertificateDer = certificate.GetEncoded();
        Fingerprint = string.Join(':', SHA256.HashData(CertificateDer).Select(static b => b.ToString("X2")));
        _certificate = new Certificate(new BcTlsCrypto().CreateCertificate(CertificateType.X509, CertificateDer).Yield());
    }

    /// <summary>SHA-256 certificate fingerprint in SDP colon-separated upper-case hexadecimal form.</summary>
    public string Fingerprint { get; }

    internal byte[] CertificateDer { get; }
    internal AsymmetricKeyParameter PrivateKey => _keyPair.Private;
    internal Certificate Certificate => _certificate;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(CertificateDer);
    }
}

internal static class TlsCertificateExtensions
{
    internal static Org.BouncyCastle.Tls.Crypto.TlsCertificate[] Yield(this Org.BouncyCastle.Tls.Crypto.TlsCertificate certificate) => [certificate];
}
