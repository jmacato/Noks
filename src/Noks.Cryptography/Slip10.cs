using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Math;

namespace Noks.Cryptography;

public static class Slip10
{
    public const uint HardenedOffset = 0x80000000;
    private static readonly byte[] Secp256k1MasterKey = Encoding.ASCII.GetBytes("Bitcoin seed");
    private static readonly byte[] Curve25519MasterKey = Encoding.ASCII.GetBytes("curve25519 seed");
    private static readonly BigInteger Secp256k1Order = new(
        "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141",
        16);

    public static byte[] DeriveSecp256k1(ReadOnlySpan<byte> seed, ReadOnlySpan<uint> path)
    {
        ValidateSeedAndPath(seed, path);
        ExtendedPrivateKey node = CreateSecp256k1Master(seed);
        try
        {
            foreach (uint index in path)
            {
                ExtendedPrivateKey child = DeriveSecp256k1Hardened(node, index);
                node.Dispose();
                node = child;
            }
            return (byte[])node.Key.Clone();
        }
        finally
        {
            node.Dispose();
        }
    }

    public static byte[] DeriveCurve25519(ReadOnlySpan<byte> seed, ReadOnlySpan<uint> path)
    {
        ValidateSeedAndPath(seed, path);
        ExtendedPrivateKey node = CreateMaster(Curve25519MasterKey, seed);
        try
        {
            foreach (uint index in path)
            {
                ExtendedPrivateKey child = DeriveCurve25519Hardened(node, index);
                node.Dispose();
                node = child;
            }
            return (byte[])node.Key.Clone();
        }
        finally
        {
            node.Dispose();
        }
    }

    private static ExtendedPrivateKey CreateSecp256k1Master(ReadOnlySpan<byte> seed)
    {
        byte[] input = seed.ToArray();
        try
        {
            while (true)
            {
                byte[] digest = HMACSHA512.HashData(Secp256k1MasterKey, input);
                if (IsValidSecp256k1Scalar(digest.AsSpan(0, 32)))
                {
                    ExtendedPrivateKey result = new(digest.AsSpan(0, 32), digest.AsSpan(32, 32));
                    CryptographicOperations.ZeroMemory(digest);
                    return result;
                }
                CryptographicOperations.ZeroMemory(input);
                input = digest;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private static ExtendedPrivateKey CreateMaster(ReadOnlySpan<byte> hmacKey, ReadOnlySpan<byte> seed)
    {
        byte[] digest = HMACSHA512.HashData(hmacKey, seed);
        try
        {
            return new ExtendedPrivateKey(digest.AsSpan(0, 32), digest.AsSpan(32, 32));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static ExtendedPrivateKey DeriveSecp256k1Hardened(ExtendedPrivateKey parent, uint index)
    {
        uint hardenedIndex = checked(index + HardenedOffset);
        Span<byte> data = stackalloc byte[37];
        parent.Key.CopyTo(data[1..33]);
        BinaryPrimitives.WriteUInt32BigEndian(data[33..], hardenedIndex);
        byte[] digest = HMACSHA512.HashData(parent.ChainCode, data);
        Span<byte> retry = stackalloc byte[37];
        try
        {
            while (true)
            {
                BigInteger left = new(1, digest.AsSpan(0, 32));
                BigInteger parentScalar = new(1, parent.Key);
                BigInteger child = left.Add(parentScalar).Mod(Secp256k1Order);
                if (left.CompareTo(Secp256k1Order) < 0 && child.SignValue != 0)
                {
                    byte[] key = ToFixedWidth(child, 32);
                    try
                    {
                        return new ExtendedPrivateKey(key, digest.AsSpan(32, 32));
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(key);
                    }
                }

                retry[0] = 0x01;
                digest.AsSpan(32, 32).CopyTo(retry[1..33]);
                BinaryPrimitives.WriteUInt32BigEndian(retry[33..], hardenedIndex);
                byte[] next = HMACSHA512.HashData(parent.ChainCode, retry);
                CryptographicOperations.ZeroMemory(digest);
                digest = next;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
            CryptographicOperations.ZeroMemory(retry);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static ExtendedPrivateKey DeriveCurve25519Hardened(ExtendedPrivateKey parent, uint index)
    {
        uint hardenedIndex = checked(index + HardenedOffset);
        Span<byte> data = stackalloc byte[37];
        parent.Key.CopyTo(data[1..33]);
        BinaryPrimitives.WriteUInt32BigEndian(data[33..], hardenedIndex);
        byte[] digest = HMACSHA512.HashData(parent.ChainCode, data);
        try
        {
            return new ExtendedPrivateKey(digest.AsSpan(0, 32), digest.AsSpan(32, 32));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static bool IsValidSecp256k1Scalar(ReadOnlySpan<byte> value)
    {
        BigInteger scalar = new(1, value);
        return scalar.SignValue > 0 && scalar.CompareTo(Secp256k1Order) < 0;
    }

    private static byte[] ToFixedWidth(BigInteger value, int width)
    {
        byte[] input = value.ToByteArrayUnsigned();
        byte[] result = new byte[width];
        input.CopyTo(result, width - input.Length);
        CryptographicOperations.ZeroMemory(input);
        return result;
    }

    private static void ValidateSeedAndPath(ReadOnlySpan<byte> seed, ReadOnlySpan<uint> path)
    {
        if (seed.Length is < 16 or > 64)
            throw new ArgumentException("SLIP-0010 seed length must be 16-64 bytes.", nameof(seed));
        foreach (uint index in path)
        {
            if (index >= HardenedOffset)
                throw new ArgumentOutOfRangeException(nameof(path), "Path indices must omit the hardened bit.");
        }
    }

    private sealed class ExtendedPrivateKey : IDisposable
    {
        public ExtendedPrivateKey(ReadOnlySpan<byte> key, ReadOnlySpan<byte> chainCode)
        {
            Key = key.ToArray();
            ChainCode = chainCode.ToArray();
        }

        public byte[] Key { get; }

        public byte[] ChainCode { get; }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Key);
            CryptographicOperations.ZeroMemory(ChainCode);
        }
    }
}
