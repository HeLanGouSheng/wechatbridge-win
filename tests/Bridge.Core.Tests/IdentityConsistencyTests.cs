using System.Xml.Linq;
using Xunit;

namespace Bridge.Core.Tests;

/// <summary>The package manifest, the exe's application manifest and BridgeIdentity must agree, or the
/// exe silently runs without identity and never appears in WeChat's menu.</summary>
public class IdentityConsistencyTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "packaging", "AppxManifest.xml")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("找不到仓库根目录（packaging/AppxManifest.xml）");
    }

    [Fact]
    public void 包清单与BridgeIdentity一致()
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "packaging", "AppxManifest.xml"));
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
        XNamespace uap10 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";

        var identity = doc.Root!.Element(ns + "Identity")!;
        Assert.Equal(BridgeIdentity.PackageName, identity.Attribute("Name")!.Value);
        Assert.Equal(BridgeIdentity.Publisher, identity.Attribute("Publisher")!.Value);

        Assert.Equal("true", doc.Root.Element(ns + "Properties")!.Element(uap10 + "AllowExternalContent")!.Value);

        var app = doc.Root.Element(ns + "Applications")!.Element(ns + "Application")!;
        Assert.Equal(BridgeIdentity.ApplicationId, app.Attribute("Id")!.Value);
        Assert.Equal(BridgeIdentity.ExecutableName, app.Attribute("Executable")!.Value);
        Assert.Equal("mediumIL", app.Attribute(uap10 + "TrustLevel")!.Value);
        Assert.Equal("win32App", app.Attribute(uap10 + "RuntimeBehavior")!.Value);
        Assert.Equal(BridgeIdentity.ProductName, app.Element(uap + "VisualElements")!.Attribute("DisplayName")!.Value);

        var share = app.Element(ns + "Extensions")!.Elements(uap + "Extension").Single(e => e.Attribute("Category")!.Value == "windows.shareTarget").Element(uap + "ShareTarget")!;
        // The TransferTarget platform behind WeChat's picker only lists targets that accept any file type.
        Assert.NotNull(share.Element(uap + "SupportedFileTypes")!.Element(uap + "SupportsAnyFileType"));
        Assert.Contains("StorageItems", share.Elements(uap + "DataFormat").Select(e => e.Value));
    }

    [Fact]
    public void 身份字符串不含微信字样()
    {
        // WeChat's picker hides its own share entry by name; anything that looks like it disappears too.
        foreach (var value in new[] { BridgeIdentity.PackageName, BridgeIdentity.ApplicationId, BridgeIdentity.ProductName })
        {
            Assert.DoesNotMatch("(?i)wechat|weixin|微信", value);
        }
    }

    [Fact]
    public void 应用清单的msix元素与BridgeIdentity一致()
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "src", "Bridge.App", "app.manifest"));
        XNamespace msix = "urn:schemas-microsoft-com:msix.v1";
        var element = doc.Descendants(msix + "msix").Single();

        Assert.Equal(BridgeIdentity.Publisher, element.Attribute("publisher")!.Value);
        Assert.Equal(BridgeIdentity.PackageName, element.Attribute("packageName")!.Value);
        Assert.Equal(BridgeIdentity.ApplicationId, element.Attribute("applicationId")!.Value);
    }

    [Fact]
    public void App项目的程序集名与包清单里的可执行文件一致()
    {
        var csproj = XDocument.Load(Path.Combine(RepoRoot(), "src", "Bridge.App", "Bridge.App.csproj"));
        var assemblyName = csproj.Descendants("AssemblyName").Single().Value;
        Assert.Equal(BridgeIdentity.ExecutableName, assemblyName + ".exe");
    }
}
