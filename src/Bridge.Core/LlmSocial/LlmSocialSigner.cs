using System.Security.Cryptography;
using System.Text;

namespace Bridge.Core.LlmSocial;

/// <summary>The signature llmsocial's generic webhook connector checks: HMAC-SHA256 over the exact
/// request bytes, hex in lowercase (Node's <c>digest('hex')</c>), prefixed with <c>sha256=</c>.
/// Sign the bytes that go on the wire, never a re-serialised string.</summary>
public static class LlmSocialSigner
{
    public const string HeaderName = "X-LLMSocial-Signature";

    public static string Sign(string secret, ReadOnlySpan<byte> body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
