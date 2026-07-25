using System.Security.Cryptography;
using System.Text;
using Noks.Cryptography;

namespace Noks.Cryptography.Tests;

public sealed class WakuCryptoTests
{
    [Fact]
    public void X25519MatchesRfc7748Vectors()
    {
        var alicePrivate = Hex("77076D0A7318A57D3C16C17251B26645DF4C2F87EBC0992AB177FBA51DB92C2A");
        var alicePublic = new byte[WakuCrypto.X25519KeySize];
        WakuCrypto.GetX25519PublicKey(alicePrivate, alicePublic);
        Assert.Equal(Hex("8520F0098930A754748B7DDCB43EF75A0DBF3A0D26381AF4EBA4A98EAA9B4E6A"), alicePublic);

        var bobPublic = Hex("DE9EDB7D7B7DC1B4D35B61C2ECE435373F8343C85B78674DADFC7E146F882B4F");
        var sharedSecret = new byte[WakuCrypto.X25519KeySize];
        Assert.True(WakuCrypto.TryX25519Agreement(alicePrivate, bobPublic, sharedSecret));
        Assert.Equal(Hex("4A5D9D5BA4CE2DE1728E3BF480350F25E07E21C947D19E3376F09B3C1E161742"), sharedSecret);
    }

    [Fact]
    public void X25519RejectsAllZeroSharedSecret()
    {
        var privateKey = new byte[WakuCrypto.X25519KeySize];
        WakuCrypto.GenerateX25519PrivateKey(privateKey);
        var sharedSecret = new byte[WakuCrypto.X25519KeySize];

        Assert.False(WakuCrypto.TryX25519Agreement(privateKey, new byte[WakuCrypto.X25519KeySize], sharedSecret));
        Assert.All(sharedSecret, value => Assert.Equal(0, value));
    }

    [Fact]
    public void ChaCha20Poly1305MatchesRfc8439Vector()
    {
        var key = Hex("808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9F");
        var nonce = Hex("070000004041424344454647");
        var associatedData = Hex("50515253C0C1C2C3C4C5C6C7");
        var plaintext = Encoding.ASCII.GetBytes(
            "Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");
        var expected = Hex(
            "D31A8D34648E60DB7B86AFBC53EF7EC2A4ADED51296E08FEA9E2B5A736EE62D63DBEA45E8CA9671282FAFB69DA92728B1A71DE0A9E060B2905D6A5B67ECD3B3692DDBD7F2D778B8C9803AEE328091B58FAB324E4FAD675945585808B4831D7BC3FF4DEF08E4B7A9DE576D26586CEC64B61161AE10B594F09E26A7E902ECBD0600691");
        var encrypted = new byte[plaintext.Length + WakuCrypto.ChaCha20Poly1305TagSize];

        WakuCrypto.ChaCha20Poly1305Encrypt(key, nonce, plaintext, associatedData, encrypted);
        Assert.Equal(expected, encrypted);

        var decrypted = new byte[plaintext.Length];
        Assert.True(WakuCrypto.TryChaCha20Poly1305Decrypt(key, nonce, encrypted, associatedData, decrypted));
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void ChaCha20Poly1305ClearsUnauthenticatedPlaintext()
    {
        var key = RandomNumberGenerator.GetBytes(WakuCrypto.ChaCha20Poly1305KeySize);
        var nonce = RandomNumberGenerator.GetBytes(WakuCrypto.ChaCha20Poly1305NonceSize);
        var plaintext = Encoding.UTF8.GetBytes("authenticated Noise frame");
        var encrypted = new byte[plaintext.Length + WakuCrypto.ChaCha20Poly1305TagSize];
        WakuCrypto.ChaCha20Poly1305Encrypt(key, nonce, plaintext, [], encrypted);
        encrypted[^1] ^= 1;

        var output = Enumerable.Repeat((byte)0xA5, plaintext.Length).ToArray();
        Assert.False(WakuCrypto.TryChaCha20Poly1305Decrypt(key, nonce, encrypted, [], output));
        Assert.All(output, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Secp256k1MatchesLibp2pNobleFixture()
    {
        var privateKey = new byte[WakuCrypto.Secp256k1PrivateKeySize];
        privateKey[^1] = 1;
        var publicKey = new byte[WakuCrypto.Secp256k1CompressedPublicKeySize];
        WakuCrypto.GetSecp256k1PublicKey(privateKey, publicKey);
        Assert.Equal(Hex("0279BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798"), publicKey);

        var message = Encoding.ASCII.GetBytes("noks-libp2p-interoperability");
        var expectedSignature = Hex(
            "30440220770102A6FE37B5742D2DC74EADC98997DD25A60F87BBEA69240E95D7F8E90235022006FD6669DB3F6A8ADFEF38FC58FC692DD3265BD67727C67CC8F478F309227DC3");
        var signature = WakuCrypto.SignSecp256k1Sha256(privateKey, message);

        Assert.Equal(expectedSignature, signature);
        Assert.True(WakuCrypto.VerifySecp256k1Sha256(publicKey, message, signature));
        message[0] ^= 1;
        Assert.False(WakuCrypto.VerifySecp256k1Sha256(publicKey, message, signature));
    }

    [Fact]
    public void Secp256k1RejectsMalformedInputs()
    {
        var privateKey = new byte[WakuCrypto.Secp256k1PrivateKeySize];
        privateKey[^1] = 1;
        var publicKey = new byte[WakuCrypto.Secp256k1CompressedPublicKeySize];
        WakuCrypto.GetSecp256k1PublicKey(privateKey, publicKey);
        var hash = SHA256.HashData("message"u8);

        Assert.False(WakuCrypto.VerifySecp256k1Hash(publicKey, hash, [0x30, 0x00]));
        Assert.False(WakuCrypto.VerifySecp256k1Hash([], hash, [0x30, 0x00]));
        Assert.False(WakuCrypto.VerifySecp256k1Hash(new byte[65], hash, [0x30, 0x00]));
        Assert.False(WakuCrypto.VerifySecp256k1Hash(new byte[33], hash, [0x30, 0x06, 0x02, 0x01, 0x01, 0x02, 0x01, 0x01]));
    }

    private static byte[] Hex(string value) => Convert.FromHexString(value);
}
