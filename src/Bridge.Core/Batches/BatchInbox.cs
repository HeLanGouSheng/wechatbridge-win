using System.Security.Cryptography;
using Bridge.Core.Naming;

namespace Bridge.Core.Batches;

public readonly record struct BatchId(string Value)
{
    /// <summary>Sortable by time and unique across concurrent share processes.</summary>
    public static BatchId New(TimeProvider clock)
    {
        var stamp = clock.GetLocalNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        return new BatchId($"{stamp}-{random}");
    }

    public override string ToString() => Value;
}

/// <summary>Written into the batch directory before it becomes visible; everything a later process
/// needs to know about where the files came from.</summary>
public sealed record BatchManifest(
    string Id,
    DateTimeOffset ReceivedAt,
    string Source,
    IReadOnlyList<string> Files,
    string? ShareTitle = null,
    string? ShareAppName = null,
    string? ChatName = null,
    IReadOnlyDictionary<string, long>? Timings = null)
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public const string SourceShare = "share";
    public const string SourceArgv = "argv";
    public const string SourceDrop = "drop";
}

public sealed record ReadyBatch(BatchId Id, string Directory, BatchManifest Manifest)
{
    public string ItemsDirectory => Path.Combine(Directory, BatchInbox.ItemsDirectoryName);

    public IReadOnlyList<string> FilePaths => Manifest.Files.Select(f => Path.Combine(ItemsDirectory, f)).ToArray();
}

/// <summary>
/// The inbox on disk: <c>staging\</c> for batches still being copied, <c>ready\</c> for complete ones,
/// <c>done\</c> and <c>failed\</c> for delivered ones (failed batches keep their ZIP for a retry).
/// A batch moves between these with a single directory rename, so another process never sees a
/// half-copied batch. This keeps the shape of the macOS Staging→Ready design without its App Group
/// and intent files, which only existed because the share extension and the app were two processes.
/// </summary>
public sealed class BatchInbox
{
    public const string ItemsDirectoryName = "items";
    public const string ManifestFileName = "batch.json";
    public const string OutcomeFileName = "outcome.txt";

    public BatchInbox(string root)
    {
        Root = root;
        Staging = Path.Combine(root, "staging");
        Ready = Path.Combine(root, "ready");
        Done = Path.Combine(root, "done");
        Failed = Path.Combine(root, "failed");
    }

    public string Root { get; }
    public string Staging { get; }
    public string Ready { get; }
    public string Done { get; }
    public string Failed { get; }

    public void PrepareDirectories()
    {
        foreach (var dir in new[] { Staging, Ready, Done, Failed })
        {
            Directory.CreateDirectory(dir);
        }
    }

    public StagingBatch Begin(BatchId id)
    {
        PrepareDirectories();
        var dir = Path.Combine(Staging, id.Value);
        Directory.CreateDirectory(Path.Combine(dir, ItemsDirectoryName));
        return new StagingBatch(this, id, dir);
    }

    public IReadOnlyList<ReadyBatch> ListReady() => List(Ready);

    public IReadOnlyList<ReadyBatch> ListFailed() => List(Failed);

    private static IReadOnlyList<ReadyBatch> List(string parent)
    {
        if (!Directory.Exists(parent))
        {
            return Array.Empty<ReadyBatch>();
        }

        var batches = new List<ReadyBatch>();
        foreach (var dir in Directory.EnumerateDirectories(parent))
        {
            var batch = TryRead(dir);
            if (batch is not null)
            {
                batches.Add(batch);
            }
        }

        return batches.OrderByDescending(b => b.Manifest.ReceivedAt).ToArray();
    }

