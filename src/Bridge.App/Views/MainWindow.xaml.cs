using System.Diagnostics;
using System.IO;
using System.Windows;
using Bridge.App.Identity;
using Bridge.App.Share;
using Bridge.Core.Batches;

namespace Bridge.App.Views;

/// <summary>M0's plain launch window: registration status and the buttons to change it. M1 turns this
/// into the records and settings window.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = Bridge.Core.BridgeIdentity.ProductName;
        Heading.Text = Bridge.Core.BridgeIdentity.ProductName;
        RefreshStatus();
    }

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

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        foreach (var file in files.Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
        {
            var batch = LocalIntake.Import(file, new BatchInbox(AppPaths.InboxRoot), TimeProvider.System);
            new ResultWindow(batch).Show();
        }
    }
}
