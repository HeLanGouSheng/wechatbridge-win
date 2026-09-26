using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bridge.Core.Transcripts;

/// <summary>One message as WeChat wrote it into 聊天记录.txt. <see cref="SentAt"/> is wall-clock time
/// (Kind Unspecified): the export carries no time zone.</summary>
public sealed record TranscriptMessage(string Sender, DateTime SentAt, string Text);

public sealed class Transcript
{
    public Transcript(IReadOnlyList<TranscriptMessage> messages)
    {
        Messages = messages;
        Senders = messages.Select(m => m.Sender).Distinct(StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<TranscriptMessage> Messages { get; }

    /// <summary>Distinct senders in first-seen order.</summary>
    public IReadOnlyList<string> Senders { get; }

    public DateTime? Start => Messages.Count == 0 ? null : Messages.Min(m => m.SentAt);

    public DateTime? End => Messages.Count == 0 ? null : Messages.Max(m => m.SentAt);
}

public sealed class TranscriptFormatException(string message) : Exception(message);

/// <summary>
/// Parses the 聊天记录.txt that WeChat writes for 「转发到其他应用」. The format, as seen on macOS and
/// Windows alike:
/// <code>
/// ·发送者昵称
/// 2026年9月20日 09:10
/// 正文，可以多行，直到下一个以「·」开头的头部
/// </code>
/// The rules mirror the macOS WeChatBridge parser (WeChatForward.swift) so both ports accept the same
/// files: the first header must sit at offset 0, a header needs a trailing newline, and only U+FEFF is
/// trimmed from the ends before matching.
/// </summary>
public static class TranscriptParser
{
    private static readonly Regex Header = new(
        @"^·([^\n]+)\n(\d{4}年\d{1,2}月\d{1,2}日 \d{2}:\d{2})\n",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const string DateFormat = "yyyy'年'M'月'd'日' HH:mm";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Encoding Gbk = CreateGbk();

    private static Encoding CreateGbk()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936);
    }

    public static Transcript Parse(string text)
    {
        var body = text.Replace("\r\n", "\n").Trim('﻿');
        var matches = Header.Matches(body);
        if (matches.Count == 0 || matches[0].Index != 0)
        {
            throw new TranscriptFormatException("不是微信导出的聊天记录：开头不是「·昵称」加一行日期时间");
        }

        var messages = new List<TranscriptMessage>(matches.Count);
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var stamp = match.Groups[2].Value;
            if (!DateTime.TryParseExact(stamp, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var sentAt))
            {
                throw new TranscriptFormatException($"第 {i + 1} 条消息的时间「{stamp}」不是有效的日期");
            }

            var start = match.Index + match.Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : body.Length;
            messages.Add(new TranscriptMessage(match.Groups[1].Value, sentAt, body[start..end].Trim()));
        }

        return new Transcript(messages);
    }

    /// <summary>Decodes strict UTF-8 first; a file that is not valid UTF-8 is read as GBK, which is what
    /// an older Windows tool would write. A UTF-8 BOM survives decoding as U+FEFF and is trimmed by
    /// <see cref="Parse(string)"/>.</summary>
    public static Transcript Parse(ReadOnlySpan<byte> bytes) => Parse(DecodeText(bytes));

    public static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Gbk.GetString(bytes);
        }
    }

    public static bool TryParse(string text, out Transcript? transcript, out string? error)
    {
        try
        {
            transcript = Parse(text);
            error = null;
            return true;
        }
        catch (TranscriptFormatException ex)
        {
            transcript = null;
            error = ex.Message;
            return false;
        }
    }
}
