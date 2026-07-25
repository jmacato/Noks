namespace Noks.Cryptography;

public sealed record PqcKemEncapsulation(byte[] Ciphertext, byte[] SharedSecret);
