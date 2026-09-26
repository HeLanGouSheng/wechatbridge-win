using System.IO;
using Bridge.Core.Batches;

namespace Bridge.App.Share;

/// <summary>The command-line / drag-and-drop way in: the same batch pipeline, no share broker.</summary>
public static class LocalIntake
{
    public static ReadyBatch Import(string zipPath, BatchInbox inbox, TimeProvider clock)
    {
        var staging = inbox.Begin(BatchId.New(clock));
        try
        {
            File.Copy(zipPath, staging.Reserve(Path.GetFileName(zipPath), "zip"), overwrite: true);
            return staging.Commit(new BatchManifest(staging.Id.Value, clock.GetUtcNow(), BatchManifest.SourceArgv, Array.Empty<string>()));
        }
        catch
        {
            staging.Abort();
            throw;
        }
    }
}
