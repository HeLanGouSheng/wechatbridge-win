using System.Windows;
using Bridge.App.Delivery;
using Bridge.App.Share;
using Bridge.App.Views;
using Bridge.Core;
using Bridge.Core.Batches;
using Bridge.Core.Config;

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

            if (batch is null)
            {
                var main = new MainWindow();
                main.Closed += (_, _) => Shutdown();
                main.Show();
                main.Activate();
                return;
            }

            await ShowBatchAsync(batch, closeAppWhenDone: true);
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

    /// <summary>Shows a received batch; in llmsocial mode it is delivered while the window is up.
    /// The returned task completes when delivery (if any) has finished, not when the window closes.</summary>
    public static async Task ShowBatchAsync(ReadyBatch batch, bool closeAppWhenDone)
    {
        DeliveryFlow flow;
        string? settingsProblem = null;
        try
        {
            flow = DeliveryFlow.Load();
        }
        catch (SettingsException ex)
        {
            settingsProblem = ex.Message;
            flow = DeliveryFlow.Unconfigured(ex.Message);
        }

        var window = new ResultWindow(batch, flow, settingsProblem);
        if (closeAppWhenDone)
        {
            window.Closed += (_, _) => Current.Shutdown();
        }

        window.Show();
        window.Activate();
        if (flow.AutoDeliver)
        {
            await window.DeliverAutomaticallyAsync();
        }
    }
}
