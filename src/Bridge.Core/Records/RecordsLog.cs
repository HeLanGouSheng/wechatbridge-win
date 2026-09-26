using System.Text;
using System.Text.Json;

namespace Bridge.Core.Records;

public static class RecordStatus
{
    public const string Sent = "sent";
    public const string Partial = "partial";
    public const string Failed = "failed";
    public const string Copied = "copied";
    public const string Empty = "empty";
    public const string Cancelled = "cancelled";
}

public sealed record DeliveryRecord(
    string Id,
    DateTimeOffset At,
    string BatchId,
    string? ChatName,
    int MessageCount,
    string Target,
    string Status,
    string Detail);

/// <summary>records.jsonl, one JSON object per line, append-only. Two share processes can append at the
/// same moment, so appends are serialised with a named mutex and a line is written in one call.</summary>
public sealed class RecordsLog
{
    private const string MutexName = @"Local\ChatBridge.records";

    public RecordsLog(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public void Append(DeliveryRecord record)
    {
        var line = BridgeJson.SerializeCompact(record) + "\n";
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var mutex = TryCreateMutex();
        var held = false;
        try
        {
            held = mutex?.WaitOne(TimeSpan.FromSeconds(5)) ?? false;
            using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = Encoding.UTF8.GetBytes(line);
            stream.Write(bytes, 0, bytes.Length);
        }
        finally
        {
            if (held)
            {
                mutex!.ReleaseMutex();
            }
        }
    }

    /// <summary>Newest first. A malformed line (a crash mid-write) is skipped, not fatal.</summary>
    public IReadOnlyList<DeliveryRecord> ReadAll()
    {
        if (!File.Exists(Path))
        {
            return Array.Empty<DeliveryRecord>();
        }

        var records = new List<DeliveryRecord>();
        foreach (var line in File.ReadLines(Path))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            try
            {
                var record = BridgeJson.Deserialize<DeliveryRecord>(line);
                if (record is not null)
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
                // A torn line from a crash mid-write; nothing to recover.
            }
        }

        records.Reverse();
        return records;
    }

    private static Mutex? TryCreateMutex()
    {
        try
        {
            return new Mutex(false, MutexName);
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }
}
