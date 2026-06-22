using Dalamud.Game.Config;
using Dalamud.Game.Text;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using XivMediaPlayer.GameObjects;
using XivMediaPlayer.Windows;
using MediaPlayerCore;
using MediaPlayerCore.Compositing;
using MediaPlayerCore.Twitch;
using MediaPlayerCore.YtDlp;
using XivMediaPlayer.Compositing;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Concurrent;


namespace XivMediaPlayer
{
    public sealed partial class Plugin
    {

        private void TuneIntoStream(string url, MediaPlayerCore.IMediaGameObject audioGameObject, int startTimeMs = 0, Dictionary<string, string>? httpHeaders = null, bool isAutoSync = false)
        {
            if (_disposed) return;

            // Auto-detect Emulation Server URLs (e.g. rtsp://10.0.0.30:8554/live/screen_43815929)
            // Replace any backslashes with forward slashes (FFXIV chat/clipboard can mangle them)
            string normalizedUrl = url.Trim().Replace("\\", "/");
            if (normalizedUrl.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase) && normalizedUrl.Contains("/screen_"))
            {
                try
                {
                    var uri = new Uri(normalizedUrl);
                    string ip = uri.Host;
                    string session = uri.Segments.Last().Replace("screen_", "");

                    _emulationClient?.Dispose();
                    _emulationClient = new Networking.EmulationClient(ip, session);
                    _controllerService?.Dispose();
                    _controllerService = new Networking.ControllerService(ip, session);
                    _controllerService.Start();

                    // Start FFmpeg backend instead of VLC for extreme low latency video and audio
                    _mediaManager?.PlayFFmpegStream(normalizedUrl, audioGameObject, true);

                    _lastStreamURL = normalizedUrl;
                    _currentStreamer = "Emulation";
                    _streamURLs = new string[] { normalizedUrl };
                    _videoWindow.IsOpen = _config.DefaultVideoOpen == 0;

                    if (!isAutoSync && _config.SyncWithRoom) _ = ClaimDjAsync();
                    _streamWasPlaying = true;

                    try { MuteBgm(); } catch { }
                    return;
                }
                catch { }
            }

            UpdateWatchHistory();

            url = CleanUrl(url);

            if (!isAutoSync && CurrentTvPlacement?.IsLocked == true && CurrentTvPlacement?.OwnerId != _config.OwnerId && !IsHousingMenuOpen)
            {
                PrintChatError("[媒体播放器] 无法播放: 本房间的电视已被房主锁定");
                return;
            }

            _streamURLs = new string[] { url };
            _videoWindow.IsOpen = _config.DefaultVideoOpen == 0;
            if (_streamURLs.Length > 0)
            {
                string playUrl = ((int)_videoWindow.FeedType < _streamURLs.Length) ? _streamURLs[(int)_videoWindow.FeedType] : _streamURLs[0];
                if (playUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !playUrl.Contains("127.0.0.1"))
                {
                    if (playUrl.Contains(".m3u8") || playUrl.Contains(".mpd"))
                        playUrl = MediaPlayerCore.StreamProxy.Instance.RegisterStream(playUrl, httpHeaders);
                    else
                        playUrl = MediaPlayerCore.StreamProxy.Instance.RegisterDirectMediaSession(playUrl, httpHeaders);
                }
                _mediaManager.PlayStream(audioGameObject, playUrl, _config.SpatialAudioEnabled, startTimeMs, httpHeaders);
                _lastStreamURL = url;
                _currentStreamer = "Stream";
                PrintChat(@"[媒体播放器] 正在播放!" +
                  "\r\nUse \"/media video\" to toggle the video feed." +
                  "\r\nUse \"/media stop\" to stop the stream.");
            }

            if (!isAutoSync && _config.SyncWithRoom) _ = ClaimDjAsync();
            _streamWasPlaying = true;
            try
            {
                MuteBgm();
            }
            catch (Exception e)
            {
                _pluginLog.Warning(e, e.Message);
            }
            _streamSetCooldown.Stop();
            _streamSetCooldown.Reset();
            _streamSetCooldown.Start();
        }

        private bool _isResolvingMedia = false;
        private Guid _currentResolutionId = Guid.Empty;
        private bool _lastStreamIsLive = false;
        private bool _isIntentionallyPaused = false;
        private DateTime _lastUrlLoadTime = DateTime.MinValue;

