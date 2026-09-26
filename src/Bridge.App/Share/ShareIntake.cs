using System.Diagnostics;
using System.IO;
using Bridge.Core.Batches;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Storage;

namespace Bridge.App.Share;

public sealed class ShareIntakeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Takes the files out of a share operation and lands them in the inbox. Order matters: the source
/// (WeChat) may delete its temporary ZIP as soon as the share is reported complete, so every file is
/// copied before <c>ReportDataRetrieved</c>, and the batch is committed before <c>ReportCompleted</c>.
/// After <c>ReportCompleted</c> the operation object is never touched again.
/// </summary>
public static class ShareIntake
{
    public static async Task<ReadyBatch> ReceiveAsync(ShareOperation operation, BatchInbox inbox, TimeProvider clock)
    {
        var timings = new Dictionary<string, long>();
        var watch = Stopwatch.StartNew();
        operation.ReportStarted();

        if (!operation.Data.Contains(StandardDataFormats.StorageItems))
        {
            return Fail(operation, null, $"{Bridge.Core.BridgeIdentity.ProductName}只接收文件。请在微信里多选消息 → 转发 → 转发到其他应用。");
        }

        var items = await operation.Data.GetStorageItemsAsync();
        timings["getItems"] = watch.ElapsedMilliseconds;

        var staging = inbox.Begin(BatchId.New(clock));
        try
        {
            foreach (var item in items)
            {
                if (item is not IStorageFile file)
                {
                    continue;
                }

                if (!file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    return Fail(operation, staging, $"{Bridge.Core.BridgeIdentity.ProductName}只接收微信导出的 ZIP，收到的是「{file.Name}」。");
                }

                await CopyAsync(file, staging.Reserve(file.Name, "zip"));
            }

            if (staging.Files.Count == 0)
            {
                return Fail(operation, staging, "这次分享里没有文件。");
            }

            timings["copy"] = watch.ElapsedMilliseconds;
            operation.ReportDataRetrieved();

            var properties = operation.Data.Properties;
            var manifest = new BatchManifest(
                staging.Id.Value,
                clock.GetUtcNow(),
                BatchManifest.SourceShare,
                Array.Empty<string>(),
                ShareTitle: NullIfEmpty(properties.Title),
                ShareAppName: NullIfEmpty(properties.ApplicationName),
                Timings: timings);
            var ready = staging.Commit(manifest);
            operation.ReportCompleted();
            return ready;
        }
        catch (ShareIntakeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            staging.Abort();
            operation.ReportError($"{Bridge.Core.BridgeIdentity.ProductName}没能接住这次转发：{ex.Message}");
            throw new ShareIntakeException($"没能接住这次转发：{ex.Message}", ex);
        }
    }

    private static ReadyBatch Fail(ShareOperation operation, StagingBatch? staging, string message)
    {
        staging?.Abort();
        operation.ReportError(message);
        throw new ShareIntakeException(message);
    }

    /// <summary>A plain File.Copy when the item has a path (WeChat's temp file does); the WinRT stream
    /// only for virtual files. Defender likes to hold a freshly written file for a moment, hence the
    /// short retry.</summary>
    private static async Task CopyAsync(IStorageFile file, string destination)
    {
        var path = file.Path;
        if (string.IsNullOrEmpty(path))
        {
            using var source = (await file.OpenReadAsync()).AsStreamForRead();
            using var target = File.Create(destination);
            await source.CopyToAsync(target);
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(path, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                await Task.Delay(200);
            }
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
