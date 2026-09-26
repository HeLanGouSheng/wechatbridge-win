using System.IO.Compression;
using System.Text;
using Bridge.Core.Transcripts;

namespace Bridge.Core.Archives;

public enum ArchiveFailure
{
    NotZip,
    TooLarge,
    TooManyEntries,
    UnsafeEntryName,
    Encrypted,
    EntryTooLarge,
    Corrupt,
    NoTranscript,
}

public sealed class ArchiveException(string message, ArchiveFailure reason) : Exception(message)
{
    public ArchiveFailure Reason { get; } = reason;
}

/// <summary>The transcript picked out of a WeChat export, plus the names of everything else in it.</summary>
public sealed record ArchiveTranscript(string EntryName, Transcript Transcript, IReadOnlyList<string> AttachmentNames);

/// <summary>
/// Reads the ZIP that WeChat hands to a share target. This is the Windows counterpart of the macOS
/// WeChatNativeArchive.swift, which parses the ZIP by hand to fail closed on ZIP64, encryption and
/// zip bombs. Here <see cref="ZipArchive"/> does the parsing and the same protection comes from hard
/// limits, entry-name checks, refusing encrypted entries, and a CRC check on every transcript read —
/// with one deliberate difference: entry names in GBK are accepted, because a Windows-side WeChat may
/// well write them, and the macOS parser's strict-UTF-8 rule would reject the whole archive.
/// </summary>
public static class NativeArchive
{
    public const long MaxArchiveBytes = 256L << 20;
    public const long MaxEntryBytes = 16L << 20;
    public const long MaxTotalBytes = 512L << 20;
    public const int MaxEntries = 10_000;
    public const string TranscriptFileName = "聊天记录.txt";

    private static readonly Encoding LegacyNameEncoding = CreateGbk();

