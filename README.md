# AvaPlayer

一个基于 **.NET 10 + Avalonia 12** 的本地桌面音乐播放器：

- 本地优先、尽量少联网的音乐播放体验
- `MiniAudioExNET` 驱动的本地音频播放
- SQLite 本地数据存储
- 歌词、封面、播放列表、系统托盘、会话恢复
- Linux MPRIS / Windows 系统媒体控制联动

## 界面预览

| 主播放界面 | 播放列表抽屉 | 设置界面 |
| --- | --- | --- |
| ![主播放界面](assets/player.png) | ![播放列表抽屉](assets/playlist.png) | ![设置界面](assets/settings.png) |

## 技术栈

| 类别 | 技术 |
| --- | --- |
| UI | Avalonia 12、FluentIcons.Avalonia |
| 音频后端 | JAJ.Packages.MiniAudioEx |
| 本地数据库 | Microsoft.Data.Sqlite |
| 元数据读取 | TagLibSharp |
| 网络能力 | HttpClientFactory |
| Linux 媒体控制 | Tmds.DBus.Protocol |

> 项目**不使用 EF Core**，SQLite 访问基于直接 SQL，便于保持 AOT 友好。
