using System.Buffers.Binary;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Kems;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Noks.Cryptography;

/// <summary>
/// Provides experimental primitives for an asynchronous rendezvous with a proof-of-work gate.
/// The protocol carries a temporary number only as a lookup and routing label.
/// The protocol does not use this number as secret material or as input to the AES key derivation.
/// </summary>
public static class PqcRendezvousCrypto
{
    public const int RendezvousProtocolVersion = 1;
    public const string SigningAlgorithm = "ML-DSA-65";
    public const string ChallengeAlgorithm = "ML-KEM-768";
    public const string SymmetricAlgorithm = "AES-256-GCM";
    public const int AesKeySize = 32;
    public const int AesNonceSize = 12;
    public const int AesTagSize = 16;
    public const int MlKem768PublicKeySize = 1184;
    public const int MlKem768CiphertextSize = 1088;
    public const int MlKem768SharedSecretSize = 32;
    public const int MlDsa65PublicKeySize = 1952;
    public const int MlDsa65SignatureSize = 3309;
    public const int DefaultDescriptorChunkPayloadSize = 1800;

    public static readonly PqcRendezvousAlgorithmSuite DefaultAlgorithmSuite = new(
        RendezvousProtocolVersion,
        SigningAlgorithm,
        ChallengeAlgorithm,
        SymmetricAlgorithm);

    private static readonly byte[] AesKeyDomain = "noks/rendezvous/aes-256/v1"u8.ToArray();
    private static readonly byte[] DescriptorDomain = "noks/rendezvous/descriptor/v1"u8.ToArray();
    private static readonly byte[] WorkDomain = "noks/rendezvous/pow/v1"u8.ToArray();

    public static PqcRendezvousIdentity CreateIdentity()
    {
        SecureRandom random = new();

        var signingGenerator = new MLDsaKeyPairGenerator();
        signingGenerator.Init(new MLDsaKeyGenerationParameters(random, MLDsaParameters.ml_dsa_65));
        AsymmetricCipherKeyPair signingKeyPair = signingGenerator.GenerateKeyPair();

        var challengeGenerator = new MLKemKeyPairGenerator();
        challengeGenerator.Init(new MLKemKeyGenerationParameters(random, MLKemParameters.ml_kem_768));
        AsymmetricCipherKeyPair challengeKeyPair = challengeGenerator.GenerateKeyPair();

        return new PqcRendezvousIdentity(
            (MLDsaPrivateKeyParameters)signingKeyPair.Private,
            (MLKemPrivateKeyParameters)challengeKeyPair.Private);
    }

