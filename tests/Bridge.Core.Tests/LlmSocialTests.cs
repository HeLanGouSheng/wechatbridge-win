using System.Net;
using System.Text;
using Bridge.Core.LlmSocial;
using Bridge.Core.Transcripts;
using Xunit;

namespace Bridge.Core.Tests;

public class LlmSocialSignerTests
{
    // Vectors computed with Node: crypto.createHmac('sha256', secret).update(Buffer.from(body,'utf8')).digest('hex'),
    // the same call llmsocial's webhook connector makes.
    private const string Secret = "bridge-shared-secret-at-least-24-chars";

    [Fact]
    public void MatchesNodeHmacForChineseBody()
    {
        var body = Encoding.UTF8.GetBytes("{\"messageId\":\"m1\",\"text\":\"你好\"}");
        Assert.Equal("sha256=fc6f0e9c7b6b04a040d44fc5203c1e010860d9a00893410aa863dfe0d19fbbfc", LlmSocialSigner.Sign(Secret, body));
    }

    [Fact]
    public void MatchesNodeHmacForEmptyObject()
    {
        Assert.Equal("sha256=88679991b923df3da9a7dcd783bd2a7b152b5613b4458d44853eb9221879efe3", LlmSocialSigner.Sign(Secret, "{}"u8));
    }
}

public class MessageIdsTests
{
    private static readonly TranscriptMessage Message = new("老王", new DateTime(2026, 9, 26, 15, 30, 0), "好的");

    [Fact]
    public void IsStableAndWellFormed()
    {
        var a = MessageIds.Stable("老王", Message, 0);
        var b = MessageIds.Stable("老王", Message, 0);
        Assert.Equal(a, b);
        Assert.Matches("^wx_[0-9a-f]{32}$", a);
    }

    [Fact]
    public void OrdinalAndThreadChangeTheId()
    {
        var baseline = MessageIds.Stable("老王", Message, 0);
        Assert.NotEqual(baseline, MessageIds.Stable("老王", Message, 1));
        Assert.NotEqual(baseline, MessageIds.Stable("客户群", Message, 0));
        Assert.NotEqual(baseline, MessageIds.Stable("老王", Message with { Text = "好的！" }, 0));
    }
}

public class PayloadMapperTests
{
    private static readonly TimeZoneInfo Beijing = TimeZoneInfo.CreateCustomTimeZone("bj", TimeSpan.FromHours(8), "bj", "bj");

    private static Transcript Chat(params (string Sender, string Time, string Text)[] messages)
    {
        var text = string.Concat(messages.Select(m => $"·{m.Sender}\n{m.Time}\n{m.Text}\n\n"));
        return TranscriptParser.Parse(text);
    }

    [Fact]
    public void OneToOneChatMapsOntoTheOtherPerson()
    {
        var transcript = Chat(("老王", "2026年9月26日 15:30", "在吗"), ("秋一", "2026年9月26日 15:31", "在的"));
        var result = PayloadMapper.Map(transcript, null, new[] { "秋一" }, Beijing);

        Assert.False(result.NeedsChatName);
        Assert.False(result.IsGroup);
        Assert.Equal("老王", result.Counterpart);
        Assert.Equal(2, result.Messages.Count);

        var first = result.Messages[0];
        Assert.Equal("dm", first.Kind);
        Assert.Equal("wx:老王", first.Contact.Id);
        Assert.Equal("老王", first.Contact.Name);
        Assert.Equal("老王", first.ThreadId);
        Assert.Equal("老王", first.ThreadTitle);
        Assert.Equal("在吗", first.Text);
        Assert.False(first.FromSelf);
        Assert.True(result.Messages[1].FromSelf);
        Assert.Equal("在的", result.Messages[1].Text);
    }

