namespace RenPack.Plugins;

/// <summary>Instance-facade um die static <see cref="Services.SecretProtection"/>-
/// Klasse — damit Plugins ein injectable Interface bekommen (statt gegen
/// eine statische Klasse zu koppeln). Delegiert 1:1 an die statische
/// Impl.</summary>
public interface ISecretProtection
{
    /// <summary>Verschluesselt einen Klartext-String (User-scoped auf
    /// Windows via DPAPI, AES-CBC mit Machine+User-Binding auf
    /// Linux/macOS). Rueckgabe <c>null</c> wenn Input <c>null</c> war.</summary>
    string? Protect(string? plaintext);

    /// <summary>Entschluesselt einen von <see cref="Protect"/> erzeugten
    /// Ciphertext. Wirft bei Manipulation oder Wechsel des User-Accounts.
    /// Rueckgabe <c>null</c> wenn Input <c>null</c> war.</summary>
    string? Unprotect(string? ciphertext);
}

internal sealed class SecretProtectionAdapter : ISecretProtection
{
    public string? Protect(string? plaintext) => Services.SecretProtection.Protect(plaintext);
    public string? Unprotect(string? ciphertext) => Services.SecretProtection.Unprotect(ciphertext);
}
