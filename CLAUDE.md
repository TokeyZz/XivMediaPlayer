# XivMediaPlayer 项目结构

## 概述

基于 [Sebane1/XivMediaPlayer](https://github.com/Sebane1/XivMediaPlayer) v0.1.2.8 的 FFXIV Dalamud 插件，提供 VLC + yt-dlp 视频播放、服务器同步、代理支持。

## 项目组成

| 项目 | 类型 | 说明 |
|------|------|------|
| `XivMediaPlayer` | Dalamud 插件 | 主插件（net10.0-windows7.0） |
| `MediaPlayerCore` | 类库 | 媒体核心（VLC、yt-dlp、StreamProxy） |
| `XivMediaPlayer.Shared` | 类库 | 共享模型（TvPlacement、ApiModels、RoomState） |
| `XivMediaPlayer.Server` | ASP.NET Core | 服务器端（DJ 同步、房间管理） |

## 插件主文件结构 (XivMediaPlayer/)

### Plugin 类 — partial class 拆分

`Plugin.cs` 已拆分为 9 个 partial 文件：

| 文件 | 行数 | 功能 |
|------|------|------|
| `Plugin.cs` | ~578 | 字段声明、构造函数、Dispose、屏幕命令 |
| `Plugin_Framework.cs` | ~330 | 框架初始化、登录检测、依赖解析、OnFrameworkUpdate |
| `Plugin_Commands.cs` | ~230 | `/media` 命令处理、HandleScreenCommand、PlaceScreenAtCamera |
| `Plugin_Streams.cs` | ~550 | **播放核心**——PlayRouted、PlayViaYtDlp、TuneIntoStream、ChangeStreamQuality |
| `Plugin_EventHandlers.cs` | ~450 | **事件处理**——OnMediaError、OnNewMediaTriggered、OnPlaybackFinished、OnChatMessage、OnTerritoryChanged |
| `Plugin_Sync.cs` | ~440 | **同步核心**——HeartbeatLoopAsync、FetchLoopAsync、ClaimDjAsync、ReleaseDjAsync、GetLocationKey |
| `Plugin_Rendering.cs` | ~735 | **渲染核心**——OnDraw、屏幕渲染、UI 交互、DepthTestedRenderer 调用 |
| `Plugin_Playback.cs` | ~260 | **播放控制**——PlayNext、PlayPrevious、SeekRelative、ToggleMute、KillAndRestart、DoRefreshCurrentMedia |
| `Plugin_Utilities.cs` | ~230 | **工具方法**——CleanUrl、RemoveSpecialSymbols、PrintChat、ApplyProxySettings |

### 自定义修改的关键位置

| 功能 | 文件 | 关键行 |
|------|------|--------|
| VLC 无害错误过滤 | `Plugin_EventHandlers.cs` | `OnMediaError` — 过滤 TS discontinuity、Timestamp conversion 等 |
| ClaimDj 在 TuneIntoStream | `Plugin_Streams.cs` | 两处 `ClaimDjAsync()` 调用 |
| maxRetries=10 | `Plugin_EventHandlers.cs` | `OnMediaError` 中 `maxRetries = 10` |
| m3u8/mpd 代理路由 | `Plugin_Streams.cs` | `PlayViaYtDlp` 直接流检查 `Contains(".m3u8") \|\| Contains(".mpd")` |
| DoRefreshCurrentMedia isAutoSync | `Plugin_Playback.cs` | `PlayRouted(..., !_isLocalDj)` |
| OnNewMediaTriggered 不重置错误计数 | `Plugin_EventHandlers.cs` | 已删除 `_mediaErrorCount = 0` |
| 直接流扩展名 | `YtDlpManager.cs` | `IsUrlSupported` — 跳过 `.flv`、`.ts`、`.m3u8` 等 |
| --no-playlist | `YtDlpManager.cs` | `ResolveStreamUrlInternal` — yt-dlp 参数 |
| 孤儿清理器移除 | `YtDlpManager.cs` | 已删除 `_orphanCleanupTimer` |
| 代理 URL scheme | `Plugin_Utilities.cs` | `ApplyProxySettings` — HTTP/HTTPS → `http://` |
| m3u8 分片代理重写 | `StreamProxy.cs` | 全部 segment 走 `/proxy_media` |
| Settings 窗口可拖拽折叠 | `SettingsWindow.cs` | `ImGuiWindowFlags.None` + `SizeConstraints` |
| 汉化 | `SettingsWindow.cs`、`Plugin_Streams.cs`、各处 | 中文按钮和提示文本 |

## 依赖下载链

- `cef/` + `libvlc/`：`DependencyManager.cs` 从上上游 GitHub Release 下载
- `yt-dlp.exe`：`YtDlpManager.cs` 从 GitHub 下载
- `deno.exe`：`YtDlpManager.cs` 从 GitHub 下载
- `ffmpeg.exe`：`DependencyManager.cs` 自动后台下载

## 上游同步指南

上游更新到新版本时：

1. 比较渲染文件（`Compositing/`、`VideoWindow.cs`、`DepthTestedRenderer.cs` 等），这些冲突最少
2. `Plugin.cs` 的改动按功能映射到对应 partial 文件
3. `Configuration.cs` 新增字段直接添加
4. `SettingsWindow.cs` 需要手动合并（保留汉化）
5. `YtDlpManager.cs` 需要手动对比（我们有自定义改动）
6. `ServerModels.cs` 保留 `global using XivMediaPlayer.Shared.Models`
