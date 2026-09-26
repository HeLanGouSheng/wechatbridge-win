namespace Bridge.Core;

/// <summary>
/// The one place the package identity is spelled out. packaging/AppxManifest.xml and
/// src/Bridge.App/app.manifest must agree with these values, or the exe runs without identity and
/// never shows up in WeChat's menu (0x80073D54 at activation). A test compares the two files against
/// this class so the mismatch is caught at build time, not at first share.
/// </summary>
public static class BridgeIdentity
{
    /// <summary>Identity Name in the package manifest; also the Get-AppxPackage name.</summary>
    public const string PackageName = "WeChatBridgeWin";

    /// <summary>Must equal the signing certificate's Subject, byte for byte.</summary>
    public const string Publisher = "CN=WeChatBridgeWin";

    /// <summary>Application Id in the package manifest and applicationId in app.manifest.</summary>
    public const string ApplicationId = "WeChatBridge";

    /// <summary>File name of the executable that the package's Application element points at.</summary>
    public const string ExecutableName = "WeChatBridge.exe";

    /// <summary>What the user sees in WeChat's menu and in window titles.</summary>
    public const string ProductName = "微信桥";

    /// <summary>Folder under %LOCALAPPDATA% for settings, inbox and records. ASCII on purpose.</summary>
    public const string DataDirectoryName = "WeChatBridge";

    public const string PackageFileName = "WeChatBridgeWin.msix";
    public const string CertificateFileName = "WeChatBridgeWin.cer";
}
