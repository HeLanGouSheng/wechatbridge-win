using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using Bridge.App.Config;
using Bridge.Core.Archives;
using Bridge.Core.Batches;
using Bridge.Core.Config;
using Bridge.Core.Delivery;
using Bridge.Core.LlmSocial;
using Bridge.Core.Naming;
using Bridge.Core.Records;

namespace Bridge.App.Delivery;

/// <summary>
/// The app's view of "what happens to a batch": settings loaded once, llmsocial usable or not (with the
/// reason in words), and the two targets M1 offers — llmsocial and the clipboard.
/// </summary>
public sealed class DeliveryFlow
{
    private readonly string _secret;

    private DeliveryFlow(Settings settings, string secret, string? problem)
    {
        Settings = settings;
        _secret = secret;
        LlmSocialProblem = problem;
    }

    public Settings Settings { get; }

    /// <summary>Null when llmsocial can be used; otherwise what the user has to do first.</summary>
    public string? LlmSocialProblem { get; }

    public bool LlmSocialReady => LlmSocialProblem is null;

    public bool AutoDeliver => Settings.LlmSocialMode && LlmSocialReady;

    public static SettingsStore Store => new(AppPaths.SettingsFile, new DpapiProtector());

    /// <summary>For when settings.json itself cannot be read: nothing works but the window can still say why.</summary>
    public static DeliveryFlow Unconfigured(string problem) => new(Settings.Default, "", problem);

    /// <summary>Throws <see cref="SettingsException"/> only for an unreadable settings file; a missing
    /// or incomplete configuration is a <see cref="LlmSocialProblem"/>, not an exception.</summary>
    public static DeliveryFlow Load()
    {
        var store = Store;
        var settings = store.Load();
        if (!settings.LlmSocial.IsFilledIn)
        {
            return new DeliveryFlow(settings, "", "还没有设置 llmsocial：点「设置」填地址、账号 ID 和共享密钥。");
        }

        string secret;
        try
        {
            secret = store.Secret(settings);
        }
        catch (SettingsException ex)
        {
            return new DeliveryFlow(settings, "", ex.Message);
        }

        var problem = LlmSocialConfig.Validate(settings.LlmSocial.BaseUrl, settings.LlmSocial.AccountId, secret);
        return new DeliveryFlow(settings, secret, problem);
    }

    public LlmSocialConfig Config => new(Settings.LlmSocial.BaseUrl, Settings.LlmSocial.AccountId, _secret);

    public static HttpClient CreateHttpClient(string baseUrl)
    {
        // The system proxy is for the internet; a loopback llmsocial must never be routed through it.
        var loopback = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback;
        var handler = new SocketsHttpHandler { UseProxy = !loopback };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public LlmSocialDelivery CreateLlmSocialDelivery()
    {
        if (LlmSocialProblem is not null)
        {
            throw new InvalidOperationException(LlmSocialProblem);
        }

        var client = new LlmSocialClient(CreateHttpClient(Config.BaseUrl), Config);
        return new LlmSocialDelivery(
            client,
            Settings.MyNames,
            RememberMyName,
            new GroupMemoryStore(AppPaths.GroupsFile),
            new RecordsLog(AppPaths.RecordsFile),
            new BatchInbox(AppPaths.InboxRoot),
            TimeProvider.System,
            TimeZoneInfo.Local);
    }

    /// <summary>The user pointed at their own nickname in a chat: keep it, so later forwards know which side is theirs.</summary>
    private static void RememberMyName(string name)
    {
        var store = Store;
        var settings = store.Load();
        if (settings.MyNames.Contains(name, StringComparer.Ordinal))
        {
            return;
        }

        store.Save(settings with { MyNames = settings.MyNames.Append(name).ToArray() });
    }

    public Task<BatchOutcome> DeliverAsync(ReadyBatch batch, Func<ChatNameRequest, Task<ChatNameAnswer?>> askChatName, CancellationToken ct = default) =>
        CreateLlmSocialDelivery().DeliverAsync(batch, askChatName, ct);

    /// <summary>The fallback target: every transcript in the batch as text on the clipboard.</summary>
    public BatchOutcome CopyToClipboard(ReadyBatch batch)
    {
        var inbox = new BatchInbox(AppPaths.InboxRoot);
        var records = new RecordsLog(AppPaths.RecordsFile);
        var text = new StringBuilder();
        var outcomes = new List<FileOutcome>();
        foreach (var path in batch.FilePaths)
        {
            var fileName = Path.GetFileName(path);
            try
            {
                var archive = NativeArchive.ReadTranscript(path);
                var others = archive.Transcript.Senders.Where(s => !Settings.MyNames.Contains(s.Trim(), StringComparer.Ordinal)).ToArray();
                var chatName = batch.Manifest.ChatName ?? DisplayName.ChatNameFromArchiveName(fileName) ?? (others.Length == 1 ? others[0] : null);
                if (text.Length > 0)
                {
                    text.Append("\r\n\r\n");
                }

                text.Append(ClipboardText.Format(archive.Transcript));
                outcomes.Add(new FileOutcome(fileName, chatName, archive.Transcript.Messages.Count, archive.Transcript.Messages.Count, RecordStatus.Copied, $"已复制 {archive.Transcript.Messages.Count} 条到剪贴板", false));
            }
            catch (ArchiveException ex)
            {
                outcomes.Add(new FileOutcome(fileName, null, 0, 0, RecordStatus.Failed, ex.Message, false));
            }
        }

        if (text.Length > 0)
        {
            Clipboard.SetDataObject(text.ToString(), true);
        }

        var n = 0;
        foreach (var outcome in outcomes)
        {
            n++;
            records.Append(new DeliveryRecord($"{batch.Id.Value}/{n}", DateTimeOffset.UtcNow, batch.Id.Value, outcome.ChatName, outcome.MessageCount, "clipboard", outcome.Status, outcome.Detail));
        }

        var result = new BatchOutcome(batch.Id, "clipboard", outcomes);
        if (result.Succeeded)
        {
            inbox.MarkDone(batch.Id);
        }
        else
        {
            inbox.MarkFailed(batch.Id, string.Join("\n", outcomes.Where(o => o.Status == RecordStatus.Failed).Select(o => o.Detail)));
        }

        return result;
    }
}
