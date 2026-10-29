using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Invoklet.Build.Core.Secrets;

/// <summary>The system keychain couldn't give or keep the Secret Store's key: locked, refused, or none installed.</summary>
public sealed class SecretKeyUnavailableException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>Where the key that encrypts the Secret Store's values on macOS and Linux is kept.</summary>
public interface ISecretKeySource
{
    /// <summary>The key; null when none has been created yet.</summary>
    /// <exception cref="SecretKeyUnavailableException">The keychain can't be read.</exception>
    byte[]? Find();

    /// <summary>The key, created and kept first when there is none.</summary>
    /// <exception cref="SecretKeyUnavailableException">The keychain can't be read or written.</exception>
    byte[] GetOrCreate();
}

/// <summary>
/// The Secret Store's key and the encryption of its values on macOS and Linux (Windows uses DPAPI).
/// The key is 32 random bytes kept in the system keychain — the macOS Keychain, or the Linux Secret
/// Service through <c>secret-tool</c> — never in the store's file. Invoklet and its command line read
/// it and pass it to the scripts they start in <see cref="EnvVar"/>, because a script's own process
/// can't open the keychain without asking the user for every new build.
/// </summary>
/// <remarks>
/// Invoklet.Runtime decrypts values itself for <c>Util.GetSecret</c> (its own copy of the format, as
/// for the store file): the protection name, the key derivation and the value layout must stay in step.
/// </remarks>
public static class SecretKeys
{
    public const string EnvVar = "INVOKLET_SECRETS_KEY";
    public const string FileEnvVar = "INVOKLET_SECRETS_FILE";

    /// <summary>The store file's <c>Protection</c> for values encrypted with this key.</summary>
    public const string Protection = "AES-256-CBC-HMAC-SHA256";

    private const int KeySize = 32, IvSize = 16, TagSize = 32;

    /// <summary>The system keychain on macOS and Linux; null on Windows, where DPAPI needs no key.</summary>
    public static ISecretKeySource? Keychain { get; } =
        OperatingSystem.IsMacOS() ? new MacKeychain() : OperatingSystem.IsLinux() ? new SecretTool() : null;

    /// <summary>The store's file: <see cref="FileEnvVar"/>, or <c>secrets.json</c> in Invoklet's data folder.</summary>
    public static string DefaultStorePath() =>
        Environment.GetEnvironmentVariable(FileEnvVar) is { Length: > 0 } path
            ? path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Invoklet", "secrets.json");

    /// <summary>Passes the key to <c>Util.GetSecret</c> in this process and in the scripts it starts.</summary>
    public static void ExportForScripts(byte[] key) => Environment.SetEnvironmentVariable(EnvVar, Convert.ToBase64String(key));

    /// <summary>
    /// For the command line: when the store is encrypted, reads its key and exports it. A key that
    /// can't be read is left for <c>Util.GetSecret</c> to report, only to a script that needs a secret.
    /// </summary>
    public static void ExportForScripts(string storePath)
    {
        if (Keychain is null || !IsEncrypted(storePath)) return;
        try
        {
            if (Keychain.Find() is { } key) ExportForScripts(key);
        }
        catch (SecretKeyUnavailableException) { }
    }

    /// <summary>Whether the store's values are encrypted with a keychain key. Only the file's
    /// <c>Protection</c> is read, so a store without secrets never opens the keychain.</summary>
    public static bool IsEncrypted(string storePath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(storePath));
            return document.RootElement.TryGetProperty("Protection", out var protection)
                && protection.ValueKind == JsonValueKind.String && protection.GetString() == Protection;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    /// <summary>
    /// Encrypts one value: AES-256-CBC, then HMAC-SHA256 over the secret's name, the IV and the
    /// ciphertext, so a value moved under another name fails to decrypt. Base64 of IV, ciphertext, tag.
    /// </summary>
    public static string Encrypt(byte[] key, string name, string value)
    {
        var (encryptionKey, macKey) = Derive(key);
        using var aes = Aes.Create();
        aes.Key = encryptionKey;
        aes.GenerateIV();
        var plain = Encoding.UTF8.GetBytes(value);
        var cipher = aes.EncryptCbc(plain, aes.IV);
        var tag = Tag(macKey, name, aes.IV, cipher);
        byte[] stored = [.. aes.IV, .. cipher, .. tag];
        return Convert.ToBase64String(stored);
    }

