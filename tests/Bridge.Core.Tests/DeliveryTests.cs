using System.IO.Compression;
using System.Net;
using System.Text;
using Bridge.Core.Batches;
using Bridge.Core.Config;
using Bridge.Core.Delivery;
using Bridge.Core.LlmSocial;
using Bridge.Core.Records;
using Bridge.Core.Transcripts;
using Xunit;

namespace Bridge.Core.Tests;

public class LlmSocialDeliveryTests : IDisposable
{
    private const string Secret = "bridge-shared-secret-at-least-24-chars";
    private static readonly TimeZoneInfo Beijing = TimeZoneInfo.CreateCustomTimeZone("bj", TimeSpan.FromHours(8), "bj", "bj");

    private readonly TempDir _dir = new();
    private readonly BatchInbox _inbox;
    private readonly GroupMemoryStore _groups;
    private readonly RecordsLog _records;

    public LlmSocialDeliveryTests()
    {
        _inbox = new BatchInbox(_dir.File("inbox"));
        _groups = new GroupMemoryStore(_dir.File("groups.json"));
        _records = new RecordsLog(_dir.File("records.jsonl"));
    }

    public void Dispose() => _dir.Dispose();

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<byte[], HttpResponseMessage> _respond;

        public CapturingHandler(Func<byte[], HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Bodies.Add(Encoding.UTF8.GetString(body));
            return _respond(body);
        }
    }

    private ReadyBatch Batch(string zipName, string transcript, string? chatName = null)
    {
        var staging = _inbox.Begin(BatchId.New(TimeProvider.System));
        var path = staging.Reserve(zipName, "zip");
        using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("聊天记录.txt").Open(), new UTF8Encoding(false));
            writer.Write(transcript);
        }

        return staging.Commit(new BatchManifest(staging.Id.Value, DateTimeOffset.UtcNow, BatchManifest.SourceArgv, Array.Empty<string>(), ChatName: chatName));
    }

    private readonly List<string> _rememberedNames = new();

    private LlmSocialDelivery Delivery(CapturingHandler handler, params string[] myNames)
    {
        var config = new LlmSocialConfig("http://127.0.0.1:8788", "acct_abcdefghijkl", Secret);
        return new LlmSocialDelivery(new LlmSocialClient(new HttpClient(handler), config), myNames, _rememberedNames.Add, _groups, _records, _inbox, TimeProvider.System, Beijing);
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };

    private static Task<ChatNameAnswer?> NeverAsked(ChatNameRequest _) => throw new InvalidOperationException("should not ask");

    [Fact]
    public async Task OneToOneChatIsSentAndTheBatchMovesToDone()
    {
        var batch = Batch("聊天记录_20260926_153606.zip", "·老王\n2026年9月26日 15:30\n在吗\n\n·秋一\n2026年9月26日 15:31\n在的\n");
        var handler = new CapturingHandler(_ => Ok());

        var outcome = await Delivery(handler, "秋一").DeliverAsync(batch, NeverAsked);

        Assert.True(outcome.Succeeded);
        Assert.Equal(2, outcome.Sent);
        Assert.True(outcome.AnyLastFromSelf);
        var file = Assert.Single(outcome.Files);
        Assert.Equal("老王", file.ChatName);
        Assert.Equal(RecordStatus.Sent, file.Status);
        Assert.Contains("不会起草", file.Detail);
        Assert.Contains("\"contact\":{\"id\":\"wx:老王\"", handler.Bodies[0]);
        Assert.Contains("\"fromSelf\":true", handler.Bodies[1]);

        Assert.True(Directory.Exists(Path.Combine(_inbox.Done, batch.Id.Value)));
        Assert.False(Directory.Exists(batch.Directory));
        var record = Assert.Single(_records.ReadAll());
        Assert.Equal(RecordStatus.Sent, record.Status);
        Assert.Equal("老王", record.ChatName);
        Assert.Equal(batch.Id.Value, record.BatchId);
    }

    [Fact]
    public async Task GroupChatAsksOnceThenRemembers()
    {
        const string text = "·张三\n2026年9月26日 15:30\n开会吗\n\n·李四\n2026年9月26日 15:31\n开\n";
        var handler = new CapturingHandler(_ => Ok());
        var asked = 0;

        var first = await Delivery(handler).DeliverAsync(Batch("a.zip", text), _ =>
        {
            asked++;
            return Task.FromResult<ChatNameAnswer?>(new ChatNameAnswer("客户群", true));
        });
        Assert.True(first.Succeeded);
        Assert.Equal("客户群", first.Files[0].ChatName);
        Assert.Contains("\"contact\":{\"id\":\"wxg:客户群\"", handler.Bodies[0]);
        Assert.Contains("张三：开会吗", handler.Bodies[0]);

        var second = await Delivery(handler).DeliverAsync(Batch("b.zip", text), NeverAsked);
        Assert.True(second.Succeeded);
        Assert.Equal("客户群", second.Files[0].ChatName);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task TwoUnknownSendersAskWhoIsMeAndRememberIt()
    {
        var handler = new CapturingHandler(_ => Ok());
        ChatNameRequest? seen = null;
        var outcome = await Delivery(handler).DeliverAsync(Batch("a.zip", "·老周\n2026年9月26日 16:58\n做好了吗\n\n·秋一\n2026年9月26日 16:59\n在测\n"), request =>
        {
            seen = request;
            return Task.FromResult<ChatNameAnswer?>(new ChatNameAnswer(null, false, "秋一"));
        });

        Assert.True(seen!.MyNameUnknown);
        Assert.Equal(new[] { "老周", "秋一" }, seen.Senders);
        Assert.True(outcome.Succeeded);
        Assert.Equal("老周", outcome.Files[0].ChatName);
        Assert.Contains("\"contact\":{\"id\":\"wx:老周\"", handler.Bodies[0]);
        Assert.Contains("\"fromSelf\":true", handler.Bodies[1]);
        Assert.Equal(new[] { "秋一" }, _rememberedNames);
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task ThreeUnknownSendersAskForTheGroupNameOnly()
    {
        var handler = new CapturingHandler(_ => Ok());
        ChatNameRequest? seen = null;
        await Delivery(handler).DeliverAsync(Batch("a.zip", "·甲\n2026年9月26日 16:58\na\n\n·乙\n2026年9月26日 16:59\nb\n\n·丙\n2026年9月26日 17:00\nc\n"), request =>
        {
            seen = request;
            return Task.FromResult<ChatNameAnswer?>(new ChatNameAnswer("三人群", true));
        });
        Assert.False(seen!.MyNameUnknown);
        Assert.Contains("wxg:三人群", handler.Bodies[0]);
    }

    [Fact]
    public async Task CancellingTheGroupNameKeepsTheBatchForRetry()
    {
        var batch = Batch("a.zip", "·张三\n2026年9月26日 15:30\n开会吗\n\n·李四\n2026年9月26日 15:31\n开\n");
        var handler = new CapturingHandler(_ => Ok());

        var outcome = await Delivery(handler).DeliverAsync(batch, _ => Task.FromResult<ChatNameAnswer?>(null));

        Assert.False(outcome.Succeeded);
        Assert.Equal(RecordStatus.Cancelled, outcome.Files[0].Status);
        Assert.Empty(handler.Bodies);
        Assert.True(Directory.Exists(Path.Combine(_inbox.Failed, batch.Id.Value)));
        Assert.Contains("重试", BatchInbox.ReadOutcome(Path.Combine(_inbox.Failed, batch.Id.Value)));
    }

    [Fact]
    public async Task ConnectionRefusedFailsAndRetryAfterRequeueSucceeds()
    {
        var batch = Batch("a.zip", "·老王\n2026年9月26日 15:30\n在吗\n");
        var down = new CapturingHandler(_ => throw new HttpRequestException("refused"));

        var failed = await Delivery(down).DeliverAsync(batch, NeverAsked);
        Assert.False(failed.Succeeded);
        Assert.Equal(RecordStatus.Failed, failed.Files[0].Status);
        Assert.Contains("没在运行", failed.Files[0].Detail);
        Assert.True(Directory.Exists(Path.Combine(_inbox.Failed, batch.Id.Value)));

        var requeued = _inbox.Requeue(batch.Id);
        Assert.NotNull(requeued);
        var up = new CapturingHandler(_ => Ok());
        var retried = await Delivery(up).DeliverAsync(requeued!, NeverAsked);
        Assert.True(retried.Succeeded);
        Assert.True(Directory.Exists(Path.Combine(_inbox.Done, batch.Id.Value)));
        Assert.Equal(new[] { RecordStatus.Sent, RecordStatus.Failed }, _records.ReadAll().Select(r => r.Status));
    }

    [Fact]
    public async Task OnlyMyMessagesIsEmptyNotFailed()
    {
        var batch = Batch("a.zip", "·我\n2026年9月26日 15:30\n备忘\n");
        var handler = new CapturingHandler(_ => Ok());
        var outcome = await Delivery(handler, "我").DeliverAsync(batch, NeverAsked);
        Assert.True(outcome.Succeeded);
        Assert.Equal(RecordStatus.Empty, outcome.Files[0].Status);
        Assert.Empty(handler.Bodies);
        Assert.True(Directory.Exists(Path.Combine(_inbox.Done, batch.Id.Value)));
    }

    [Fact]
    public async Task UnreadableZipIsFailed()
    {
        var staging = _inbox.Begin(BatchId.New(TimeProvider.System));
        File.WriteAllBytes(staging.Reserve("bad.zip", "zip"), new byte[] { 1, 2, 3, 4 });
        var batch = staging.Commit(new BatchManifest(staging.Id.Value, DateTimeOffset.UtcNow, BatchManifest.SourceArgv, Array.Empty<string>()));
        var outcome = await Delivery(new CapturingHandler(_ => Ok())).DeliverAsync(batch, NeverAsked);
        Assert.False(outcome.Succeeded);
        Assert.Equal(RecordStatus.Failed, outcome.Files[0].Status);
    }
}

public class ClipboardTextTests
{
    [Fact]
    public void FormatsSenderTimeAndText()
    {
        var transcript = TranscriptParser.Parse("·老王\n2026年9月26日 15:30\n在吗\n第二行\n\n·秋一\n2026年9月26日 15:31\n在的\n");
        Assert.Equal("老王 2026-09-26 15:30\r\n在吗\r\n第二行\r\n\r\n秋一 2026-09-26 15:31\r\n在的", ClipboardText.Format(transcript));
    }
}
