# 微信桥

微信 Windows 版里多选几条聊天记录 → 转发 → 转发到其他应用 → 选择电脑中的应用 → **微信桥**，这段记录就到了 llmsocial 的收件箱（以后还会有 AI 应用和 Obsidian）。

不读微信数据库、不注入、不碰微信进程：拿到的是微信自己打包导出的 ZIP（`聊天记录.txt` + 图片视频），和 macOS 版 [WeChatBridge](https://github.com/freestylefly/WeChatBridge) 收到的是同一种文件。本项目就是它的 Windows 版，Core 的行为规则按它的源码和测试逐条移植。

## 它怎么出现在微信的菜单里

微信 Windows 版的「选择电脑中的应用」列出的是 **Windows 共享目标**（注册了 `windows.shareTarget` 且能收 `.zip` 的应用）。微信桥用「外部位置包」把这个身份挂在自己的 exe 上——腾讯自己的 `WeixinShare` 包用的是同一种注册方式。

## 安装（开发期）

需要 Windows 10 2004 或更新、Windows 10 SDK（`makeappx` / `signtool`，装 Visual Studio 时带上）、.NET 8 SDK。

```powershell
.\scripts\publish.ps1
& 'src\Bridge.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\WeChatBridge.exe' --register
```

`--register` 把自签证书导入当前用户的「受信任人」并注册包，不需要管理员。然后把微信整个退出再打开一次。

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
