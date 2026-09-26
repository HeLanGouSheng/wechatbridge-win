using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Bridge.App.Delivery;
using Bridge.Core.Archives;
using Bridge.Core.Batches;
using Bridge.Core.Delivery;
using Bridge.Core.Naming;
using Bridge.Core.Records;

namespace Bridge.App.Views;

/// <summary>What arrived, and what happened to it. With llmsocial mode on it delivers by itself and
/// closes a few seconds after success; otherwise the buttons decide.</summary>
public partial class ResultWindow : Window
{
    private const int AutoCloseSeconds = 6;

    private readonly ReadyBatch _batch;
    private readonly DeliveryFlow _flow;
    private readonly string? _settingsProblem;
    private BatchOutcome? _outcome;
    private DispatcherTimer? _closeTimer;
    private int _secondsLeft;

    public ResultWindow(ReadyBatch batch, DeliveryFlow flow, string? settingsProblem = null)
    {
        _batch = batch;
        _flow = flow;
        _settingsProblem = settingsProblem;
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
            var fileName = Path.GetFileName(path);
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
        Detail.Text = $"{source}\n{string.Join("\n", notes)}";
        Messages.ItemsSource = rows;

        if (_settingsProblem is not null)
        {
            ShowDelivery($"设置文件有问题：{_settingsProblem}", ok: false);
        }
        else if (!_flow.LlmSocialReady)
        {
            ShowDelivery(_flow.LlmSocialProblem!, ok: false);
        }

        SendButton.IsEnabled = _flow.LlmSocialReady && total > 0;
        CopyButton.IsEnabled = total > 0;
    }

    /// <summary>llmsocial mode: called right after Show(), so the window is on screen while the requests run.</summary>
    public async Task DeliverAutomaticallyAsync()
    {
        await SendAsync();
        if (_outcome is { Succeeded: true })
        {
            StartAutoClose();
        }
    }

    private async Task SendAsync()
    {
        SendButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        ShowDelivery("正在发给 llmsocial…", ok: true);
        try
        {
            _outcome = await _flow.DeliverAsync(_batch, request => ChatNameDialog.AskAsync(this, request));
            ShowOutcome(_outcome);
        }
        catch (Exception ex)
        {
            ShowDelivery($"没发出去：{ex.Message}", ok: false);
            SendButton.IsEnabled = true;
            CopyButton.IsEnabled = true;
        }
    }

    private void ShowOutcome(BatchOutcome outcome)
    {
        var lines = outcome.Files.Select(f =>
        {
            var who = f.ChatName is null ? "" : $"「{f.ChatName}」";
            return f.Status switch
            {
                RecordStatus.Sent => $"✔ {who}{f.Detail}",
                RecordStatus.Copied => $"✔ {who}{f.Detail}",
                RecordStatus.Partial => $"◐ {who}{f.Detail}",
                RecordStatus.Empty => $"— {who}{f.Detail}",
                _ => $"✖ {who}{f.Detail}",
            };
        });
        ShowDelivery(string.Join("\n", lines), ok: outcome.Succeeded);
        Headline.Text = outcome.Succeeded
            ? (outcome.Target == "clipboard" ? "已复制到剪贴板" : $"已发给 llmsocial：{outcome.Sent} 条")
            : (outcome.Target == "clipboard" ? "复制失败" : "没有发给 llmsocial");
        if (!outcome.Succeeded)
        {
            Detail.Text += "\n这批文件留在 failed 目录里，主窗口的记录页可以重试。";
        }

        SendButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
    }

    private void ShowDelivery(string text, bool ok)
    {
        DeliveryPanel.Visibility = Visibility.Visible;
        DeliveryPanel.Background = ok ? new SolidColorBrush(Color.FromRgb(0xF3, 0xF7, 0xF3)) : new SolidColorBrush(Color.FromRgb(0xFB, 0xF1, 0xF1));
        DeliveryPanel.BorderBrush = ok ? new SolidColorBrush(Color.FromRgb(0xCF, 0xE3, 0xCF)) : new SolidColorBrush(Color.FromRgb(0xE8, 0xC4, 0xC4));
        DeliveryText.Text = text;
    }

    private void StartAutoClose()
    {
        _secondsLeft = AutoCloseSeconds;
        CloseButton.Content = $"关闭 ({_secondsLeft})";
        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _closeTimer.Tick += (_, _) =>
        {
            _secondsLeft--;
            if (_secondsLeft <= 0)
            {
                _closeTimer.Stop();
                Close();
                return;
            }

            CloseButton.Content = $"关闭 ({_secondsLeft})";
        };
        _closeTimer.Start();
        MouseMove += (_, _) => StopAutoClose();
        PreviewKeyDown += (_, _) => StopAutoClose();
    }

    private void StopAutoClose()
    {
        if (_closeTimer is null)
        {
            return;
        }

        _closeTimer.Stop();
        _closeTimer = null;
        CloseButton.Content = "关闭";
    }

    private string CurrentDirectory()
    {
        if (Directory.Exists(_batch.Directory))
        {
            return _batch.Directory;
        }

        var inbox = new BatchInbox(AppPaths.InboxRoot);
        foreach (var parent in new[] { inbox.Done, inbox.Failed })
        {
            var candidate = Path.Combine(parent, _batch.Id.Value);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return inbox.Root;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CurrentDirectory()}\"") { UseShellExecute = true });
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        StopAutoClose();
        await SendAsync();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        StopAutoClose();
        try
        {
            _outcome = _flow.CopyToClipboard(_batch);
            ShowOutcome(_outcome);
        }
        catch (Exception ex)
        {
            ShowDelivery($"没复制成：{ex.Message}", ok: false);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
