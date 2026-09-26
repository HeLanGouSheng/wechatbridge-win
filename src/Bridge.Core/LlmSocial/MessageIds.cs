using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bridge.Core.Transcripts;

namespace Bridge.Core.LlmSocial;

/// <summary>A message id that is the same every time the same message is forwarded, so llmsocial's
/// per-account platformMsgId uniqueness turns a repeated forward into a no-op. The ordinal keeps two
/// identical messages in one minute (a second "好的") apart.</summary>
public static class MessageIds
{
    public static string Stable(string threadId, TranscriptMessage message, int ordinal)
    {
        var material = string.Join('\n', threadId, message.Sender, message.SentAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), message.Text, ordinal.ToString(CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return "wx_" + Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }
}
