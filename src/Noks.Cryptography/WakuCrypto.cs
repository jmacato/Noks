using System;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;
using Org.BouncyCastle.Math.EC.Custom.Sec;
using Org.BouncyCastle.Math.EC.Rfc7748;
using BouncyChaCha20Poly1305 = Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305;
using BouncyECPoint = Org.BouncyCastle.Math.EC.ECPoint;

namespace Noks.Cryptography;

public static class WakuCrypto
{
    public const int X25519KeySize = 32;
    public const int ChaCha20Poly1305KeySize = 32;
    public const int ChaCha20Poly1305NonceSize = 12;
    public const int ChaCha20Poly1305TagSize = 16;
    public const int Secp256k1PrivateKeySize = 32;
    public const int Secp256k1CompressedPublicKeySize = 33;

    private static readonly SecP256K1Curve Secp256k1Curve = new();
    private static readonly BigInteger Secp256k1Order = Secp256k1Curve.Order;
    private static readonly BigInteger Secp256k1HalfOrder = Secp256k1Order.ShiftRight(1);
    private static readonly BouncyECPoint Secp256k1Generator = Secp256k1Curve.DecodePoint(Convert.FromHexString(
        "0479BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798" +
        "483ADA7726A3C4655DA4FBFC0E1108A8FD17B448A68554199C47D08FFB10D4B8"));

    public static void GenerateX25519PrivateKey(Span<byte> privateKey)
    {
        RequireLength(privateKey, X25519KeySize, nameof(privateKey));
        RandomNumberGenerator.Fill(privateKey);
        privateKey[0] &= 0xF8;
        privateKey[^1] &= 0x7F;
        privateKey[^1] |= 0x40;
    }

    public static void GetX25519PublicKey(ReadOnlySpan<byte> privateKey, Span<byte> publicKey)
    {
        RequireLength(privateKey, X25519KeySize, nameof(privateKey));
        RequireLength(publicKey, X25519KeySize, nameof(publicKey));
        X25519.ScalarMultBase(privateKey, publicKey);
    }

    public static bool TryX25519Agreement(
        ReadOnlySpan<byte> privateKey,
        ReadOnlySpan<byte> peerPublicKey,
        Span<byte> sharedSecret)
    {
        RequireLength(privateKey, X25519KeySize, nameof(privateKey));
        RequireLength(peerPublicKey, X25519KeySize, nameof(peerPublicKey));
        RequireLength(sharedSecret, X25519KeySize, nameof(sharedSecret));
        return X25519.CalculateAgreement(privateKey, peerPublicKey, sharedSecret);
    }

