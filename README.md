# XivMediaPlayer (汉化增强版)

在 FFXIV 中添加 VRChat 风格的视频播放器。

基于 [Sebane1/XivMediaPlayer](https://github.com/Sebane1/XivMediaPlayer) v0.1.1.0，增加以下功能：

- 完整汉化界面
- 服务器同步 DJ/跟随者模式（v2 心跳协议）
- SOCKS5/HTTP 代理支持（yt-dlp + VLC + StreamProxy）
- YouTube 通过代理播放（含 Cookie 支持）
- 流代理本地转发（解决 CDN 直连被墙问题）
- 多个 VLC/网络稳定性修复

## 安装

在 Dalamud 设置中添加自定义插件仓库：

```
https://raw.githubusercontent.com/TokeyZz/XivMediaPlayer/main/repo.json
```

依赖包（CEF + VLC）会自动从上游下载。

## 使用方法

1. 进入房屋装修菜单 → 放置电视
2. 复制视频/图片/音乐 URL
3. 点击电视屏幕上的粘贴图标
4. 媒体开始播放

支持的网站：[yt-dlp 支持列表](https://github.com/yt-dlp/yt-dlp/blob/master/supportedsites.md)

### YouTube 播放

需要安装浏览器扩展导出 cookies：

- Chrome/Brave/Chromium: [VRCVideoCacher](https://chromewebstore.google.com/detail/vrcvideocacher-cookies-ex/kfgelknbegappcajiflgfbjbdpbpokge)
- Firefox: [VRCVideoCacher](https://addons.mozilla.org/en-US/firefox/addon/vrcvideocachercookiesexporter/)

### 代理设置

在设置窗口配置代理类型（SOCKS5/HTTP）、地址和端口，用于 yt-dlp 解析和视频流代理。

## 命令

| 命令 | 功能 |
|------|------|
| `/media video` | 切换视频窗口 |
| `/media stop` | 停止播放 |
| `/media refresh` | 刷新当前媒体 |
| `/xlsettings` | 打开 Dalamud 设置 |

## 上游

- [Sebane1/XivMediaPlayer](https://github.com/Sebane1/XivMediaPlayer)
