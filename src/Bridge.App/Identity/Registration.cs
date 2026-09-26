using System.IO;
using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Bridge.Core;
using Windows.Management.Deployment;

namespace Bridge.App.Identity;

/// <summary>
/// Registers the identity package that makes this exe a Windows share target. Runs without admin
/// rights: the self-signed certificate goes into the current user's Trusted People store and the
/// package is registered per user with the exe's own folder as the external location.
/// </summary>
public static class Registration
{
    private const int ErrorPackagesInUse = unchecked((int)0x80073D02);
    private const int ErrorInstallFailed = unchecked((int)0x80073CF9);
    private const int ErrorAlreadyExists = unchecked((int)0x80073CFB);

    public static string PackagePath => Path.Combine(AppPaths.InstallDirectory, BridgeIdentity.PackageFileName);

    public static string CertificatePath => Path.Combine(AppPaths.InstallDirectory, BridgeIdentity.CertificateFileName);

    public static async Task<int> RegisterAsync(TextWriter log)
    {
        if (!File.Exists(PackagePath))
        {
            log.WriteLine($"找不到 {PackagePath}。先运行 scripts\\publish.ps1，它会把 msix 和证书放到 exe 旁边。");
            return 1;
        }

        if (!File.Exists(CertificatePath))
        {
            log.WriteLine($"找不到 {CertificatePath}。msix 和 .cer 必须来自同一次 build-package.ps1。");
            return 1;
        }

        var mismatch = CheckManifest(PackagePath);
        if (mismatch is not null)
        {
            log.WriteLine(mismatch);
            return 1;
        }

        if (!EnsureCertificateTrusted(CertificatePath, log))
        {
            return 1;
        }

        var manager = new PackageManager();
        var options = new AddPackageOptions
        {
            ExternalLocationUri = new Uri(AppPaths.InstallDirectory.TrimEnd('\\') + "\\"),
            // The dev loop re-registers the same version after every build; without this it is 0x80073CF9.
            ForceUpdateFromAnyVersion = true,
        };

        log.WriteLine($"正在注册 {BridgeIdentity.PackageName}，外部位置 {AppPaths.InstallDirectory} …");
        var result = await manager.AddPackageByUriAsync(new Uri(PackagePath), options);

        if (result.ExtendedErrorCode is { HResult: ErrorPackagesInUse })
        {
            log.WriteLine("微信桥正在运行；注册会在它退出后生效。");
            options.DeferRegistrationWhenPackagesAreInUse = true;
            result = await manager.AddPackageByUriAsync(new Uri(PackagePath), options);
        }
        else if (result.ExtendedErrorCode is { HResult: ErrorInstallFailed or ErrorAlreadyExists })
        {
            // Not removing first by default: this very process may be running with identity, and removing
            // the package would end it mid-command. Only fall back to removal when the update is refused.
            log.WriteLine("同版本已注册且无法就地更新，先解除再注册 …");
            await RemoveExistingAsync(manager, log);
            result = await manager.AddPackageByUriAsync(new Uri(PackagePath), options);
        }

        if (result.ExtendedErrorCode is not null && result.ExtendedErrorCode.HResult != 0)
        {
            log.WriteLine(Explain(result));
            return 1;
        }

        log.WriteLine("已注册。微信 → 多选消息 → 转发 → 转发到其他应用 → 选择电脑中的应用，里面应该有「微信桥」。");
        log.WriteLine("没有的话先把微信整个退出再打开一次，它会重新读一遍系统里的共享目标。");
        return 0;
    }

    public static async Task<int> UnregisterAsync(TextWriter log)
    {
        var manager = new PackageManager();
        var removed = await RemoveExistingAsync(manager, log);
        log.WriteLine(removed == 0 ? "没有找到已注册的微信桥。" : $"已解除注册 {removed} 个。");
        return 0;
    }

