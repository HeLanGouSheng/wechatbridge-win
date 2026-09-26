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

## 平台事实（已查实，别再查）

- 共享激活用纯 WinRT `Windows.ApplicationModel.AppInstance.GetActivatedEventArgs()`，不装 Windows App SDK。
- `GetCurrentPackageFullName` 返回 15700 = 无身份；不是「返回 0」。
- 同版本重复注册用 `ForceUpdateFromAnyVersion`；**别默认先删包再注册**——`--register` 进程自己可能带身份，删包会杀掉自己。
- 每次共享激活起一个新进程；**不要加单实例互斥退出第二个进程**，否则转发静默丢失。
- 自签证书必须进 **`LocalMachine\TrustedPeople`**（当前用户那份不算，实测 0x800B0109）——`--register` 自我提权跑一次 `--trust-cert`。
- 包的图标由 Windows 从**外部位置（exe 所在目录）的 `Assets\`** 解析，不是从 msix；缺了会让 `AppListEntry.DisplayInfo.GetLogo` 抛 0x80070490，微信的「选择电脑中的应用」会把拿不到图标的应用整个丢掉（系统分享面板只是显示空白图标）。`publish.ps1` 负责拷贝。
- 同版本、内容不同的包不能重复注册（0x80073CF9）：`build-package.ps1` 每次构建换版本号；带身份的进程绝不替自己解除注册（会把自己杀掉，留下未注册状态）。
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
