# Drives a real Windows share activation of this app without WeChat, through the same system API
# WeChat's shareSender.exe uses (Windows.ApplicationModel.DataTransfer.TransferTargetWatcher).
# Lists the transfer targets the OS offers for the given file, then invokes the one whose
# AppUserModelId starts with -TargetIdPrefix. Needs Windows 11 build 26100.7015 or newer.
#
# Usage: .\scripts\share-activate.ps1 -Path .\tests\fixtures\sample.zip [-TargetIdPrefix ChatBridgeWin_] [-ListOnly]
#
# Run in Windows PowerShell 5.1 (not pwsh): it compiles a small C# helper against the OS's own
# .winmd metadata, which is how the new API is reachable without an SDK that ships its projection.
# ASCII only in this file.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$TargetIdPrefix = "ChatBridgeWin_",
    [switch]$ListOnly,
    [int]$MaxTargets = 8
)

$ErrorActionPreference = "Stop"
$Path = (Resolve-Path $Path).Path
$fw = [System.IO.Path]::GetDirectoryName([object].Assembly.Location)

$code = @'
using System;
using System.Collections.Generic;
using System.Threading;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
public static class ShareActivate {
  public static List<string> Run(string path, string targetIdPrefix, long hwnd, int maxTargets, bool listOnly) {
    var log = new List<string>();
    var op = StorageFile.GetFileFromPathAsync(path);
    var opened = new ManualResetEventSlim(false);
    op.Completed = (a, s) => opened.Set();
    opened.Wait(10000);
    var file = op.GetResults();
    var pkg = new DataPackage();
    pkg.SetStorageItems(new IStorageItem[] { file });
    pkg.Properties.Title = "share-activate.ps1";
    var view = pkg.GetView();
    log.Add("IsSupported=" + TransferTargetWatcher.IsSupported(view));
    var opts = new TransferTargetDiscoveryOptions(view);
    opts.MaxAppTargets = maxTargets;
    var watcher = TransferTarget.CreateWatcher(opts);
    TransferTarget found = null;
    var done = new ManualResetEventSlim(false);
    int n = 0;
    watcher.Added += (s, e) => { lock (log) { log.Add("#" + Interlocked.Increment(ref n) + "  " + e.Target.Id + "  label=" + e.Target.Label + "  enabled=" + e.Target.IsEnabled); if (e.Target.Id.StartsWith(targetIdPrefix)) found = e.Target; } };
    watcher.EnumerationCompleted += (s, e) => done.Set();
    watcher.Start();
    done.Wait(12000);
    Thread.Sleep(1000);
    if (listOnly) { watcher.Stop(); return log; }
    if (found == null) { log.Add("target not found: " + targetIdPrefix); watcher.Stop(); return log; }
    var id = new Windows.UI.WindowId(); id.Value = (ulong)hwnd;
    var invoke = watcher.TransferToAsync(found, id);
    var finished = new ManualResetEventSlim(false);
    invoke.Completed = (a, s) => finished.Set();
    finished.Wait(30000);
    try { var r = invoke.GetResults(); log.Add("TransferToAsync: Succeeded=" + r.Succeeded + (r.ExtendedError == null ? "" : "  error=0x" + r.ExtendedError.HResult.ToString("X8") + " " + r.ExtendedError.Message)); }
    catch (Exception ex) { log.Add("TransferToAsync threw: " + ex.Message); }
    watcher.Stop();
    return log;
  }
}
'@

$provider = New-Object Microsoft.CSharp.CSharpCodeProvider
$params = New-Object System.CodeDom.Compiler.CompilerParameters
$params.OutputAssembly = Join-Path $env:TEMP ("share-activate-" + [guid]::NewGuid().ToString("N") + ".dll")
$params.ReferencedAssemblies.AddRange([string[]]@(
    (Join-Path $fw "System.Runtime.dll"),
    (Join-Path $fw "System.Runtime.InteropServices.WindowsRuntime.dll"),
    "C:\Windows\System32\WinMetadata\Windows.Foundation.winmd",
    "C:\Windows\System32\WinMetadata\Windows.ApplicationModel.winmd",
    "C:\Windows\System32\WinMetadata\Windows.Storage.winmd",
    "C:\Windows\System32\WinMetadata\Windows.UI.winmd"))
$result = $provider.CompileAssemblyFromSource($params, $code)
if ($result.Errors.HasErrors) { $result.Errors | ForEach-Object { "COMPILE $($_.ErrorNumber) line $($_.Line): $($_.ErrorText)" }; throw "helper did not compile" }
$type = [System.Reflection.Assembly]::LoadFrom($params.OutputAssembly).GetType("ShareActivate")

Add-Type -AssemblyName System.Windows.Forms
$form = New-Object System.Windows.Forms.Form
$form.Show()
try {
    $type.GetMethod("Run").Invoke($null, @($Path, $TargetIdPrefix, $form.Handle.ToInt64(), $MaxTargets, [bool]$ListOnly))
    if (-not $ListOnly) { Start-Sleep -Seconds 3 }
} finally {
    $form.Close()
}
