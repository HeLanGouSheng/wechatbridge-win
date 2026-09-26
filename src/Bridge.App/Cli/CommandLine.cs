using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Bridge.App.Identity;
using Bridge.Core;

namespace Bridge.App.Cli;

/// <summary>
/// The <c>--</c> commands. A WinExe has no console of its own, so the parent's console is attached when
/// there is one (running from a terminal or from llmsocial's installer) and a message box carries the
/// summary otherwise. Exit code 0 means done, 1 means the text says what to do next.
/// </summary>
public static class CommandLine
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    public static int Run(string[] args)
    {
        var quiet = args.Contains("--quiet", StringComparer.Ordinal);
        var output = new StringWriter();
        var console = OpenConsole();
        var log = console is null ? (TextWriter)output : new TeeWriter(console, output);

        int code;
        try
        {
            code = args[0] switch
            {
                "--register" => Task.Run(() => Registration.RegisterAsync(log)).GetAwaiter().GetResult(),
                "--unregister" => Task.Run(() => Registration.UnregisterAsync(log)).GetAwaiter().GetResult(),
                "--status" => Registration.Status(log),
                "--trust-cert" => Registration.TrustCertificate(log),
                "--identity" => Identity(log),
                "--help" or "-h" or "/?" => Help(log),
                _ => Unknown(args[0], log),
            };
        }
        catch (Exception ex)
        {
            log.WriteLine($"出错了：{ex.Message}");
            code = 1;
        }

        if (!quiet && console is null)
        {
            MessageBox.Show(output.ToString(), BridgeIdentity.ProductName, MessageBoxButton.OK, code == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        return code;
    }

    private static int Identity(TextWriter log)
    {
        log.WriteLine(PackageIdentity.FullName() ?? "无身份");
        return 0;
    }

    private static int Help(TextWriter log)
    {
        log.WriteLine($"{BridgeIdentity.ProductName} {typeof(CommandLine).Assembly.GetName().Version}");
        log.WriteLine("  WeChatBridge.exe --register     注册到 Windows 共享目标（出现在微信「选择电脑中的应用」里）");
        log.WriteLine("  WeChatBridge.exe --unregister   解除注册");
        log.WriteLine("  WeChatBridge.exe --status       身份、注册和证书状态");
        log.WriteLine("  WeChatBridge.exe --identity     只打印本进程的包身份");
        log.WriteLine("  WeChatBridge.exe --trust-cert   （管理员）把自签证书放进这台电脑的「受信任人」；--register 会自动调用");
        log.WriteLine("  WeChatBridge.exe <文件.zip>      不经微信，直接处理一个导出的压缩包");
        log.WriteLine("  加 --quiet 不弹结果窗口（给安装脚本用）");
        return 0;
    }

    private static int Unknown(string command, TextWriter log)
    {
        log.WriteLine($"不认识的命令 {command}，运行 --help 看用法。");
        return 1;
    }

    /// <summary>A terminal parent gets its console attached; a parent that redirected stdout (an
    /// installer, a script) already handed this process a pipe handle, which needs no console at all.
    /// Neither: null, and the summary goes to a message box.</summary>
    private static TextWriter? OpenConsole()
    {
        var attached = AttachConsole(AttachParentProcess);
        var stdout = Console.OpenStandardOutput();
        if (ReferenceEquals(stdout, Stream.Null))
        {
            return null;
        }

        var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };
        if (attached)
        {
            writer.WriteLine();
        }

        return writer;
    }

    private sealed class TeeWriter(TextWriter a, TextWriter b) : TextWriter
    {
        public override Encoding Encoding => a.Encoding;

        public override void Write(char value)
        {
            a.Write(value);
            b.Write(value);
        }

        public override void Write(string? value)
        {
            a.Write(value);
            b.Write(value);
        }

        public override void WriteLine(string? value)
        {
            a.WriteLine(value);
            b.WriteLine(value);
        }
    }
}