    /// <exception cref="CryptographicException">Another key, another name, or a damaged value.</exception>
    public static string Decrypt(byte[] key, string name, string value)
    {
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length < IvSize + 16 + TagSize) throw new CryptographicException("The value is damaged.");
        var (encryptionKey, macKey) = Derive(key);
        var iv = bytes[..IvSize];
        var cipher = bytes[IvSize..^TagSize];
        if (!CryptographicOperations.FixedTimeEquals(bytes[^TagSize..], Tag(macKey, name, iv, cipher)))
            throw new CryptographicException("The value doesn't match its key or name.");
        using var aes = Aes.Create();
        aes.Key = encryptionKey;
        return Encoding.UTF8.GetString(aes.DecryptCbc(cipher, iv));
    }

    private static (byte[] Encryption, byte[] Mac) Derive(byte[] key)
    {
        if (key.Length != KeySize) throw new CryptographicException("The Secret Store's key is damaged.");
        return (HMACSHA256.HashData(key, "invoklet-secrets-encryption"u8), HMACSHA256.HashData(key, "invoklet-secrets-mac"u8));
    }

    // The name goes first with its length, so name and data can't trade bytes.
    private static byte[] Tag(byte[] macKey, string name, byte[] iv, byte[] cipher)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, nameBytes.Length);
        byte[] data = [.. length, .. nameBytes, .. iv, .. cipher];
        return HMACSHA256.HashData(macKey, data);
    }

    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeySize);

    // Kept in the keychain as Base64 text, which secret-tool reads and prints as a line.
    private static byte[] Decode(string text, string where)
    {
        try
        {
            var key = Convert.FromBase64String(text.Trim());
            if (key.Length == KeySize) return key;
        }
        catch (FormatException) { }
        throw new SecretKeyUnavailableException($"The Secret Store's key in {where} is damaged.");
    }

    private const string Service = "Invoklet", Account = "secret-store-key";

    // The login keychain through Security.framework, so the item's access list names Invoklet
    // itself (the security command line tool would name itself, and so any process using it).
    private sealed class MacKeychain : ISecretKeySource
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const int ErrSecItemNotFound = -25300, ErrSecDuplicateItem = -25299;

        [DllImport(Security)]
        private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
            uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

        [DllImport(Security)]
        private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
            uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, out IntPtr itemRef);

        [DllImport(Security)]
        private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr cf);

        private static readonly byte[] ServiceName = Encoding.UTF8.GetBytes(Service), AccountName = Encoding.UTF8.GetBytes(Account);

        public byte[]? Find()
        {
            var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)ServiceName.Length, ServiceName,
                (uint)AccountName.Length, AccountName, out var length, out var data, out var item);
            if (status == ErrSecItemNotFound) return null;
            if (status != 0) throw Unavailable(status);
            try
            {
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, (int)length);
                return Decode(Encoding.ASCII.GetString(bytes), "the macOS Keychain");
            }
            finally
            {
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                if (item != IntPtr.Zero) CFRelease(item);
            }
        }

        public byte[] GetOrCreate()
        {
            if (Find() is { } existing) return existing;
            var key = NewKey();
            var text = Encoding.ASCII.GetBytes(Convert.ToBase64String(key));
            var status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)ServiceName.Length, ServiceName,
                (uint)AccountName.Length, AccountName, (uint)text.Length, text, out var item);
            if (item != IntPtr.Zero) CFRelease(item);
            // Another Invoklet kept one first: that one is the key.
            if (status == ErrSecDuplicateItem) return Find() ?? throw Unavailable(status);
            if (status != 0) throw Unavailable(status);
            return key;
        }

        private static SecretKeyUnavailableException Unavailable(int status) =>
            new($"The macOS Keychain didn't give the Secret Store's key (error {status}).");
    }

    // The Secret Service (GNOME Keyring, KWallet…) through secret-tool from libsecret. The key goes
    // through standard input, never the command line, where other processes could see it.
    private sealed class SecretTool : ISecretKeySource
    {
        private static readonly string[] Attributes = ["service", "invoklet", "account", Account];

        // Unlocking the keyring may ask the user for a password first.
        private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(2);

        public byte[]? Find()
        {
            var (exit, output, error) = Run(["lookup", .. Attributes], input: null);
            if (exit == 0 && output.Trim().Length > 0) return Decode(output, "the Secret Service");
            // Nothing stored: secret-tool fails quietly. Anything it says is a real problem.
            if (exit is 0 or 1 && error.Trim().Length == 0) return null;
            throw Unavailable(error);
        }

        public byte[] GetOrCreate()
        {
            if (Find() is { } existing) return existing;
            var key = NewKey();
            var (exit, _, error) = Run(["store", "--label=Invoklet Secret Store key", .. Attributes], Convert.ToBase64String(key));
            if (exit != 0) throw Unavailable(error);
            return key;
        }

        private static (int Exit, string Output, string Error) Run(string[] arguments, string? input)
        {
            var start = new ProcessStartInfo("secret-tool")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            Process process;
            try
            {
                process = Process.Start(start) ?? throw Unavailable("");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new SecretKeyUnavailableException(
                    "secret-tool isn't installed: install libsecret-tools (or libsecret) and a Secret Service such as GNOME Keyring.", ex);
            }
            using (process)
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (input is not null) process.StandardInput.Write(input);
                process.StandardInput.Close();
                if (!process.WaitForExit(MaxWait))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    throw Unavailable("it didn't answer");
                }
                return (process.ExitCode, output.Result, error.Result);
            }
        }

        private static SecretKeyUnavailableException Unavailable(string error) =>
            new($"The Secret Service didn't give the Secret Store's key{(error.Trim().Length > 0 ? ": " + error.Trim() : ".")}");
    }
}
