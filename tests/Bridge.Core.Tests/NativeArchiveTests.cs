using System.IO.Compression;
using System.Text;
using Bridge.Core.Archives;
using Xunit;

namespace Bridge.Core.Tests;

public class NativeArchiveTests
{
    private const string TwoMessages = "·甲\n2026年9月5日 08:05\n你好\n·乙\n2026年9月5日 08:06\n[图片] 微信图片_1.jpg\n";
    private const string ThreeMessages = "·甲\n2026年9月5日 08:05\n一\n·乙\n2026年9月5日 08:06\n二\n·甲\n2026年9月5日 08:07\n三\n";

    private static byte[] Zip(params (string Name, byte[] Content)[] entries) => Zip(CompressionLevel.Optimal, null, entries);

    private static byte[] Zip(CompressionLevel level, Encoding? nameEncoding, params (string Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, nameEncoding))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name, level);
                using var s = entry.Open();
                s.Write(content);
            }
        }

        return ms.ToArray();
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static ArchiveTranscript Read(byte[] zip) => NativeArchive.ReadTranscript(new MemoryStream(zip));

    [Fact]
    public void 读出聊天记录并列出附件名()
    {
        var zip = Zip(("聊天记录.txt", Utf8(TwoMessages)), ("images/微信图片_1.jpg", new byte[] { 1, 2, 3 }));

        var result = Read(zip);

        Assert.Equal("聊天记录.txt", result.EntryName);
        Assert.Equal(2, result.Transcript.Messages.Count);
        Assert.Equal(new[] { "images/微信图片_1.jpg" }, result.AttachmentNames);
    }

    [Fact]
    public void 没有聊天记录txt时选记录最多的文本文件()
    {
        var zip = Zip(("readme.txt", Utf8("这是说明，不是聊天记录")), ("其他.txt", Utf8(ThreeMessages)), ("少.txt", Utf8(TwoMessages)));

        var result = Read(zip);

        Assert.Equal("其他.txt", result.EntryName);
        Assert.Equal(3, result.Transcript.Messages.Count);
        Assert.Equal(new[] { "readme.txt", "少.txt" }, result.AttachmentNames);
    }

    [Fact]
    public void 子目录里的聊天记录txt也算_根目录优先()
    {
        var zip = Zip(("导出/聊天记录.txt", Utf8(TwoMessages)), ("聊天记录.txt", Utf8(ThreeMessages)));

        Assert.Equal("聊天记录.txt", Read(zip).EntryName);
        Assert.Equal("导出/聊天记录.txt", Read(Zip(("导出/聊天记录.txt", Utf8(TwoMessages)))).EntryName);
    }

    [Fact]
    public void 聊天记录txt格式不对就报NoTranscript()
    {
        var zip = Zip(("聊天记录.txt", Utf8("not a transcript")));
        var ex = Assert.Throws<ArchiveException>(() => Read(zip));
        Assert.Equal(ArchiveFailure.NoTranscript, ex.Reason);
    }

    [Fact]
    public void 没有任何文本文件就报NoTranscript()
    {
        var ex = Assert.Throws<ArchiveException>(() => Read(Zip(("a.jpg", new byte[] { 1 }))));
        Assert.Equal(ArchiveFailure.NoTranscript, ex.Reason);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("C:/x.txt")]
    [InlineData("a\u0001.txt")]
    [InlineData("/abs.txt")]
    [InlineData("a//b.txt")]
    public void 不安全的文件名拒绝整个压缩包(string name)
    {
        var zip = Zip(("聊天记录.txt", Utf8(TwoMessages)), (name, Utf8("x")));
        var ex = Assert.Throws<ArchiveException>(() => Read(zip));
        Assert.Equal(ArchiveFailure.UnsafeEntryName, ex.Reason);
    }

    [Fact]
    public void 单条目16MiB通过_多一字节拒绝()
    {
        var ok = new byte[16 << 20];
        var tooBig = new byte[(16 << 20) + 1];

        // 16 MiB of zeros is not a transcript, so the failure is NoTranscript, not EntryTooLarge.
        var okEx = Assert.Throws<ArchiveException>(() => Read(Zip(("聊天记录.txt", ok))));
        Assert.Equal(ArchiveFailure.NoTranscript, okEx.Reason);

        // One byte over: the size pre-check drops it from the candidates entirely.
        var bigEx = Assert.Throws<ArchiveException>(() => Read(Zip(("聊天记录.txt", tooBig))));
        Assert.Equal(ArchiveFailure.NoTranscript, bigEx.Reason);
    }

    [Fact]
    public void 中央目录谎报大小时按实际读到的字节数拒绝()
    {
        var tooBig = new byte[(16 << 20) + 1];
        var zip = Zip(("聊天记录.txt", tooBig));
        PatchUncompressedSize(zip, 16 << 20);

        // .NET itself stops reading when the data outgrows the declared size (Corrupt); if it ever stops
        // doing that, the byte counter in ReadEntry catches it (EntryTooLarge). Either way nothing over
        // the cap lands in memory.
        var ex = Assert.Throws<ArchiveException>(() => Read(zip));
        Assert.Contains(ex.Reason, new[] { ArchiveFailure.EntryTooLarge, ArchiveFailure.Corrupt });
    }

    [Fact]
    public void 加密的条目读不出就整体拒绝()
    {
        // .NET 8 does not refuse the encryption bit by itself; what matters is that unreadable data fails
        // closed instead of half-parsing. Flag the entry and scramble its bytes like a real cipher would.
        var zip = Zip(CompressionLevel.NoCompression, null, ("聊天记录.txt", Utf8(TwoMessages)));
        SetEncryptedFlag(zip);
        var at = IndexOf(zip, Utf8("·甲"));
        for (var i = at; i < at + 20; i++)
        {
            zip[i] ^= 0x5A;
        }

        var ex = Assert.Throws<ArchiveException>(() => Read(zip));
        Assert.Contains(ex.Reason, new[] { ArchiveFailure.Encrypted, ArchiveFailure.Corrupt, ArchiveFailure.NoTranscript });
    }

    [Fact]
    public void GBK编码的条目名仍能按名找到()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var zip = Zip(CompressionLevel.Optimal, Encoding.GetEncoding(936), ("聊天记录.txt", Utf8(TwoMessages)), ("其他.txt", Utf8(ThreeMessages)));

        Assert.Equal("聊天记录.txt", Read(zip).EntryName);
    }

    [Fact]
    public void 不是ZIP就报NotZip()
    {
        var random = new byte[64];
        new Random(1).NextBytes(random);
        Assert.Equal(ArchiveFailure.NotZip, Assert.Throws<ArchiveException>(() => Read(random)).Reason);
        Assert.Equal(ArchiveFailure.NotZip, Assert.Throws<ArchiveException>(() => Read(Utf8(TwoMessages))).Reason);
    }

    [Fact]
    public void 内容被改动时CRC不符就拒绝()
    {
        var zip = Zip(CompressionLevel.NoCompression, null, ("聊天记录.txt", Utf8(TwoMessages)));
        var needle = Utf8("你好");
        var at = IndexOf(zip, needle);
        Assert.True(at > 0);
        zip[at] ^= 0x01;

        var ex = Assert.Throws<ArchiveException>(() => Read(zip));
        Assert.Equal(ArchiveFailure.Corrupt, ex.Reason);
    }

    [Fact]
    public void 目录条目被跳过()
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            z.CreateEntry("images/");
            using var s = z.CreateEntry("聊天记录.txt").Open();
            s.Write(Utf8(TwoMessages));
        }

        var result = Read(ms.ToArray());
        Assert.Empty(result.AttachmentNames);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Sets bit 0 of the general-purpose flag in every local and central header.</summary>
    private static void SetEncryptedFlag(byte[] zip)
    {
        for (var i = 0; i < zip.Length - 4; i++)
        {
            if (zip[i] == 0x50 && zip[i + 1] == 0x4B)
            {
                if (zip[i + 2] == 0x03 && zip[i + 3] == 0x04)
                {
                    zip[i + 6] |= 1;
                }
                else if (zip[i + 2] == 0x01 && zip[i + 3] == 0x02)
                {
                    zip[i + 8] |= 1;
                }
            }
        }
    }

    /// <summary>Rewrites the uncompressed size in every central directory header, which is where
    /// ZipArchive reads entry.Length from.</summary>
    private static void PatchUncompressedSize(byte[] zip, uint size)
    {
        for (var i = 0; i < zip.Length - 4; i++)
        {
            if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x01 && zip[i + 3] == 0x02)
            {
                BitConverter.TryWriteBytes(zip.AsSpan(i + 24, 4), size);
            }
        }
    }
}
