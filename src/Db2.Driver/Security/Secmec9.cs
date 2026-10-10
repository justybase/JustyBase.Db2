using System.Numerics;
using System.Security.Cryptography;

namespace JustyBase.Db2.Security;

/// <summary>
/// DRDA EUSRIDPWD (security mechanism 9) helpers: 256-bit Diffie-Hellman key
/// agreement and DES-CBC/PKCS5 password and userid encryption. The parameter
/// values and derivation match the published DRDA values used by Apache
/// Derby's EncryptionManager/DecryptionManager.
/// </summary>
internal static class Secmec9
{
    private static readonly BigInteger Prime = FromHex("C62112D73EE613F0947AB31F0F6846A1BFF5B3A4CA0D60BC1E4C7A0D8C16B3E3");
    private static readonly BigInteger Base = FromHex("4690FA1F7B9E1D4442C86C9114603FDECF071EDCEC5F626E21E256AED9EA34E4");

    private static BigInteger FromHex(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        return new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
    }

    private static void ValidatePrivateKey(BigInteger privateKey)
    {
        if (privateKey < 2 || privateKey >= Prime - 1) throw new ArgumentOutOfRangeException(nameof(privateKey), "DB2 EUSRIDPWD private key is out of range.");
    }

    private static byte[] ToFixed32(BigInteger value)
    {
        var output = new byte[32];
        value.TryWriteBytes(output, out _, isUnsigned: true, isBigEndian: true);
        return output;
    }

    private static byte[] NormalizePeer(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length > 32) throw new ArgumentException("DB2 EUSRIDPWD peer public key is longer than 32 bytes.", nameof(publicKey));
        if (publicKey.Length == 32) return publicKey.ToArray();
        var padded = new byte[32];
        publicKey.CopyTo(padded.AsSpan(32 - publicKey.Length));
        return padded;
    }

    /// <summary>Generates a 255-bit Diffie-Hellman private exponent.</summary>
    public static BigInteger GeneratePrivateKey()
    {
        Span<byte> bytes = stackalloc byte[32];
        while (true)
        {
            RandomNumberGenerator.Fill(bytes);
            bytes[0] &= 0x7f;
            var candidate = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
            if (candidate >= 2 && candidate < Prime - 1) return candidate;
        }
    }

    /// <summary>Computes g^private mod p as the 32-byte big-endian DRDA public key.</summary>
    public static byte[] CalculatePublicKey(BigInteger privateKey)
    {
        ValidatePrivateKey(privateKey);
        return ToFixed32(BigInteger.ModPow(Base, privateKey, Prime));
    }

    /// <summary>Derives the 32-byte shared secret from the server public key and client private key.</summary>
    public static byte[] CalculateSessionKey(ReadOnlySpan<byte> serverPublicKey, BigInteger privateKey)
    {
        ValidatePrivateKey(privateKey);
        var peer = NormalizePeer(serverPublicKey);
        var peerValue = new BigInteger(peer, isUnsigned: true, isBigEndian: true);
        if (peerValue <= 1 || peerValue >= Prime - 1) throw new ArgumentException("DB2 EUSRIDPWD server public key is out of range.", nameof(serverPublicKey));
        return ToFixed32(BigInteger.ModPow(peerValue, privateKey, Prime));
    }

    /// <summary>Encrypts one SECMEC 9 field with DES-CBC/PKCS5 using the DRDA derived key and server-key IV.</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> serverPublicKey, BigInteger privateKey)
    {
        var peer = NormalizePeer(serverPublicKey);
        if (peer.Length < 20) throw new ArgumentException("DB2 EUSRIDPWD server security token is too short.", nameof(serverPublicKey));
        var sessionKey = CalculateSessionKey(peer, privateKey);
        using var des = DES.Create();
        des.Mode = CipherMode.CBC;
        des.Padding = PaddingMode.PKCS7;
        des.Key = sessionKey.AsSpan(12, 8).ToArray();
        des.IV = peer.AsSpan(12, 8).ToArray();
        using var encryptor = des.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext.ToArray(), 0, plaintext.Length);
    }
}
