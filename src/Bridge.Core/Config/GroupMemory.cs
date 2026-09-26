using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Bridge.Core.Config;

public sealed record GroupMemoryEntry(string ChatName, IReadOnlyList<string> Senders, DateTimeOffset UpdatedAt);

public sealed record GroupMemoryFile(int Version, IReadOnlyList<GroupMemoryEntry> Groups)
{
    public static GroupMemoryFile Empty => new(1, Array.Empty<GroupMemoryEntry>());
}

/// <summary>
/// Recognises a group chat by who spoke in it, since the export carries no group name. Ported from
/// the macOS GroupFingerprint: at least two senders, overlap over the smaller set at least 0.7, and
/// a tie between two groups counts as no match rather than a guess.
/// </summary>
public static class GroupFingerprint
{
    public const int MinimumSenderCount = 2;
    public const double ScoreThreshold = 0.7;
    public const int MaxRememberedSenders = 200;

    public static string Normalize(string raw)
    {
        var trimmed = raw.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    public static string? Match(IEnumerable<string> senders, IEnumerable<GroupMemoryEntry> memories)
    {
        var current = new HashSet<string>(senders.Select(Normalize).Where(s => s.Length > 0));
        if (current.Count < MinimumSenderCount)
        {
            return null;
        }

        string? best = null;
        var bestScore = 0.0;
        var tied = false;
        foreach (var memory in memories)
        {
            var known = new HashSet<string>(memory.Senders.Select(Normalize).Where(s => s.Length > 0));
            if (known.Count < MinimumSenderCount)
            {
                continue;
            }

            var score = (double)current.Intersect(known).Count() / Math.Min(current.Count, known.Count);
            if (score < ScoreThreshold)
            {
                continue;
            }

            if (best is null || score > bestScore)
            {
                best = memory.ChatName;
                bestScore = score;
                tied = false;
            }
            else if (score == bestScore)
            {
                tied = true;
            }
        }

        return tied ? null : best;
    }
}

/// <summary>groups.json: one entry per group the user has named, with the senders seen so far.</summary>
public sealed class GroupMemoryStore
{
    public GroupMemoryStore(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public GroupMemoryFile Load()
    {
        if (!File.Exists(Path))
        {
            return GroupMemoryFile.Empty;
        }

        try
        {
            return BridgeJson.Deserialize<GroupMemoryFile>(File.ReadAllText(Path)) ?? GroupMemoryFile.Empty;
        }
        catch (JsonException)
        {
            return GroupMemoryFile.Empty;
        }
    }

    public string? Match(IEnumerable<string> senders) => GroupFingerprint.Match(senders, Load().Groups);

    /// <summary>Adds the senders to the named group (creating it), so the next forward from the same
    /// people is recognised without asking.</summary>
    public void Remember(string chatName, IEnumerable<string> senders, DateTimeOffset now)
    {
        var file = Load();
        var name = chatName.Trim();
        var existing = file.Groups.FirstOrDefault(g => string.Equals(g.ChatName, name, StringComparison.Ordinal));
        var merged = new List<string>(existing?.Senders ?? Array.Empty<string>());
        foreach (var sender in senders.Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            if (!merged.Contains(sender, StringComparer.Ordinal))
            {
                merged.Add(sender);
            }
        }

        if (merged.Count > GroupFingerprint.MaxRememberedSenders)
        {
            merged.RemoveRange(0, merged.Count - GroupFingerprint.MaxRememberedSenders);
        }

        var entry = new GroupMemoryEntry(name, merged, now);
        var groups = file.Groups.Where(g => !string.Equals(g.ChatName, name, StringComparison.Ordinal)).Append(entry).ToArray();
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, BridgeJson.Serialize(file with { Groups = groups }));
        File.Move(tmp, Path, overwrite: true);
    }
}
