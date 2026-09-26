using System.IO;
using Bridge.Core;

namespace Bridge.App;

/// <summary>Where this app keeps its own files. All ASCII, all under the current user, nothing roaming:
/// the inbox holds the ZIPs WeChat handed over.</summary>
public static class AppPaths
{
    public static string DataRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), BridgeIdentity.DataDirectoryName);

    public static string InboxRoot => Path.Combine(DataRoot, "inbox");

    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");

    public static string RecordsFile => Path.Combine(DataRoot, "records.jsonl");

    public static string LogsDirectory => Path.Combine(DataRoot, "logs");

    /// <summary>The folder the exe runs from. Single-file publish leaves Assembly.Location empty, so this
    /// is the only reliable source; it is also the "external location" the identity package binds to.</summary>
    public static string InstallDirectory { get; } = Path.GetDirectoryName(Environment.ProcessPath!)!;
}
