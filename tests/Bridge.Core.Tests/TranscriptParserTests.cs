using System.Text;
using Bridge.Core.Transcripts;
using Xunit;

namespace Bridge.Core.Tests;

public class TranscriptParserTests
{
    private const string TwoMessages = "·甲\n2026年9月5日 08:05\n第一行\n第二行\n\n·甲\n2026年9月5日 08:06\n再见\n";

    [Fact]
    public void 多行正文保留内部换行_尾部空行去掉()
    {
        var t = TranscriptParser.Parse(TwoMessages);

        Assert.Equal(new[] { "第一行\n第二行", "再见" }, t.Messages.Select(m => m.Text));
        Assert.Equal("甲", t.Messages[0].Sender);
        Assert.Equal(new DateTime(2026, 9, 5, 8, 5, 0), t.Messages[0].SentAt);
        Assert.Equal(DateTimeKind.Unspecified, t.Messages[0].SentAt.Kind);
        Assert.Equal(new[] { "甲" }, t.Senders);
    }

    [Theory]
    [InlineData("unrecognized text")]
    [InlineData("")]
    [InlineData("﻿")]
    [InlineData("hello\n·张三\n2026年9月20日 10:23\n你好\n")]
    [InlineData("\n·张三\n2026年9月20日 10:23\n你好\n")]
    public void 开头不是头部就拒绝(string text)
    {
        Assert.Throws<TranscriptFormatException>(() => TranscriptParser.Parse(text));
    }

    [Fact]
    public void BOM和CRLF不改变结果()
    {
        var plain = TranscriptParser.Parse("·张三\n2026年9月20日 10:23\n你好\n");
        var withBomCrlf = TranscriptParser.Parse("﻿·张三\r\n2026年9月20日 10:23\r\n你好\r\n");

        Assert.Equal(plain.Messages, withBomCrlf.Messages);
    }

    [Theory]
    [InlineData("2026年9月5日 08:05", 2026, 9, 5, 8, 5)]
    [InlineData("2026年12月31日 23:59", 2026, 12, 31, 23, 59)]
    public void 月日不补零也能解析(string stamp, int y, int mo, int d, int h, int mi)
    {
        var t = TranscriptParser.Parse($"·甲\n{stamp}\n正文\n");
        Assert.Equal(new DateTime(y, mo, d, h, mi, 0), t.Messages[0].SentAt);
    }

    [Fact]
    public void 无效日期报错并指出第几条()
    {
        var ex = Assert.Throws<TranscriptFormatException>(() => TranscriptParser.Parse("·甲\n2026年13月1日 10:00\n正文\n"));
        Assert.Contains("第 1 条", ex.Message);
    }

    [Fact]
    public void 正文里以点开头但后面不是日期的行不算头部()
    {
        var t = TranscriptParser.Parse("·甲\n2026年9月5日 08:05\n·注意事项\n请看附件\n");
        Assert.Single(t.Messages);
        Assert.Equal("·注意事项\n请看附件", t.Messages[0].Text);
    }

    [Fact]
    public void 文末没有换行的头部并入上一条()
    {
        var t = TranscriptParser.Parse("·甲\n2026年9月5日 08:05\n正文\n·乙\n2026年9月5日 08:06");
        Assert.Single(t.Messages);
        Assert.Equal("正文\n·乙\n2026年9月5日 08:06", t.Messages[0].Text);
    }

    [Fact]
    public void 图片占位原样保留()
    {
        var t = TranscriptParser.Parse("·甲\n2026年9月5日 08:05\n[图片] 微信图片_20260920102301.jpg\n");
        Assert.Equal("[图片] 微信图片_20260920102301.jpg", t.Messages[0].Text);
    }

    [Fact]
    public void 发送者原样不裁剪_正文两端裁剪()
    {
        var t = TranscriptParser.Parse("·张 三 \n2026年9月5日 08:05\n  正文  \n\n");
        Assert.Equal("张 三 ", t.Messages[0].Sender);
        Assert.Equal("正文", t.Messages[0].Text);
    }

    [Fact]
    public void GBK字节也能解()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding(936).GetBytes("·张三\n2026年9月20日 10:23\n你好\n");
        var utf8 = Encoding.UTF8.GetBytes("·张三\n2026年9月20日 10:23\n你好\n");

        Assert.Equal(TranscriptParser.Parse(utf8).Messages, TranscriptParser.Parse(gbk).Messages);
    }

    [Fact]
    public void 起止时间()
    {
        var t = TranscriptParser.Parse(TwoMessages);
        Assert.Equal(new DateTime(2026, 9, 5, 8, 5, 0), t.Start);
        Assert.Equal(new DateTime(2026, 9, 5, 8, 6, 0), t.End);
    }
}
