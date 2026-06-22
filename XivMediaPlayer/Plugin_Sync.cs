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

        private bool _isDraggingSeek => _videoWindow?.IsDraggingSeek ?? false;

        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, ct);
                    if (!_isLocalDj) continue;
                    if (_isTransitioning) { Debug.WriteLine("[Sync] Heartbeat Skip: isTransitioning"); continue; }
                    if (_isDraggingSeek) { Debug.WriteLine("[Sync] Heartbeat Skip: isDraggingSeek"); continue; }

                    var snapshot = _mediaManager?.GetSnapshot();
                    if (snapshot == null || string.IsNullOrEmpty(snapshot.Url)) continue;

                    var request = new HeartbeatRequest
                    {
                        OwnerId = _config.OwnerId,
                        StateVersion = _stateVersion,
                        CurrentUrl = _lastStreamURL,
                        TimecodeMs = snapshot.TimeMs,
                        IsPlaying = snapshot.IsPlaying,
                        SpeedRate = 1.0f,
                        Queue = _mediaQueue.ToList()
                    };

                    var result = await ServerClient.HeartbeatAsync(LocationKey, request);
                    if (result.Success)
                    {
                        _stateVersion = result.Data!.AcceptedVersion;
                        Debug.WriteLine("[Sync] Heartbeat: v=" + _stateVersion + ", url=" + _lastStreamURL + ", time=" + snapshot.TimeMs + "ms, playing=" + snapshot.IsPlaying + ", result=200");
                    }
                    else
                    {
                        Debug.WriteLine("[Sync] Heartbeat: result=409, error=" + result.Error);
                        _isLocalDj = false;
                        StopHeartbeatLoop();
                        StartFetchLoop();
                        PrintChatError("播放权已被接管，切换到跟随模式");
                    }

                    if (snapshot.IsPlaying)
                    {
                        if (_lastSuccessfulTimecode == snapshot.TimeMs)
                        {
                            _stalledDetectCount++;
                            if (_stalledDetectCount >= 5)
                            {
                                Debug.WriteLine("[VLC] Stalled: time=" + snapshot.TimeMs + "ms, sameFor=" + (_stalledDetectCount * 2) + "s, action=retry");
                                _stalledDetectCount = 0;
                                RefreshCurrentMedia();
                            }
                        }
                        else
                        {
                            _stalledDetectCount = 0;
                            _lastSuccessfulTimecode = snapshot.TimeMs;
                        }
                    }
                }
                catch (OperationCanceledException) { /* normal shutdown, ignore */ }
                catch (Exception ex) { _pluginLog.Warning(ex, "[Sync] Heartbeat loop error"); }
            }
        }

        private void StartHeartbeatLoop()
        {
            StopHeartbeatLoop();
            _heartbeatCts = new CancellationTokenSource();
            _ = HeartbeatLoopAsync(_heartbeatCts.Token);
        }
        private void StopHeartbeatLoop()
        {
            _heartbeatCts?.Cancel();
            _heartbeatCts?.Dispose();
            _heartbeatCts = null;
        }

        private async Task FetchLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(5000, ct);
                    if (_isLocalDj) continue;
                    if (!_config.SyncWithRoom) continue;
                    if (_isSyncing) { Debug.WriteLine("[Sync] Follow Skip: isSyncing"); continue; }

                    var state = await ServerClient.GetStateAsync(LocationKey);
                    if (state == null) continue;

                    Debug.WriteLine("[Sync] State Fetch: v=" + state.StateVersion + ", djAge=" + state.DjHeartbeatAgeSeconds.ToString("F0") + "s, djDisconnected=" + state.DjDisconnected + ", url=" + state.CurrentUrl);

                    if (state.DjDisconnected)
                    {
                        Debug.WriteLine("[Sync] Follow Skip: djDisconnected");
                        continue;
                    }

                    if (string.IsNullOrEmpty(state.CurrentUrl)) continue;

                    bool urlChanged = _lastStreamURL != state.CurrentUrl;
                    bool versionChanged = state.StateVersion > _stateVersion;
                    _stateVersion = state.StateVersion;

                    if (urlChanged || versionChanged)
                    {
                        _consecutiveSyncFailures = 0;
                        _isSyncing = true;
                        int syncStartMs = _config.ForceSyncProgress ? (int)state.TimecodeMs : 0;
                        Debug.WriteLine("[Sync] Follow: reason=" + (urlChanged ? "urlChanged" : "versionChanged") + ", action=play, forceSyncProgress=" + _config.ForceSyncProgress);
                        PlayRouted(state.CurrentUrl, CurrentAudioSource, syncStartMs, isAutoSync: true);
                    }
                    else
                    {
                        if (!_config.ForceSyncProgress) continue;

                        var active = _mediaManager?.GetActiveStream();
                        if (active == null) continue;

                        long timeDiff = Math.Abs(active.Time - state.TimecodeMs);
                        if (timeDiff > 5000 && state.IsPlaying)
                        {
                            Debug.WriteLine("[Sync] Follow: reason=sameUrl, action=seek, diff=" + timeDiff + "ms");
                            _mediaManager.SeekStream(state.TimecodeMs);
                        }
                        if (state.IsPlaying && active.PlaybackState == NAudio.Wave.PlaybackState.Paused)
                        {
                            Debug.WriteLine("[Sync] Follow: reason=sameUrl, action=resume");
                            active.Resume();
                        }
                        if (!state.IsPlaying && active.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                        {
                            Debug.WriteLine("[Sync] Follow: reason=sameUrl, action=pause");
                            active.Pause();
                        }
                    }
                }
                catch (OperationCanceledException) { /* normal shutdown, ignore */ }
                catch (Exception ex) { _pluginLog.Warning(ex, "[Sync] Fetch loop error"); }
            }
        }

        private void StartFetchLoop()
        {
            StopFetchLoop();
            _fetchCts = new CancellationTokenSource();
            _ = FetchLoopAsync(_fetchCts.Token);
        }
        private void StopFetchLoop()
        {
            _fetchCts?.Cancel();
            _fetchCts?.Dispose();
            _fetchCts = null;
        }

        private async Task<bool> ClaimDjAsync()
        {
            var request = new ClaimDjRequest { OwnerId = _config.OwnerId, ExpectedVersion = _stateVersion };
            var result = await ServerClient.ClaimDjAsync(LocationKey, request);
            Debug.WriteLine("[Sync] Claim DJ: ownerId=" + _config.OwnerId + ", result=" + (result.Success ? "200" : "409") + ", version=" + _stateVersion);
            if (result.Success)
            {
                _isLocalDj = true;
                _stateVersion = result.Data!.CurrentVersion;
                StartHeartbeatLoop();
                StopFetchLoop();
                return true;
            }
            PrintChatError("房间已有 DJ 在播放，请稍后再试");
            return false;
        }

        private async Task ReleaseDjAsync()
        {
            if (!_isLocalDj) return;
            var request = new ReleaseDjRequest { OwnerId = _config.OwnerId };
            await ServerClient.ReleaseDjAsync(LocationKey, request);
            _isLocalDj = false;
            StopHeartbeatLoop();
            Debug.WriteLine("[Sync] Release DJ: ownerId=" + _config.OwnerId + ", reason=manual");
        }

        /// Generates a unique key for the current location.
        public string LocationKey => GetLocationKey();

        /// <summary>
        /// Generates a unique string identifier for the current in-game location.
        /// Regular zones: "zone_{territoryId}"
        /// Housing: "house_{worldId}_{territoryId}_{ward}_{plot}_{room}"
        /// </summary>
        public List<string> GetCurrentLocationKeys()
        {
            var keys = new List<string>();
            var key = GetLocationKey();
            if (!string.IsNullOrEmpty(key))
            {
                keys.Add(key);
            }
            return keys;
        }

        public unsafe string GetLocationKey()
        {
            try
            {
                var territoryId = _clientState.TerritoryType;
                if (territoryId == 0) return null;

                ushort worldId = (ushort)_cachedLocalPlayerWorldId;

                var housingMgr = FFXIVClientStructs.FFXIV.Client.Game.HousingManager.Instance();
                short ward = housingMgr != null ? housingMgr->GetCurrentWard() : (short)-1;
                short plot = housingMgr != null ? housingMgr->GetCurrentPlot() : (short)-1;
                short room = housingMgr != null ? housingMgr->GetCurrentRoom() : (short)-1;
                ulong indoorHouseId = housingMgr != null ? housingMgr->GetCurrentIndoorHouseId().Id : 0;

                if (housingMgr != null && housingMgr->IsInside())
                {
                    return $"house_{worldId}_{territoryId}_{ward}_{plot}_{room}_{indoorHouseId}";
                }

                if (territoryId == 1055)
                {
                    var mji = FFXIVClientStructs.FFXIV.Client.Game.MJI.MJIManager.Instance();
                    if (mji != null && mji->IsPlayerInSanctuary)
                    {
                        var localPlayer = GetLocalPlayer();
                        if (localPlayer != null)
                        {
                            return $"island_{worldId}_{localPlayer.Name.TextValue}";
                        }
                    }
                    else
                    {
                        // Visiting another island. Try to guess owner from party leader, or use automated fallback.
                        if (_partyList.Length > 0 && _partyList[0] != null)
                        {
                            return $"island_{worldId}_{_partyList[0].Name.TextValue}";
                        }

                        // Fallback: guess the owner based on the first other player in the object table.
                        // The island owner is almost always the first person in the instance.
                        var lp = GetLocalPlayer();
                        foreach (var obj in _objectTable)
                        {
                            if (obj is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc && 
                                lp != null && pc.Name.TextValue != lp.Name.TextValue)
                            {
                                return $"island_{worldId}_{pc.Name.TextValue}";
                            }
                        }

                        return null; // Don't know who we are visiting
                    }
                }

                var playerPos = _cachedLocalPlayerPosition;
                if (playerPos != null)
                {
                    int gridX = (int)Math.Floor(playerPos.Value.X / 50.0f);
                    int gridZ = (int)Math.Floor(playerPos.Value.Z / 50.0f);
                    return $"zone_{worldId}_{ward}_{territoryId}_grid_{gridX}_{gridZ}";
                }

                return $"zone_{worldId}_{ward}_{territoryId}";
            }
            catch
            {
                return $"zone_0_-1_{_clientState.TerritoryType}";
            }
        }

        private void OnLogin()
        {
            _hasBeenInitialized = false;
        }

        private void OnLogout(int type, int code)
        {
            if (_videoWindow != null) _videoWindow.IsOpen = false;
            if (_screenSettingsWindow != null) _screenSettingsWindow.IsOpen = false;
            if (_settingsWindow != null) _settingsWindow.IsOpen = false;
            _mediaManager?.CleanSounds();
            ResetStreamValues();
        }

        private int _mediaErrorCount = 0;
        private DateTime _lastMediaErrorTime = DateTime.MinValue;

        private DateTime _lastMediaRefreshTime = DateTime.MinValue;

        private int _mediaErrorRetryDelayMs = 5000; // starts at 5s, doubles each retry

        private void OnMediaError(object? sender, MediaError e)
        {
            string errorMsg = e.Exception?.Message ?? string.Empty;

            _pluginLog.Warning(e.Exception, $"[VLC] Error: {errorMsg}");

            if (errorMsg.Contains("Failed to set on top", StringComparison.OrdinalIgnoreCase))
                return;

            // Harmless VLC clock/timestamp warnings — recover on their own, not playback failures
            if (errorMsg.Contains("Timestamp conversion failed", StringComparison.OrdinalIgnoreCase)) return;
            if (errorMsg.Contains("Could not convert timestamp", StringComparison.OrdinalIgnoreCase)) return;
            if (errorMsg.Contains("no reference clock", StringComparison.OrdinalIgnoreCase)) return;
            if (errorMsg.Contains("TS discontinuity", StringComparison.OrdinalIgnoreCase)) return;
            if (errorMsg.Contains("libdvbpsi", StringComparison.OrdinalIgnoreCase)) return;

            if ((DateTime.UtcNow - _lastMediaErrorTime).TotalMilliseconds < 500)
            {
                _pluginLog.Warning(e.Exception, "[VLC] Media error occurred (grouped within 500ms).");
                return;
            }

            _lastMediaErrorTime = DateTime.UtcNow;
            _mediaErrorCount++;
            _pluginLog.Warning(e.Exception, $"[VLC] Media error occurred. Error count: {_mediaErrorCount}");

            int maxRetries = 10;
            if (_mediaErrorCount < maxRetries)
            {
                // Exponential backoff: 5s, 10s, 20s, 40s, 80s, 160s...
                int delay = _mediaErrorRetryDelayMs;
                _mediaErrorRetryDelayMs *= 2;
                _pluginLog.Information($"[VLC] Error #{_mediaErrorCount}/{maxRetries}, retrying in {delay / 1000}s");
                Task.Delay(delay).ContinueWith(_ =>
                {
                    if (_mediaManager?.GetActiveStream() != null)
                        RequestRefreshCurrentMedia();
                });
            }
            else if (_mediaErrorCount == maxRetries)
            {
                PrintChatError($"[媒体播放器] {maxRetries}次尝试后仍无法播放，正在重启播放内核...");
                _mediaErrorCount = 0;
                _mediaErrorRetryDelayMs = 5000;
                EnqueueFrameworkAction(() => RequestKillAndRestart());
            }
        }

        private void UpdateWatchHistory()
        {
            if (string.IsNullOrEmpty(_lastStreamURL) || _mediaManager?.GetActiveStream() == null) return;
            
            long time = _mediaManager.GetActiveStream().Time;
            long length = _mediaManager.GetActiveStream().Length;
            
            // Only track media that has been watched for at least 5 seconds
            if (time <= 5000) return;
            
            // If it has reached within 5 seconds of the end, don't update it, let the End hook remove it
            if (length > 0 && time >= length - 5000) return;

            string title = !string.IsNullOrEmpty(_currentMediaTitle) && _currentMediaTitle != "Loading..." 
                ? _currentMediaTitle 
                : _lastStreamURL;

            var entry = new MediaHistoryEntry {
                Url = _lastStreamURL,
                Title = title,
                TimecodeMs = time,
                LastPlayed = DateTime.UtcNow
            };
            
            _config.WatchHistory[_lastStreamURL] = entry;
            _config.Save();
        }

        /// <summary>
        /// Mutes the in-game BGM, saving the previous state so we can restore it.
        /// </summary>
        private void MuteBgm()
        {
            try
            {
                _gameConfig.TryGet(SystemConfigOption.IsSndBgm, out bool wasMuted);
                if (!wasMuted)
                {
                    _bgmWasMutedByUs = true;
                    _gameConfig.Set(SystemConfigOption.IsSndBgm, true);
                }
            }
            catch (Exception e)
            {
                _pluginLog.Warning(e, "[Media Player] Failed to mute BGM");
            }
        }

        /// <summary>
        /// Restores BGM if we were the ones who muted it.
        /// </summary>
        private void RestoreBgm()
        {
            try
            {
                if (_bgmWasMutedByUs)
                {
                    _bgmWasMutedByUs = false;
                    _gameConfig.Set(SystemConfigOption.IsSndBgm, false);
                }
            }
            catch (Exception e)
            {
                _pluginLog.Warning(e, "[Media Player] Failed to restore BGM");
            }
        }

        /// <summary>
        /// Fixes the Windows audio mixer volume for this process.
        /// VLC can zero it out via the Windows audio session API.
        /// Uses COM interfaces + WaveOutEvent trick (same approach as ArtemisRoleplayingKit).
        /// </summary>
        private void FixWindowsVolume()
        {
            try
            {
                int pid = Process.GetCurrentProcess().Id;
                VolumeMixer.SetApplicationVolume(pid, 100);
                VolumeMixer.SetApplicationMute(pid, false);

                // Force Windows to re-register the audio session at full volume
                using (var tempPlayer = new NAudio.Wave.WaveOutEvent())
                {
                    tempPlayer.Volume = 1;
                }
            }
            catch (Exception e)
            {
                _pluginLog.Warning(e, "[Media Player] Failed to fix Windows volume");
            }
        }

    }
}
