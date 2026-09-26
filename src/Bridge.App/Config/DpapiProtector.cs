using System.Security.Cryptography;
using System.Text;
using Bridge.Core.Config;

namespace Bridge.App.Config;

/// <summary>The shared secret at rest: DPAPI, bound to this Windows user, so settings.json can be
/// copied around or backed up without carrying the secret in the clear.</summary>
public sealed class DpapiProtector : ISecretProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ChatBridge llmsocial secret v1");

    public string Protect(string plain) =>
        Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public string Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new SettingsException("设置里的密钥格式不认识。在设置里重新填一次密钥。");
        }

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new SettingsException("设置里的密钥解不开（是别的 Windows 用户保存的？）。在设置里重新填一次密钥。");
        }
    }
}
