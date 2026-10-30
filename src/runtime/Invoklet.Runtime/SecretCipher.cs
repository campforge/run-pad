using System.Security.Cryptography;
using System.Text;

namespace Invoklet.Runtime;

// Mirrors Invoklet.Build.Core.Secrets.SecretKeys (the key Invoklet passes in INVOKLET_SECRETS_KEY and
// the value layout), duplicated rather than shared: this assembly is netstandard2.0 and references
// none of Invoklet's own. Decryption only — Invoklet writes the store.
internal static class SecretCipher
{
    internal const string Protection = "AES-256-CBC-HMAC-SHA256";
    internal const string KeyEnvVar = "INVOKLET_SECRETS_KEY";

    private const int KeySize = 32, IvSize = 16, BlockSize = 16, TagSize = 32;

    /// <exception cref="CryptographicException">Another key, another name, or a damaged value.</exception>
    public static string Decrypt(byte[] key, string name, string value)
    {
        if (key.Length != KeySize) throw new CryptographicException("The Secret Store's key is damaged.");
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length < IvSize + BlockSize + TagSize) throw new CryptographicException("The value is damaged.");
        var iv = Slice(bytes, 0, IvSize);
        var cipher = Slice(bytes, IvSize, bytes.Length - IvSize - TagSize);
        var tag = Slice(bytes, bytes.Length - TagSize, TagSize);

        byte[] encryptionKey, macKey;
        using (var derive = new HMACSHA256(key))
        {
            encryptionKey = derive.ComputeHash(Encoding.ASCII.GetBytes("invoklet-secrets-encryption"));
            macKey = derive.ComputeHash(Encoding.ASCII.GetBytes("invoklet-secrets-mac"));
        }
        if (!SameBytes(tag, Tag(macKey, name, iv, cipher)))
            throw new CryptographicException("The value doesn't match its key or name.");

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor(encryptionKey, iv);
        return Encoding.UTF8.GetString(decryptor.TransformFinalBlock(cipher, 0, cipher.Length));
    }

    // The name first with its big-endian length, then IV and ciphertext, as SecretKeys.Tag.
    private static byte[] Tag(byte[] macKey, string name, byte[] iv, byte[] cipher)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        using var data = new MemoryStream();
        var length = nameBytes.Length;
        data.Write(new[] { (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length }, 0, 4);
        data.Write(nameBytes, 0, nameBytes.Length);
        data.Write(iv, 0, iv.Length);
        data.Write(cipher, 0, cipher.Length);
        using var mac = new HMACSHA256(macKey);
        return mac.ComputeHash(data.ToArray());
    }

    private static byte[] Slice(byte[] source, int start, int count)
    {
        var result = new byte[count];
        Buffer.BlockCopy(source, start, result, 0, count);
        return result;
    }

    // Constant time, so a wrong tag tells nothing by how long the comparison took.
    private static bool SameBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        var difference = 0;
        for (var i = 0; i < a.Length; i++) difference |= a[i] ^ b[i];
        return difference == 0;
    }
}
