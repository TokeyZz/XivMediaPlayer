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
            ResetStreamValues();
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
                        _worldRenderer.Transform.Opacity = tv.Opacity;
                        _worldRenderer.Transform.IsProjectorMode = tv.IsProjectorMode;
                        _worldRenderer.Transform.ScreensaverColor = new System.Numerics.Vector3(tv.ScreensaverColorR, tv.ScreensaverColorG, tv.ScreensaverColorB);
                        _worldRenderer.Transform.ScreensaverStyle = tv.ScreensaverStyle;

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
                        Enabled = true,
                        Opacity = tv.Opacity,
                        IsProjectorMode = tv.IsProjectorMode,
                        ScreensaverColor = new System.Numerics.Vector3(tv.ScreensaverColorR, tv.ScreensaverColorG, tv.ScreensaverColorB),
                        ScreensaverStyle = tv.ScreensaverStyle
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
                _worldRenderer.Transform.Opacity = saved.Opacity;
                _worldRenderer.Transform.IsProjectorMode = saved.IsProjectorMode;
                _worldRenderer.Transform.ScreensaverColor = saved.ScreensaverColor;
            }
            else
            {
                _worldRenderer.Transform.Position = System.Numerics.Vector3.Zero;
                _worldRenderer.Transform.RotationDegrees = System.Numerics.Vector3.Zero;
                _worldRenderer.Transform.Scale = new System.Numerics.Vector2(3.0f, 1.6875f);
                _worldRenderer.Transform.Enabled = false; // Turn off 3D screen in new zones by default
                _worldRenderer.Transform.Opacity = 1.0f;
                _worldRenderer.Transform.IsProjectorMode = false;
                _worldRenderer.Transform.ScreensaverColor = new System.Numerics.Vector3(0.0f, 0.0f, 0.0f);
                _worldRenderer.Transform.ScreensaverStyle = 0;
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

    }
}
