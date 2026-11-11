# AvaPlayer 架构说明

## 设计原则

- **播放状态只有一个权威来源。** `PlaybackSession` 接收播放命令、协调音频与播放列表，并发布不可变的 `PlaybackSnapshot`。界面和系统媒体控制只消费快照，不自行拼接状态。
- **依赖通过窄接口跨模块。** ViewModel 通过 `IPlaybackSessionClient`、`IPlaylistService` 等接口表达意图；平台音频、数据库和媒体控制实现留在服务模块。
- **本地数据与可再生缓存分开。** `IDatabaseService` 保存播放列表、曲目元数据及设置；`ICacheService` 管理歌词/封面缓存及其 TTL、负缓存和容量策略。
- **平台差异由适配器收口。** Linux 使用 MPRIS，Windows 使用 SMTC，其余平台使用无操作实现；调用方只依赖 `IMediaTransportService`。

## 模块与职责

| 模块 | 责任与主要接口 | 不应承担的责任 |
| --- | --- | --- |
| `Views` / `ViewModels` | Avalonia 页面、绑定与用户命令；播放器通过 `IPlaybackSessionClient` 操作播放 | 直接创建音频后端、数据库连接或网络提供方 |
| `PlaybackSession` | 串行处理播放/暂停/跳转/切歌；发布带递增 revision 的快照；持久化会话 | 让 UI 或媒体平台成为播放状态的第二权威 |
| `Playlist` | `IPlaylistService` 管理歌单、当前队列和导航；`ITrackScannerService` 扫描本地目录 | 移动或复制用户的音乐文件 |
| `Audio` | `IPlayerService` 封装音频引擎及其状态事件 | 直接更新 ViewModel 或选择下一首曲目 |
| `Database` / `Settings` | `IDatabaseService` 负责 SQLite 持久化；设置服务对上提供键值和偏好接口 | 将缓存索引和缓存文件并入用户库事务 |
| `Lyrics` / `AlbumArt` | 服务按本地数据、缓存、网络提供方的顺序解析内容；Provider 接口隔离具体来源 | 让 UI 了解远端服务协议 |
| `Cache` | 独立的 SQLite 索引、磁盘 payload、L1 小对象缓存、TTL/负缓存和 LRU 淘汰 | 作为用户数据的唯一持久存储 |
| `MediaTransport` | 把播放快照映射到操作系统媒体控制，并把外部命令交回播放会话 | 自行执行音频播放状态转换 |

## 关键数据流

### 播放

```text
PlayerBarViewModel
    -> IPlaybackSessionClient 命令
    -> PlaybackSession（串行命令队列）
       -> IPlayerService / IPlaylistService
       -> PlaybackSnapshot
          -> PlayerBarViewModel
          -> IMediaTransportService（MPRIS / SMTC / No-op）
```

订阅快照时，订阅者会立即收到当前快照，之后收到每次状态变更；订阅返回的 `IDisposable` 用于解除订阅。`PlaybackSession` 是会话生命周期内的唯一写入者。

### 歌单与持久化

```text
PlaylistViewModel -> IPlaylistService -> ITrackScannerService
                                  \-> IDatabaseService -> SQLite
```

扫描器读取音频元数据并返回曲目记录。数据库更新歌单/曲目元数据，不触碰音频文件本身。数据库初始化可重复调用，建表与迁移由实现保持幂等。

### 歌词与封面

歌词和封面服务先检查本地来源及缓存；仅在允许联网且缓存未命中时调用对应 Provider。Provider 可独立替换，外部网络请求通过 `IHttpClientFactory` 创建的客户端发送。远端查无结果时使用带有效期的负缓存，避免重复请求。

## 依赖注入生命周期

`ApplicationServices.Create()` 是应用组合根。当前服务生命周期如下：

| 生命周期 | 注册内容 |
| --- | --- |
| Singleton | 缓存、SQLite、设置、网络访问、曲目扫描、音频播放器、播放列表、媒体控制、播放会话及播放位置记忆 |
| Scoped | 歌词/封面服务与 Provider 管理器、歌词偏好、各页面 ViewModel |

窗口运行时使用一个 scope；释放窗口时释放 ViewModel 和 scoped 服务，播放器/播放会话等 singleton 保留在应用生命周期内。新增服务应在组合根注册，并通过接口注入其消费者；不要在 ViewModel 或其他服务中直接构造基础设施实现。

## 测试边界

- `AvaPlayer.Tests/Playback` 验证播放会话命令、快照、恢复和持久化契约。
- `AvaPlayer.Tests/Library`、`Media` 验证歌单、扫描及歌词/封面查询逻辑。
- `AvaPlayer.Tests/UI` 使用 Avalonia Headless 验证窗口与 ViewModel 连接；它不能代替真实桌面环境下的视觉/平台交互检查。
- `AvaPlayer.Tests/Infrastructure` 验证 DI 身份、缓存和基础设施不变量。

所有跨模块行为优先通过已有接口和测试 fake 验证；若测试必须访问实现私有状态，应先检查接口/模块职责是否放错位置。
