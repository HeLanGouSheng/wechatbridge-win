using System.Windows;
using Bridge.App.Share;
using Bridge.App.Views;
using Bridge.Core;
using Bridge.Core.Batches;

namespace Bridge.App;

public partial class App : Application
{
    private readonly LaunchMode _mode;

    /// <summary>Only for the XAML-generated entry point, which Program.Main replaces (StartupObject).</summary>
    public App()
        : this(new LaunchMode.Window())
    {
    }

    public App(LaunchMode mode)
    {
        _mode = mode;
    }

    /// <summary>Async on purpose: from here every await resumes on the STA thread, which the WinRT share
    /// operation needs. Nothing here blocks the thread.</summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var inbox = new BatchInbox(AppPaths.InboxRoot);
            ReadyBatch? batch = _mode switch
            {
                LaunchMode.Share share => await ShareIntake.ReceiveAsync(share.Operation, inbox, TimeProvider.System),
                LaunchMode.LocalZip local => LocalIntake.Import(local.Path, inbox, TimeProvider.System),
                _ => null,
            };

            Window window = batch is null ? new MainWindow() : new ResultWindow(batch);
            window.Closed += (_, _) => Shutdown();
            window.Show();
            window.Activate();
        }
        catch (ShareIntakeException ex)
        {
            MessageBox.Show(ex.Message, BridgeIdentity.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"出错了：{ex.Message}", BridgeIdentity.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
