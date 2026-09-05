# Kinolincopy

一个本机 Windows 剪贴板历史看板。使用 WPF 和系统自带的 .NET Framework 4.8，单文件体积很小，不附带 Chromium。

## 功能

- 自动记录最近 48 小时内复制过的内容，过期记录会自动清理。
- 支持文本、剪贴板图片，以及从资源管理器复制的视频/图片文件。
- 按具体时间点显示单列时间轴，文本、图片和视频预览都直接显示在时间轴条目里。
- 可停靠在屏幕左侧或右侧，收起后只在同一段侧边区域唤醒，拖动窗口上下移动后会记住自定义位置。
- 启动后常驻托盘；默认开机自启；单实例运行，重复打开会唤起已有进程。
- 支持暂停记录、隐藏内容、固定展开、锁定保留、清空未锁定历史，以及粘贴并隐藏。

## 运行要求

Windows 10 或更高版本（已包含 .NET Framework 4.8）。

## 开发运行

```powershell
dotnet build -c Debug
dotnet run -c Debug
```

## 打包便携版

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

产物在 `release/Kinolincopy.exe`。