        private bool IsUrlSafeForPublic(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            
            try
            {
                var uri = new Uri(url);
                var host = uri.Host.ToLowerInvariant();
                
                string[] safeDomains = {
                    "youtube.com", "youtu.be",
                    "twitch.tv",
                    "vimeo.com",
                    "soundcloud.com"
                };
                
                foreach (var domain in safeDomains)
                {
                    if (host == domain || host.EndsWith("." + domain))
                        return true;
                }
            }
            catch { }
            
            return false;
        }

        private void PlayRouted(string url, IMediaGameObject audioGameObject, int startTimeMs = 0, bool isAutoSync = false)
        {
            _isTransitioning = true;

            if (startTimeMs == 0 && (url.Contains("youtube.com") || url.Contains("youtu.be")))
                startTimeMs = ExtractYouTubeStartTimeMs(url);

            url = CleanUrl(url);
            if (YtDlpManager.IsUrlSupported(url) && _ytDlpManager.IsAvailable())
            {
                PlayViaYtDlp(url, audioGameObject, startTimeMs, isAutoSync);
            }
            else
            {
                TuneIntoStream(url, audioGameObject, startTimeMs, null, isAutoSync);
            }
        }
        private void PlayViaYtDlp(string url, IMediaGameObject audioGameObject, int startTimeMs = 0, bool isAutoSync = false)
        {
            if (_disposed) return;
            UpdateWatchHistory();
            DateTime resolutionStartTime = DateTime.UtcNow;

            url = CleanUrl(url);

            // Intercept local files or unsupported URLs and route them directly to TuneIntoStream
            if (!YtDlpManager.IsUrlSupported(url) || !_ytDlpManager.IsAvailable())
            {
                TuneIntoStream(url, audioGameObject, startTimeMs, null, isAutoSync);
                return;
            }

            _isIntentionallyPaused = false;
            _lastUrlLoadTime = DateTime.UtcNow;

            if (url != _lastStreamURL) _mediaErrorCount = 0;

            // If it's an auto-sync and we're already resolving something, ignore it to prevent spam.
            // But if it's a manual play, we ALLOW it to interrupt the current resolution!
            if (isAutoSync && _isResolvingMedia) return;

            if (!isAutoSync && CurrentTvPlacement?.IsLocked == true && CurrentTvPlacement?.OwnerId != _config.OwnerId && !IsHousingMenuOpen)
            {
                PrintChatError("[媒体播放器] 无法播放: 本房间的电视已被房主锁定");
                return;
            }

            string locationKey = _lastLocationKey;
            if (locationKey != null && locationKey.StartsWith("zone_") && _config.OnlySafeDomainsPublicScreens)
            {
                if (!IsUrlSafeForPublic(url))
                {
                    if (!isAutoSync) PrintChatError("[媒体播放器] 无法播放: 室外安全模式已启用, 仅允许已验证的域名 (YouTube, Twitch, Vimeo)");
                    _pluginLog.Warning($"[Social] Blocked playback of unsafe URL {url} due to Safe Mode.");
                    return;
                }
            }

            if (!isAutoSync)
            {
                _isLocalDj = true;
                _currentMediaOwnerId = _config.OwnerId;
            }

            Guid resolutionId = Guid.NewGuid();
            _currentResolutionId = resolutionId;

            // Direct playback for raw feeds (ignore query strings)
            string urlWithoutQuery = url.Split('?')[0];
            if (urlWithoutQuery.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                urlWithoutQuery.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                urlWithoutQuery.EndsWith(".flv", StringComparison.OrdinalIgnoreCase) ||
                urlWithoutQuery.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
                urlWithoutQuery.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ||
                urlWithoutQuery.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
                urlWithoutQuery.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
            {
                _lastStreamURL = url;
                _lastStreamIsLive = urlWithoutQuery.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                                 || urlWithoutQuery.EndsWith(".flv", StringComparison.OrdinalIgnoreCase)
                                 || urlWithoutQuery.EndsWith(".ts", StringComparison.OrdinalIgnoreCase);
                _lastStreamObject = audioGameObject;
                _streamURLs = new string[] { url };
                _videoWindow.IsOpen = _config.DefaultVideoOpen == 0;

                string playUrl = url;
                if (playUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    if (playUrl.Contains(".m3u8") || playUrl.Contains(".mpd"))
                        playUrl = MediaPlayerCore.StreamProxy.Instance.RegisterStream(url, null);
                    else
                        playUrl = MediaPlayerCore.StreamProxy.Instance.RegisterDirectMediaSession(url, null);
                }

                _mediaManager.PlayStream(audioGameObject, playUrl, _config.SpatialAudioEnabled, startTimeMs, null);

                _currentMediaDurationMs = null;
                _currentStreamer = "Direct Stream";
                _currentMediaTitle = "Direct Stream";

                PrintChat($"[媒体播放器] 正在直接播放!\r\n使用 \"/media video\" 切换视频窗口\r\n使用 \"/media stop\" 停止");

                if (!isAutoSync && _config.SyncWithRoom) _ = ClaimDjAsync();
                _isTransitioning = false;
                _isSyncing = false;
                _streamWasPlaying = true;
                try { MuteBgm(); } catch (Exception e) { _pluginLog.Warning(e, e.Message); }
                _isResolvingMedia = false;
                return;
            }

            _isResolvingMedia = true;

            Task.Run(async () =>
            {
                if (_ytDlpInitTask != null && !_ytDlpInitTask.IsCompleted)
                {
                    EnqueueFrameworkAction(() => PrintChat("[媒体播放器] 等待 yt-dlp 下载/更新完成...", ChatSeverity.Info));
                    await _ytDlpInitTask;
                }
                if (resolutionId != _currentResolutionId) return;

                try
                {
                    _lastStreamURL = url; // Save the original requested URL so PushMediaToServerAsync pushes it instead of the raw .m3u8

                    // Try to get metadata for a nice chat message
                    var metadataTask = _ytDlpManager.GetMetadata(url);
                    var resolveTask = _ytDlpManager.ResolveStreamUrl(url);

                    if (!Uri.TryCreate(url, UriKind.Absolute, out _))
                    {
                        _pluginLog.Warning($"[Media Player] Invalid stream URL rejected: {url}");
                        return;
                    }

                    string[]? streamUrls = null;
                    try
                    {
                        streamUrls = await resolveTask;
                        if (resolutionId != _currentResolutionId) return;
                    }
                    catch (Exception resolveEx)
                    {
                        _pluginLog.Warning(resolveEx, "[yt-dlp] Failed to resolve stream URL.");
                        string errorStr = resolveEx.ToString();
                        
                        if (errorStr.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase))
                        {
                            EnqueueFrameworkAction(() => PrintChatError("[媒体播放器] YouTube 拒绝了请求 (Bot 检测), 请通过 VRCVideoCacher 或 cookies.txt 配置 Cookie!"));
                            return;
                        }
                        
                        if (errorStr.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase) || errorStr.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase))
                        {
                            MediaPlayerCore.YtDlp.YtDlpManager.MarkUrlAsFailed(url);
                        }
                    }

                    MediaPlayerCore.YtDlp.YtDlpMetadata? metadata = null;
                    try
                    {
                        metadata = await metadataTask;
                    }
                    catch (Exception metadataEx)
                    {
                        _pluginLog.Warning(metadataEx, "[yt-dlp] Failed to get metadata.");
                        string errorStr = metadataEx.ToString();
                        
                        if (errorStr.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase))
                        {
                            EnqueueFrameworkAction(() => PrintChatError("[媒体播放器] YouTube 拒绝了请求 (Bot 检测), 请通过 VRCVideoCacher 或 cookies.txt 配置 Cookie!"));
                            return;
                        }
                        
                        if (errorStr.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase) || errorStr.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase))
                        {
                            MediaPlayerCore.YtDlp.YtDlpManager.MarkUrlAsFailed(url);
                        }
                    }

                    if (streamUrls == null || streamUrls.Length == 0 || string.IsNullOrEmpty(streamUrls[0]))
                        {
                            // Fallback to CefSharp for heavily protected sites
                            EnqueueFrameworkAction(() => PrintChat("[媒体播放器] yt-dlp 解析失败, 正在使用内置浏览器解析...", ChatSeverity.Info));

                        MediaPlayerCore.Resolvers.CefSharpResolverResult? cefResult = null;
                        try
                        {
                            MediaPlayerCore.Resolvers.CefSharpResolver.Initialize(_dependencyManager.DependenciesDir);
                            cefResult = await MediaPlayerCore.Resolvers.CefSharpResolver.ResolveStreamUrlAsync(url);
                        }
                        catch (Exception ex)
                        {
                            _pluginLog.Warning(ex, "[Media Player] CefSharp resolver crashed.");
                        }

                        if (cefResult != null && !string.IsNullOrEmpty(cefResult.Url))
                        {
                            streamUrls = new string[] { cefResult.Url };
                            metadata = new MediaPlayerCore.YtDlp.YtDlpMetadata { HttpHeaders = cefResult.Headers };
                            _cefBrowserHandle = cefResult.BrowserHandle;

                            if (!_isLocalDj && url != streamUrls[0] && startTimeMs < 5000)
                            {
                                _pluginLog.Information("[Social] Guest successfully resolved a raw Cef URL to a direct stream. Rescuing the host by pushing the .m3u8 back to the server!");
                                string rescuedStreamUrl = streamUrls[0];
                                EnqueueFrameworkAction(() =>
                                {
                                    _lastStreamURL = rescuedStreamUrl;
                                    _ = ClaimDjAsync();
                                });
                            }

                            EnqueueFrameworkAction(() => PrintChat("[媒体播放器] 内置浏览器成功找到流链接", ChatSeverity.Info));

                            // Merge headers
                            if (metadata == null) metadata = new MediaPlayerCore.YtDlp.YtDlpMetadata();
                            if (metadata.HttpHeaders == null) metadata.HttpHeaders = new Dictionary<string, string>();
                            foreach (var kvp in cefResult.Headers)
                            {
                                metadata.HttpHeaders[kvp.Key] = kvp.Value;
                            }

                            // Proxy the stream so VLC can bypass Cloudflare using our extracted Cookies and Headers
                            try
                            {
                                streamUrls[0] = MediaPlayerCore.StreamProxy.Instance.RegisterStream(cefResult.Url, metadata.HttpHeaders, cefResult.M3u8Content);
                                _pluginLog.Info($"[Media Player] Proxying stream URL: {streamUrls[0]}");
                            }
                            catch (Exception proxyEx)
                            {
                                _pluginLog.Warning(proxyEx, "[Media Player] Failed to proxy stream URL, falling back to direct.");
                            }
                        }
                        else
                        {
                            var fallbackHeaders = metadata?.HttpHeaders;
                            EnqueueFrameworkAction(() =>
                            {
                                PrintChatError("[媒体播放器] 原生解析失败, 正在尝试直接播放...");
                                TuneIntoStream(url, audioGameObject, startTimeMs, fallbackHeaders);
                            });
                            return;
                        }
                    }
                    string title = metadata?.Title;
                    if (string.IsNullOrWhiteSpace(title) || title == "Unknown") title = url;
                    string uploader = metadata?.Uploader ?? "";

                    // Twitch streams often don't explicitly return is_live=true, but they lack a duration!
                    // Also explicitly check if it's a twitch channel URL (not a video)
                    bool isTwitchLive = url.Contains("twitch.tv") && !url.Contains("/videos/");
                    bool isLive = (metadata?.IsLive == true) || (metadata != null && metadata.Duration == null) || isTwitchLive;
                    var resolvedStreamUrl = streamUrls[0];
                    var resolvedSlaveAudioUrl = streamUrls.Length > 1 ? streamUrls[1] : null;
                    var resolvedHeaders = metadata?.HttpHeaders;

                    // Cookie fallback: if yt-dlp didn't return headers but we have local cookies, inject them
                    if ((resolvedHeaders == null || !resolvedHeaders.ContainsKey("Cookie"))
                        && _ytDlpManager.HasCookies)
                    {
                        string cookies = _ytDlpManager.LoadCookiesForUrl(url);
                        if (!string.IsNullOrEmpty(cookies))
                        {
                            resolvedHeaders ??= new Dictionary<string, string>();
                            resolvedHeaders["Cookie"] = cookies;
                            _pluginLog.Information($"[yt-dlp] Injected {cookies.Split(';').Length} cookies from local file for {new Uri(url).Host}");
                        }
                    }

                    var resolvedDurationMs = metadata?.Duration * 1000.0;
                    string statusMsg = isLive ? "LIVE" : (metadata?.Duration.HasValue == true
                      ? TimeSpan.FromSeconds(metadata.Duration.Value).ToString(@"mm\:ss") : "");

                    EnqueueFrameworkAction(() =>
                    {
                        if (_disposed || resolutionId != _currentResolutionId || string.IsNullOrEmpty(resolvedStreamUrl)) return;

                        _lastStreamIsLive = isLive;
                        _lastStreamObject = audioGameObject;
                        _streamURLs = new string[] { resolvedStreamUrl };
                        _videoWindow.IsOpen = _config.DefaultVideoOpen == 0;

                        string playUrl = resolvedStreamUrl;
                        if (playUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !playUrl.Contains("127.0.0.1"))
                        {
                            // m3u8 playlists need segment rewriting so VLC hits all segments through the proxy
                            if (playUrl.Contains(".m3u8") || playUrl.Contains(".mpd"))
                                playUrl = MediaPlayerCore.StreamProxy.Instance.RegisterStream(resolvedStreamUrl, resolvedHeaders);
                            else
                                playUrl = MediaPlayerCore.StreamProxy.Instance.RegisterDirectMediaSession(resolvedStreamUrl, resolvedHeaders);
                        }

                        int finalStartTimeMs = startTimeMs;
                        if (isAutoSync && !isLive)
                            finalStartTimeMs += (int)(DateTime.UtcNow - resolutionStartTime).TotalMilliseconds;

                        _mediaManager.PlayStream(audioGameObject, playUrl, _config.SpatialAudioEnabled, finalStartTimeMs, resolvedHeaders, false, resolvedSlaveAudioUrl);
                        _lastStreamURL = url;
                        _currentMediaDurationMs = resolvedDurationMs;
                        _currentStreamer = !string.IsNullOrEmpty(uploader) ? uploader : title;
                        _currentMediaTitle = title;

                        PrintChat($"[媒体播放器] 正在播放: {title}" +
                          (!string.IsNullOrEmpty(uploader) ? $" by {uploader}" : "") +
                          (!string.IsNullOrEmpty(statusMsg) ? $" [{statusMsg}]" : "") +
                          "\r\nUse \"/media video\" to toggle the video feed." +
                          "\r\nUse \"/media stop\" to stop.");

                        if (!isAutoSync)
                        {
                            if (!isAutoSync && _config.SyncWithRoom) _ = ClaimDjAsync();
                            _isTransitioning = false;
                            _isSyncing = false;
                        }

                        _streamWasPlaying = true;
                        try
                        {
                            MuteBgm();
                        }
                        catch (Exception e)
                        {
                            _pluginLog.Warning(e, e.Message);
                        }
                        _streamSetCooldown.Stop();
                        _streamSetCooldown.Reset();
                        _streamSetCooldown.Start();
                    });
                }
                finally
                {
                    if (resolutionId == _currentResolutionId)
                    {
                        _isResolvingMedia = false;
                    }
                }
            });
        }

        private void ChangeStreamQuality()
        {
            if (_streamURLs != null)
            {
                if (_streamWasPlaying && _streamURLs.Length > 0)
                {
                    if ((int)_videoWindow.FeedType < _streamURLs.Length)
                    {
                        if (_lastStreamObject != null)
                        {
                            try
                            {
                                _mediaManager.ChangeStream(_lastStreamObject, _streamURLs[(int)_videoWindow.FeedType], _videoWindow.Size.Value.X);
                            }
                            catch (Exception e)
                            {
                                _pluginLog.Warning(e, e.Message);
                            }
                        }
                    }
                }
            }
        }

        private void OnVideoWindowResized(object sender, EventArgs e)
        {
            ChangeStreamQuality();
        }

        private void _mediaManager_OnNewMediaTriggered(object sender, EventArgs e)
        {
            EnqueueFrameworkAction(() => {
                _isSyncing = false;
                _consecutiveLocalFailures = 0;
                _consecutiveSyncFailures = 0;
                PrintChat("[媒体播放器] 正在启动流...");
            });
        }

        private void _mediaManager_OnPlaybackFinished(object? sender, string e)
        {
            PrintChat("[媒体播放器] 播放完毕");

            if (!string.IsNullOrEmpty(_lastStreamURL))
            {
                _config.WatchHistory.Remove(_lastStreamURL);
                _config.Save();
            }

            if (_mediaQueue.Count == 0 || e == "Emulation")
            {
                ResetStreamValues();
            }
            else
            {
                PlayNext();
            }
        }

        private unsafe void ResetStreamValues(bool pushToServer = true)
        {
            _lastStreamObject = null;
            _streamURLs = null;
            _potentialStream = "";
            _lastStreamURL = "";
            _currentMediaDurationMs = null;
            _currentStreamer = "";
            _currentMediaTitle = "";
            _videoWindow.IsOpen = false;
            _emulationClient?.Dispose();
            _emulationClient = null;
            _controllerService?.Dispose();
            _controllerService = null;
            _cefBrowserHandle?.Dispose();
            _cefBrowserHandle = null;

            bool wasPlaying = _streamWasPlaying;
            _streamWasPlaying = false;
            _streamSetCooldown.Stop();
            _streamSetCooldown.Reset();
            _mediaErrorCount = 0; // Reset error count when stream stops

            if (wasPlaying)
            {
                _deferredBgmRestoreTime = DateTime.UtcNow.AddSeconds(1);
            }

            if (pushToServer) {
                // No-op: v2 heartbeat handles pushing state
            }
        }

    }
}