    /// <summary>
    /// Creates a stable PQC identity from profile-rooted secret material. The
    /// temporary rendezvous number is deliberately not an input here.
    /// </summary>
    public static PqcRendezvousIdentity CreateIdentity(ReadOnlySpan<byte> profileSecret)
    {
        if (profileSecret.Length < 32)
            throw new ArgumentException("At least 32 bytes of profile secret material are required.", nameof(profileSecret));

        byte[] signingSeed = HMACSHA256.HashData(profileSecret, "noks/pqc-rendezvous/ml-dsa-65/v1"u8);
        byte[] challengeSeed = HMACSHA512.HashData(profileSecret, "noks/pqc-rendezvous/ml-kem-768/v1"u8);
        try
        {
            return new PqcRendezvousIdentity(
                MLDsaPrivateKeyParameters.FromSeed(MLDsaParameters.ml_dsa_65, signingSeed),
                MLKemPrivateKeyParameters.FromSeed(MLKemParameters.ml_kem_768, challengeSeed));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingSeed);
            CryptographicOperations.ZeroMemory(challengeSeed);
        }
    }

    public static bool IsSupported(PqcRendezvousAlgorithmSuite suite) =>
        DefaultAlgorithmSuite == suite;

    public static byte[] SignMlDsa65(
        PqcRendezvousIdentity identity,
        ReadOnlySpan<byte> message)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var signer = new MLDsaSigner(MLDsaParameters.ml_dsa_65, deterministic: true);
        signer.Init(true, identity.SigningPrivateKey);
        signer.BlockUpdate(message);
        byte[] signature = signer.GenerateSignature();
        if (signature.Length != MlDsa65SignatureSize)
            throw new CryptographicException("Unexpected ML-DSA-65 signature length.");
        return signature;
    }

    public static bool VerifyMlDsa65(
        ReadOnlySpan<byte> publicKeyEncoding,
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> signature)
    {
        if (publicKeyEncoding.Length != MlDsa65PublicKeySize ||
            signature.Length != MlDsa65SignatureSize)
        {
            return false;
        }
        try
        {
            MLDsaPublicKeyParameters publicKey = MLDsaPublicKeyParameters.FromEncoding(
                MLDsaParameters.ml_dsa_65,
                publicKeyEncoding.ToArray());
            var signer = new MLDsaSigner(MLDsaParameters.ml_dsa_65, deterministic: true);
            signer.Init(false, publicKey);
            signer.BlockUpdate(message);
            return signer.VerifySignature(signature.ToArray());
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool IsValidMlDsa65PublicKey(ReadOnlySpan<byte> publicKeyEncoding)
    {
        if (publicKeyEncoding.Length != MlDsa65PublicKeySize)
            return false;
        try
        {
            _ = MLDsaPublicKeyParameters.FromEncoding(
                MLDsaParameters.ml_dsa_65,
                publicKeyEncoding.ToArray());
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Encapsulates an independent ML-KEM-768 shared secret for use by packet
    /// protocols outside the rendezvous request. This API does not accept a
    /// temporary identifier. As a result, the identifier cannot accidentally become key material.
    /// </summary>
    public static PqcKemEncapsulation EncapsulateMlKem768(ReadOnlySpan<byte> publicKeyEncoding)
    {
        if (publicKeyEncoding.Length != MlKem768PublicKeySize)
            throw new ArgumentException("An ML-KEM-768 public key must contain 1184 bytes.", nameof(publicKeyEncoding));

        MLKemPublicKeyParameters publicKey = MLKemPublicKeyParameters.FromEncoding(
            MLKemParameters.ml_kem_768,
            publicKeyEncoding.ToArray());
        var encapsulator = new MLKemEncapsulator(MLKemParameters.ml_kem_768);
        encapsulator.Init(publicKey);
        byte[] ciphertext = new byte[encapsulator.EncapsulationLength];
        byte[] sharedSecret = new byte[encapsulator.SecretLength];
        encapsulator.Encapsulate(ciphertext, sharedSecret);
        if (ciphertext.Length != MlKem768CiphertextSize ||
            sharedSecret.Length != MlKem768SharedSecretSize)
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
            throw new CryptographicException("Unexpected ML-KEM-768 output length.");
        }
        return new PqcKemEncapsulation(ciphertext, sharedSecret);
    }

    public static bool IsValidMlKem768PublicKey(ReadOnlySpan<byte> publicKeyEncoding)
    {
        if (publicKeyEncoding.Length != MlKem768PublicKeySize)
            return false;
        try
        {
            _ = MLKemPublicKeyParameters.FromEncoding(
                MLKemParameters.ml_kem_768,
                publicKeyEncoding.ToArray());
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool TryDecapsulateMlKem768(
        PqcRendezvousIdentity recipient,
        ReadOnlySpan<byte> ciphertext,
        [MaybeNullWhen(false)] out byte[] sharedSecret)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        sharedSecret = null;
        if (ciphertext.Length != MlKem768CiphertextSize)
            return false;

        try
        {
            var decapsulator = new MLKemDecapsulator(MLKemParameters.ml_kem_768);
            decapsulator.Init(recipient.ChallengePrivateKey);
            if (decapsulator.EncapsulationLength != MlKem768CiphertextSize ||
                decapsulator.SecretLength != MlKem768SharedSecretSize)
            {
                return false;
            }
            sharedSecret = new byte[decapsulator.SecretLength];
            decapsulator.Decapsulate(ciphertext.ToArray(), sharedSecret);
            return true;
        }
        catch (ArgumentException)
        {
            if (sharedSecret is not null)
                CryptographicOperations.ZeroMemory(sharedSecret);
            sharedSecret = null;
            return false;
        }
    }

    public static byte[] DeriveAes256Key(
        ReadOnlySpan<byte> sharedSecret,
        ReadOnlySpan<byte> protocolDomain)
    {
        if (sharedSecret.Length < MlKem768SharedSecretSize)
            throw new ArgumentException("Insufficient shared-secret material.", nameof(sharedSecret));
        if (protocolDomain.IsEmpty)
            throw new ArgumentException("A protocol domain is required.", nameof(protocolDomain));

        byte[] material = new byte[protocolDomain.Length + sharedSecret.Length];
        protocolDomain.CopyTo(material);
        sharedSecret.CopyTo(material.AsSpan(protocolDomain.Length));
        try
        {
            return SHA256.HashData(material);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    public static PqcRendezvousDescriptor CreateDescriptor(
        PqcRendezvousIdentity identity,
        string temporaryId,
        long sequence,
        DateTimeOffset expiresAt,
        int minimumWorkBits)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidateTemporaryId(temporaryId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumWorkBits, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumWorkBits, 30);

        byte[] descriptorId = RandomNumberGenerator.GetBytes(16);
        byte[] proofOfWorkSeed = RandomNumberGenerator.GetBytes(32);
        var unsigned = new PqcRendezvousDescriptor(
            DefaultAlgorithmSuite,
            temporaryId,
            descriptorId,
            sequence,
            expiresAt.ToUnixTimeMilliseconds(),
            identity.SigningPublicKey,
            identity.ChallengePublicKey,
            proofOfWorkSeed,
            minimumWorkBits,
            []);
        byte[] signature = SignDescriptor(identity.SigningPrivateKey, GetDescriptorSigningBytes(unsigned));
        return unsigned with { Signature = signature };
    }

    public static bool VerifyDescriptor(PqcRendezvousDescriptor descriptor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!IsSupported(descriptor.AlgorithmSuite) ||
            descriptor.ExpiresAtUnixMilliseconds <= now.ToUnixTimeMilliseconds() ||
            descriptor.Sequence <= 0 ||
            descriptor.MinimumWorkBits is < 1 or > 30 ||
            descriptor.DescriptorId.Length != 16 ||
            descriptor.ProofOfWorkSeed.Length != 32 ||
            descriptor.SigningPublicKey.Length == 0 ||
            descriptor.ChallengePublicKey.Length == 0 ||
            descriptor.Signature.Length == 0)
        {
            return false;
        }

        try
        {
            var publicKey = MLDsaPublicKeyParameters.FromEncoding(
                MLDsaParameters.ml_dsa_65,
                descriptor.SigningPublicKey);
            var signer = new MLDsaSigner(MLDsaParameters.ml_dsa_65, deterministic: true);
            signer.Init(false, publicKey);
            signer.BlockUpdate(GetDescriptorSigningBytes(descriptor));
            return signer.VerifySignature(descriptor.Signature);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static PqcRendezvousOutbound CreateRequest(
        PqcRendezvousDescriptor descriptor,
        string contentTopic,
        ReadOnlySpan<byte> plaintext)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentTopic);
        if (!VerifyDescriptor(descriptor, DateTimeOffset.UtcNow))
            throw new ArgumentException("The rendezvous descriptor is not valid.", nameof(descriptor));

        MLKemPublicKeyParameters publicKey = MLKemPublicKeyParameters.FromEncoding(
            MLKemParameters.ml_kem_768,
            descriptor.ChallengePublicKey);
        var encapsulator = new MLKemEncapsulator(MLKemParameters.ml_kem_768);
        encapsulator.Init(publicKey);
        byte[] challenge = new byte[encapsulator.EncapsulationLength];
        byte[] sharedSecret = new byte[encapsulator.SecretLength];
        encapsulator.Encapsulate(challenge, sharedSecret);

        try
        {
            byte[] key = DeriveAesKey(sharedSecret);
            try
            {
                byte[] nonce = RandomNumberGenerator.GetBytes(AesNonceSize);
                byte[] ciphertext = new byte[plaintext.Length];
                byte[] tag = new byte[AesTagSize];
                var unsigned = new PqcRendezvousRequest(
                    descriptor.TemporaryId,
                    descriptor.DescriptorId,
                    descriptor.Sequence,
                    descriptor.ExpiresAtUnixMilliseconds,
                    contentTopic,
                    challenge,
                    nonce,
                    ciphertext,
                    tag,
                    0);
                byte[] associatedData = GetAssociatedData(unsigned);
                EncryptAes256Gcm(key, nonce, plaintext, associatedData, ciphertext, tag);

                PqcRendezvousRequest encrypted = unsigned with
                {
                    Ciphertext = ciphertext,
                    AuthenticationTag = tag,
                };
                (ulong nonceValue, int attempts) = SolveProofOfWork(descriptor, encrypted);
                return new PqcRendezvousOutbound(encrypted with { ProofOfWorkNonce = nonceValue }, attempts);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    /// <summary>
    /// Splits a signed descriptor into packet-safe Store records. The hash and
    /// the chunk metadata stay visible during transport. The code verifies the
    /// descriptor signature only after a complete reassembly.
    /// </summary>
    public static PqcRendezvousDescriptorChunk[] CreateDescriptorChunks(
        PqcRendezvousDescriptor descriptor,
        int maxPayloadBytes = DefaultDescriptorChunkPayloadSize)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!VerifyDescriptor(descriptor, DateTimeOffset.UtcNow))
            throw new ArgumentException("The rendezvous descriptor is not valid.", nameof(descriptor));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadBytes, 256);

        byte[] encoded = EncodeDescriptor(descriptor);
        byte[] descriptorHash = SHA256.HashData(encoded);
        int count = checked((encoded.Length + maxPayloadBytes - 1) / maxPayloadBytes);
        var chunks = new PqcRendezvousDescriptorChunk[count];
        for (int index = 0; index < count; index++)
        {
            int offset = checked(index * maxPayloadBytes);
            int length = Math.Min(maxPayloadBytes, encoded.Length - offset);
            byte[] payload = new byte[length];
            encoded.AsSpan(offset, length).CopyTo(payload);
            chunks[index] = new PqcRendezvousDescriptorChunk(
                descriptor.AlgorithmSuite.ProtocolVersion,
                descriptorHash,
                index,
                count,
                payload);
        }

        return chunks;
    }

    public static bool TryReassembleDescriptor(
        IEnumerable<PqcRendezvousDescriptorChunk> chunks,
        DateTimeOffset now,
        [MaybeNullWhen(false)] out PqcRendezvousDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        descriptor = null;
        PqcRendezvousDescriptorChunk[] all = chunks.ToArray();
        if (all.Length == 0 || all.Length > 64)
            return false;

        PqcRendezvousDescriptorChunk first = all[0];
        if (first.Count <= 0 || first.Count > 64 || all.Length != first.Count || first.DescriptorHash.Length != 32)
            return false;
        if (all.Any(chunk =>
                chunk.ProtocolVersion != first.ProtocolVersion ||
                chunk.Count != first.Count ||
                !chunk.DescriptorHash.AsSpan().SequenceEqual(first.DescriptorHash) ||
                chunk.Index < 0 ||
                chunk.Index >= first.Count ||
                chunk.Payload.Length == 0 ||
                chunk.Payload.Length > DefaultDescriptorChunkPayloadSize))
        {
            return false;
        }

        PqcRendezvousDescriptorChunk[] ordered = all.OrderBy(chunk => chunk.Index).ToArray();
        if (ordered.Select(chunk => chunk.Index).Distinct().Count() != first.Count)
            return false;

        int length;
        try
        {
            length = ordered.Sum(chunk => checked(chunk.Payload.Length));
        }
        catch (OverflowException)
        {
            return false;
        }

        if (length > 64 * DefaultDescriptorChunkPayloadSize)
            return false;
        byte[] encoded = new byte[length];
        int offset = 0;
        foreach (PqcRendezvousDescriptorChunk chunk in ordered)
        {
            chunk.Payload.CopyTo(encoded, offset);
            offset += chunk.Payload.Length;
        }

        if (!SHA256.HashData(encoded).AsSpan().SequenceEqual(first.DescriptorHash) ||
            !TryDecodeDescriptor(encoded, out descriptor) ||
            descriptor.AlgorithmSuite.ProtocolVersion != first.ProtocolVersion ||
            !VerifyDescriptor(descriptor, now))
        {
            descriptor = null;
            return false;
        }

        return true;
    }

    public static PqcRendezvousReceiveResult TryReceive(
        PqcRendezvousIdentity recipient,
        PqcRendezvousDescriptor descriptor,
        PqcRendezvousRequest request,
        ISet<string> receivedEventIds,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(receivedEventIds);

        if (!VerifyDescriptor(descriptor, now))
            return PqcRendezvousReceiveResult.Rejected("Descriptor signature or expiry is invalid.");
        if (!MatchesDescriptor(descriptor, request))
            return PqcRendezvousReceiveResult.Rejected("Request is not bound to this descriptor.");
        if (!VerifyProofOfWork(descriptor, request))
            return PqcRendezvousReceiveResult.Rejected("Proof of work is invalid.");

        string eventId = Convert.ToHexString(SHA256.HashData(GetRequestBytes(request)));
        if (!receivedEventIds.Add(eventId))
            return PqcRendezvousReceiveResult.Rejected("Duplicate request.");

        try
        {
            var decapsulator = new MLKemDecapsulator(MLKemParameters.ml_kem_768);
            decapsulator.Init(recipient.ChallengePrivateKey);
            if (request.Challenge.Length != decapsulator.EncapsulationLength)
                return PqcRendezvousReceiveResult.Rejected("ML-KEM challenge has an invalid length.");

            byte[] sharedSecret = new byte[decapsulator.SecretLength];
            decapsulator.Decapsulate(request.Challenge, sharedSecret);
            try
            {
                byte[] key = DeriveAesKey(sharedSecret);
                try
                {
                    byte[] plaintext = new byte[request.Ciphertext.Length];
                    if (!TryDecryptAes256Gcm(
                            key,
                            request.AesNonce,
                            request.Ciphertext,
                            request.AuthenticationTag,
                            GetAssociatedData(request),
                            plaintext))
                    {
                        receivedEventIds.Remove(eventId);
                        return PqcRendezvousReceiveResult.Rejected("AES-256-GCM authentication failed.");
                    }
                    return PqcRendezvousReceiveResult.Accepted(eventId, plaintext);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sharedSecret);
            }
        }
        catch (CryptographicException)
        {
            receivedEventIds.Remove(eventId);
            return PqcRendezvousReceiveResult.Rejected("AES-256-GCM authentication failed.");
        }
        catch (ArgumentException)
        {
            receivedEventIds.Remove(eventId);
            return PqcRendezvousReceiveResult.Rejected("ML-KEM challenge is malformed.");
        }
    }

    public static bool VerifyProofOfWork(PqcRendezvousDescriptor descriptor, PqcRendezvousRequest request)
    {
        byte[] root = SHA256.HashData(GetProofOfWorkRootBytes(descriptor, request));
        Span<byte> input = stackalloc byte[root.Length + sizeof(ulong)];
        root.CopyTo(input);
        BinaryPrimitives.WriteUInt64BigEndian(input[root.Length..], request.ProofOfWorkNonce);
        byte[] hash = SHA256.HashData(input);
        return CountLeadingZeroBits(hash) >= descriptor.MinimumWorkBits;
    }

    private static (ulong Nonce, int Attempts) SolveProofOfWork(
        PqcRendezvousDescriptor descriptor,
        PqcRendezvousRequest request)
    {
        byte[] root = SHA256.HashData(GetProofOfWorkRootBytes(descriptor, request));
        Span<byte> input = stackalloc byte[root.Length + sizeof(ulong)];
        root.CopyTo(input);
        for (ulong nonce = 0, attempts = 1; ; nonce++, attempts++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(input[root.Length..], nonce);
            if (CountLeadingZeroBits(SHA256.HashData(input)) >= descriptor.MinimumWorkBits)
                return (nonce, checked((int)attempts));
        }
    }

    private static byte[] SignDescriptor(MLDsaPrivateKeyParameters signingKey, byte[] descriptorBytes)
    {
        var signer = new MLDsaSigner(MLDsaParameters.ml_dsa_65, deterministic: true);
        signer.Init(true, signingKey);
        signer.BlockUpdate(descriptorBytes);
        return signer.GenerateSignature();
    }

    private static byte[] DeriveAesKey(ReadOnlySpan<byte> mlKemSharedSecret)
    {
        byte[] material = new byte[AesKeyDomain.Length + mlKemSharedSecret.Length];
        AesKeyDomain.CopyTo(material, 0);
        mlKemSharedSecret.CopyTo(material.AsSpan(AesKeyDomain.Length));
        return SHA256.HashData(material);
    }

    public static void EncryptAes256Gcm(
        byte[] key,
        byte[] nonce,
        ReadOnlySpan<byte> plaintext,
        byte[] associatedData,
        byte[] ciphertext,
        byte[] tag)
    {
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(true, new AeadParameters(new KeyParameter(key), AesTagSize * 8, nonce, associatedData));
        byte[] output = new byte[cipher.GetOutputSize(plaintext.Length)];
        int length = cipher.ProcessBytes(plaintext, output);
        length += cipher.DoFinal(output.AsSpan(length));
        if (length != plaintext.Length + AesTagSize)
            throw new CryptographicException("Unexpected AES-256-GCM output length.");

        output.AsSpan(0, ciphertext.Length).CopyTo(ciphertext);
        output.AsSpan(ciphertext.Length, tag.Length).CopyTo(tag);
    }

    public static bool TryDecryptAes256Gcm(
        byte[] key,
        byte[] nonce,
        byte[] ciphertext,
        byte[] tag,
        byte[] associatedData,
        byte[] plaintext)
    {
        byte[] encrypted = new byte[ciphertext.Length + tag.Length];
        ciphertext.CopyTo(encrypted, 0);
        tag.CopyTo(encrypted, ciphertext.Length);
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(false, new AeadParameters(new KeyParameter(key), AesTagSize * 8, nonce, associatedData));

        try
        {
            int length = cipher.ProcessBytes(encrypted, plaintext);
            length += cipher.DoFinal(plaintext.AsSpan(length));
            return length == plaintext.Length;
        }
        catch (InvalidCipherTextException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            return false;
        }
    }

    private static bool MatchesDescriptor(PqcRendezvousDescriptor descriptor, PqcRendezvousRequest request) =>
        string.Equals(descriptor.TemporaryId, request.TemporaryId, StringComparison.Ordinal) &&
        descriptor.DescriptorId.AsSpan().SequenceEqual(request.DescriptorId) &&
        descriptor.Sequence == request.DescriptorSequence &&
        descriptor.ExpiresAtUnixMilliseconds == request.DescriptorExpiresAtUnixMilliseconds &&
        request.AesNonce.Length == AesNonceSize &&
        request.AuthenticationTag.Length == AesTagSize;

    private static byte[] GetDescriptorSigningBytes(PqcRendezvousDescriptor descriptor) =>
        Join(
            DescriptorDomain,
            Int32(descriptor.AlgorithmSuite.ProtocolVersion),
            Utf8(descriptor.AlgorithmSuite.SigningAlgorithm),
            Utf8(descriptor.AlgorithmSuite.ChallengeAlgorithm),
            Utf8(descriptor.AlgorithmSuite.SymmetricAlgorithm),
            Utf8(descriptor.TemporaryId),
            descriptor.DescriptorId,
            Int64(descriptor.Sequence),
            Int64(descriptor.ExpiresAtUnixMilliseconds),
            descriptor.SigningPublicKey,
            descriptor.ChallengePublicKey,
            descriptor.ProofOfWorkSeed,
            Int32(descriptor.MinimumWorkBits));

    private static byte[] GetAssociatedData(PqcRendezvousRequest request) =>
        Join(
            Utf8("noks/rendezvous/aad/v1"),
            Utf8(request.TemporaryId),
            request.DescriptorId,
            Int64(request.DescriptorSequence),
            Int64(request.DescriptorExpiresAtUnixMilliseconds),
            Utf8(request.ContentTopic),
            request.Challenge,
            request.AesNonce);

    private static byte[] GetProofOfWorkRootBytes(PqcRendezvousDescriptor descriptor, PqcRendezvousRequest request) =>
        Join(
            WorkDomain,
            Int32(descriptor.AlgorithmSuite.ProtocolVersion),
            Utf8(descriptor.AlgorithmSuite.SigningAlgorithm),
            Utf8(descriptor.AlgorithmSuite.ChallengeAlgorithm),
            Utf8(descriptor.AlgorithmSuite.SymmetricAlgorithm),
            descriptor.DescriptorId,
            Int64(descriptor.Sequence),
            Int64(descriptor.ExpiresAtUnixMilliseconds),
            descriptor.ProofOfWorkSeed,
            Int32(descriptor.MinimumWorkBits),
            Utf8(request.ContentTopic),
            request.Challenge,
            request.AesNonce,
            request.Ciphertext,
            request.AuthenticationTag);

    private static byte[] GetRequestBytes(PqcRendezvousRequest request) =>
        Join(
            GetAssociatedData(request),
            request.Ciphertext,
            request.AuthenticationTag,
            UInt64(request.ProofOfWorkNonce));

    private static byte[] EncodeDescriptor(PqcRendezvousDescriptor descriptor) =>
        Join(
            Int32(descriptor.AlgorithmSuite.ProtocolVersion),
            Utf8(descriptor.AlgorithmSuite.SigningAlgorithm),
            Utf8(descriptor.AlgorithmSuite.ChallengeAlgorithm),
            Utf8(descriptor.AlgorithmSuite.SymmetricAlgorithm),
            Utf8(descriptor.TemporaryId),
            descriptor.DescriptorId,
            Int64(descriptor.Sequence),
            Int64(descriptor.ExpiresAtUnixMilliseconds),
            descriptor.SigningPublicKey,
            descriptor.ChallengePublicKey,
            descriptor.ProofOfWorkSeed,
            Int32(descriptor.MinimumWorkBits),
            descriptor.Signature);

    private static bool TryDecodeDescriptor(ReadOnlySpan<byte> encoded, [MaybeNullWhen(false)] out PqcRendezvousDescriptor descriptor)
    {
        descriptor = null;
        var fields = new byte[13][];
        int offset = 0;
        for (int index = 0; index < fields.Length; index++)
        {
            if (offset > encoded.Length - sizeof(int))
                return false;
            int length = BinaryPrimitives.ReadInt32BigEndian(encoded[offset..]);
            offset += sizeof(int);
            if (length < 0 || length > encoded.Length - offset)
                return false;
            fields[index] = encoded.Slice(offset, length).ToArray();
            offset += length;
        }

        if (offset != encoded.Length || fields[0].Length != sizeof(int) || fields[6].Length != sizeof(long) ||
            fields[7].Length != sizeof(long) || fields[11].Length != sizeof(int))
        {
            return false;
        }

        try
        {
            descriptor = new PqcRendezvousDescriptor(
                new PqcRendezvousAlgorithmSuite(
                    BinaryPrimitives.ReadInt32BigEndian(fields[0]),
                    Encoding.UTF8.GetString(fields[1]),
                    Encoding.UTF8.GetString(fields[2]),
                    Encoding.UTF8.GetString(fields[3])),
                Encoding.UTF8.GetString(fields[4]),
                fields[5],
                BinaryPrimitives.ReadInt64BigEndian(fields[6]),
                BinaryPrimitives.ReadInt64BigEndian(fields[7]),
                fields[8],
                fields[9],
                fields[10],
                BinaryPrimitives.ReadInt32BigEndian(fields[11]),
                fields[12]);
            return true;
        }
        catch (ArgumentException)
        {
            descriptor = null;
            return false;
        }
    }

    private static byte[] Join(params byte[][] values)
    {
        int length = values.Sum(value => checked(value.Length + sizeof(int)));
        byte[] output = new byte[length];
        int offset = 0;
        foreach (byte[] value in values)
        {
            BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(offset), value.Length);
            offset += sizeof(int);
            value.CopyTo(output.AsSpan(offset));
            offset += value.Length;
        }

        return output;
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static byte[] Int32(int value)
    {
        byte[] bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Int64(long value)
    {
        byte[] bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] UInt64(ulong value)
    {
        byte[] bytes = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    private static int CountLeadingZeroBits(ReadOnlySpan<byte> hash)
    {
        int bits = 0;
        foreach (byte value in hash)
        {
            if (value == 0)
            {
                bits += 8;
                continue;
            }

            return bits + BitOperations.LeadingZeroCount((uint)value) - 24;
        }

        return bits;
    }

    private static void ValidateTemporaryId(string temporaryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryId);
        if (temporaryId.Length > 32 || temporaryId.Any(character => character is < '0' or > '9'))
            throw new ArgumentException("A temporary ID must contain only up to 32 decimal digits.", nameof(temporaryId));
    }
}
