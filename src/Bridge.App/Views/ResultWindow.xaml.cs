using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Bridge.Core.Archives;
using Bridge.Core.Batches;
using Bridge.Core.Naming;

namespace Bridge.App.Views;

/// <summary>M0's proof window: what arrived, parsed. Later milestones deliver instead of just showing.</summary>
public partial class ResultWindow : Window
{
    private readonly ReadyBatch _batch;

    public ResultWindow(ReadyBatch batch)
    {
        _batch = batch;
        InitializeComponent();
        Title = Bridge.Core.BridgeIdentity.ProductName;
        Load();
    }

    /// <summary>Bound by the ListView columns; public properties are what the binding engine reads.</summary>
    internal sealed record Row(string Time, string Sender, string Text);

    private void Load()
    {
        var rows = new List<Row>();
        var notes = new List<string>();
        var total = 0;
        foreach (var path in _batch.FilePaths)
        {
            var fileName = System.IO.Path.GetFileName(path);
            try
            {
                var archive = NativeArchive.ReadTranscript(path);
                total += archive.Transcript.Messages.Count;
                var chat = DisplayName.ChatNameFromArchiveName(fileName);
                notes.Add($"{fileName}：{archive.Transcript.Messages.Count} 条，{archive.Transcript.Senders.Count} 个发送者" +
                          (chat is null ? "" : $"，文件名里的聊天名「{chat}」") +
                          (archive.AttachmentNames.Count == 0 ? "" : $"，{archive.AttachmentNames.Count} 个附件"));
                foreach (var m in archive.Transcript.Messages)
                {
                    rows.Add(new Row(m.SentAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), m.Sender, m.Text.Replace("\n", " ⏎ ")));
                }
            }
            catch (ArchiveException ex)
            {
                notes.Add($"{fileName}：{ex.Message}");
            }
        }

        Headline.Text = total == 0 ? "没有读出聊天记录" : $"收到 {total} 条消息";
        var source = _batch.Manifest.Source == BatchManifest.SourceShare ? "来自微信「转发到其他应用」" : "来自本地文件";
        var share = _batch.Manifest.ShareTitle is null ? "" : $"；分享标题「{_batch.Manifest.ShareTitle}」";
        var app = _batch.Manifest.ShareAppName is null ? "" : $"；来源应用「{_batch.Manifest.ShareAppName}」";
        Detail.Text = $"{source}{share}{app}\n{string.Join("\n", notes)}";
        Messages.ItemsSource = rows;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_batch.Directory}\"") { UseShellExecute = true });
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
