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

        private void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage msg)
        {
            if (_disposed || _mediaManager == null) return;

            var type = msg.LogKind;
            if (type == XivChatType.Yell || type == XivChatType.Shout || type == XivChatType.TellIncoming)
            {
                TwitchChatCheck(msg.Message.TextValue, type);
            }
        }

        private unsafe void TwitchChatCheck(string messageText, XivChatType type)
        {
            if (_config.TuneIntoTwitchStreams && IsResidential())
            {
                if (!_streamSetCooldown.IsRunning || _streamSetCooldown.ElapsedMilliseconds > 10000)
                {
                    var strings = messageText.Split(' ');
                    foreach (string value in strings)
                    {
                        if (value.Contains("twitch.tv") && _lastStreamURL != value)
                        {
                            if (_playerObject != null)
                            {
                                var audioGameObject = CurrentAudioSource;
                                if (_mediaManager.IsAllowedToStartStream(audioGameObject))
                                {
                                    _lastStreamObject = CurrentAudioSource;
                                    PlayRouted(value.Trim('(').Trim(')').Trim('[').Trim(']').Trim('!').Trim('@'), audioGameObject, 0);
                                }
                            }
                            break;
                        }
                    }
                }
            }
            else if (_config.TuneIntoTwitchStreamPrompt)
            {
                var strings = messageText.Split(' ');
                foreach (string value in strings)
                {
                    if (value.Contains("twitch.tv") && _lastStreamURL != value)
                    {
                        _potentialStream = value;
                        _lastStreamURL = value;
                        string cleanedURL = RemoveSpecialSymbols(value);
                        string streamer = cleanedURL.Replace(@"https://", null).Replace(@"www.", null).Replace("twitch.tv/", null);
                        PrintChat("[媒体播放器] " + streamer + " 正在直播! 使用 \"/media listen\" 收听", ChatSeverity.Info);
                    }
                }
            }
        }

        private unsafe bool IsResidential()
        {
            return HousingManager.Instance()->IsInside() || HousingManager.Instance()->OutdoorTerritory != null;
        }


        private void OnTerritoryChanged(uint territoryId)
        {
            SaveMediaStateForCurrentLocation();
            _ = ReleaseDjAsync();
            _videoWindow.IsOpen = false;
            if (_screenSettingsWindow != null) _screenSettingsWindow.IsOpen = false;
            _mediaManager?.CleanSounds();
            ResetStreamValues(false);
            CurrentTvPlacement = null;

            _deferredTerritoryChangeTime = DateTime.UtcNow.AddSeconds(3);
        }

        private async Task FetchServerDataForCurrentLocationAsync()
        {
            // Fetch public TVs from the server
            var keys = GetCurrentLocationKeys();
            if (keys.Count == 0) return;

            string primaryKey = GetLocationKey();
            bool isHouse = !string.IsNullOrEmpty(primaryKey) && primaryKey.StartsWith("house_");
            bool isZone = !string.IsNullOrEmpty(primaryKey) && primaryKey.StartsWith("zone_");

            if (isHouse || (isZone && _config.EnableOutdoorPublicScreens))
            {
                var tvs = await ServerClient.GetTvsBatchAsync(keys);
                _nearbyTvs = tvs;

                if (tvs.Count > 0)
                {
                    // If multiple TVs are found, select the one closest to the player
                    if (tvs.Count > 1)
                    {
                        var playerPos = _cachedLocalPlayerPosition;
                        if (playerPos != null)
                        {
                            tvs = tvs.OrderBy(t => System.Numerics.Vector3.Distance(playerPos.Value, new System.Numerics.Vector3(t.PositionX, t.PositionY, t.PositionZ))).ToList();
                        }
                    }

                    var tv = tvs[0];
                    CurrentTvPlacement = tv;
                    
                    // Apply to the active renderer transform only if we aren't actively editing it.
                    // Server placement state is authoritative, if a TV exists on the server,
                    // render it unless the owner removes it from the area.
                    if (_worldRenderer != null && !IsHousingMenuOpen && !(_screenSettingsWindow?.IsOpen == true))
                    {
                        _worldRenderer.Transform.Enabled = true;
                        _worldRenderer.Transform.Position = new System.Numerics.Vector3(tv.PositionX, tv.PositionY, tv.PositionZ);
                        _worldRenderer.Transform.RotationDegrees = new System.Numerics.Vector3(tv.RotationX, tv.RotationY, tv.RotationZ);
                        _worldRenderer.Transform.Scale = new System.Numerics.Vector2(tv.ScaleX, tv.ScaleY);
                        
                        _screenSettingsWindow?.SyncFromTransform();
                    }

                    // Persist server coordinates into local config so that RestoreScreenForCurrentLocation
                    // (which fires before this async fetch completes) won't push stale coordinates.
                    // This prevents co-owners of the same house from overwriting each other's TV placement.
                    var serverKey = !string.IsNullOrEmpty(tv.LocationKey) ? tv.LocationKey : primaryKey;
                    _config.ScreenPlacements[serverKey] = new MediaPlayerCore.Compositing.WorldScreenTransform {
                        Position = new System.Numerics.Vector3(tv.PositionX, tv.PositionY, tv.PositionZ),
                        RotationDegrees = new System.Numerics.Vector3(tv.RotationX, tv.RotationY, tv.RotationZ),
                        Scale = new System.Numerics.Vector2(tv.ScaleX, tv.ScaleY),
                        Enabled = true
                    };
                    // Also store under the primary key in case they differ (e.g. batch vs single key)
                    if (serverKey != primaryKey)
                    {
                        _config.ScreenPlacements[primaryKey] = _config.ScreenPlacements[serverKey];
                    }
                    _config.Save();

                    _pluginLog.Info($"[Social] Loaded public TV placement from room {tv.LocationKey}.");
                }
                else
                {
                    CurrentTvPlacement = null;
                    if (!IsHousingMenuOpen)
                    {
                        RestoreScreenForCurrentLocation();
                    }
                }

                // v2: Start fetch loop on room entry if not DJ and sync is enabled
                if (!_isLocalDj && _config.SyncWithRoom)
                    StartFetchLoop();
            }
            else if (isZone)
            {
                // If outdoor screens are disabled, ensure we don't hold onto a stale TV placement
                CurrentTvPlacement = null;
            }
        }

        /// <summary>
        /// Saves the current screen placement to the config for the current location.
        /// </summary>
        private void SaveScreenForCurrentLocation()
        {
            if (_worldRenderer?.Transform == null) return;
            var key = _lastLocationKey;
            if (string.IsNullOrEmpty(key)) return;
            var transform = _worldRenderer.Transform.Clone();
            transform.Enabled = _worldRenderer.Transform.Enabled;
            _config.ScreenPlacements[key] = transform;
            _config.Save();
        }

        /// <summary>
        /// Restores a saved screen placement for the current location, if one exists.
        /// </summary>
        private void RestoreScreenForCurrentLocation()
        {
            var key = GetLocationKey();
            if (string.IsNullOrEmpty(key)) return;
            if (_config.ScreenPlacements.TryGetValue(key, out var saved))
            {
                _worldRenderer.Transform.Position = saved.Position;
                _worldRenderer.Transform.RotationDegrees = saved.RotationDegrees;
                _worldRenderer.Transform.Scale = saved.Scale;
                _worldRenderer.Transform.Enabled = saved.Enabled;
            }
            else
            {
                _worldRenderer.Transform.Position = System.Numerics.Vector3.Zero;
                _worldRenderer.Transform.RotationDegrees = System.Numerics.Vector3.Zero;
                _worldRenderer.Transform.Scale = new System.Numerics.Vector2(3.0f, 1.6875f);
                _worldRenderer.Transform.Enabled = false; // Turn off 3D screen in new zones by default
            }
        }

        /// <summary>
        /// Saves the current media URL, queue, and timecode for the current location.
        /// </summary>
        private void SaveMediaStateForCurrentLocation()
        {
            var key = CurrentTvPlacement?.LocationKey ?? _lastLocationKey;
            if (string.IsNullOrEmpty(key)) return;

            var state = new RoomMediaState();

            var activeStream = _mediaManager?.GetActiveStream();
            if (activeStream != null && !string.IsNullOrEmpty(activeStream.SoundPath))
            {
                // We use _lastStreamURL to save the original un-resolved YouTube/Twitch URL
                // so we can re-resolve it via yt-dlp upon entering the room next time!
                string fallbackPath = activeStream.SoundPath;
                // Never save local StreamProxy URLs they are ephemeral and won't work on next launch or for other players
                if (fallbackPath != null && fallbackPath.Contains("127.0.0.1")) fallbackPath = "";
                state.CurrentUrl = !string.IsNullOrEmpty(_lastStreamURL) ? _lastStreamURL : fallbackPath;
                state.TimecodeMs = activeStream.Time;
            }

            state.Playlist = new System.Collections.Generic.List<string>(_mediaQueue);

            _config.RoomMediaStates[key] = state;
            _config.Save();
            _pluginLog.Information($"Saved media state for {key}: {state.CurrentUrl} @ {state.TimecodeMs}ms ({state.Playlist.Count} queued)");
        }

        /// <summary>
        /// Restores media playback from config for the current location.
        /// </summary>
        private void RestoreMediaForCurrentLocation()
        {
            var key = CurrentTvPlacement?.LocationKey ?? GetLocationKey();
            if (string.IsNullOrEmpty(key)) return;

            // Track location for future saving
            _lastLocationKey = key;

            if (_config.RoomMediaStates.TryGetValue(key, out var state))
            {
                _pluginLog.Information($"Restoring media state for {key}");

                // Restore queue
                _mediaQueue.Clear();
                if (state.Playlist != null)
                {
                    foreach (var url in state.Playlist)
                    {
                        _mediaQueue.Enqueue(url);
                    }
                }

                // Start playback if there was a URL and auto resume is enabled
                if (_config.AutoResumeMedia && !string.IsNullOrEmpty(state.CurrentUrl) && _playerObject != null)
                {
                    PrintChat($"[媒体播放器] 正在恢复本房间的播放...");
                    _lastStreamObject = CurrentAudioSource;
                    PlayRouted(state.CurrentUrl, CurrentAudioSource, (int)state.TimecodeMs, isAutoSync: true);
                }
            }
        }

        private async Task PushMediaToServerAsync(bool isBackgroundSync = false)
        {
            var key = CurrentTvPlacement?.LocationKey ?? _lastLocationKey;
            var activeStream = _mediaManager?.GetActiveStream();
            string lastUrl = CleanUrl(_lastStreamURL ?? "");
            string soundPath = activeStream?.SoundPath ?? "";
            // Never let local StreamProxy URLs (e.g. http://127.0.0.1:xxxxx/stream.m3u8?sid=...) leak as fallback
            if (soundPath.Contains("127.0.0.1")) soundPath = "";
            long activeTime = (long)(activeStream?.Time ?? 0);
            bool isIntentionallyPaused = _isIntentionallyPaused;
            var mediaQueueArray = _mediaQueue.ToArray();
            var duration = _currentMediaDurationMs;

            await Task.Run(async () =>
            {
                _pluginLog.Information($"[Sync] PushMediaToServerAsync invoked. Key: {key}");
                _pluginLog.Information($"[Sync] Attempting to push media to server for key: {key}");
                if (string.IsNullOrEmpty(key) || (!key.StartsWith("house_") && !key.StartsWith("zone_")))
                {
                    _pluginLog.Information($"[Sync] Aborting push because key is invalid.");
                    return;
                }

                // Don't push local files
                if (!string.IsNullOrEmpty(lastUrl) && !lastUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !lastUrl.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase) && !lastUrl.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase)) return;
                
                // Don't push local StreamProxy URLs to the sync server
                if (!string.IsNullOrEmpty(lastUrl) && lastUrl.Contains("127.0.0.1")) return;
                
                // Only return early if we are doing a background sync and have nothing to push.
                // If it's a foreground sync (e.g. user pressed stop, or video finished), WE MUST push the empty state!
                if (isBackgroundSync && string.IsNullOrEmpty(lastUrl) && string.IsNullOrEmpty(soundPath) && !_isResolvingMedia) return;

                var sync = new RoomMediaStateSync
                {
                    LocationKey = key,
                    CurrentUrl = !string.IsNullOrEmpty(lastUrl) ? lastUrl : soundPath,
                    TimecodeMs = activeTime,
                    // Only push "Paused" if the DJ explicitly pressed the pause button!
                    // Otherwise, random network buffering on the DJ's client will accidentally force-pause the entire room!
                    IsPlaying = !isIntentionallyPaused,
                    OwnerId = _config.OwnerId,
                    PlaylistJson = System.Text.Json.JsonSerializer.Serialize(mediaQueueArray),
                    BypassLock = IsHousingMenuOpen || key.StartsWith("zone_"),
                    DurationMs = duration,
                    IsBackgroundSync = isBackgroundSync
                };

                // Update local config immediately so we don't lose our place if we crash or the server is unavailable
                var state = new RoomMediaState
                {
                    CurrentUrl = sync.CurrentUrl,
                    TimecodeMs = sync.TimecodeMs,
                    Playlist = new List<string>(mediaQueueArray)
                };
                _config.RoomMediaStates[key] = state;
                _config.Save();

                try
                {
                    await ServerClient.UpdateMediaStateAsync(key, sync);
                    _currentMediaOwnerId = _config.OwnerId;

                    // If we successfully pushed a foreground sync, we are definitely the DJ now.
                    if (!isBackgroundSync)
                    {
                        PrintChat($"[媒体播放器] 服务端推送成功", ChatSeverity.Info);
                        _isLocalDj = true;
                        _currentMediaOwnerId = _config.OwnerId;
                    }
                    _pluginLog.Information($"[Sync] Payload successfully pushed to server.");
                }
                catch (InvalidOperationException)
                {
                    // We were deposed as the DJ!
                    _isLocalDj = false;
                    _currentMediaOwnerId = "";
                    await FetchMediaFromServerAsync();
                }
                catch (UnauthorizedAccessException)
                {
                    _isLocalDj = false;
                    _currentMediaOwnerId = "";
                    PrintChatError("[媒体播放器] 无法分享媒体: 本房间的电视已被房主锁定");
                    await FetchMediaFromServerAsync();
                }
                catch (ArgumentException ex)
                {
                    _isLocalDj = false; // Strip DJ status so background sync stops spamming the server
                    _currentMediaOwnerId = "";

                    if (IsPlayerAlone())
                    {
                        PrintChat($"[媒体播放器] {ex.Message} (因为你独自一人,仅本地播放)", ChatSeverity.Info);
                    }
                    else
                    {
                        PrintChatError($"[媒体播放器] {ex.Message} 因为周围有其他玩家, 无法分享视频");
                        await FetchMediaFromServerAsync();
                    }
                }
                catch (HttpRequestException ex)
                {
                    _pluginLog.Warning(ex, "[Sync] Server connection failed.");
                    if (!isBackgroundSync)
                    {
                        PrintChatError("[媒体播放器] 无法连接到同步服务器, 可能已离线");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Error(ex, "[Sync] Failed to push media state to server.");
                }
            });
        }

        private async Task FetchServerTimeAsync()
        {
            if (ServerClient == null) return;
            long? st = await ServerClient.GetServerTimeAsync();
            if (st > 0) {
                _serverTimeOffsetMs = st!.Value - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _pluginLog.Information($"[Time Sync] Server time offset calculated: {_serverTimeOffsetMs}ms");
            } else {
                _hasFetchedServerTime = false;
            }
        }

        public async Task FetchMediaFromServerAsync()
        {
            var key = CurrentTvPlacement?.LocationKey ?? _lastLocationKey;
            _pluginLog.Information($"[Sync] FetchMediaFromServerAsync invoked. Key: {key}");
            if (string.IsNullOrEmpty(key)) return;
            bool isHouse = key.StartsWith("house_");
            bool isZone = key.StartsWith("zone_");
            if (!isHouse && !(isZone && _config.EnableOutdoorPublicScreens)) return;

            var sync = await ServerClient.GetMediaStateAsync(key);
            if (sync == null || string.IsNullOrEmpty(sync.CurrentUrl)) return;

            _currentMediaOwnerId = sync.OwnerId;

            if (_isLocalDj) return;
            if (!_config.SyncWithRoom) return;

            int realPlayerCount = _cachedRealPlayerCount;
            bool isRoomEmpty = realPlayerCount <= 1; // 1 means only we are here

            _pluginLog.Information($"[Social] Reclaim Check: RealPlayers={realPlayerCount}, isRoomEmpty={isRoomEmpty}, Owner={sync.OwnerId}=={_config.OwnerId}, LocalStateFound={_config.RoomMediaStates.TryGetValue(key, out var localState)}, CurrentUrl={localState?.CurrentUrl}=={sync.CurrentUrl}");

            if (isRoomEmpty && sync.OwnerId == _config.OwnerId && localState != null && localState.CurrentUrl == sync.CurrentUrl)
            {
                _pluginLog.Information("[Social] Reclaiming DJ status and trusting local timecode over server state. The room was empty.");
                _isLocalDj = true;
                return;
            }

            // Use the DataAgeMs calculated purely by the server to completely eliminate client clock drift issues!
            // We only add the age if the video is currently playing.
            var targetTimeMs = sync.IsPlaying ? sync.TimecodeMs + (long)sync.DataAgeMs : sync.TimecodeMs;

            _pluginLog.Information($"[Social] Fetched media sync: Server TimecodeMs={sync.TimecodeMs}, DataAgeMs={sync.DataAgeMs}. Calculated TargetTimeMs={targetTimeMs}.");

            // Update local config
            var state = new RoomMediaState
            {
                CurrentUrl = sync.CurrentUrl,
                TimecodeMs = targetTimeMs,
                Playlist = new List<string>(System.Text.Json.JsonSerializer.Deserialize<string[]>(sync.PlaylistJson) ?? Array.Empty<string>())
            };
            _config.RoomMediaStates[key] = state;
            _config.Save();

            // Actually play it if it's different or out of sync
            if (_mediaManager != null && _mediaManager.IsFFmpegPlaying)
            {
                return; // NEVER interrupt a local FFmpeg stream with a server sync!
            }
            if (_isResolvingMedia) return;

            var activeStream = _mediaManager?.GetActiveStream();
            bool isLocalEnded = activeStream != null && activeStream.VlcState == LibVLCSharp.Shared.VLCState.Ended;
            bool isDifferentUrl = activeStream == null || (!string.IsNullOrEmpty(_lastStreamURL) && _lastStreamURL != sync.CurrentUrl) || (isLocalEnded && sync.IsPlaying);
            // Only sync VODs. Live streams cannot be reliably timecode-synced.
            bool isOutofSync = !_lastStreamIsLive && activeStream != null && activeStream.Length > 0 && Math.Abs(activeStream.Time - targetTimeMs) > 5000;
            bool localIsPlaying = activeStream != null && activeStream.PlaybackState == NAudio.Wave.PlaybackState.Playing;

            if (isDifferentUrl)
            {
                _pluginLog.Information($"[Social] Syncing NEW media from server: {sync.CurrentUrl} at {targetTimeMs}ms (Playing: {sync.IsPlaying})");
                EnqueueFrameworkAction(() =>
                {
                    PrintChat($"[媒体播放器] 服务端同步: 正在播放房主设置的媒体");

                    _mediaQueue.Clear();
                    foreach (var url in state.Playlist) _mediaQueue.Enqueue(url);

                    if (_playerObject != null)
                    {
                        // Starts the stream. If sync.IsPlaying is false, we should pause it immediately after it loads...
                        // But yt-dlp might take a while, so we just let it start and the next poll will poll it.
                        PlayRouted(state.CurrentUrl, CurrentAudioSource, (int)targetTimeMs, isAutoSync: true);
                    }
                });
            }
            else if (activeStream != null)
            {
                if (isOutofSync)
                {
                    // If the server is paused, and the data is old, ignore timecode sync!
                    if (!sync.IsPlaying && sync.DataAgeMs >= 15000 && localIsPlaying)
                    {
                        _pluginLog.Information("[Social] Ignoring timecode sync because server is paused and data is stale.");
                    }
                    else
                    {
                        _pluginLog.Information($"[Social] Adjusting timecode to sync with server. Local Time: {activeStream.Time}ms | Target Time: {targetTimeMs}ms");
                        activeStream.Time = (long)targetTimeMs;
                    }
                }

                if (!_lastStreamIsLive)
                {
                    if (sync.IsPlaying && !localIsPlaying)
                    {
                        _pluginLog.Information($"[Social] Server says play, but we are paused. Resuming.");
                        activeStream.Resume();
                    }
                    else if (!sync.IsPlaying && localIsPlaying)
                    {
                        bool isNewlyLoaded = (DateTime.UtcNow - _lastUrlLoadTime).TotalSeconds < 20;

                        // Check sync staleness or new stream status
                        if (sync.DataAgeMs < 15000 || isNewlyLoaded)
                        {
                            _pluginLog.Information($"[Social] Server says paused (NewlyLoaded: {isNewlyLoaded}). Pausing.");
                            activeStream.Pause();
                        }
                        else
                        {
                            _pluginLog.Information($"[Social] Server says paused, but it is {sync.DataAgeMs}ms old. Ignoring.");
                        }
                    }
                }
            }
        }

        /// <summary>
    }
}
