using Bridge.Core.Transcripts;

namespace Bridge.Core.LlmSocial;

/// <summary>
/// What became of a transcript. <see cref="NeedsChatName"/> means the transcript is a group chat and
/// no name is known yet — the caller has to ask (or remember one) and map again.
/// </summary>
public sealed record MappingResult(
    IReadOnlyList<InboundPayload> Messages,
    int Skipped,
    bool IsGroup,
    string? Counterpart,
    IReadOnlyList<string> OtherSenders,
    bool NeedsChatName)
{
    public static MappingResult Empty(IReadOnlyList<string> others, bool isGroup, int skipped, bool needsChatName) =>
        new(Array.Empty<InboundPayload>(), skipped, isGroup, null, others, needsChatName);
}

/// <summary>
/// Turns a WeChat transcript into llmsocial webhook messages. llmsocial models one conversation per
/// contact, so a 1:1 chat maps onto the other person and a group chat onto the group, with each
/// member's name folded into the text so the model still knows who said what. The export carries no
/// user ids, only nicknames — a renamed contact will look like a new one; that is documented.
/// </summary>
public static class PayloadMapper
{
    public const string Kind = "dm";

    public static MappingResult Map(Transcript transcript, string? chatName, IReadOnlyCollection<string> myNames, TimeZoneInfo timeZone)
    {
        var mine = new HashSet<string>(myNames.Select(n => n.Trim()).Where(n => n.Length > 0), StringComparer.Ordinal);
        var others = transcript.Senders.Where(s => !mine.Contains(s.Trim())).Select(s => s.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        var isGroup = others.Length >= 2;

        if (others.Length == 0)
        {
            return MappingResult.Empty(others, isGroup: false, skipped: transcript.Messages.Count, needsChatName: false);
        }

        var name = string.IsNullOrWhiteSpace(chatName) ? null : chatName.Trim();
        if (isGroup && name is null)
        {
            return MappingResult.Empty(others, isGroup: true, skipped: 0, needsChatName: true);
        }

        var counterpart = isGroup ? name! : others[0];
        var threadId = isGroup ? name! : name ?? counterpart;
        var contact = isGroup ? new ContactRef("wxg:" + name, name!) : new ContactRef("wx:" + counterpart, counterpart);

        var ordinals = new Dictionary<(string, DateTime, string), int>();
        var messages = new List<InboundPayload>(transcript.Messages.Count);
        var skipped = 0;
        foreach (var m in transcript.Messages)
        {
            var text = m.Text.Trim();
            if (text.Length == 0)
            {
                skipped++;
                continue;
            }

            var key = (m.Sender, m.SentAt, m.Text);
            ordinals.TryGetValue(key, out var ordinal);
            ordinals[key] = ordinal + 1;

            var fromSelf = mine.Contains(m.Sender.Trim());
            var body = isGroup && !fromSelf ? $"{m.Sender.Trim()}：{text}" : text;
            var offset = timeZone.GetUtcOffset(m.SentAt);
            var timestamp = new DateTimeOffset(DateTime.SpecifyKind(m.SentAt, DateTimeKind.Unspecified), offset).ToUnixTimeMilliseconds();
            messages.Add(new InboundPayload(MessageIds.Stable(threadId, m, ordinal), Kind, threadId, threadId, contact, body, timestamp, fromSelf));
        }

        return new MappingResult(messages, skipped, isGroup, counterpart, others, NeedsChatName: false);
    }
}
