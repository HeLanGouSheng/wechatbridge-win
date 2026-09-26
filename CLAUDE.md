# 微信桥（wechatbridge-win）

Windows 桌面程序：把微信「转发到其他应用」导出的聊天记录 ZIP 送到 llmsocial（以及以后的 AI 应用、Obsidian）。原理：注册成 Windows 共享目标（外部位置包 / sparse package），出现在微信「选择电脑中的应用」菜单里，直接收到微信打的 ZIP。源自 macOS 的 [freestylefly/WeChatBridge](https://github.com/freestylefly/WeChatBridge)（MIT），Core 的行为规则逐条对照它的 Swift 源码和测试移植。

## 硬规则

1. 聊天内容只来自微信自己导出、用户主动转发的文件。**不读微信数据库、不解密、不注入、不扫描微信进程内存、不做界面自动化去读微信。**
2. 只进不出：程序不替用户往微信里发消息、不按回车。将来「粘贴到输入框」也止于粘贴。
3. 除用户自己配置的 llmsocial 地址和 Obsidian 目录外不联网、不上传、无遥测。
4. 从原版移植的模块先移植它的测试。`Bridge.Core` 不得引用 Windows TFM 或任何 Windows API；Windows 专属能力（DPAPI、剪贴板、WinRT）通过 Core 里声明的接口由 `Bridge.App` 注入。
5. 界面文案中文；每条报错说下一步怎么做；CLI 每条错误带下一步。
6. 包身份三要素（包名 / 发布者 / 应用 ID）只在 `src/Bridge.Core/BridgeIdentity.cs` 定义；`packaging/AppxManifest.xml` 与 `src/Bridge.App/app.manifest` 必须一致，`IdentityConsistencyTests` 会核对。改动任何一处要三处同改。
7. MIT 许可，README 注明源自 freestylefly/WeChatBridge。

## 结构

- `src/Bridge.Core/` — 纯逻辑（net8.0）：`Transcripts`（聊天记录.txt 解析）、`Archives`（ZIP 安全读取）、`Naming`（文件名清洗、从 ZIP 名取群名）、`Batches`（staging → ready → done/failed 的批次目录）、`BridgeJson`（统一 JSON 方言，源生成）。
- `src/Bridge.App/` — WPF（net8.0-windows10.0.19041.0），程序集名 `WeChatBridge`：`Program` 入口分发；`LaunchMode` 检测共享激活；`Identity/` 身份检查与注册；`Cli/` `--register --unregister --status --identity`；`Share/` 共享接收与本地导入；`Views/` 窗口。
- `packaging/` — 外部位置包清单与图标。`scripts/build-package.ps1` 造证书 + 打包 + 签名；`scripts/publish.ps1` 发布 + 打包 + 拷贝到发布目录。**ps1 只写 ASCII**（PowerShell 5.1 把无 BOM 的脚本当 ANSI）。
- `tests/Bridge.Core.Tests/` — xunit。

## 微信那边的机制（2026-09-26 在微信 4.1.15.13 / Windows 11 25H2 上逐条验证）

- 「转发到其他应用 → 选择电脑中的应用」由微信按需下载的插件 `%APPDATA%\Tencent\xwechat\xplugin\Plugins\WinWeixinShare\<ver>\extracted\shareSender.exe` 实现，调用系统 API `Windows.ApplicationModel.DataTransfer.TransferTargetWatcher`（Windows 11 26100.7015+）枚举共享目标并用 `TransferToAsync` 调起。图标缓存在同目录 `icons\<AUMID>.png`——那里有我们的图标就说明微信收到了我们的条目。
- **只有声明 `SupportsAnyFileType` 的共享目标会被 `TransferTargetWatcher` 列出**；只声明 `.zip` 的在资源管理器「共享」子菜单里有、在这个 API 里没有。清单因此固定为任意文件类型。
- `TransferTargetDiscoveryOptions.MaxAppTargets` 默认 0 = 一个都不返回，调用方必须设上限；返回顺序由系统排序。
- **身份字符串（包名、应用 ID、显示名）不能含 WeChat / Weixin / 微信**：微信会把看起来像自己的条目（它自己的 `WeixinShare`）从列表里去掉。`WeChatBridgeWin` / 微信桥 被丢，`ChatBridgeWin` / 聊天桥 就显示。`IdentityConsistencyTests` 守着这条。
- 微信打的 ZIP：单个条目 `聊天记录.txt`，deflate，UTF-8 名字标志位置 1，无 BOM，LF 换行，内容以 `·昵称` 开头；文件名 `聊天记录_YYYYMMDD_HHMMSS.zip`；分享标题是微信代码里留下的常量「Win32 TransferTarget sample」。**聊天名/群名从任何字段都拿不到。**
- 可以不经微信做真实的共享激活：`scripts\share-activate.ps1 -Path x.zip`（Windows PowerShell 5.1 里跑；`-ListOnly` 只列系统给出的目标）。它用 `CSharpCodeProvider` 引用 `C:\Windows\System32\WinMetadata\*.winmd` 编译一段 C#，`TransferTarget.CreateWatcher` → `TransferToAsync(target, WindowId)`。
- 首次被微信调起时崩过一次（DllNotFoundException，WPF 找不到 PresentationNative_cor3.dll），当前构建复现不出；`CrashLog` 会把每个原生 DLL 的加载结果写到 `%LOCALAPPDATA%\WeChatBridge\logs\crash-*.txt`，`--diag` 手动生成一份。

## 平台事实（已查实，别再查）

- 共享激活用纯 WinRT `Windows.ApplicationModel.AppInstance.GetActivatedEventArgs()`，不装 Windows App SDK。
- `GetCurrentPackageFullName` 返回 15700 = 无身份；不是「返回 0」。
- 同版本重复注册用 `ForceUpdateFromAnyVersion`；**别默认先删包再注册**——`--register` 进程自己可能带身份，删包会杀掉自己。
- 每次共享激活起一个新进程；**不要加单实例互斥退出第二个进程**，否则转发静默丢失。
- 自签证书必须进 **`LocalMachine\TrustedPeople`**（当前用户那份不算，实测 0x800B0109）——`--register` 自我提权跑一次 `--trust-cert`。
- 包的图标由 Windows 从**外部位置（exe 所在目录）的 `Assets\`** 解析，不是从 msix；缺了会让 `AppListEntry.DisplayInfo.GetLogo` 抛 0x80070490，微信的「选择电脑中的应用」会把拿不到图标的应用整个丢掉（系统分享面板只是显示空白图标）。`publish.ps1` 负责拷贝。
- 同版本、内容不同的包不能重复注册（0x80073CF9）：`build-package.ps1` 每次构建换版本号；带身份的进程绝不替自己解除注册（会把自己杀掉，留下未注册状态）。注册过之后 `--register` 进程自己就带身份，Windows 拒绝更新「使用中」的包，排队更新也不会生效——所以带身份时把更新交给一个在本进程退出后运行的 PowerShell `Add-AppxPackage -ExternalLocation … -ForceUpdateFromAnyVersion -ForceTargetApplicationShutdown`（微信注册自己的包也这么做）。开发期直接用这条 PowerShell 最省事。
- WPF 原生 DLL 放 exe 旁边（`IncludeNativeLibrariesForSelfExtract=false`），不压缩单文件：共享代理在等，启动要快。
- llmsocial 的 webhook：签名 hex **小写**、`timestamp` **毫秒**、重复 `messageId` 返回 200、`fromSelf` 消息会作废 AI 草稿。

## 日常命令

```
dotnet test                                   # Core 全部测试
.\scripts\publish.ps1                         # 发布 + 打包 + 签名
<发布目录>\WeChatBridge.exe --register        # 注册到微信菜单
<发布目录>\WeChatBridge.exe --status          # 看身份/注册/证书
<发布目录>\WeChatBridge.exe 某个导出.zip       # 不经微信测流程
```

无 .NET SDK 时用 `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`（dotnet-install.ps1 装的每用户版）。