    /// <summary>A batch this build cannot read (missing manifest, newer schema, files gone) is left
    /// alone rather than guessed at.</summary>
    public static ReadyBatch? TryRead(string directory)
    {
        var manifestPath = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        BatchManifest? manifest;
        try
        {
            manifest = BridgeJson.Deserialize<BatchManifest>(File.ReadAllText(manifestPath));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (manifest is null || manifest.SchemaVersion > BatchManifest.CurrentSchemaVersion)
        {
            return null;
        }

        var items = Path.Combine(directory, ItemsDirectoryName);
        var present = manifest.Files.Where(f => File.Exists(Path.Combine(items, f))).ToArray();
        if (present.Length == 0)
        {
            return null;
        }

        return new ReadyBatch(new BatchId(Path.GetFileName(directory)), directory, manifest with { Files = present });
    }

    public void MarkDone(BatchId id) => Move(id, Done, null);

    public void MarkFailed(BatchId id, string reason) => Move(id, Failed, reason);

    /// <summary>A failed batch goes back to ready\ for another attempt.</summary>
    public ReadyBatch? Requeue(BatchId id)
    {
        var from = Path.Combine(Failed, id.Value);
        if (!Directory.Exists(from))
        {
            return null;
        }

        var to = Path.Combine(Ready, id.Value);
        Directory.CreateDirectory(Ready);
        Directory.Move(from, to);
        File.Delete(Path.Combine(to, OutcomeFileName));
        return TryRead(to);
    }

    private void Move(BatchId id, string parent, string? outcome)
    {
        var from = Path.Combine(Ready, id.Value);
        var to = Path.Combine(parent, id.Value);
        Directory.CreateDirectory(parent);
        if (Directory.Exists(to))
        {
            Directory.Delete(to, recursive: true);
        }

        Directory.Move(from, to);
        if (outcome is not null)
        {
            File.WriteAllText(Path.Combine(to, OutcomeFileName), outcome);
        }
    }

    public static string? ReadOutcome(string directory)
    {
        var path = Path.Combine(directory, OutcomeFileName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Removes done and failed batches older than <paramref name="olderThan"/> (strictly older;
    /// zero means keep forever), and staging leftovers older than an hour. Returns how many went.</summary>
    public int Purge(TimeSpan olderThan, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var removed = 0;
        if (olderThan > TimeSpan.Zero)
        {
            foreach (var parent in new[] { Done, Failed })
            {
                if (!Directory.Exists(parent))
                {
                    continue;
                }

                foreach (var dir in Directory.EnumerateDirectories(parent))
                {
                    var batch = TryRead(dir);
                    if (batch is null)
                    {
                        // Unreadable: age by directory time, but never sweep a newer schema.
                        var manifestPath = Path.Combine(dir, ManifestFileName);
                        if (File.Exists(manifestPath) && IsNewerSchema(manifestPath))
                        {
                            continue;
                        }

                        if (now - Directory.GetCreationTimeUtc(dir) > olderThan)
                        {
                            Directory.Delete(dir, recursive: true);
                            removed++;
                        }

                        continue;
                    }

                    if (now - batch.Manifest.ReceivedAt > olderThan)
                    {
                        Directory.Delete(dir, recursive: true);
                        removed++;
                    }
                }
            }
        }

        if (Directory.Exists(Staging))
        {
            foreach (var dir in Directory.EnumerateDirectories(Staging))
            {
                if (now - NewestWriteTimeUtc(dir) > TimeSpan.FromHours(1))
                {
                    Directory.Delete(dir, recursive: true);
                    removed++;
                }
            }
        }

        return removed;
    }

    private static bool IsNewerSchema(string manifestPath)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
            return doc.RootElement.TryGetProperty("schemaVersion", out var v) && v.TryGetInt32(out var n) && n > BatchManifest.CurrentSchemaVersion;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static DateTime NewestWriteTimeUtc(string dir)
    {
        var newest = Directory.GetLastWriteTimeUtc(dir);
        foreach (var path in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
        {
            var t = File.GetLastWriteTimeUtc(path);
            if (t > newest)
            {
                newest = t;
            }
        }

        return newest;
    }
}

public sealed class StagingBatch
{
    private readonly BatchInbox _inbox;
    private readonly List<string> _files = new();

    internal StagingBatch(BatchInbox inbox, BatchId id, string directory)
    {
        _inbox = inbox;
        Id = id;
        Directory = directory;
    }

    public BatchId Id { get; }

    public string Directory { get; }

    public string ItemsDirectory => Path.Combine(Directory, BatchInbox.ItemsDirectoryName);

    public IReadOnlyList<string> Files => _files;

    /// <summary>Claims a file name inside the batch and returns the absolute path to write to. Two
    /// attachments with the same name get "name (2).zip" style suffixes rather than overwriting.</summary>
    public string Reserve(string originalFileName, string? fallbackExtension = null)
    {
        var name = DisplayName.Sanitize(originalFileName, fallbackExtension);
        var candidate = name;
        var n = 2;
        while (_files.Contains(candidate, StringComparer.OrdinalIgnoreCase) || File.Exists(Path.Combine(ItemsDirectory, candidate)))
        {
            var ext = DisplayName.Extension(name);
            candidate = ext.Length == 0 ? $"{name} ({n})" : $"{DisplayName.Stem(name)} ({n}).{ext}";
            n++;
        }

        _files.Add(candidate);
        return Path.Combine(ItemsDirectory, candidate);
    }

    /// <summary>Writes the manifest and publishes the batch with one rename into ready\.</summary>
    public ReadyBatch Commit(BatchManifest manifest)
    {
        var complete = manifest with { Id = Id.Value, Files = _files.ToArray() };
        File.WriteAllText(Path.Combine(Directory, BatchInbox.ManifestFileName), BridgeJson.Serialize(complete));
        System.IO.Directory.CreateDirectory(_inbox.Ready);
        var destination = Path.Combine(_inbox.Ready, Id.Value);
        System.IO.Directory.Move(Directory, destination);
        return new ReadyBatch(Id, destination, complete);
    }

    public void Abort()
    {
        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
