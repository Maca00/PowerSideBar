<div align="center">
    <img src="doc/logo-powersidebar.png" width="258" height="200" alt="PowerSideBar">
</div>
<br />
<div align="center">

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![WPF](https://img.shields.io/badge/WPF-Desktop-blue)
![WebView2](https://img.shields.io/badge/WebView2-Chromium-green)
  
</div>

# PowerSideBar

> A persistent Windows sidebar docked to the edge of the screen, inspired by the classic Microsoft Edge side pane — combining home automation and web tabs.

## ✨ Features

- **Sidebar browser** — Docked to the right or left of the screen as a Windows AppBar, automatically reserves screen space
- **Custom shortcuts** — Add/remove websites with automatic favicon download
- **Drag & drop** — Reorder shortcuts by dragging
- **Weather** — Local conditions shown in the bar via Open-Meteo
- **Philips Hue** — Control lights from the sidebar
- **Dooya shutters** — Manage motorized Dooya blinds
- **Spotify** — Play and control music from the bar
- **Ad blocker** — EasyList-based filtering with automatic updates every 24h
- **System tray** — Tray icon with context menu (show/hide, auto-start, etc.)
- **Auto-updates** — Velopack installer + checks from GitHub Releases
- **Single instance** — Prevents multiple instances via mutex
- **Dark theme** — Modern dark-mode UI

## 🔨 Prerequisites

- Windows 10/11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (dev / build)
- [WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) (included by default on Windows 11)

## 📦 Installation

1. Download `PowerSideBar-win-Setup.exe` from the [GitHub Releases](https://github.com/Maca00/PowerSideBar/releases)
2. Run the installer
3. Updates download automatically (tray → *Check for updates*, or on startup)

## License

MIT
