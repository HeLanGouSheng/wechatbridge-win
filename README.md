# 聊天桥（名字待定）

微信 Windows 版里多选几条聊天记录 → 转发 → 转发到其他应用 → 选择电脑中的应用 → **聊天桥**，这段记录就到了 llmsocial 的收件箱（以后还会有 AI 应用和 Obsidian）。

> 名字里不能有「微信」：微信会把看起来像它自己的共享目标从菜单里去掉，「微信桥」就是这么消失的。正式名字待定。

**状态（2026-09-26）**：M0 跑通——微信里选它，程序被启动、收到微信打的 ZIP、解析出消息并显示。下一步 M1：送进 llmsocial。

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

## 不经微信测试

```powershell
WeChatBridge.exe 某个导出的.zip
```

或把 ZIP 拖进主窗口。

## 数据放在哪

`%LOCALAPPDATA%\WeChatBridge\`：`inbox\`（收到的 ZIP，按批次分目录）、`settings.json`、`records.jsonl`。

## 许可

MIT。源自 [freestylefly/WeChatBridge](https://github.com/freestylefly/WeChatBridge)（MIT）。
