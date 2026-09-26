using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Bridge.App.Diagnostics;

/// <summary>
/// An unhandled exception in a share-activated process has no console and no window to speak through,
/// and the Windows Error Reporting entry only says "Dll was not found". This writes what actually
/// matters to logs\crash-*.txt: where the process runs from, what the runtime probes, and for each native
/// DLL beside the exe whether the loader can load it and with which Win32 error.
/// </summary>
public static class CrashLog
{
    private static readonly string[] NativeDlls = { "PresentationNative_cor3.dll", "wpfgfx_cor3.dll", "vcruntime140_cor3.dll", "D3DCompiler_47_cor3.dll", "PenImc_cor3.dll" };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr reserved, uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool FreeLibrary(IntPtr module);

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject as Exception, "unhandled");
    }

    public static string? Write(Exception? exception, string kind)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            var path = Path.Combine(AppPaths.LogsDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.txt");
            File.WriteAllText(path, Report(exception, kind), new UTF8Encoding(false));
            return path;
        }
        catch
        {
            return null;
        }
    }

    public static string Report(Exception? exception, string kind)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"kind: {kind}");
        sb.AppendLine($"time: {DateTimeOffset.Now:O}");
        sb.AppendLine($"identity: {Identity.PackageIdentity.FullName() ?? "none"}");
        sb.AppendLine($"ProcessPath: {Environment.ProcessPath}");
        sb.AppendLine($"BaseDirectory: {AppContext.BaseDirectory}");
        sb.AppendLine($"CurrentDirectory: {Environment.CurrentDirectory}");
        sb.AppendLine($"NATIVE_DLL_SEARCH_DIRECTORIES: {AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES")}");
        sb.AppendLine($"APP_CONTEXT_BASE_DIRECTORY: {AppContext.GetData("APP_CONTEXT_BASE_DIRECTORY")}");
        sb.AppendLine($"TEMP: {Path.GetTempPath()}");
        sb.AppendLine($"PATH length: {(Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Length}");
        sb.AppendLine();

        var dir = AppPaths.InstallDirectory;
        foreach (var name in NativeDlls)
        {
            var full = Path.Combine(dir, name);
            sb.Append($"{name}: exists={File.Exists(full)}");
            sb.Append($"  byName={Probe(name, 0)}");
            sb.Append($"  byPath={Probe(full, 0)}");
            sb.Append($"  byPath+AlteredSearch={Probe(full, 0x00000008)}");
            sb.Append($"  byPath+DefaultDirs={Probe(full, 0x00001000)}");
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine(exception?.ToString() ?? "(no exception object)");
        return sb.ToString();
    }

    private static string Probe(string fileName, uint flags)
    {
        var handle = LoadLibraryExW(fileName, IntPtr.Zero, flags);
        if (handle == IntPtr.Zero)
        {
            return $"FAIL(err {Marshal.GetLastWin32Error()})";
        }

        FreeLibrary(handle);
        return "ok";
    }
}