    public static void ChaCha20Poly1305Encrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> associatedData,
        Span<byte> ciphertextAndTag)
    {
        RequireLength(key, ChaCha20Poly1305KeySize, nameof(key));
        RequireLength(nonce, ChaCha20Poly1305NonceSize, nameof(nonce));
        RequireLength(ciphertextAndTag, plaintext.Length + ChaCha20Poly1305TagSize, nameof(ciphertextAndTag));

        var cipher = CreateChaCha20Poly1305(true, key, nonce, associatedData);
        var written = cipher.ProcessBytes(plaintext, ciphertextAndTag);
        written += cipher.DoFinal(ciphertextAndTag[written..]);
        if (written != ciphertextAndTag.Length)
            throw new CryptographicException("Unexpected ChaCha20-Poly1305 output length.");
    }

    public static bool TryChaCha20Poly1305Decrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertextAndTag,
        ReadOnlySpan<byte> associatedData,
        Span<byte> plaintext)
    {
        RequireLength(key, ChaCha20Poly1305KeySize, nameof(key));
        RequireLength(nonce, ChaCha20Poly1305NonceSize, nameof(nonce));
        if (ciphertextAndTag.Length < ChaCha20Poly1305TagSize)
            throw new ArgumentException("Ciphertext must contain a 16-byte authentication tag.", nameof(ciphertextAndTag));
        RequireLength(plaintext, ciphertextAndTag.Length - ChaCha20Poly1305TagSize, nameof(plaintext));

        try
        {
            var cipher = CreateChaCha20Poly1305(false, key, nonce, associatedData);
            var written = cipher.ProcessBytes(ciphertextAndTag, plaintext);
            written += cipher.DoFinal(plaintext[written..]);
            if (written != plaintext.Length)
                throw new CryptographicException("Unexpected ChaCha20-Poly1305 output length.");
            return true;
        }
        catch (InvalidCipherTextException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            return false;
        }
    }

    public static void GenerateSecp256k1PrivateKey(Span<byte> privateKey)
    {
        RequireLength(privateKey, Secp256k1PrivateKeySize, nameof(privateKey));
        do
        {
            RandomNumberGenerator.Fill(privateKey);
        }
        while (!IsValidSecp256k1PrivateKey(privateKey));
    }

    public static void GetSecp256k1PublicKey(ReadOnlySpan<byte> privateKey, Span<byte> compressedPublicKey)
    {
        RequireLength(compressedPublicKey, Secp256k1CompressedPublicKeySize, nameof(compressedPublicKey));
        var scalar = ReadSecp256k1PrivateKey(privateKey);
        Secp256k1Generator.Multiply(scalar).Normalize().EncodeTo(compressed: true, compressedPublicKey);
    }

    public static byte[] SignSecp256k1Sha256(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> message)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(message, hash);
        try
        {
            return SignSecp256k1Hash(privateKey, hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public static bool VerifySecp256k1Sha256(
        ReadOnlySpan<byte> compressedPublicKey,
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> derSignature)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(message, hash);
        try
        {
            return VerifySecp256k1Hash(compressedPublicKey, hash, derSignature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public static byte[] SignSecp256k1Hash(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> hash)
    {
        RequireLength(hash, 32, nameof(hash));
        var d = ReadSecp256k1PrivateKey(privateKey);
        var z = new BigInteger(1, hash);
        using var kGenerator = new Rfc6979KGenerator(privateKey, hash);
        while (true)
        {
            var k = kGenerator.Next();
            var r = Secp256k1Generator.Multiply(k).Normalize().AffineXCoord.ToBigInteger().Mod(Secp256k1Order);
            if (r.SignValue == 0)
                continue;

            var s = k.ModInverse(Secp256k1Order).Multiply(z.Add(r.Multiply(d))).Mod(Secp256k1Order);
            if (s.SignValue == 0)
                continue;
            if (s.CompareTo(Secp256k1HalfOrder) > 0)
                s = Secp256k1Order.Subtract(s);
            return EncodeDerSignature(r, s);
        }
    }

    public static bool VerifySecp256k1Hash(
        ReadOnlySpan<byte> compressedPublicKey,
        ReadOnlySpan<byte> hash,
        ReadOnlySpan<byte> derSignature)
    {
        RequireLength(hash, 32, nameof(hash));
        if (compressedPublicKey.Length != Secp256k1CompressedPublicKeySize ||
            (compressedPublicKey[0] != 0x02 && compressedPublicKey[0] != 0x03))
            return false;
        if (!TryDecodeDerSignature(derSignature, out var r, out var s))
            return false;
        if (r.SignValue <= 0 || s.SignValue <= 0 ||
            r.CompareTo(Secp256k1Order) >= 0 || s.CompareTo(Secp256k1HalfOrder) > 0)
            return false;

        try
        {
            var publicPoint = Secp256k1Curve.DecodePoint(compressedPublicKey);
            if (publicPoint.IsInfinity || !publicPoint.IsValid())
                return false;

            var z = new BigInteger(1, hash);
            var inverse = s.ModInverse(Secp256k1Order);
            var u1 = z.Multiply(inverse).Mod(Secp256k1Order);
            var u2 = r.Multiply(inverse).Mod(Secp256k1Order);
            var point = ECAlgorithms.SumOfTwoMultiplies(Secp256k1Generator, u1, publicPoint, u2).Normalize();
            return !point.IsInfinity && point.AffineXCoord.ToBigInteger().Mod(Secp256k1Order).Equals(r);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static BouncyChaCha20Poly1305 CreateChaCha20Poly1305(
        bool forEncryption,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> associatedData)
    {
        var cipher = new BouncyChaCha20Poly1305();
        cipher.Init(forEncryption, new AeadParameters(
            new KeyParameter(key),
            ChaCha20Poly1305TagSize * 8,
            nonce.ToArray(),
            associatedData.ToArray()));
        return cipher;
    }

    private static bool IsValidSecp256k1PrivateKey(ReadOnlySpan<byte> privateKey)
    {
        var scalar = new BigInteger(1, privateKey);
        return scalar.SignValue > 0 && scalar.CompareTo(Secp256k1Order) < 0;
    }

    private static BigInteger ReadSecp256k1PrivateKey(ReadOnlySpan<byte> privateKey)
    {
        RequireLength(privateKey, Secp256k1PrivateKeySize, nameof(privateKey));
        var scalar = new BigInteger(1, privateKey);
        if (scalar.SignValue <= 0 || scalar.CompareTo(Secp256k1Order) >= 0)
            throw new ArgumentException("Invalid secp256k1 private key.", nameof(privateKey));
        return scalar;
    }

    private static byte[] Combine(params byte[][] values)
    {
        var length = 0;
        foreach (var value in values)
            length += value.Length;
        var result = new byte[length];
        var offset = 0;
        foreach (var value in values)
        {
            value.CopyTo(result, offset);
            offset += value.Length;
        }
        return result;
    }

    private static byte[] ToFixedWidth(BigInteger value, int width)
    {
        var bytes = value.ToByteArrayUnsigned();
        if (bytes.Length > width)
            throw new CryptographicException("Integer exceeded expected width.");
        var result = new byte[width];
        bytes.CopyTo(result, width - bytes.Length);
        CryptographicOperations.ZeroMemory(bytes);
        return result;
    }

    private static byte[] EncodeDerSignature(BigInteger r, BigInteger s)
    {
        var rBytes = EncodeDerInteger(r);
        var sBytes = EncodeDerInteger(s);
        var result = new byte[6 + rBytes.Length + sBytes.Length];
        var offset = 0;
        result[offset++] = 0x30;
        result[offset++] = (byte)(result.Length - 2);
        result[offset++] = 0x02;
        result[offset++] = (byte)rBytes.Length;
        rBytes.CopyTo(result, offset);
        offset += rBytes.Length;
        result[offset++] = 0x02;
        result[offset++] = (byte)sBytes.Length;
        sBytes.CopyTo(result, offset);
        return result;
    }

    private static byte[] EncodeDerInteger(BigInteger value)
    {
        var unsigned = value.ToByteArrayUnsigned();
        if ((unsigned[0] & 0x80) == 0)
            return unsigned;
        var result = new byte[unsigned.Length + 1];
        unsigned.CopyTo(result, 1);
        return result;
    }

    private static bool TryDecodeDerSignature(
        ReadOnlySpan<byte> signature,
        out BigInteger r,
        out BigInteger s)
    {
        r = BigInteger.Zero;
        s = BigInteger.Zero;
        if (signature.Length < 8 || signature.Length > 72 || signature[0] != 0x30 || signature[1] != signature.Length - 2)
            return false;

        var offset = 2;
        if (!TryReadDerInteger(signature, ref offset, out r) || !TryReadDerInteger(signature, ref offset, out s))
            return false;
        return offset == signature.Length;
    }

    private static bool TryReadDerInteger(ReadOnlySpan<byte> input, ref int offset, out BigInteger value)
    {
        value = BigInteger.Zero;
        if (offset + 2 > input.Length || input[offset++] != 0x02)
            return false;
        var length = input[offset++];
        if (length == 0 || length > 33 || offset + length > input.Length)
            return false;
        var bytes = input.Slice(offset, length);
        offset += length;
        if ((bytes[0] & 0x80) != 0 || (bytes.Length > 1 && bytes[0] == 0 && (bytes[1] & 0x80) == 0))
            return false;
        value = new BigInteger(1, bytes);
        return true;
    }

    private static void RequireLength<T>(ReadOnlySpan<T> value, int length, string parameterName)
    {
        if (value.Length != length)
            throw new ArgumentException($"Expected exactly {length} elements.", parameterName);
    }

    private static void RequireLength<T>(Span<T> value, int length, string parameterName)
    {
        if (value.Length != length)
            throw new ArgumentException($"Expected exactly {length} elements.", parameterName);
    }

    private sealed class Rfc6979KGenerator : IDisposable
    {
        private byte[] k = new byte[32];
        private byte[] v = new byte[32];

        public Rfc6979KGenerator(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> hash)
        {
            var x = privateKey.ToArray();
            var h1 = ToFixedWidth(new BigInteger(1, hash).Mod(Secp256k1Order), 32);
            Array.Fill(v, (byte)1);
            try
            {
                UpdateK(Combine(v, new byte[] { 0 }, x, h1));
                UpdateV();
                UpdateK(Combine(v, new byte[] { 1 }, x, h1));
                UpdateV();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(x);
                CryptographicOperations.ZeroMemory(h1);
            }
        }

        public BigInteger Next()
        {
            while (true)
            {
                UpdateV();
                var candidate = new BigInteger(1, v);
                if (candidate.SignValue > 0 && candidate.CompareTo(Secp256k1Order) < 0)
                    return candidate;
                UpdateK(Combine(v, new byte[] { 0 }));
                UpdateV();
            }
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(k);
            CryptographicOperations.ZeroMemory(v);
        }

        private void UpdateK(byte[] data)
        {
            byte[] next;
            try
            {
                next = HMACSHA256.HashData(k, data);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(data);
            }
            CryptographicOperations.ZeroMemory(k);
            k = next;
        }

        private void UpdateV()
        {
            var next = HMACSHA256.HashData(k, v);
            CryptographicOperations.ZeroMemory(v);
            v = next;
        }
    }
}
