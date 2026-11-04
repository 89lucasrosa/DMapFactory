using System.Security.Cryptography;
using System.Text;

namespace DMapFactory.Core.Security;

/// <summary>
/// Protege e desprotege segredos (senhas de servidores OPC UA) no escopo do usuário Windows atual (RF-CON-04).
/// Em Windows usa DPAPI (CryptProtectData); em outros sistemas usa fallback Base64 com prefixo explícito.
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = "DMapFactory.Secret.v1"u8.ToArray();

    public static string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return null;
        byte[] bytes = Encoding.UTF8.GetBytes(plainText);

        if (OperatingSystem.IsWindows())
        {
            byte[] encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return "dpapi:" + Convert.ToBase64String(encrypted);
        }

        return "b64:" + Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return null;

        try
        {
            if (protectedText.StartsWith("dpapi:", StringComparison.Ordinal) && OperatingSystem.IsWindows())
            {
                byte[] encrypted = Convert.FromBase64String(protectedText["dpapi:".Length..]);
                byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }

            if (protectedText.StartsWith("b64:", StringComparison.Ordinal))
            {
                byte[] plain = Convert.FromBase64String(protectedText["b64:".Length..]);
                return Encoding.UTF8.GetString(plain);
            }

            // Compatibilidade com Base64 puro
            if (OperatingSystem.IsWindows())
            {
                byte[] encrypted = Convert.FromBase64String(protectedText);
                byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }
}
