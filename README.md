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

## Linux 平台支持（优先 Wayland，保留 X11）

AvaPlayer 在 Linux 上**优先使用原生 Wayland**，但在没有可用 Wayland 合成器时
**自动回退到 X11 / XWayland**——这与 Avalonia 自身的默认行为一致：X11 仍是 Avalonia
在 Linux 上的默认后端，而 Wayland 后端官方标注为 experimental。

### 配置方式

- `Program.cs` 保留 `UsePlatformDetect()` 作为基础服务加载入口（Skia 渲染、HarfBuzz
  文本整形、字体等）并配置默认窗口后端（Linux 上为 X11），随后在其**之后**调用
  `UseWaylandWithFallback()`——这是官方文档要求的「在 `UseX11` / `UsePlatformDetect`
  之后调用」模式：有可用合成器时走 Wayland，否则回退到前面配置的后端。
- 未使用无回退的 `UseWayland()`：那会让应用在没有 Wayland 合成器时直接启动失败。
- `WaylandPlatformOptions` 关键取值（仅在真正走 Wayland 时生效，回退 X11 时被忽略）：
  `EnableReconnects = true`（显式声明默认行为，合成器重启后自动重连）；
  `UseGLibMainLoop = false`（DBus 工作全部走托管 `Tmds.DBus.Protocol`，无需 GLib 主循环）；
  `UseDmabufSwapchain` 保持 `null`，由后端按合成器 / 驱动能力自决（对 NVIDIA 等驱动最稳）。

### 已知限制（仅 Wayland 后端，按 Avalonia 官方文档 / 发布说明；X11 下不适用）

- **实验性后端**：Avalonia 12.1 起内置，官方标注 experimental；`UsePlatformDetect()`
  不会自动选中它，必须显式启用。
- `Topmost` 无效（本项目未使用，不受影响）。
- `WindowTransparencyLevel` 恒为 `Transparent` 且 setter 无效（本项目未设置，不受影响）。
- **无法绝对定位窗口**：`Position` 恒为默认值，因此 `WindowStartupLocation="CenterScreen"`
  无法保证居中，实际位置由合成器决定。
- 窗口图标 / 任务栏图标无效。
- KDE 专属集成尚不可用：全局应用菜单、窗口图标、背景模糊（blur-behind）。
- `ExtendClientArea` 已实现但欠完善；本项目主窗口依赖它自绘标题栏，个别合成器上
  装饰行为可能存在差异。
- 渲染走 OpenGL / OpenGL ES（经 EGL），可选 dma-buf 交换链，软件 framebuffer 作为
  回退；**Wayland 客户端没有 Vulkan 路径**。
- 托盘图标**是**支持的：Wayland 下经 DBus `StatusNotifierItem`（AppIndicator）注册，
  若会话总线上没有 watcher 服务则静默降级（仅无托盘，不影响其它功能）；X11 下走 XEmbed。
  注意当前托盘图标资源为 `.ico`，而 SNI 通常期望 PNG 像素图，个别 Wayland 桌面环境下
  托盘图标可能显示异常（后续计划补充 PNG 资源）。