    private static Encoding CreateGbk()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936);
    }

    public static ArchiveTranscript ReadTranscript(string zipPath)
    {
        using var stream = File.OpenRead(zipPath);
        return ReadTranscript(stream);
    }

    public static ArchiveTranscript ReadTranscript(Stream zip)
    {
        if (zip.Length > MaxArchiveBytes)
        {
            throw new ArchiveException($"压缩包超过 {MaxArchiveBytes >> 20} MB，微信导出的聊天记录不会这么大；少选几条再转发", ArchiveFailure.TooLarge);
        }

        var archive = Open(zip, encoding: null);
        try
        {
            if (archive.Entries.Count > MaxEntries)
            {
                throw new ArchiveException($"压缩包里有 {archive.Entries.Count} 个文件，超过上限 {MaxEntries}", ArchiveFailure.TooManyEntries);
            }

            // .NET applies an explicit entryNameEncoding to every entry, UTF-8 flag or not, so the archive
            // is opened as UTF-8 first and re-opened as GBK only when some name did not decode: those are
            // the entries an older Windows tool wrote without the flag. Entry order is the central
            // directory's in both passes, so names can be taken by index.
            var names = archive.Entries.Select(e => e.FullName).ToArray();
            if (names.Any(n => n.Contains('�')))
            {
                archive.Dispose();
                zip.Position = 0;
                archive = Open(zip, LegacyNameEncoding);
                for (var i = 0; i < names.Length; i++)
                {
                    if (names[i].Contains('�'))
                    {
                        names[i] = archive.Entries[i].FullName;
                    }
                }
            }

            long total = 0;
            var candidates = new List<(string Name, ZipArchiveEntry Entry)>();
            var attachments = new List<string>();
            for (var i = 0; i < names.Length; i++)
            {
                var entry = archive.Entries[i];
                var name = ValidateName(names[i]);
                if (name.EndsWith('/'))
                {
                    continue;
                }

                total += entry.Length;
                if (total > MaxTotalBytes)
                {
                    throw new ArchiveException($"压缩包解压后超过 {MaxTotalBytes >> 20} MB", ArchiveFailure.TooLarge);
                }

                if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && entry.Length <= MaxEntryBytes)
                {
                    candidates.Add((name, entry));
                }
                else
                {
                    attachments.Add(name);
                }
            }

            return Select(candidates, attachments);
        }
        finally
        {
            archive.Dispose();
        }
    }

    private static ZipArchive Open(Stream zip, Encoding? encoding)
    {
        try
        {
            return new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true, encoding);
        }
        catch (InvalidDataException)
        {
            throw new ArchiveException("这不是一个 ZIP 文件，微信「转发到其他应用」给出的应该是压缩包；重新从微信转发一次", ArchiveFailure.NotZip);
        }
    }

    private static ArchiveTranscript Select(List<(string Name, ZipArchiveEntry Entry)> candidates, List<string> attachments)
    {
        if (candidates.Count == 0)
        {
            throw new ArchiveException($"压缩包里没有 {TranscriptFileName}，确认是从微信「转发到其他应用」导出的", ArchiveFailure.NoTranscript);
        }

        // The file WeChat itself names wins; the root-level one if there are several.
        var native = candidates
            .Where(c => LastComponent(c.Name) == TranscriptFileName)
            .OrderBy(c => c.Name.Contains('/') ? 1 : 0)
            .FirstOrDefault();
        if (native.Entry is not null)
        {
            var text = TranscriptParser.DecodeText(ReadEntry(native.Entry, native.Name));
            if (!TranscriptParser.TryParse(text, out var transcript, out var error))
            {
                throw new ArchiveException($"{native.Name} 的格式不是预期的：{error}", ArchiveFailure.NoTranscript);
            }

            return new ArchiveTranscript(native.Name, transcript!, Others(candidates, native.Name, attachments));
        }

        // Otherwise the .txt with the most messages; a tie keeps the first in directory order.
        (string Name, Transcript Transcript)? best = null;
        foreach (var candidate in candidates)
        {
            var text = TranscriptParser.DecodeText(ReadEntry(candidate.Entry, candidate.Name));
            if (TranscriptParser.TryParse(text, out var transcript, out _) && (best is null || transcript!.Messages.Count > best.Value.Transcript.Messages.Count))
            {
                best = (candidate.Name, transcript!);
            }
        }

        if (best is null)
        {
            throw new ArchiveException($"压缩包里的文本文件都不是微信聊天记录，确认是从微信「转发到其他应用」导出的", ArchiveFailure.NoTranscript);
        }

        return new ArchiveTranscript(best.Value.Name, best.Value.Transcript, Others(candidates, best.Value.Name, attachments));
    }

    private static IReadOnlyList<string> Others(List<(string Name, ZipArchiveEntry Entry)> candidates, string chosen, List<string> attachments)
    {
        var list = new List<string>(attachments);
        list.AddRange(candidates.Where(c => c.Name != chosen).Select(c => c.Name));
        return list;
    }

    private static string LastComponent(string name)
    {
        var slash = name.LastIndexOf('/');
        return slash < 0 ? name : name[(slash + 1)..];
    }

    /// <summary>Normalises separators and refuses anything that could escape the extraction directory or
    /// smuggle a control character into a file name. Returns the name with '/' separators.</summary>
    internal static string ValidateName(string fullName)
    {
        var name = fullName.Replace('\\', '/');
        if (name.Length == 0 || name.StartsWith('/') || name.Contains(':'))
        {
            throw Unsafe(fullName);
        }

        foreach (var ch in name)
        {
            if (ch < 0x20 || ch == 0x7F)
            {
                throw Unsafe(fullName);
            }
        }

        var trimmed = name.EndsWith('/') ? name[..^1] : name;
        foreach (var segment in trimmed.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                throw Unsafe(fullName);
            }
        }

        return name;
    }

    private static ArchiveException Unsafe(string fullName) =>
        new($"压缩包里有一个不安全的文件名「{fullName}」，已拒绝整个压缩包", ArchiveFailure.UnsafeEntryName);

    /// <summary>Reads one entry fully, capped at <see cref="MaxEntryBytes"/> by counting bytes actually
    /// produced (the central directory's size claim is not trusted), and verifies the CRC.</summary>
    private static byte[] ReadEntry(ZipArchiveEntry entry, string name)
    {
        Stream source;
        try
        {
            source = entry.Open();
        }
        catch (InvalidDataException)
        {
            throw new ArchiveException($"「{name}」加密了或用了不支持的压缩方式，微信导出的压缩包不会这样；重新从微信转发一次", ArchiveFailure.Encrypted);
        }

        using (source)
        {
            var buffer = new byte[64 * 1024];
            using var output = new MemoryStream();
            var crc = Crc32.Initial;
            long produced = 0;
            try
            {
                int n;
                while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    produced += n;
                    if (produced > MaxEntryBytes)
                    {
                        throw new ArchiveException($"「{name}」超过 {MaxEntryBytes >> 20} MB，不是聊天记录该有的大小", ArchiveFailure.EntryTooLarge);
                    }

                    output.Write(buffer, 0, n);
                    crc = Crc32.Update(crc, buffer.AsSpan(0, n));
                }
            }
            catch (InvalidDataException)
            {
                throw new ArchiveException($"「{name}」的压缩数据损坏，重新从微信转发一次", ArchiveFailure.Corrupt);
            }

            if (Crc32.Finish(crc) != entry.Crc32)
            {
                throw new ArchiveException($"「{name}」校验失败（文件不完整或被改动），重新从微信转发一次", ArchiveFailure.Corrupt);
            }

            return output.ToArray();
        }
    }
}
