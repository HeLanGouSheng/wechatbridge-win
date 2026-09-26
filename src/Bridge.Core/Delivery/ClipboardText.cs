using System.Globalization;
using System.Text;
using Bridge.Core.Transcripts;

namespace Bridge.Core.Delivery;

/// <summary>The plain-text form of a transcript for pasting anywhere: one block per message, sender and
/// wall time on the first line, the text below, blank line between messages.</summary>
public static class ClipboardText
{
    public static string Format(Transcript transcript)
    {
        var sb = new StringBuilder();
        foreach (var m in transcript.Messages)
        {
            if (sb.Length > 0)
            {
                sb.Append("\r\n\r\n");
            }

            sb.Append(m.Sender).Append(' ').Append(m.SentAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append(m.Text.Replace("\n", "\r\n"));
        }

        return sb.ToString();
    }
}
