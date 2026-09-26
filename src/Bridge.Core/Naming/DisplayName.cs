using System.Globalization;
using System.Text;

namespace Bridge.Core.Naming;

/// <summary>
/// Turns whatever name a shared file arrived with into something safe to write to disk. Ported from
/// the macOS DisplayName.swift, plus the Windows-only rules (reserved device names, characters NTFS
/// refuses, trailing dots and spaces).
/// </summary>
public static class DisplayName
{
    public const string FallbackBaseName = "共享文件";

    private const int MaxUtf8Bytes = 200;

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string? raw, string? fallbackExtension = null)
    {
        var name = LastPathComponent(raw ?? string.Empty);

        var builder = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
        {
            var value = rune.Value;
            if (value is '/' or ':' or 0 || value is '\\' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                builder.Append('-');
            }
            else if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append(rune.ToString());
            }
        }

        name = builder.ToString().Trim().TrimEnd('.', ' ');

        if (name.Length == 0 || name == "." || name == ".." || name.StartsWith('.') || ReservedDeviceNames.Contains(Stem(name)))
        {
            name = FallbackBaseName + name;
        }

        if (!string.IsNullOrEmpty(fallbackExtension) && Extension(name).Length == 0)
        {
            name = $"{name}.{fallbackExtension}";
        }

        return Truncate(name, MaxUtf8Bytes);
    }

    /// <summary>The chat a WeChat export belongs to, when the ZIP's own name says so:
    /// 「产品讨论组的聊天.zip」→ 产品讨论组. Null when the name carries no chat name.</summary>
    public static string? ChatNameFromArchiveName(string fileName)
    {
        var stem = Stem(LastPathComponent(fileName)).Trim();
        foreach (var suffix in new[] { "的聊天记录", "的聊天", "聊天记录" })
        {
            if (stem.EndsWith(suffix, StringComparison.Ordinal))
            {
                var name = stem[..^suffix.Length].Trim();
                return name.Length == 0 ? null : name;
            }
        }

        return null;
    }

    /// <summary>Both '/' and '\' separate components: a name may arrive from either world.</summary>
    internal static string LastPathComponent(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var cut = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }

    /// <summary>NSString semantics: a leading dot is not an extension separator, and only the last
    /// extension counts ("a.tar.gz" → "gz").</summary>
    internal static string Extension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? string.Empty : name[(dot + 1)..];
    }

    internal static string Stem(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? name : name[..dot];
    }

    /// <summary>Drops grapheme clusters from the end of the stem until the whole name fits the byte
    /// budget, keeping the extension; a stem that vanishes becomes the fallback name.</summary>
    internal static string Truncate(string name, int limit)
    {
        if (Encoding.UTF8.GetByteCount(name) <= limit)
        {
            return name;
        }

        var extension = Extension(name);
        var stem = extension.Length == 0 ? name : Stem(name);
        var suffix = extension.Length == 0 ? string.Empty : "." + extension;
        var budget = Math.Max(1, limit - Encoding.UTF8.GetByteCount(suffix));

        var graphemes = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(stem);
        while (enumerator.MoveNext())
        {
            graphemes.Add(enumerator.GetTextElement());
        }

        while (graphemes.Count > 0 && Encoding.UTF8.GetByteCount(string.Concat(graphemes)) > budget)
        {
            graphemes.RemoveAt(graphemes.Count - 1);
        }

        var kept = graphemes.Count == 0 ? FallbackBaseName : string.Concat(graphemes);
        return kept + suffix;
    }
}
