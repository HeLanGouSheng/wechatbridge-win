using System.Diagnostics;
using System.IO;
using System.Windows;
using Bridge.App.Delivery;
using Bridge.App.Identity;
using Bridge.App.Share;
using Bridge.Core.Batches;
using Bridge.Core.Config;
using Bridge.Core.Records;

namespace Bridge.App.Views;

/// <summary>The window a plain double-click opens: what has been forwarded and where it went, the
/// llmsocial settings, and the registration into the WeChat menu.</summary>
public partial class MainWindow : Window
{
    private readonly BatchInbox _inbox = new(AppPaths.InboxRoot);
    private DeliveryFlow? _flow;
    private string? _settingsProblem;

    public MainWindow()
    {
        InitializeComponent();
        Title = Bridge.Core.BridgeIdentity.ProductName;
        Heading.Text = Bridge.Core.BridgeIdentity.ProductName;
        ReloadFlow();
        RefreshRecords();
        try
        {
            _inbox.Purge(TimeSpan.FromDays(_flow?.Settings.HistoryDays ?? 7), TimeProvider.System);
        }
        catch (IOException)
        {
            // A batch in use by another process; next launch sweeps it.
        }
    }

    /// <summary>Bound by the ListView columns.</summary>
    internal sealed record RecordRow(string Time, string Chat, int Count, string Target, string Status, string Detail, string BatchId, bool CanRetry);

    private void ReloadFlow()
    {
        try
        {
            _flow = DeliveryFlow.Load();
            _settingsProblem = null;
        }
        catch (SettingsException ex)
        {
            _flow = null;
            _settingsProblem = ex.Message;
        }

        ConfigLine.Text = _settingsProblem is not null
            ? $"设置文件有问题：{_settingsProblem}"
            : _flow!.LlmSocialReady
                ? $"llmsocial：{_flow.Settings.LlmSocial.BaseUrl} · 账号 {_flow.Settings.LlmSocial.AccountId} · " +
                  (_flow.AutoDeliver ? "收到转发后直接发送" : "收到转发后先弹窗确认") +
                  (_flow.Settings.MyNames.Count == 0 ? " · 还没填「我的昵称」，你发的消息会被当成对方的" : $" · 我的昵称：{string.Join("、", _flow.Settings.MyNames)}")
                : $"llmsocial：{_flow.LlmSocialProblem}";
    }

    private void RefreshRecords()
    {
        var failedIds = new HashSet<string>(_inbox.ListFailed().Select(b => b.Id.Value), StringComparer.Ordinal);
        var rows = new RecordsLog(AppPaths.RecordsFile).ReadAll().Select(r => new RecordRow(
            r.At.ToLocalTime().ToString("MM-dd HH:mm"),
            r.ChatName ?? "—",
            r.MessageCount,
            r.Target == "clipboard" ? "剪贴板" : r.Target,
            StatusLabel(r.Status),
            r.Detail,
            r.BatchId,
            failedIds.Contains(r.BatchId))).ToList();
        Records.ItemsSource = rows;
        RetryButton.IsEnabled = false;
    }

    private static string StatusLabel(string status) => status switch
    {
        RecordStatus.Sent => "已发送",
        RecordStatus.Partial => "部分发送",
        RecordStatus.Failed => "失败",
        RecordStatus.Copied => "已复制",
        RecordStatus.Empty => "无可发内容",
        RecordStatus.Cancelled => "未发送",
        _ => status,
    };

    private void Records_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RetryButton.IsEnabled = Records.SelectedItem is RecordRow { CanRetry: true } && _flow is { LlmSocialReady: true };
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (Records.SelectedItem is not RecordRow row || _flow is null)
        {
            return;
        }

        var batch = _inbox.Requeue(new BatchId(row.BatchId));
        if (batch is null)
        {
            MessageBox.Show("这批文件已经不在了（可能被清理）。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshRecords();
            return;
        }

        RetryButton.IsEnabled = false;
        try
        {
            var outcome = await _flow.DeliverAsync(batch, request => ChatNameDialog.AskAsync(this, request));
            if (!outcome.Succeeded)
            {
                MessageBox.Show(string.Join("\n", outcome.Files.Select(f => f.Detail)), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"重试出错：{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        RefreshRecords();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow { Owner = this };
        dialog.ShowDialog();
        ReloadFlow();
        RefreshRecords();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        ReloadFlow();
        RefreshRecords();
    }

    private void RegistrationExpanded(object sender, RoutedEventArgs e) => RefreshStatus();

    private void RefreshStatus()
    {
        var writer = new StringWriter();
        Registration.Status(writer);
        StatusText.Text = writer.ToString();
    }

    private async void Register_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(Registration.RegisterAsync);
    }

    private async void Unregister_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(Registration.UnregisterAsync);
    }

    private async Task RunAsync(Func<TextWriter, Task<int>> command)
    {
        IsEnabled = false;
        var writer = new StringWriter();
        try
        {
            await Task.Run(() => command(writer));
        }
        catch (Exception ex)
        {
            writer.WriteLine($"出错了：{ex.Message}");
        }
        finally
        {
            IsEnabled = true;
        }

        var status = new StringWriter();
        Registration.Status(status);
        StatusText.Text = writer + "\n" + status;
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataRoot}\"") { UseShellExecute = true });
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        foreach (var file in files.Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
        {
            var batch = LocalIntake.Import(file, _inbox, TimeProvider.System);
            await App.ShowBatchAsync(batch, closeAppWhenDone: false);
            RefreshRecords();
        }
    }
}
