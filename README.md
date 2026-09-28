# 聊天桥（名字待定）

微信 Windows 版里多选几条聊天记录 → 转发 → 转发到其他应用 → 选择电脑中的应用 → **聊天桥**，这段记录就到了 llmsocial 的收件箱（以后还会有 AI 应用和 Obsidian）。

> 名字里不能有「微信」：微信会把看起来像它自己的共享目标从菜单里去掉，「微信桥」就是这么消失的。正式名字待定。

**状态（2026-09-26）**：M0 跑通（微信里选它，程序被启动、收到微信打的 ZIP、解析出消息）；M1 完成——设置好 llmsocial 账号后，转发的记录直接进 llmsocial 收件箱，单聊按对方昵称建对话，群聊问一次群名后记住；重复转发不会产生重复消息；失败的批次留着可重试；剪贴板兜底。已用临时 llmsocial 实例逐条验过，等真实微信 + 真实 llmsocial 验收。

不读微信数据库、不注入、不碰微信进程：拿到的是微信自己打包导出的 ZIP（`聊天记录.txt` + 图片视频），和 macOS 版 [WeChatBridge](https://github.com/freestylefly/WeChatBridge) 收到的是同一种文件。本项目就是它的 Windows 版，Core 的行为规则按它的源码和测试逐条移植。

## 它怎么出现在微信的菜单里

微信 Windows 版的「选择电脑中的应用」由微信按需下载的 `shareSender.exe` 通过系统 API `TransferTargetWatcher` 列出 **Windows 共享目标**——注册了 `windows.shareTarget`、声明收任意文件类型、名字不像微信自己的应用。本程序用「外部位置包」把这个身份挂在自己的 exe 上——腾讯自己的 `WeixinShare` 包用的是同一种注册方式。

## 安装（开发期）

需要 Windows 10 2004 或更新、Windows 10 SDK（`makeappx` / `signtool`，装 Visual Studio 时带上）、.NET 8 SDK。

```powershell
.\scripts\publish.ps1
& 'src\Bridge.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\WeChatBridge.exe' --register
```

第一次 `--register` 要把自签证书放进这台电脑的「受信任人」，会弹一次管理员确认；之后不用。然后把微信整个退出再打开一次。

改了程序要更新注册时，在 PowerShell 里执行（`--register` 自己在带身份运行时也会转成这条）：

```powershell
Add-AppxPackage -Path '<发布目录>\ChatBridgeWin.msix' -ExternalLocation '<发布目录>' -ForceUpdateFromAnyVersion -ForceTargetApplicationShutdown
```

**别移动发布目录**：注册绑定的是目录路径，移动后要重新 `--register`。`--status` 能看出不一致。

## 接 llmsocial

最省事的路：在 llmsocial 里建账号（平台微信、连接方式通用 Webhook），卡片上点「安装聊天桥」——llmsocial 自己下载发布包、校验、解压到 `%LOCALAPPDATA%\Programs\WeChatBridge`，用 `--configure` 写好地址/账号/密钥（密钥走环境变量 `CHATBRIDGE_SECRET`），再 `--register`。下面是手动的做法。

1. 在 llmsocial 的「账号」里新建一个账号：平台选**微信个人号**，连接方式选**通用 Webhook**，出站地址留空，填一个至少 24 个字符的共享密钥。记下账号卡片上 `acct_` 开头的账号 ID。
2. 双击 `WeChatBridge.exe` 打开主窗口 → 「设置…」：llmsocial 地址（默认 `http://127.0.0.1:8788`，是 llmsocial 的**回调端口**，不是管理界面的 8787）、账号 ID、共享密钥、**我的昵称**（微信里你自己显示的昵称，一行一个；没填的话你发的消息会被当成对方的）。点「测试连接」看到「连接正常」再保存。
3. 勾上「收到转发后直接发给 llmsocial」就是一键：微信里转发 → 窗口显示「已发给 llmsocial：N 条」→ 几秒后自己关掉。不勾的话每次先弹窗口，按「发给 llmsocial」或「复制到剪贴板」。

安装脚本可以用命令行代替设置窗口：

```powershell
WeChatBridge.exe --configure --account-id acct_… --secret … [--base-url http://127.0.0.1:8788] [--my-names 甲,乙] [--auto on] --quiet
WeChatBridge.exe --test-connection
WeChatBridge.exe --status --json                        # 给程序读的状态（注册、版本、连的账号；不含密钥）
WeChatBridge.exe --send 某个导出.zip [--chat-name 群名]     # 不经微信、不弹窗口，直接发给 llmsocial
```

怎么映射：

| 微信导出 | llmsocial |
|---|---|
| 单聊（去掉我方后只剩一个人） | 联系人 `wx:<对方昵称>`，对话标题 = 对方昵称 |
| 群聊（≥2 个别人） | 联系人 `wxg:<群名>`，每条正文前加「发送者：」；群名第一次问，之后按发言人集合（重合 ≥ 0.7）记住 |
| 我方消息（发送者在「我的昵称」里） | 存成我方已发，llmsocial 不会再起草这一条的回复 |
| 消息时间 | 微信写的墙上时间按本机时区转成毫秒时间戳 |
| 消息 ID | 由「对话 + 发送者 + 分钟 + 正文 + 同分钟序号」哈希而来，所以同一段记录再转发一次，llmsocial 里不会多出一份 |

已知限制：微信导出的记录里**只有昵称，没有用户 ID**，对方改了昵称就会成为 llmsocial 里的新联系人；只有你转发的消息才会进去；最后一条是你自己发的时候 llmsocial 不会起草回复（它把这当作「主人已经回了」）。

## 发布包

```powershell
.\scripts\package-release.ps1        # publish + dist\WeChatBridge-win-x64.zip + .sha256
```

llmsocial 从 GitHub Release 的 `WeChatBridge-win-x64.zip` 下载（旁边要有 `.sha256`），版本号钉在 llmsocial 的 `src/server/config.ts`（`WECHAT_BRIDGE_RELEASE`）：发了新版要去那边改一行；`LLMSOCIAL_WECHAT_BRIDGE_URL` 可以改成别的地址或本地路径。

## 不经微信测试

```powershell
WeChatBridge.exe 某个导出的.zip
```

或把 ZIP 拖进主窗口。真实的共享激活（走微信同一条系统 API）：`scripts\share-activate.ps1 -Path 某个导出.zip`。

## 数据放在哪

`%LOCALAPPDATA%\WeChatBridge\`：`inbox\`（收到的 ZIP，`ready\` 待处理、`done\` 已送达、`failed\` 留着重试）、`settings.json`（密钥用 DPAPI 加密，只有这个 Windows 用户能解）、`groups.json`（记住的群）、`records.jsonl`（每批的去向）、`logs\`。主窗口的记录列表就是 `records.jsonl`；选一条失败的按「重试所选」。记录和批次目录按设置里的天数清理（默认 7 天，0 = 永久）。

## 许可

MIT（见 LICENSE）。源自 [freestylefly/WeChatBridge](https://github.com/freestylefly/WeChatBridge)（MIT）。
