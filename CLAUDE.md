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

- `src/Bridge.Core/` — 纯逻辑（net8.0）：`Transcripts`（聊天记录.txt 解析）、`Archives`（ZIP 安全读取）、`Naming`（文件名清洗、从 ZIP 名取群名）、`Batches`（staging → ready → done/failed 的批次目录）、`LlmSocial/`（签名、稳定消息 ID、单聊/群聊映射、HTTP 客户端）、`Delivery/`（一批从读 ZIP 到写记录、挪目录的全流程；剪贴板文本）、`Config/`（settings.json、`ISecretProtector`、群指纹记忆 groups.json）、`Records/`（records.jsonl，命名互斥追加）、`BridgeJson`（统一 JSON 方言，源生成；新类型要加进 `BridgeJsonContext`）。
- `src/Bridge.App/` — WPF（net8.0-windows10.0.19041.0），程序集名 `WeChatBridge`：`Program` 入口分发；`LaunchMode` 检测共享激活；`Identity/` 身份检查与注册；`Cli/` `--register --unregister --status [--json] --identity --configure --test-connection --send --parse`（`--configure` 的密钥可来自环境变量 `CHATBRIDGE_SECRET`；`--status --json` 是 llmsocial 安装器读的契约，改字段要同步 llmsocial 的 `src/server/bridge/wechatBridge.ts`）；`Share/` 共享接收与本地导入；`Config/DpapiProtector` 密钥加密；`Delivery/DeliveryFlow` 读设置、建客户端（回环地址不走系统代理）、剪贴板；`Views/` 主窗口（记录 + 设置入口 + 注册）、结果窗口（自动投递时几秒后自关）、设置窗口、群名对话框。
- `packaging/` — 外部位置包清单与图标。`scripts/build-package.ps1` 造证书 + 打包 + 签名；`scripts/publish.ps1` 发布 + 打包 + 拷贝到发布目录；`scripts/package-release.ps1` 再打成 `dist/WeChatBridge-win-x64.zip` + `.sha256`（bsdtar，只装运行需要的文件）。**ps1 只写 ASCII**（PowerShell 5.1 把无 BOM 的脚本当 ANSI）。
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
- llmsocial 的 webhook：签名 hex **小写**、对**实际发送的字节**签名、`timestamp` **毫秒**、重复 `messageId` 返回 200（去重靠我们的 `MessageIds.Stable`，同一段记录再转发不会多一份）、`fromSelf` 消息存成我方已发且会作废 AI 草稿。空体的签名 POST 返回 400 = 密钥和账号都对，「测试连接」就靠这个，不会写进任何数据。
- 微信导出里没有群名也没有用户 ID：群聊第一次问人（`ChatNameDialog` / CLI `--chat-name`），之后 `GroupFingerprint` 按发言人集合认（≥2 人、重合 ≥0.7、平手算不认）；单聊用对方昵称当联系人 ID，改昵称 = 新联系人，这是已知限制，写在 README。
- 投递失败的批次留在 `inboxailed\`，主窗口「重试所选」`Requeue` 回 ready 再走一遍；重发已送达的消息是安全的（llmsocial 去重）。`records.jsonl` 一批一个文件一行，多个共享进程同时写靠 `Local\ChatBridge.records` 互斥。
- 本机验证不用真号：起一个临时 llmsocial（`LLMSOCIAL_PORT=8799 LLMSOCIAL_WEBHOOK_PORT=8798 LLMSOCIAL_DATA_DIR=<临时目录> node src/server/index.ts`），管理 API 要 `X-LLMSocial: 1` 头 + 登录 cookie，带中文的请求体用 `--data-binary @文件`（Git Bash 里 `-d '中文'` 会让 Content-Length 对不上）；它自带的 Mock 模型会起草回复，不花钱。

## 和 llmsocial 的关系

llmsocial 的账号卡片「安装聊天桥」= 下载发布包（默认 GitHub Release，可设本地路径）→ 校验 SHA-256 → bsdtar 解压到 `%LOCALAPPDATA%\Programs\WeChatBridge` → `--configure --base-url http://127.0.0.1:<回调端口> --account-id … --auto on`（密钥在环境变量）→ 若 `--status --json` 的 `registeredUpToDate` 不为真则 `--register`。注册绑定目录，所以从发布目录换到 Programs 目录会把注册挪过去，这是预期行为。一台电脑上的聊天桥同时只连一个账号。

## 日常命令

```
dotnet test                                   # Core 全部测试
.\scripts\publish.ps1                         # 发布 + 打包 + 签名
<发布目录>\WeChatBridge.exe --register        # 注册到微信菜单
<发布目录>\WeChatBridge.exe --status          # 看身份/注册/证书
<发布目录>\WeChatBridge.exe 某个导出.zip       # 不经微信测流程（弹窗口）
<发布目录>\WeChatBridge.exe --send 某个导出.zip [--chat-name 群名]   # 不经微信、不弹窗，直接发 llmsocial
```

无 .NET SDK 时用 `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`（dotnet-install.ps1 装的每用户版）。
