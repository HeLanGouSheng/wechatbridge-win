using Bridge.App.Cli;

namespace Bridge.App;

public static class Program
{
    /// <summary>
    /// Three ways in: a <c>--command</c> (register, status, …) that never touches WPF; a share activation
    /// from WeChat, detected through the package-identity activation args; or a plain launch, optionally
    /// with a .zip path for testing without WeChat. Everything after detection runs inside the WPF
    /// application so awaits land back on the STA thread.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
        {
            return CommandLine.Run(args);
        }

        var mode = LaunchMode.Detect(args);
        var app = new App(mode);
        app.InitializeComponent();
        return app.Run();
    }
}
