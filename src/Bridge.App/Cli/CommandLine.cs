using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using Bridge.App.Delivery;
using Bridge.App.Identity;
using Bridge.App.Share;
using Bridge.Core;
using Bridge.Core.Batches;
using Bridge.Core.Config;
using Bridge.Core.Delivery;
using Bridge.Core.LlmSocial;

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
                "--status" when args.Contains("--json", StringComparer.Ordinal) => StatusJson(log),
                "--status" => Registration.Status(log),
                "--trust-cert" => Registration.TrustCertificate(log),
                "--identity" => Identity(log),
                "--parse" when args.Length > 1 => Parse(args[1], log),
                "--configure" => Configure(args, log),
                "--test-connection" => Task.Run(() => TestConnectionAsync(log)).GetAwaiter().GetResult(),
                "--send" when args.Length > 1 => Task.Run(() => SendAsync(args, log)).GetAwaiter().GetResult(),
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

    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The same facts as --status, for programs (llmsocial's installer) rather than people. The
    /// secret itself is never part of it.</summary>
    private static int StatusJson(TextWriter log)
    {
        var info = Registration.Inspect();
        object? settings = null;
        string? settingsError = null;
        try
        {
            var loaded = DeliveryFlow.Store.Load();
            settings = new
            {
                configured = loaded.LlmSocial.IsFilledIn,
                baseUrl = loaded.LlmSocial.BaseUrl,
                accountId = loaded.LlmSocial.AccountId,
                autoDeliver = loaded.LlmSocialMode,
                myNames = loaded.MyNames,
            };
        }
        catch (SettingsException ex)
        {
            settingsError = ex.Message;
        }

        var payload = new
        {
            version = typeof(CommandLine).Assembly.GetName().Version?.ToString(3),
            installDirectory = info.InstallDirectory,
            dataDirectory = info.DataDirectory,
            identity = info.Identity,
            packageFile = info.PackageFilePresent,
            packageFileVersion = info.PackageFileVersion,
            certificateFile = info.CertificateFilePresent,
            certificateTrusted = info.CertificateTrusted,
            registered = info.Registered,
            packageFullName = info.PackageFullName,
            packageVersion = info.PackageVersion,
            externalLocation = info.ExternalLocation,
            externalLocationMatches = info.ExternalLocationMatches,
            registeredUpToDate = info.RegisteredUpToDate,
            settings,
            settingsError,
        };
        log.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        return 0;
    }

    /// <summary>What the parser makes of a ZIP, without touching the inbox — for checking an export by
    /// hand and for bug reports.</summary>
    private static int Parse(string zipPath, TextWriter log)
    {
        if (!File.Exists(zipPath))
        {
            log.WriteLine($"找不到文件 {zipPath}");
            return 1;
        }

        try
        {
            var archive = Bridge.Core.Archives.NativeArchive.ReadTranscript(zipPath);
            var chat = Bridge.Core.Naming.DisplayName.ChatNameFromArchiveName(Path.GetFileName(zipPath));
            log.WriteLine($"{archive.EntryName}：{archive.Transcript.Messages.Count} 条消息，发送者 {string.Join("、", archive.Transcript.Senders)}" +
                          (chat is null ? "" : $"，文件名里的聊天名「{chat}」") +
                          (archive.AttachmentNames.Count == 0 ? "" : $"，附件 {archive.AttachmentNames.Count} 个"));
            foreach (var m in archive.Transcript.Messages)
            {
                log.WriteLine($"{m.SentAt:yyyy-MM-dd HH:mm}  {m.Sender}：{m.Text.Replace("\n", " ⏎ ")}");
            }

            return 0;
        }
        catch (Bridge.Core.Archives.ArchiveException ex)
        {
            log.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary><c>--configure --account-id … --secret … [--base-url …] [--my-names 甲,乙] [--auto on|off]</c>:
    /// what llmsocial's installer runs; options left out keep their current value.</summary>
    private static int Configure(string[] args, TextWriter log)
    {
        var options = Options(args);
        var store = DeliveryFlow.Store;
        var current = store.Load();
        // llmsocial hands the secret over in the environment so it never shows up in a process listing.
        var secret = options.TryGetValue("secret", out var s) ? s
            : Environment.GetEnvironmentVariable("CHATBRIDGE_SECRET") is { Length: > 0 } fromEnv ? fromEnv
            : TrySecret(store, current);
        var llm = current.LlmSocial with
        {
            BaseUrl = options.TryGetValue("base-url", out var url) ? url.Trim() : current.LlmSocial.BaseUrl,
            AccountId = options.TryGetValue("account-id", out var acct) ? acct.Trim() : current.LlmSocial.AccountId,
        };
        var names = options.TryGetValue("my-names", out var raw)
            ? raw.Split(new[] { ',', '，', '、', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray()
            : current.MyNames;
        var auto = options.TryGetValue("auto", out var a) ? a is "on" or "true" or "1" : current.LlmSocialMode;

        var problem = LlmSocialConfig.Validate(llm.BaseUrl, llm.AccountId, secret);
        if (problem is not null)
        {
            log.WriteLine(problem);
            return 1;
        }

        var settings = current with { LlmSocial = llm, MyNames = names, LlmSocialMode = auto };
        store.Save(store.WithSecret(settings, secret));
        log.WriteLine($"已保存到 {store.Path}");
        log.WriteLine($"  llmsocial 地址：{llm.BaseUrl}");
        log.WriteLine($"  账号 ID：{llm.AccountId}");
        log.WriteLine($"  我的昵称：{(names.Count == 0 ? "（未填）" : string.Join("、", names))}");
        log.WriteLine($"  收到转发后直接发送：{(auto ? "开" : "关")}");
        return 0;
    }

    private static string TrySecret(SettingsStore store, Settings settings)
    {
        try
        {
            return store.Secret(settings);
        }
        catch (SettingsException)
        {
            return "";
        }
    }

    private static async Task<int> TestConnectionAsync(TextWriter log)
    {
        var flow = DeliveryFlow.Load();
        if (!flow.LlmSocialReady)
        {
            log.WriteLine(flow.LlmSocialProblem);
            return 1;
        }

        var client = new LlmSocialClient(DeliveryFlow.CreateHttpClient(flow.Config.BaseUrl), flow.Config);
        var result = await client.TestAsync();
        log.WriteLine(result.Detail);
        return result.Ok ? 0 : 1;
    }

    /// <summary><c>--send 文件.zip [--chat-name 群名]</c>: the whole llmsocial path without WeChat or a window.</summary>
    private static async Task<int> SendAsync(string[] args, TextWriter log)
    {
        var zipPath = args[1];
        if (!File.Exists(zipPath))
        {
            log.WriteLine($"找不到文件 {zipPath}");
            return 1;
        }

        var flow = DeliveryFlow.Load();
        if (!flow.LlmSocialReady)
        {
            log.WriteLine(flow.LlmSocialProblem);
            return 1;
        }

        var options = Options(args.Skip(1).ToArray());
        options.TryGetValue("chat-name", out var chatName);
        var batch = LocalIntake.Import(zipPath, new BatchInbox(AppPaths.InboxRoot), TimeProvider.System);
        var outcome = await flow.DeliverAsync(batch, request =>
        {
            if (chatName is null)
            {
                log.WriteLine($"这是群聊（{string.Join("、", request.Senders)}），需要加 --chat-name 群名");
                return Task.FromResult<ChatNameAnswer?>(null);
            }

            return Task.FromResult<ChatNameAnswer?>(new ChatNameAnswer(chatName, true));
        });

        log.WriteLine($"批次 {batch.Id.Value}");
        foreach (var file in outcome.Files)
        {
            log.WriteLine($"  {file.FileName}：{file.Status}，{file.Detail}");
        }

        return outcome.Succeeded ? 0 : 1;
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[key] = args[++i];
            }
            else
            {
                options[key] = "";
            }
        }

        return options;
    }

    private static int Help(TextWriter log)
    {
        log.WriteLine($"{BridgeIdentity.ProductName} {typeof(CommandLine).Assembly.GetName().Version}");
        log.WriteLine("  WeChatBridge.exe --register     注册到 Windows 共享目标（出现在微信「选择电脑中的应用」里）");
        log.WriteLine("  WeChatBridge.exe --unregister   解除注册");
        log.WriteLine("  WeChatBridge.exe --status       身份、注册和证书状态（加 --json 给程序读）");
        log.WriteLine("  WeChatBridge.exe --identity     只打印本进程的包身份");
        log.WriteLine("  WeChatBridge.exe --trust-cert   （管理员）把自签证书放进这台电脑的「受信任人」；--register 会自动调用");
        log.WriteLine("  WeChatBridge.exe --configure --account-id acct_… --secret … [--base-url http://127.0.0.1:8788] [--my-names 甲,乙] [--auto on|off]");
        log.WriteLine("                                  写 llmsocial 设置（给安装脚本用；没给的项保持不变；密钥也可以放在环境变量 CHATBRIDGE_SECRET 里）");
        log.WriteLine("  WeChatBridge.exe --test-connection  用当前设置连一次 llmsocial");
        log.WriteLine("  WeChatBridge.exe --send <文件.zip> [--chat-name 群名]  把一个导出的压缩包发给 llmsocial");
        log.WriteLine("  WeChatBridge.exe <文件.zip>      不经微信，直接处理一个导出的压缩包（弹窗口）");
        log.WriteLine("  WeChatBridge.exe --parse <文件.zip>  只解析、打印消息，不进收件箱");
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
