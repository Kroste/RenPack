using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using NLog;

namespace RenPack.Services;

/// <summary>
/// Schützt API-Keys für die persistente JSON-Ablage. Windows: DPAPI (Per-User-
/// Scope, keine externe Abhängigkeit). Linux/macOS: AES mit deterministischem
/// Schlüssel aus MachineName + UserName + statischer Salz-Konstante — nicht so
/// sicher wie ein echter Keyring, aber verhindert wenigstens, dass ein
/// versehentlich veröffentlichter Config-Dump die Keys direkt preisgibt (nach
/// Magnat-Vorbild, `AppSettings.cs`). Für v0.4b reicht das; libsecret-Support
/// kann später nachgerüstet werden.
/// </summary>
public static class SecretProtection
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly byte[] Salt = "renpack-secret-v1"u8.ToArray();

    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;
        try
        {
            byte[] cipher = OperatingSystem.IsWindows()
                ? ProtectWindows(Encoding.UTF8.GetBytes(plaintext))
                : ProtectAes(Encoding.UTF8.GetBytes(plaintext));
            return "v1:" + Convert.ToBase64String(cipher);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Secret konnte nicht verschlüsselt werden — fällt auf null zurück");
            return null;
        }
    }

    public static string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return null;
        if (!ciphertext.StartsWith("v1:", StringComparison.Ordinal)) return null;
        try
        {
            byte[] cipher = Convert.FromBase64String(ciphertext[3..]);
            byte[] plain = OperatingSystem.IsWindows()
                ? UnprotectWindows(cipher)
                : UnprotectAes(cipher);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Secret konnte nicht entschlüsselt werden — fällt auf null zurück " +
                "(Maschinen-/User-Wechsel oder korrupte Config)");
            return null;
        }
    }

    // ---- Windows: DPAPI ----------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] plain)
        => ProtectedData.Protect(plain, Salt, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] cipher)
        => ProtectedData.Unprotect(cipher, Salt, DataProtectionScope.CurrentUser);

    // ---- Linux/macOS: AES mit Maschinen-/User-Binding ----------------------

    private static byte[] ProtectAes(byte[] plain)
    {
        using var aes = Aes.Create();
        aes.Key = DeriveKey();
        aes.GenerateIV();
        using var enc = aes.CreateEncryptor();
        byte[] body = enc.TransformFinalBlock(plain, 0, plain.Length);
        byte[] result = new byte[aes.IV.Length + body.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(body, 0, result, aes.IV.Length, body.Length);
        return result;
    }

    private static byte[] UnprotectAes(byte[] cipher)
    {
        using var aes = Aes.Create();
        aes.Key = DeriveKey();
        byte[] iv = new byte[16];
        Buffer.BlockCopy(cipher, 0, iv, 0, iv.Length);
        aes.IV = iv;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(cipher, iv.Length, cipher.Length - iv.Length);
    }

    private static byte[] DeriveKey()
    {
        string material = $"{Environment.MachineName}|{Environment.UserName}|renpack";
        return SHA256.HashData(Encoding.UTF8.GetBytes(material));
    }
}
