using Bridge.Core.Config;
using Bridge.Core.Records;
using Xunit;

namespace Bridge.Core.Tests;

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bridge-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Reversible stand-in for DPAPI so the tests can see that the secret never lands in the file as typed.</summary>
public sealed class ReverseProtector : ISecretProtector
{
    public string Protect(string plain) => "prot:" + new string(plain.Reverse().ToArray());

    public string Unprotect(string protectedValue) => new(protectedValue["prot:".Length..].Reverse().ToArray());
}

public class SettingsStoreTests
{
    [Fact]
    public void MissingFileGivesDefaults()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"), new ReverseProtector());
        var settings = store.Load();
        Assert.Equal("http://127.0.0.1:8788", settings.LlmSocial.BaseUrl);
        Assert.Empty(settings.MyNames);
        Assert.False(settings.LlmSocialMode);
        Assert.Equal(7, settings.HistoryDays);
    }

    [Fact]
    public void RoundTripsAndKeepsTheSecretOutOfTheFile()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"), new ReverseProtector());
        var settings = Settings.Default with
        {
            MyNames = new[] { "秋一", "我" },
            LlmSocialMode = true,
            LlmSocial = new LlmSocialSettings("http://127.0.0.1:8799", "acct_abcdefghijkl", ""),
        };
        store.Save(store.WithSecret(settings, "bridge-shared-secret-at-least-24-chars"));

        var text = File.ReadAllText(dir.File("settings.json"));
        Assert.DoesNotContain("bridge-shared-secret", text);
        Assert.Contains("秋一", text);

        var loaded = store.Load();
        Assert.Equal(new[] { "秋一", "我" }, loaded.MyNames);
        Assert.True(loaded.LlmSocialMode);
        Assert.Equal("acct_abcdefghijkl", loaded.LlmSocial.AccountId);
        Assert.Equal("bridge-shared-secret-at-least-24-chars", store.Secret(loaded));
        Assert.True(loaded.LlmSocial.IsFilledIn);
    }

    [Fact]
    public void CorruptFileIsReportedWithItsPath()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{ not json");
        var store = new SettingsStore(dir.File("settings.json"), new ReverseProtector());
        var ex = Assert.Throws<SettingsException>(() => store.Load());
        Assert.Contains(dir.File("settings.json"), ex.Message);
    }

    [Fact]
    public void UnknownKeysAreIgnored()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{\"version\":1,\"future\":true,\"myNames\":[\"我\"],\"llmSocialMode\":false,\"llmSocial\":{\"baseUrl\":\"http://x\",\"accountId\":\"\",\"secretProtected\":\"\"},\"historyDays\":3}");
        var loaded = new SettingsStore(dir.File("settings.json"), new ReverseProtector()).Load();
        Assert.Equal(new[] { "我" }, loaded.MyNames);
        Assert.Equal(3, loaded.HistoryDays);
    }
}

public class GroupFingerprintTests
{
    private static readonly GroupMemoryEntry A = new("客户群 A", new[] { "张三", "李四", "王五" }, DateTimeOffset.UnixEpoch);
    private static readonly GroupMemoryEntry B = new("客户群 B", new[] { "赵六", "钱七", "孙八" }, DateTimeOffset.UnixEpoch);

    [Fact]
    public void MatchesTheGroupWithEnoughOverlap()
    {
        Assert.Equal("客户群 A", GroupFingerprint.Match(new[] { "张三", "李四" }, new[] { A, B }));
    }

    [Fact]
    public void OneSenderIsNeverEnough()
    {
        Assert.Null(GroupFingerprint.Match(new[] { "张三" }, new[] { A, B }));
    }

    [Fact]
    public void TieBetweenTwoGroupsIsNoMatch()
    {
        var a = new GroupMemoryEntry("A", new[] { "张三", "甲" }, DateTimeOffset.UnixEpoch);
        var b = new GroupMemoryEntry("B", new[] { "赵六", "乙" }, DateTimeOffset.UnixEpoch);
        Assert.Null(GroupFingerprint.Match(new[] { "张三", "赵六" }, new[] { a, b }));
    }

    [Fact]
    public void BelowThresholdIsNoMatch()
    {
        // 1 of 3 = 0.33 < 0.7
        Assert.Null(GroupFingerprint.Match(new[] { "张三", "陌生1", "陌生2" }, new[] { A }));
    }

    [Fact]
    public void NormalisationIgnoresCaseAndSurroundingSpace()
    {
        var entry = new GroupMemoryEntry("英文群", new[] { "Alice", "Bob" }, DateTimeOffset.UnixEpoch);
        Assert.Equal("英文群", GroupFingerprint.Match(new[] { " alice ", "BOB" }, new[] { entry }));
    }

    [Fact]
    public void StoreRemembersAndRecognises()
    {
        using var dir = new TempDir();
        var store = new GroupMemoryStore(dir.File("groups.json"));
        Assert.Null(store.Match(new[] { "张三", "李四" }));

        store.Remember("客户群 A", new[] { "张三", "李四" }, DateTimeOffset.UnixEpoch);
        Assert.Equal("客户群 A", store.Match(new[] { "张三", "李四" }));

        store.Remember("客户群 A", new[] { "李四", "王五" }, DateTimeOffset.UnixEpoch);
        var entry = Assert.Single(store.Load().Groups);
        Assert.Equal(new[] { "张三", "李四", "王五" }, entry.Senders);
        Assert.Equal("客户群 A", store.Match(new[] { "王五", "张三" }));
    }
}

public class RecordsLogTests
{
    private static DeliveryRecord Record(string id) =>
        new(id, DateTimeOffset.UnixEpoch.AddSeconds(int.Parse(id)), "batch-" + id, "老王", 3, "llmsocial", RecordStatus.Sent, "已发 3 条");

    [Fact]
    public void AppendsAndReadsNewestFirst()
    {
        using var dir = new TempDir();
        var log = new RecordsLog(dir.File("records.jsonl"));
        log.Append(Record("1"));
        log.Append(Record("2"));
        var all = log.ReadAll();
        Assert.Equal(new[] { "2", "1" }, all.Select(r => r.Id));
        Assert.Equal("老王", all[0].ChatName);
        Assert.Equal(2, File.ReadAllLines(dir.File("records.jsonl")).Length);
    }

    [Fact]
    public void TornLineIsSkipped()
    {
        using var dir = new TempDir();
        var log = new RecordsLog(dir.File("records.jsonl"));
        log.Append(Record("1"));
        File.AppendAllText(dir.File("records.jsonl"), "{\"id\":\"half");
        Assert.Single(log.ReadAll());
    }

    [Fact]
    public void ConcurrentAppendsStayWhole()
    {
        using var dir = new TempDir();
        var path = dir.File("records.jsonl");
        Parallel.For(0, 200, i => new RecordsLog(path).Append(Record(i.ToString())));
        var all = new RecordsLog(path).ReadAll();
        Assert.Equal(200, all.Count);
        Assert.Equal(200, all.Select(r => r.Id).Distinct().Count());
    }
}
