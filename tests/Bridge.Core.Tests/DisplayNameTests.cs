using System.Text;
using Bridge.Core.Naming;
using Xunit;

namespace Bridge.Core.Tests;

public class DisplayNameTests
{
    [Fact]
    public void 普通中文名原样保留() => Assert.Equal("聊天记录 2026.zip", DisplayName.Sanitize("聊天记录 2026.zip"));

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("/tmp/聊天.zip", "聊天.zip")]
    [InlineData(@"C:\Users\x\聊天.zip", "聊天.zip")]
    public void 只取最后一个路径分量(string raw, string expected) => Assert.Equal(expected, DisplayName.Sanitize(raw));

    [Fact]
    public void 冒号换成横线() => Assert.Equal("2026-09-05.zip", DisplayName.Sanitize("2026:09:05.zip"));

    [Fact]
    public void 控制字符换成空格() => Assert.Equal("chat log.zip", DisplayName.Sanitize("chat\nlog.zip"));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(null)]
    public void 不能当文件名的回退到共享文件(string? raw) => Assert.StartsWith(DisplayName.FallbackBaseName, DisplayName.Sanitize(raw));

    [Fact]
    public void 不产生隐藏文件() => Assert.False(DisplayName.Sanitize(".zshrc").StartsWith('.'));

    [Fact]
    public void 只在没有扩展名时补上回退扩展名()
    {
        Assert.Equal("聊天记录.zip", DisplayName.Sanitize("聊天记录", "zip"));
        Assert.Equal("聊天记录.txt", DisplayName.Sanitize("聊天记录.txt", "zip"));
    }

    [Fact]
    public void 截断时保留扩展名()
    {
        var name = DisplayName.Sanitize(new string('记', 300) + ".zip");
        Assert.EndsWith(".zip", name);
        Assert.True(Encoding.UTF8.GetByteCount(name) <= 200);
        Assert.StartsWith("记", name);
    }

    [Theory]
    [InlineData("a*b?c.zip", "a-b-c.zip")]
    [InlineData("name.zip.", "name.zip")]
    [InlineData("name   ", "name")]
    public void Windows不允许的字符和结尾点空格(string raw, string expected) => Assert.Equal(expected, DisplayName.Sanitize(raw));

    [Theory]
    [InlineData("CON")]
    [InlineData("con.zip")]
    [InlineData("LPT1.txt")]
    public void 保留设备名加前缀(string raw) => Assert.StartsWith(DisplayName.FallbackBaseName, DisplayName.Sanitize(raw));

    [Theory]
    [InlineData("产品讨论组的聊天.zip", "产品讨论组")]
    [InlineData("张三的聊天记录.zip", "张三")]
    [InlineData("聊天记录.zip", null)]
    [InlineData("Zip归档.zip", null)]
    [InlineData(@"D:\x\小闹钟的聊天.zip", "小闹钟")]
    public void 从压缩包名里取群名(string fileName, string? expected) => Assert.Equal(expected, DisplayName.ChatNameFromArchiveName(fileName));
}
