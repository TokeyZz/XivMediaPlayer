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

        private unsafe void OnFrameworkUpdate(IFramework framework)
        {
            if (_disposed) return;

            while (_frameworkActions.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    _pluginLog.Warning(e, "[Media Player] Framework action failed.");
                }
            }

            if (!_clientState.IsLoggedIn) return;

            var localPlayerObj = GetLocalPlayer();
            if (localPlayerObj != null) {
                _playerObject?.Update(localPlayerObj);
            }
            
            _playerCamera?.Update();
            
            if (_worldRenderer?.Transform.Enabled == true && _tvAudioObject != null) {
                _tvAudioObject.SetPosition(_worldRenderer.Transform.Position);
            }

            // Cache local player data for background threads to avoid "Not on main thread!" exceptions
            var localPlayer = _objectTable[0] as Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter;
            _cachedLocalPlayerPosition = localPlayer?.Position;
            _cachedLocalPlayerWorldId = localPlayer?.CurrentWorld.RowId ?? 0;

            // Cache real player count safely on the main thread for background sync tasks
            int realPlayerCount = 0;
            foreach (var obj in _objectTable)
            {
                if (obj is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter)
                {
                    string name = obj.Name.TextValue.ToLowerInvariant();
                    if (!name.Contains("reborn") && !name.Contains("cnpc"))
                    {
                        realPlayerCount++;
                    }
                }
            }
            _cachedRealPlayerCount = realPlayerCount;

            if (_deferredTerritoryChangeTime.HasValue && DateTime.UtcNow >= _deferredTerritoryChangeTime.Value)
            {
                // Wait until local player and housing manager are fully loaded
                bool isHousingLoaded = true;
                if (_clientState.TerritoryType != 0)
                {
                    unsafe
                    {
                        var housingMgr = FFXIVClientStructs.FFXIV.Client.Game.HousingManager.Instance();
                        if (housingMgr != null && housingMgr->IsInside())
                        {
                            if (housingMgr->GetCurrentIndoorHouseId().Id == 0)
                            {
                                isHousingLoaded = false;
                            }
                        }
                    }

                    if (_cachedLocalPlayerWorldId == 0)
                    {
                        isHousingLoaded = false;
                    }
                }

                if (isHousingLoaded)
                {
                    _deferredTerritoryChangeTime = null;
                    RestoreScreenForCurrentLocation();
                    RestoreMediaForCurrentLocation();
                    _ = FetchServerDataForCurrentLocationAsync();
                }
                else
                {
                    // Delay another 0.5s to wait for loading to finish
                    _deferredTerritoryChangeTime = DateTime.UtcNow.AddSeconds(0.5);
                }
            }

            if (_deferredBgmRestoreTime.HasValue && DateTime.UtcNow >= _deferredBgmRestoreTime.Value)
            {
                unsafe
                {
                    if (!Conditions.Instance()->BetweenAreas)
                    {
                        _deferredBgmRestoreTime = null;
                        RestoreBgm();
                    }
                }
            }

            if (!_hasBeenInitialized && _clientState.IsLoggedIn)
            {
                if (!_dependencyManager.IsReady)
                {
                    if (!_dependencyManager.IsDownloading && !_dependencyManager.HasError)
                    {
                        _ = _dependencyManager.DownloadDependenciesAsync();
                    }
                    return;
                }

                try
                {
                    InitializeMediaManager();
                    // Validate initialization success
                    _hasBeenInitialized = _playerObject != null;
                    if (_hasBeenInitialized)
                    {
                        // Restore saved screen placement for current location on plugin load
                        RestoreScreenForCurrentLocation();

                        // Automatically fetch TV placement and media state from the server if already in a house
                        _ = FetchServerDataForCurrentLocationAsync();
                    }
                }
                catch (Exception e)
                {
                    _pluginLog.Error(e, "Failed to initialize media manager");
                }
            }

            // Auto-open/close screen placement menu based on Housing Menu state
            unsafe
            {
                var housingGoods = _gameGui.GetAddonByName("HousingGoods", 1);
                var mjiFurnishing = _gameGui.GetAddonByName("MJIFurnishing", 1);
                var mjiHousingGoods = _gameGui.GetAddonByName("MJIHousingGoods", 1);
                var mjiFurnishingGlamour = _gameGui.GetAddonByName("MJIFurnishingGlamour", 1);
                
                bool isHousingMenuOpen = (housingGoods != IntPtr.Zero) || (mjiFurnishing != IntPtr.Zero) || (mjiHousingGoods != IntPtr.Zero) || (mjiFurnishingGlamour != IntPtr.Zero);

                if (isHousingMenuOpen && !_wasHousingMenuOpen)
                {
                    _wasHousingMenuOpen = isHousingMenuOpen;
                    _screenSettingsWindow.IsOpen = true;
                    _screenSettingsWindow.SyncFromTransform();

                    if (CurrentTvPlacement != null && CurrentTvPlacement.OwnerId != _config.OwnerId && !string.IsNullOrEmpty(LocationKey))
                    {
                        // Only re-register if the server TV belongs to someone else.
                        // If it's ours already, the server coordinates are authoritative and we don't
                        // want to overwrite them with stale local coordinates.
                        CurrentTvPlacement.OwnerId = _config.OwnerId;
                        _pluginLog.Info($"[Social] Automatically restoring TV ownership for {LocationKey} because housing menu was opened.");
                        _screenSettingsWindow.RegisterTvAsync(LocationKey);
                    }
                }
                else if (!isHousingMenuOpen && _wasHousingMenuOpen)
                {
                    _wasHousingMenuOpen = isHousingMenuOpen;
                    _screenSettingsWindow.IsOpen = false;

                    // Auto-save and register TV when closing the menu
                    if (!string.IsNullOrEmpty(LocationKey) && LocationKey.StartsWith("house_"))
                    {
                        _screenSettingsWindow.RegisterTvAsync(LocationKey);
                    }
                }
                else
                {
                    _wasHousingMenuOpen = isHousingMenuOpen;
                }
            }

            // Track grid location changes
            string currentLocKey = LocationKey;
            if (_lastGridLocationKey != currentLocKey)
            {
                if ((DateTime.UtcNow - _lastGridChangeTime).TotalSeconds >= 3)
                {
                    _lastGridLocationKey = currentLocKey;
                    _lastGridChangeTime = DateTime.UtcNow;

                    // If it changed purely due to grid crossing, not territory
                    if (!string.IsNullOrEmpty(currentLocKey) && currentLocKey.StartsWith("zone_"))
                    {
                        bool wasPublicTv = _worldRenderer?.Transform.Enabled == true;

                        RestoreScreenForCurrentLocation();

                        bool isPublicTv = _worldRenderer?.Transform.Enabled == true;

                        // If we are leaving a TV, entering a TV, or moving between TVs, stop the old stream to prevent double audio
                        if (wasPublicTv || isPublicTv) 
                        {
                            _mediaManager?.StopStream();
                        }

                        RestoreMediaForCurrentLocation();
                        _ = FetchServerDataForCurrentLocationAsync();
                    }
                }
            }

            // v2 Sync: heartbeat and fetch loops run independently via ClaimDjAsync/ReleaseDjAsync
            // Only keep server time sync and TV data fetch
            bool isHouse = !string.IsNullOrEmpty(LocationKey) && LocationKey.StartsWith("house_");
            bool isZone = !string.IsNullOrEmpty(LocationKey) && LocationKey.StartsWith("zone_");

            if (isHouse || (isZone && _config.EnableOutdoorPublicScreens))
            {
                if (!_hasFetchedServerTime)
                {
                    _hasFetchedServerTime = true;
                    _ = FetchServerTimeAsync();
                }

                if ((DateTime.UtcNow - _lastServerSyncFetch).TotalSeconds >= 10)
                {
                    _lastServerSyncFetch = DateTime.UtcNow;
                    _ = FetchServerDataForCurrentLocationAsync();
                }
            }


            if ((DateTime.UtcNow - _lastHistoryUpdate).TotalSeconds >= 10)
            {
                _lastHistoryUpdate = DateTime.UtcNow;
                if (_mediaManager?.GetActiveStream() != null && !_isIntentionallyPaused) {
                    UpdateWatchHistory();
                }
            }
            // Clipboard cookie watcher — check every 5 seconds
            CheckClipboardForCookies();
        }

        private void CheckClipboardForCookies()
        {
            if ((DateTime.UtcNow - _lastClipboardCheck).TotalSeconds < 5) return;
            _lastClipboardCheck = DateTime.UtcNow;

            try
            {
                string? clipText = ImGui.GetClipboardText();
                if (string.IsNullOrEmpty(clipText)) return;

                int hash = clipText.GetHashCode();
                if (hash == _lastCookieHash) return;

                if (YtDlpManager.IsNetscapeCookieFormat(clipText))
                {
                    _lastCookieHash = hash;
                    if (_ytDlpManager.SaveCookiesFromText(clipText))
                    {
                        _pluginLog.Info("[yt-dlp] Auto-detected YouTube cookies from clipboard.");
                    }
                }
            }
            catch
            {
                // Clipboard access can throw — silently ignore
            }
        }

        private bool IsPlayerAlone()
        {
            try
            {
                int playerCount = 0;
                foreach (var obj in _objectTable)
                {
                    if (obj is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter)
                    {
                        playerCount++;
                        if (playerCount > 1) return false;
                    }
                }
                return true; // only the local player
            }
            catch (Exception ex)
            {
                _pluginLog.Error(ex, "Failed to check if player is alone");
                return false; // assume not alone to be safe
            }
        }

        private unsafe void InitializeMediaManager()
        {
            var localPlayer = GetLocalPlayer();
            if (localPlayer == null)
            {
                _pluginLog.Warning("[Media Player] LocalPlayer is null, cannot initialize media manager.");
                _hasBeenInitialized = false; // Allow retry next frame
                return;
            }

            _pluginLog.Info("[Media Player] Initializing media manager...");
            _playerObject = new MediaGameObject(localPlayer.Name.TextValue, localPlayer.Position);
            _tvAudioObject = new MediaGameObject("TV", System.Numerics.Vector3.Zero);
            _camera = CameraManager.Instance()->GetActiveCamera();
            _playerCamera = new MediaCameraObject(_camera);
            _mediaManager = new MediaManager(_playerObject, _playerCamera, _dependencyManager.DependenciesDir);
            _mediaManager.OnErrorReceived += OnMediaError;
            _mediaManager.OnNewMediaTriggered += _mediaManager_OnNewMediaTriggered;
            _mediaManager.OnPlaybackFinished += _mediaManager_OnPlaybackFinished;
            _mediaManager.LiveStreamVolume = _config.LivestreamVolume;
            _videoWindow.MediaManager = _mediaManager;
            _pluginLog.Info("[Media Player] Media manager initialized successfully.");
        }

        private Dalamud.Game.ClientState.Objects.Types.IGameObject GetLocalPlayer()
        {
            try
            {
                // ObjectTable[0] is always the local player in Dalamud
                var player = _objectTable[0];
                if (player != null)
                {
                    // _pluginLog.Debug($"[Media Player] Found LocalPlayer from ObjectTable: {player.Name}");
                }
                return player;
            }
            catch (Exception e)
            {
                _pluginLog.Warning(e, "[Media Player] Failed to get LocalPlayer from ObjectTable");
            }
            return null;
        }

    }
}
