using Bridge.Core.Batches;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bridge.Core.Tests;

public sealed class BatchInboxTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 4, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "wechatbridge-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(T0);

    private BatchInbox Inbox => new(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static BatchManifest Manifest(string id, DateTimeOffset? receivedAt = null) =>
        new(id, receivedAt ?? T0, BatchManifest.SourceShare, Array.Empty<string>(), ShareTitle: "群聊的聊天记录");

    private ReadyBatch CommitOne(DateTimeOffset? receivedAt = null)
    {
        var staging = Inbox.Begin(BatchId.New(_clock));
        File.WriteAllText(staging.Reserve("聊天记录.zip"), "PK");
        return staging.Commit(Manifest(staging.Id.Value, receivedAt));
    }

    [Fact]
    public void 批次ID按时间排序且带随机尾巴()
    {
        var id = BatchId.New(_clock);
        var prefix = _clock.GetLocalNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-";

        Assert.StartsWith(prefix, id.Value);
        Assert.Equal(prefix.Length + 8, id.Value.Length);
        Assert.NotEqual(id, BatchId.New(_clock));
    }

    [Fact]
    public void 同名附件不互相覆盖_不安全的名字被清洗()
    {
        var staging = Inbox.Begin(BatchId.New(_clock));

        Assert.EndsWith(Path.Combine("items", "a.zip"), staging.Reserve("a.zip"));
        Assert.EndsWith(Path.Combine("items", "a (2).zip"), staging.Reserve("a.zip"));
        Assert.EndsWith(Path.Combine("items", "x.zip"), staging.Reserve("../x.zip"));
        Assert.Equal(new[] { "a.zip", "a (2).zip", "x.zip" }, staging.Files);
    }

    [Fact]
    public void 提交把整个目录搬进ready_暂存目录消失()
    {
        var ready = CommitOne();

        Assert.True(Directory.Exists(ready.Directory));
        Assert.Equal(Path.Combine(_root, "ready"), Path.GetDirectoryName(ready.Directory));
        Assert.True(File.Exists(Path.Combine(ready.Directory, BatchInbox.ManifestFileName)));
        Assert.Empty(Directory.GetDirectories(Inbox.Staging));
        Assert.Equal(new[] { "聊天记录.zip" }, ready.Manifest.Files);
        Assert.Equal("群聊的聊天记录", ready.Manifest.ShareTitle);
    }

    [Fact]
    public void 未提交的批次在ready里看不见_放弃后目录消失()
    {
        var staging = Inbox.Begin(BatchId.New(_clock));
        File.WriteAllText(staging.Reserve("a.zip"), "x");

        Assert.Empty(Inbox.ListReady());
        staging.Abort();
        Assert.False(Directory.Exists(staging.Directory));
    }

    [Fact]
    public void 清单经过磁盘往返不变()
    {
        var ready = CommitOne();
        var read = Inbox.ListReady().Single();

        Assert.Equal(ready.Id, read.Id);
        Assert.Equal(ready.Manifest.Id, read.Manifest.Id);
        Assert.Equal(ready.Manifest.ReceivedAt, read.Manifest.ReceivedAt);
        Assert.Equal(ready.Manifest.Source, read.Manifest.Source);
        Assert.Equal(ready.Manifest.ShareTitle, read.Manifest.ShareTitle);
        Assert.Null(read.Manifest.ChatName);
        Assert.Equal(1, read.Manifest.SchemaVersion);
        Assert.Equal(new[] { "聊天记录.zip" }, read.Manifest.Files);
        Assert.Equal(new[] { Path.Combine(ready.Directory, "items", "聊天记录.zip") }, read.FilePaths);
    }

    [Fact]
    public void 清单里的中文不转义_键是camelCase()
    {
        var ready = CommitOne();
        var json = File.ReadAllText(Path.Combine(ready.Directory, BatchInbox.ManifestFileName));

        Assert.Contains("\"shareTitle\": \"群聊的聊天记录\"", json);
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.DoesNotContain("chatName", json);
    }

    [Fact]
    public void 文件不在了的条目不列出_一个都没有的批次不算批次()
    {
        var ready = CommitOne();
        File.Delete(ready.FilePaths[0]);

        Assert.Empty(Inbox.ListReady());
    }

    [Fact]
    public void 新版schema的批次被跳过而不是猜()
    {
        var ready = CommitOne();
        var manifestPath = Path.Combine(ready.Directory, BatchInbox.ManifestFileName);
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"));

        Assert.Empty(Inbox.ListReady());
    }

    [Fact]
    public void 完成和失败各进各的目录_失败的可以重排队()
    {
        var a = CommitOne();
        var b = CommitOne();

        Inbox.MarkDone(a.Id);
        Inbox.MarkFailed(b.Id, "llmsocial 没在运行");

        Assert.Empty(Inbox.ListReady());
        Assert.True(Directory.Exists(Path.Combine(Inbox.Done, a.Id.Value)));
        var failed = Inbox.ListFailed().Single();
        Assert.Equal(b.Id, failed.Id);
        Assert.Equal("llmsocial 没在运行", BatchInbox.ReadOutcome(failed.Directory));

        var requeued = Inbox.Requeue(b.Id);
        Assert.NotNull(requeued);
        Assert.Equal(b.Id, Inbox.ListReady().Single().Id);
        Assert.Null(BatchInbox.ReadOutcome(requeued!.Directory));
    }

    [Fact]
    public void 清理只删严格超过窗口的_零表示永不()
    {
        var old = CommitOne(T0);
        var fresh = CommitOne(T0 + TimeSpan.FromDays(1));
        Inbox.MarkDone(old.Id);
        Inbox.MarkDone(fresh.Id);

        // Exactly at the window: kept.
        _clock.SetUtcNow(T0 + TimeSpan.FromDays(7));
        Assert.Equal(0, Inbox.Purge(TimeSpan.FromDays(7), _clock));

        // One minute past the window: 'old' goes, 'fresh' (a day younger) stays; zero means never.
        _clock.SetUtcNow(T0 + TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        Assert.Equal(0, Inbox.Purge(TimeSpan.Zero, _clock));
        Assert.Equal(1, Inbox.Purge(TimeSpan.FromDays(7), _clock));
        Assert.False(Directory.Exists(Path.Combine(Inbox.Done, old.Id.Value)));
        Assert.True(Directory.Exists(Path.Combine(Inbox.Done, fresh.Id.Value)));
    }

    [Fact]
    public void 清理不碰ready里的批次()
    {
        CommitOne();
        _clock.Advance(TimeSpan.FromDays(30));

        Assert.Equal(0, Inbox.Purge(TimeSpan.FromDays(1), _clock));
        Assert.Single(Inbox.ListReady());
    }

    [Fact]
    public void 清理扫掉一小时前的暂存残留_但正在写入的不动()
    {
        var stale = Inbox.Begin(BatchId.New(_clock));
        File.WriteAllText(stale.Reserve("a.zip"), "x");
        var active = Inbox.Begin(BatchId.New(_clock));
        File.WriteAllText(active.Reserve("b.zip"), "y");
        var twoHoursAgo = DateTime.UtcNow - TimeSpan.FromHours(2);
        foreach (var path in Directory.EnumerateFileSystemEntries(stale.Directory, "*", SearchOption.AllDirectories).Append(stale.Directory))
        {
            if (Directory.Exists(path))
            {
                Directory.SetLastWriteTimeUtc(path, twoHoursAgo);
            }
            else
            {
                File.SetLastWriteTimeUtc(path, twoHoursAgo);
            }
        }

        Assert.Equal(1, Inbox.Purge(TimeSpan.Zero, TimeProvider.System));
        Assert.False(Directory.Exists(stale.Directory));
        Assert.True(Directory.Exists(active.Directory));
    }
}