    /// <summary>Removes every registration of our package name for this user, whatever certificate signed
    /// it, so a registration left behind by an older dev certificate cannot shadow the new one.</summary>
    private static async Task<int> RemoveExistingAsync(PackageManager manager, TextWriter log)
    {
        var removed = 0;
        foreach (var package in manager.FindPackagesForUser(string.Empty))
        {
            if (!string.Equals(package.Id.Name, BridgeIdentity.PackageName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Say it before doing it: if this process has identity it may not survive the removal.
            log.WriteLine($"正在解除注册 {package.Id.FullName} …");
            var result = await manager.RemovePackageAsync(package.Id.FullName);
            if (result.ExtendedErrorCode is not null && result.ExtendedErrorCode.HResult != 0)
            {
                log.WriteLine(Explain(result));
            }
            else
            {
                removed++;
            }
        }

        return removed;
    }

    public static int Status(TextWriter log)
    {
        log.WriteLine($"程序目录：{AppPaths.InstallDirectory}");
        log.WriteLine($"数据目录：{AppPaths.DataRoot}");
        log.WriteLine($"本进程身份：{PackageIdentity.FullName() ?? "无（不是从注册过的目录启动，或还没注册）"}");
        log.WriteLine($"msix：{(File.Exists(PackagePath) ? "在" : "缺失")}    证书：{(File.Exists(CertificatePath) ? "在" : "缺失")}");

        if (File.Exists(CertificatePath))
        {
            var cert = new X509Certificate2(CertificatePath);
            log.WriteLine($"证书 {cert.Subject}（{cert.Thumbprint[..8]}…）{(IsTrusted(cert) ? "已在这台电脑的「受信任人」里" : "还没导入这台电脑的「受信任人」（--register 会做，需要管理员确认一次）")}");
        }

        var manager = new PackageManager();
        var any = false;
        foreach (var package in manager.FindPackagesForUser(string.Empty))
        {
            if (!string.Equals(package.Id.Name, BridgeIdentity.PackageName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            any = true;
            var external = package.EffectiveExternalPath;
            var matches = string.Equals(Path.TrimEndingDirectorySeparator(external), Path.TrimEndingDirectorySeparator(AppPaths.InstallDirectory), StringComparison.OrdinalIgnoreCase);
            log.WriteLine($"已注册：{package.Id.FullName}");
            log.WriteLine($"  注册的外部位置：{external}{(matches ? "" : "  ← 和程序目录不一样！程序目录移动过，重新运行 --register")}");
        }

        if (!any)
        {
            log.WriteLine("未注册：运行 WeChatBridge.exe --register");
        }

        return 0;
    }

    /// <summary>
    /// The deployment service validates the package signature against the machine's Trusted People
    /// store; the current user's store is not consulted (tried: 0x800B0109). Writing to the machine
    /// store needs elevation, so this process re-launches itself once with <c>--trust-cert</c> behind a
    /// UAC prompt. A production certificate from a real CA makes this step unnecessary.
    /// </summary>
    private static bool EnsureCertificateTrusted(string cerPath, TextWriter log)
    {
        var cert = new X509Certificate2(cerPath);
        if (IsTrusted(cert))
        {
            return true;
        }

        log.WriteLine("需要管理员确认一次：把自签证书放进这台电脑的「受信任人」。（换成正式证书的发行版不需要这一步。）");
        try
        {
            using var elevated = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = "--trust-cert --quiet",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
            elevated!.WaitForExit();
            if (elevated.ExitCode != 0 || !IsTrusted(cert))
            {
                log.WriteLine("证书没有导入成功（管理员那一步返回了错误）。可以手动执行：以管理员身份运行 WeChatBridge.exe --trust-cert");
                return false;
            }

            log.WriteLine($"已把证书 {cert.Subject} 导入这台电脑的「受信任人」。");
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            log.WriteLine("管理员确认被取消了。没有它，Windows 不接受自签名的包。重新运行 --register 并在弹出的窗口里点「是」。");
            return false;
        }
    }

    /// <summary>The elevated half of <see cref="EnsureCertificateTrusted"/>.</summary>
    public static int TrustCertificate(TextWriter log)
    {
        if (!File.Exists(CertificatePath))
        {
            log.WriteLine($"找不到 {CertificatePath}");
            return 1;
        }

        var cert = new X509Certificate2(CertificatePath);
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        try
        {
            store.Open(OpenFlags.ReadWrite);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            log.WriteLine("打不开这台电脑的证书存储：这个命令要以管理员身份运行。");
            return 1;
        }

        if (store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false).Count == 0)
        {
            store.Add(cert);
        }

        log.WriteLine($"已信任 {cert.Subject}");
        return 0;
    }

    private static bool IsTrusted(X509Certificate2 cert)
    {
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false).Count > 0;
    }

    /// <summary>Reads AppxManifest.xml out of the msix (it is a zip) and compares the three identity values
    /// with the ones compiled into this exe. A mismatch would register fine and then silently run without
    /// identity, so it is refused here with all three values printed.</summary>
    internal static string? CheckManifest(string msixPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(msixPath);
            var entry = zip.GetEntry("AppxManifest.xml");
            if (entry is null)
            {
                return "msix 里没有 AppxManifest.xml，重新运行 scripts\\build-package.ps1";
            }

            using var stream = entry.Open();
            var doc = XDocument.Load(stream);
            XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            var identity = doc.Root!.Element(ns + "Identity")!;
            var app = doc.Root.Element(ns + "Applications")!.Element(ns + "Application")!;
            var name = identity.Attribute("Name")!.Value;
            var publisher = identity.Attribute("Publisher")!.Value;
            var appId = app.Attribute("Id")!.Value;
            if (name != BridgeIdentity.PackageName || publisher != BridgeIdentity.Publisher || appId != BridgeIdentity.ApplicationId)
            {
                return $"msix 里的身份和这个 exe 不一致，注册了也拿不到身份：\n  msix：{name} / {publisher} / {appId}\n  exe： {BridgeIdentity.PackageName} / {BridgeIdentity.Publisher} / {BridgeIdentity.ApplicationId}\n改一致后重新 build-package + publish。";
            }

            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or NullReferenceException)
        {
            return $"读不了 msix 里的清单（{ex.Message}），重新运行 scripts\\build-package.ps1";
        }
    }

    private static string Explain(DeploymentResult result)
    {
        var hr = result.ExtendedErrorCode?.HResult ?? 0;
        var hint = hr switch
        {
            unchecked((int)0x800B0109) => "证书不被信任：确认 .cer 和 msix 出自同一次 build-package.ps1，再运行一次 --register",
            unchecked((int)0x800B010A) or unchecked((int)0x800B0100) => "msix 没有签名或签名损坏：重新运行 build-package.ps1",
            unchecked((int)0x80080204) => "AppxManifest.xml 格式错误：看下面的错误文本里的行号",
            unchecked((int)0x80080205) or unchecked((int)0x80080206) => "msix 损坏：重新打包",
            unchecked((int)0x80073CF0) or unchecked((int)0x80073CF1) => "打不开 msix：路径里有特殊字符，或文件被占用",
            unchecked((int)0x80073CFD) => "系统版本太低：需要 Windows 10 2004（19041）或更新",
            unchecked((int)0x80073CFF) or unchecked((int)0x80073D01) => "组策略禁止安装未上架的应用，联系管理员",
            unchecked((int)0x80070005) => "程序目录不可读：不要放在受保护目录或网络盘里",
            ErrorInstallFailed or ErrorAlreadyExists => "同版本已注册：先运行 --unregister，再 --register",
            ErrorPackagesInUse => "微信桥正在运行：关掉它再试",
            _ => "详情看事件查看器：应用程序和服务日志 → Microsoft → Windows → AppXDeployment-Server",
        };
        return $"注册失败 0x{hr:X8}：{result.ErrorText}\n{hint}";
    }
}