    [Fact]
    public void TimestampIsWallTimeInTheGivenZoneAsEpochMilliseconds()
    {
        var transcript = Chat(("老王", "2026年9月26日 15:30", "在吗"));
        var result = PayloadMapper.Map(transcript, null, Array.Empty<string>(), Beijing);
        var expected = new DateTimeOffset(2026, 9, 26, 15, 30, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
        Assert.Equal(expected, result.Messages[0].Timestamp);
        Assert.True(result.Messages[0].Timestamp > 1_700_000_000_000L, "must be milliseconds, not seconds");
    }

    [Fact]
    public void GroupWithoutNameAsksForOne()
    {
        var transcript = Chat(("张三", "2026年9月26日 15:30", "a"), ("李四", "2026年9月26日 15:31", "b"), ("我", "2026年9月26日 15:32", "c"));
        var result = PayloadMapper.Map(transcript, null, new[] { "我" }, Beijing);
        Assert.True(result.NeedsChatName);
        Assert.True(result.IsGroup);
        Assert.Empty(result.Messages);
        Assert.Equal(new[] { "张三", "李四" }, result.OtherSenders);
    }

    [Fact]
    public void GroupWithNamePrefixesOtherSendersOnly()
    {
        var transcript = Chat(("张三", "2026年9月26日 15:30", "a"), ("我", "2026年9月26日 15:32", "c"), ("李四", "2026年9月26日 15:33", "b"));
        var result = PayloadMapper.Map(transcript, " 客户群 ", new[] { "我" }, Beijing);
        Assert.False(result.NeedsChatName);
        Assert.Equal("wxg:客户群", result.Messages[0].Contact.Id);
        Assert.Equal("客户群", result.Messages[0].Contact.Name);
        Assert.Equal("客户群", result.Messages[0].ThreadId);
        Assert.Equal("张三：a", result.Messages[0].Text);
        Assert.Equal("c", result.Messages[1].Text);
        Assert.True(result.Messages[1].FromSelf);
        Assert.Equal("李四：b", result.Messages[2].Text);
    }

    [Fact]
    public void ChatNameOverridesThreadIdForOneToOne()
    {
        var transcript = Chat(("老王", "2026年9月26日 15:30", "在吗"));
        var result = PayloadMapper.Map(transcript, "王总", Array.Empty<string>(), Beijing);
        Assert.Equal("wx:老王", result.Messages[0].Contact.Id);
        Assert.Equal("王总", result.Messages[0].ThreadId);
        Assert.Equal("王总", result.Messages[0].ThreadTitle);
    }

    [Fact]
    public void EmptyTextIsSkippedAndCounted()
    {
        var transcript = new Transcript(new[]
        {
            new TranscriptMessage("老王", new DateTime(2026, 9, 26, 15, 30, 0), "   "),
            new TranscriptMessage("老王", new DateTime(2026, 9, 26, 15, 31, 0), "在吗"),
        });
        var result = PayloadMapper.Map(transcript, null, Array.Empty<string>(), Beijing);
        Assert.Single(result.Messages);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void RepeatedMessagesInTheSameMinuteGetDistinctIds()
    {
        var transcript = Chat(("老王", "2026年9月26日 15:30", "好的"), ("老王", "2026年9月26日 15:30", "好的"));
        var result = PayloadMapper.Map(transcript, null, Array.Empty<string>(), Beijing);
        Assert.Equal(2, result.Messages.Count);
        Assert.NotEqual(result.Messages[0].MessageId, result.Messages[1].MessageId);

        var again = PayloadMapper.Map(transcript, null, Array.Empty<string>(), Beijing);
        Assert.Equal(result.Messages.Select(m => m.MessageId), again.Messages.Select(m => m.MessageId));
    }

    [Fact]
    public void OnlyMyOwnMessagesYieldNothing()
    {
        var transcript = Chat(("我", "2026年9月26日 15:30", "备忘"));
        var result = PayloadMapper.Map(transcript, null, new[] { "我" }, Beijing);
        Assert.Empty(result.Messages);
        Assert.Empty(result.OtherSenders);
        Assert.False(result.NeedsChatName);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void PayloadSerialisesTheWayLlmSocialReads()
    {
        var transcript = Chat(("老王", "2026年9月26日 15:30", "在吗"));
        var payload = PayloadMapper.Map(transcript, null, Array.Empty<string>(), Beijing).Messages[0];
        var json = Encoding.UTF8.GetString(BridgeJson.SerializeToUtf8Bytes(payload));
        Assert.Contains("\"messageId\":\"wx_", json);
        Assert.Contains("\"kind\":\"dm\"", json);
        Assert.Contains("\"contact\":{\"id\":\"wx:老王\",\"name\":\"老王\"}", json);
        Assert.Contains("\"fromSelf\":false", json);
        Assert.DoesNotContain("\\u", json);
    }
}

public class LlmSocialClientTests
{
    private const string Secret = "bridge-shared-secret-at-least-24-chars";
    private static readonly LlmSocialConfig Config = new("http://127.0.0.1:8788/", "acct_abcdefghijkl", Secret);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, byte[], HttpResponseMessage> _respond;

        public FakeHandler(Func<HttpRequestMessage, byte[], HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public List<(string Url, string Signature, byte[] Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var signature = request.Headers.TryGetValues(LlmSocialSigner.HeaderName, out var values) ? values.First() : "";
            Requests.Add((request.RequestUri!.ToString(), signature, body));
            return _respond(request, body);
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string text = "") =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private static InboundPayload Payload(string id, string text) =>
        new(id, "dm", "老王", "老王", new ContactRef("wx:老王", "老王"), text, 1_790_000_000_000L, false);

    [Fact]
    public async Task PostsEachMessageInOrderWithSignatureOverTheExactBytes()
    {
        var handler = new FakeHandler((_, _) => Response(HttpStatusCode.OK, "{\"ok\":true}"));
        var client = new LlmSocialClient(new HttpClient(handler), Config);

        var result = await client.DeliverAsync(new[] { Payload("a", "一"), Payload("b", "二") });

        Assert.True(result.Complete);
        Assert.Equal(2, result.Sent);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("http://127.0.0.1:8788/webhooks/acct_abcdefghijkl", r.Url));
        Assert.Contains("\"messageId\":\"a\"", Encoding.UTF8.GetString(handler.Requests[0].Body));
        Assert.Contains("\"messageId\":\"b\"", Encoding.UTF8.GetString(handler.Requests[1].Body));
        Assert.All(handler.Requests, r => Assert.Equal(LlmSocialSigner.Sign(Secret, r.Body), r.Signature));
    }

    [Fact]
    public async Task BadSignatureAbortsWithNextStep()
    {
        var handler = new FakeHandler((_, _) => Response(HttpStatusCode.Unauthorized, "{\"error\":\"bad signature\"}"));
        var result = await new LlmSocialClient(new HttpClient(handler), Config).DeliverAsync(new[] { Payload("a", "一"), Payload("b", "二") });
        Assert.False(result.Complete);
        Assert.Equal(0, result.Sent);
        Assert.Contains("共享密钥不对", result.AbortReason);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UnknownAccountAborts()
    {
        var handler = new FakeHandler((_, _) => Response(HttpStatusCode.NotFound, "{\"error\":\"unknown webhook\"}"));
        var result = await new LlmSocialClient(new HttpClient(handler), Config).DeliverAsync(new[] { Payload("a", "一") });
        Assert.Contains("账号 ID", result.AbortReason);
    }

    [Fact]
    public async Task BadRequestSkipsThatMessageAndContinues()
    {
        var handler = new FakeHandler((_, body) =>
            Encoding.UTF8.GetString(body).Contains("\"messageId\":\"a\"") ? Response(HttpStatusCode.BadRequest, "{\"error\":\"text required\"}") : Response(HttpStatusCode.OK));
        var result = await new LlmSocialClient(new HttpClient(handler), Config).DeliverAsync(new[] { Payload("a", "一"), Payload("b", "二") });
        Assert.True(result.Complete);
        Assert.Equal(1, result.Sent);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("a", failure.MessageId);
        Assert.Equal(400, failure.Status);
    }

    [Fact]
    public async Task ServerErrorRetriesOnceThenAborts()
    {
        var handler = new FakeHandler((_, _) => Response(HttpStatusCode.InternalServerError, "boom"));
        var result = await new LlmSocialClient(new HttpClient(handler), Config).DeliverAsync(new[] { Payload("a", "一"), Payload("b", "二") });
        Assert.False(result.Complete);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("500", result.AbortReason);
    }

    [Fact]
    public async Task ServerErrorThenSuccessCounts()
    {
        var calls = 0;
        var handler = new FakeHandler((_, _) => ++calls == 1 ? Response(HttpStatusCode.BadGateway) : Response(HttpStatusCode.OK));
        var result = await new LlmSocialClient(new HttpClient(handler), Config).DeliverAsync(new[] { Payload("a", "一") });
        Assert.True(result.Complete);
        Assert.Equal(1, result.Sent);
    }

    [Fact]
    public async Task ConnectionRefusedAbortsSayingLlmSocialIsNotRunning()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("No connection could be made"));
        var result = await new LlmSocialClient(new HttpClient(handler), Config).DeliverAsync(new[] { Payload("a", "一") });
        Assert.False(result.Complete);
        Assert.Contains("没在运行", result.AbortReason);
        Assert.Contains("http://127.0.0.1:8788", result.AbortReason);
    }

    [Fact]
    public async Task ConnectionTestPassesOnHealthyServerAndAcceptedSignature()
    {
        var handler = new FakeHandler((request, _) =>
            request.Method == HttpMethod.Get ? Response(HttpStatusCode.OK, "{\"ok\":true}") : Response(HttpStatusCode.BadRequest, "{\"error\":\"messageId required\"}"));
        var test = await new LlmSocialClient(new HttpClient(handler), Config).TestAsync();
        Assert.True(test.Ok);
        Assert.Equal("http://127.0.0.1:8788/healthz", handler.Requests[0].Url);
        Assert.Equal("{}", Encoding.UTF8.GetString(handler.Requests[1].Body));
    }

    [Fact]
    public async Task ConnectionTestReportsWrongSecret()
    {
        var handler = new FakeHandler((request, _) =>
            request.Method == HttpMethod.Get ? Response(HttpStatusCode.OK, "{\"ok\":true}") : Response(HttpStatusCode.Unauthorized));
        var test = await new LlmSocialClient(new HttpClient(handler), Config).TestAsync();
        Assert.False(test.Ok);
        Assert.Contains("密钥", test.Detail);
    }

    [Fact]
    public async Task ConnectionTestReportsUnreachableServer()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("refused"));
        var test = await new LlmSocialClient(new HttpClient(handler), Config).TestAsync();
        Assert.False(test.Ok);
        Assert.Contains("没在运行", test.Detail);
    }

    [Theory]
    [InlineData("127.0.0.1:8788", "acct_abcdefghijkl", "bridge-shared-secret-at-least-24-chars", "地址")]
    [InlineData("http://127.0.0.1:8788", "acct_short", "bridge-shared-secret-at-least-24-chars", "账号 ID")]
    [InlineData("http://127.0.0.1:8788", "acct_abcdefghijkl", "short", "密钥")]
    public void ValidateExplainsWhatToFix(string url, string account, string secret, string expected)
    {
        Assert.Contains(expected, LlmSocialConfig.Validate(url, account, secret));
    }

    [Fact]
    public void ValidateAcceptsGoodInput()
    {
        Assert.Null(LlmSocialConfig.Validate("http://127.0.0.1:8788", "acct_abcdefghijkl", Secret));
        Assert.Null(LlmSocialConfig.Validate("https://host.example/", "acct_AB-_efghij12", Secret));
    }
}
