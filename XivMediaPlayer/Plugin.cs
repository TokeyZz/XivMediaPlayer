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
using MediaPlayerCore.Catalog;
using MediaPlayerCore.Compositing;
using MediaPlayerCore.Twitch;
using MediaPlayerCore.YtDlp;
using XivMediaPlayer.Compositing;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XivMediaPlayer
{
    public sealed class Plugin : IDalamudPlugin
    {
        public string Name => "XIV Media Player";

        // Static PluginService properties (following Dalamud SamplePlugin template)
        [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
        [PluginService] internal static IPluginLog Log { get; private set; } = null!;

        private readonly IDalamudPluginInterface _pluginInterface;
        private readonly ICommandManager _commandManager;
        private readonly IChatGui _chat;
        private readonly IClientState _clientState;
        private readonly IFramework _framework;
        private readonly IGameConfig _gameConfig;
        private readonly IPluginLog _pluginLog;
        private readonly ITextureProvider _textureProvider;
        private readonly IGameGui _gameGui;
        private readonly IObjectTable _objectTable;
        private readonly IGameInteropProvider _gameInterop;

        private readonly Configuration _config;
        private readonly WindowSystem _windowSystem;
        private readonly VideoWindow _videoWindow;
        private readonly SettingsWindow _settingsWindow;
        private readonly MediaBrowserWindow _browserWindow;
        private readonly ScreenSettingsWindow _screenSettingsWindow;
        private WorldVideoRenderer _worldRenderer;
        private DepthPreviewWindow _depthPreviewWindow;
        private DepthBufferCapture _depthCapture;
        private UILayerCapture _uiCapture;

        private MediaManager _mediaManager;
        private YtDlpManager _ytDlpManager;
        private MediaGameObject _playerObject;
        private MediaCameraObject _playerCamera;
        private unsafe Camera* _camera;

        private string[] _streamURLs;
        private MediaGameObject _lastStreamObject;
        private string _lastStreamURL;
        private string _currentStreamer;
        private string _potentialStream;
        private bool _streamWasPlaying;
        private bool _disposed;
        private bool _bgmWasMutedByUs;

        // Clipboard cookie watcher
        private DateTime _lastClipboardCheck = DateTime.MinValue;
        private int _lastCookieHash;
        private bool _hasBeenInitialized;

        private Stopwatch _streamSetCooldown = new Stopwatch();

        public Plugin(
          IDalamudPluginInterface pluginInterface,
          ICommandManager commandManager,
          IChatGui chat,
          IClientState clientState,
          IFramework framework,
          IGameConfig gameConfig,
          IPluginLog pluginLog,
          ITextureProvider textureProvider,
          IGameGui gameGui,
          IObjectTable objectTable,
          IGameInteropProvider gameInterop)
        {
            _pluginInterface = pluginInterface;
            _commandManager = commandManager;
            _chat = chat;
            _clientState = clientState;
            _framework = framework;
            _gameConfig = gameConfig;
            _pluginLog = pluginLog;
            _textureProvider = textureProvider;
            _gameGui = gameGui;
            _objectTable = objectTable;
            _gameInterop = gameInterop;

            // Load configuration
            _config = (Configuration)_pluginInterface.GetPluginConfig()
                 ?? new Configuration();
            _config.Initialize(_pluginInterface);

            // Initialize yt-dlp manager
            string pluginDir = Path.GetDirectoryName(_pluginInterface.AssemblyLocation.FullName) ?? "";
            _ytDlpManager = new YtDlpManager(pluginDir, _config.PreferredQuality);
            _ytDlpManager.OnStatusUpdate += (s, msg) => _pluginLog.Info("[yt-dlp] " + msg);
            _ytDlpManager.OnError += (s, ex) => _pluginLog.Warning(ex, "[yt-dlp] " + ex.Message);

            // Auto-download if missing, then always self-update
            Task.Run(async () => await _ytDlpManager.EnsureAvailableAsync());

            // Initialize world-space video renderer
            _worldRenderer = new WorldVideoRenderer(_config.WorldScreen, _gameGui);

            _uiCapture = new UILayerCapture();
            _uiCapture.Initialize();

            // Create windows
            _windowSystem = new WindowSystem("XivMediaPlayer");
            _videoWindow = new VideoWindow(_pluginInterface, _textureProvider, _pluginLog);
            _settingsWindow = new SettingsWindow(_config, FixWindowsVolume);
            _browserWindow = new MediaBrowserWindow();
            _screenSettingsWindow = new ScreenSettingsWindow(
              _config.WorldScreen,
              _worldRenderer,
              onSave: () =>
              {
                  _config.WorldScreen = _worldRenderer.Transform.Clone();
                  SaveScreenForCurrentLocation();
                  _config.Save();
                  _chat.Print("[Media Player] Screen placement saved.");
              },
              onPlaceAtCamera: () => PlaceScreenAtCamera()
            );

            // Set up catalog providers
            string playlistDir = Path.Combine(Path.GetDirectoryName(_pluginInterface.AssemblyLocation.FullName) ?? "", "playlists");
            var localProvider = new LocalPlaylistProvider(playlistDir);
            localProvider.CreateSamplePlaylist();
            _browserWindow.AddProvider(localProvider);

            if (_ytDlpManager.IsAvailable())
            {
                var ytProvider = new YtDlpPlaylistProvider(_ytDlpManager);
                _browserWindow.AddProvider(ytProvider);
            }

            _depthCapture = new DepthBufferCapture();
            _depthCapture.Initialize();

            _depthPreviewWindow = new DepthPreviewWindow(_textureProvider, _pluginLog);
            _depthPreviewWindow.Capture = _depthCapture;

            _browserWindow.OnPlayRequested += OnBrowserPlayRequested;

            _windowSystem.AddWindow(_videoWindow);
            _windowSystem.AddWindow(_settingsWindow);
            _windowSystem.AddWindow(_browserWindow);
            _windowSystem.AddWindow(_screenSettingsWindow);
            _windowSystem.AddWindow(_depthPreviewWindow);

            // Register draw + config UI
            _pluginInterface.UiBuilder.Draw += OnDraw;
            _pluginInterface.UiBuilder.OpenConfigUi += OnOpenConfig;
            _pluginInterface.UiBuilder.DisableUserUiHide = true;
            _pluginInterface.UiBuilder.DisableGposeUiHide = true;
            _pluginInterface.UiBuilder.DisableCutsceneUiHide = true;
            _pluginInterface.UiBuilder.DisableAutomaticUiHide = true;

            // Register commands
            _commandManager.AddHandler("/media", new Dalamud.Game.Command.CommandInfo(OnMediaCommand)
            {
                HelpMessage = "Media Player commands.\n" +
                " /media — Open settings\n" +
                " /media twitch <url> — Tune into a Twitch stream\n" +
                " /media rtmp <url> — Tune into an RTMP stream\n" +
                " /media play <url> — Play a media URL\n" +
                " /media stop — Stop current stream\n" +
                " /media video — Toggle video window\n" +
                " /media browse — Open media browser",
                ShowInHelp = true,
            });

            // Hook events
            _framework.Update += OnFrameworkUpdate;
            _clientState.TerritoryChanged += OnTerritoryChanged;
            _clientState.Login += OnLogin;
            _clientState.Logout += OnLogout;
            _videoWindow.WindowResized += OnVideoWindowResized;

            // Chat listener for twitch detection
            _chat.ChatMessage += OnChatMessage;
        }

        #region Framework / Initialization

        private unsafe void OnFrameworkUpdate(IFramework framework)
        {
            if (_disposed) return;

            if (!_hasBeenInitialized && _clientState.IsLoggedIn)
            {
                try
                {
                    InitializeMediaManager();
                    // Only mark done if we actually succeeded
                    _hasBeenInitialized = _playerObject != null;
                    if (_hasBeenInitialized)
                    {
                        // Restore saved screen placement for current location on plugin load
                        RestoreScreenForCurrentLocation();
                    }
                } catch (Exception e)
                {
                    _pluginLog.Error(e, "Failed to initialize media manager");
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
            } catch
            {
                // Clipboard access can throw — silently ignore
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
            _playerObject = new MediaGameObject(localPlayer);
            _camera = CameraManager.Instance()->GetActiveCamera();
            _playerCamera = new MediaCameraObject(_camera);
            _mediaManager = new MediaManager(_playerObject, _playerCamera, Path.GetDirectoryName(_pluginInterface.AssemblyLocation.FullName));
            _mediaManager.OnErrorReceived += OnMediaError;
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
                    _pluginLog.Debug($"[Media Player] Found LocalPlayer from ObjectTable: {player.Name}");
                }
                return player;
            } catch (Exception e)
            {
                _pluginLog.Warning(e, "[Media Player] Failed to get LocalPlayer from ObjectTable");
            }
            return null;
        }

        #endregion

        #region Commands

        private void OnMediaCommand(string command, string args)
        {
            if (_disposed) return;

            string[] splitArgs = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (splitArgs.Length == 0)
            {
                _settingsWindow.Toggle();
                return;
            }

            switch (splitArgs[0].ToLower())
            {
                case "twitch":
                    if (splitArgs.Length > 1 && splitArgs[1].Contains("twitch.tv"))
                    {
                        if (_playerObject != null)
                        {
                            _lastStreamObject = _playerObject;
                            TuneIntoStream(splitArgs[1], _playerObject, false);
                        }
                    } else
                    {
                        // Open twitch chat for current streamer
                        if (!string.IsNullOrEmpty(_currentStreamer))
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo()
                                {
                                    FileName = @"https://www.twitch.tv/popout/" + _currentStreamer + @"/chat?popout=",
                                    UseShellExecute = true,
                                    Verb = "OPEN"
                                });
                            } catch (Exception e)
                            {
                                _pluginLog.Warning(e, e.Message);
                            }
                        } else
                        {
                            _chat.PrintError("[Media Player] No active stream. Use: /media twitch <url>");
                        }
                    }
                    break;

                case "rtmp":
                    if (splitArgs.Length > 1 && splitArgs[1].Contains("rtmp"))
                    {
                        if (_playerObject != null)
                        {
                            _lastStreamObject = _playerObject;
                            TuneIntoStream(splitArgs[1], _playerObject, true);
                        }
                    }
                    break;

                case "play":
                    if (splitArgs.Length > 1)
                    {
                        string url = splitArgs[1];
                        if (_playerObject == null)
                        {
                            _chat.PrintError("[Media Player] Not initialized yet. Are you logged in?");
                            _pluginLog.Warning("[Media Player] _playerObject is null. _hasBeenInitialized=" + _hasBeenInitialized);
                            break;
                        }
                        _lastStreamObject = _playerObject;
                        /* if (url.Contains("twitch.tv")) {
                           TuneIntoStream(url, _playerObject, false);
                         } else if (url.StartsWith("rtmp")) {
                           TuneIntoStream(url, _playerObject, true);
                         } else */
                        if (YtDlpManager.IsUrlSupported(url) && _ytDlpManager.IsAvailable())
                        {
                            // Resolve via yt-dlp then play
                            _chat.Print("[Media Player] Resolving URL via yt-dlp...");
                            PlayViaYtDlp(url, _playerObject);
                        } else
                        {
                            // Fallback — direct URL to VLC
                            TuneIntoStream(url, _playerObject, true);
                        }
                    } else
                    {
                        _chat.PrintError("[Media Player] Usage: /media play <url>");
                    }
                    break;

                case "ytdlp-update":
                    if (_ytDlpManager.IsAvailable())
                    {
                        _chat.Print("[Media Player] Updating yt-dlp...");
                        Task.Run(async () =>
                        {
                            bool success = await _ytDlpManager.SelfUpdate();
                            _chat.Print(success ? "[Media Player] yt-dlp updated." : "[Media Player] yt-dlp update failed.");
                        });
                    } else
                    {
                        _chat.PrintError("[Media Player] yt-dlp not found. Set the path in /media settings.");
                    }
                    break;

                case "stop":
                    _mediaManager?.StopStream();
                    RestoreBgm();
                    ResetStreamValues();
                    _chat.Print("[Media Player] Stream stopped.");
                    break;

                case "fixaudio":
                    RestoreBgm();
                    FixWindowsVolume();
                    _chat.Print("[Media Player] Game audio restored.");
                    break;

                case "video":
                    _videoWindow.Toggle();
                    break;

                case "browse":
                    _browserWindow.Toggle();
                    break;

                case "listen":
                    if (!string.IsNullOrEmpty(_potentialStream) && _playerObject != null)
                    {
                        TuneIntoStream(_potentialStream, _playerObject, false);
                    }
                    break;

                case "screen":
                    if (splitArgs.Length < 2)
                    {
                        // No subcommand: toggle the settings window
                        _screenSettingsWindow.Toggle();
                    } else
                    {
                        HandleScreenCommand(splitArgs);
                    }
                    break;

                case "help":
                    _chat.Print("[Media Player] Commands:\n" +
                      " /media — Open settings\n" +
                      " /media twitch <url> — Tune into a Twitch stream\n" +
                      " /media rtmp <url> — Tune into an RTMP stream\n" +
                      " /media play <url> — Play a media URL\n" +
                      " /media stop — Stop current stream\n" +
                      " /media video — Toggle video window\n" +
                      " /media browse — Open media browser\n" +
                      " /media screen [place|move|rotate|scale|reset|save] — 3D screen\n" +
                      " /media listen — Tune into a shared stream\n" +
                      " /media ytdlp-update — Update yt-dlp\n" +
                      " /media help — Show this help");
                    break;

                default:
                    _settingsWindow.Toggle();
                    break;
            }
        }

        #endregion

        #region Stream Management

        private void TuneIntoStream(string url, MediaPlayerCore.IMediaGameObject audioGameObject, bool isNotTwitch, Dictionary<string, string>? httpHeaders = null)
        {
            Task.Run(async () =>
            {
                string cleanedURL = RemoveSpecialSymbols(url);
                _streamURLs = isNotTwitch ? new string[] { url } : TwitchFeedManager.GetServerResponse(cleanedURL);
                _videoWindow.IsOpen = _config.DefaultVideoOpen == 0;
                if (_streamURLs.Length > 0)
                {
                    _mediaManager.PlayStream(audioGameObject, _streamURLs[(int)_videoWindow.FeedType], 0, httpHeaders);
                    _lastStreamURL = cleanedURL;
                    if (!isNotTwitch)
                    {
                        _currentStreamer = cleanedURL.Replace(@"https://", null).Replace(@"www.", null).Replace("twitch.tv/", null);
                        _chat.Print(@"[Media Player] Tuning into " + _currentStreamer + @"!" +
                          "\r\nUse \"/media video\" to toggle the video feed." +
                          "\r\nUse \"/media stop\" to stop the stream.");
                    } else
                    {
                        _currentStreamer = "Stream";
                        _chat.Print(@"[Media Player] Playing stream!" +
                          "\r\nUse \"/media video\" to toggle the video feed." +
                          "\r\nUse \"/media stop\" to stop the stream.");
                    }
                }
            });
            _streamWasPlaying = true;
            try
            {
                MuteBgm();
            } catch (Exception e)
            {
                _pluginLog.Warning(e, e.Message);
            }
            _streamSetCooldown.Stop();
            _streamSetCooldown.Reset();
            _streamSetCooldown.Start();
        }

        private void PlayViaYtDlp(string url, MediaGameObject audioGameObject)
        {
            Task.Run(async () =>
            {
                try
                {
                    // Try to get metadata for a nice chat message
                    var metadataTask = _ytDlpManager.GetMetadata(url);
                    var resolveTask = _ytDlpManager.ResolveStreamUrl(url);

                    string? streamUrl = null;
                    try
                    {
                        streamUrl = await resolveTask;
                    } catch (Exception resolveEx)
                    {
                        _pluginLog.Warning(resolveEx, "[yt-dlp] Failed to resolve stream URL.");
                    }

                    var metadata = await metadataTask;

                    if (string.IsNullOrEmpty(streamUrl))
                    {
                        _chat.PrintError("[Media Player] Failed to resolve URL via yt-dlp. Trying direct playback...");
                        TuneIntoStream(url, audioGameObject, true, metadata?.HttpHeaders);
                        return;
                    }
                    string title = metadata?.Title ?? "Unknown";
                    string uploader = metadata?.Uploader ?? "";
                    bool isLive = metadata?.IsLive ?? false;

                    _lastStreamObject = audioGameObject;
                    _streamURLs = new string[] { streamUrl };
                    _videoWindow.IsOpen = _config.DefaultVideoOpen == 0;

                    _mediaManager.PlayStream(audioGameObject, streamUrl, 0, metadata?.HttpHeaders);
                    _lastStreamURL = url;
                    _currentStreamer = !string.IsNullOrEmpty(uploader) ? uploader : title;

                    string statusMsg = isLive ? "LIVE" : (metadata?.Duration.HasValue == true
                      ? TimeSpan.FromSeconds(metadata.Duration.Value).ToString(@"mm\:ss") : "");

                    _chat.Print($"[Media Player] Now playing: {title}" +
                      (!string.IsNullOrEmpty(uploader) ? $" by {uploader}" : "") +
                      (!string.IsNullOrEmpty(statusMsg) ? $" [{statusMsg}]" : "") +
                      "\r\nUse \"/media video\" to toggle the video feed." +
                      "\r\nUse \"/media stop\" to stop.");

                    _streamWasPlaying = true;
                    try
                    {
                        MuteBgm();
                    } catch (Exception e)
                    {
                        _pluginLog.Warning(e, e.Message);
                    }
                    _streamSetCooldown.Stop();
                    _streamSetCooldown.Reset();
                    _streamSetCooldown.Start();
                } catch (Exception e)
                {
                    _pluginLog.Warning(e, "[yt-dlp] " + e.Message);
                    _chat.PrintError("[Media Player] yt-dlp error: " + e.Message);
                }
            });
        }

        private void ChangeStreamQuality()
        {
            if (_streamURLs != null)
            {
                if (_streamWasPlaying && _streamURLs.Length > 0)
                {
                    Task.Run(async () =>
                    {
                        if ((int)_videoWindow.FeedType < _streamURLs.Length)
                        {
                            if (_lastStreamObject != null)
                            {
                                try
                                {
                                    _mediaManager.ChangeStream(_lastStreamObject, _streamURLs[(int)_videoWindow.FeedType], _videoWindow.Size.Value.X);
                                } catch (Exception e)
                                {
                                    _pluginLog.Warning(e, e.Message);
                                }
                            }
                        }
                    });
                }
            }
        }

        private void OnVideoWindowResized(object sender, EventArgs e)
        {
            ChangeStreamQuality();
        }

        private unsafe void ResetStreamValues()
        {
            Task.Run(async () =>
            {
                Thread.Sleep(1000);
                while (Conditions.Instance()->BetweenAreas)
                {
                    Thread.Sleep(500);
                }
                _lastStreamObject = null;
                _streamURLs = null;
                if (_streamWasPlaying)
                {
                    _streamWasPlaying = false;
                    _videoWindow.IsOpen = false;
                    try
                    {
                        RestoreBgm();
                    } catch (Exception e)
                    {
                        _pluginLog.Warning(e, e.Message);
                    }
                }
                _potentialStream = "";
                _lastStreamURL = "";
                _currentStreamer = "";
                _streamSetCooldown.Stop();
                _streamSetCooldown.Reset();
            });
        }

        #endregion

        #region Chat Twitch Detection

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
                                var audioGameObject = _playerObject;
                                if (_mediaManager.IsAllowedToStartStream(audioGameObject))
                                {
                                    _lastStreamObject = _playerObject;
                                    TuneIntoStream(value
                                      .Trim('(').Trim(')')
                                      .Trim('[').Trim(']')
                                      .Trim('!').Trim('@'), audioGameObject, false);
                                }
                            }
                            break;
                        }
                    }
                }
            } else if (_config.TuneIntoTwitchStreamPrompt)
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
                        _chat.Print("[Media Player] " + streamer + " is hosting a stream! Use \"/media listen\" to tune in.");
                    }
                }
            }
        }

        private unsafe bool IsResidential()
        {
            return HousingManager.Instance()->IsInside() || HousingManager.Instance()->OutdoorTerritory != null;
        }

        #endregion

        #region Event Handlers

        private void OnTerritoryChanged(uint territoryId)
        {
            _videoWindow.IsOpen = false;
            _mediaManager?.CleanSounds();
            ResetStreamValues();

            // Auto-restore screen placement for the new location (deferred — housing data isn't ready yet)
            Task.Run(async () =>
            {
                await Task.Delay(3000); // Wait for housing data to be available
                RestoreScreenForCurrentLocation();
            });
        }

        /// <summary>
        /// Saves the current screen placement to the config for the current location.
        /// </summary>
        private void SaveScreenForCurrentLocation()
        {
            if (_worldRenderer?.Transform == null || !_worldRenderer.Transform.Enabled) return;
            var key = GetLocationKey();
            if (string.IsNullOrEmpty(key)) return;
            _config.ScreenPlacements[key] = _worldRenderer.Transform.Clone();
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
            } else
            {
                _worldRenderer.Transform.Enabled = false; // Turn off 3D screen in new zones by default
            }
        }

        /// <summary>
        /// Generates a unique key for the current location.
        /// Regular zones: "zone_{territoryId}"
        /// Housing: "house_{worldId}_{territoryId}_{ward}_{plot}_{room}"
        /// </summary>
        private unsafe string GetLocationKey()
        {
            try
            {
                var territoryId = _clientState.TerritoryType;
                if (territoryId == 0) return null;

                var housingMgr = HousingManager.Instance();
                if (housingMgr != null && housingMgr->IsInside())
                {
                    // Get world ID from local player character struct
                    ushort worldId = 0;
                    try
                    {
                        var charMgr = FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterManager.Instance();
                        if (charMgr != null)
                        {
                            var localChar = charMgr->LookupBattleCharaByEntityId(
                              _objectTable[0]?.EntityId ?? 0);
                            if (localChar != null)
                            {
                                worldId = localChar->HomeWorld;
                            }
                        }
                    } catch { }

                    short ward = housingMgr->GetCurrentWard();
                    short plot = housingMgr->GetCurrentPlot();
                    short room = housingMgr->GetCurrentRoom();
                    return $"house_{worldId}_{territoryId}_{ward}_{plot}_{room}";
                }

                return $"zone_{territoryId}";
            } catch
            {
                return $"zone_{_clientState.TerritoryType}";
            }
        }

        private void OnLogin()
        {
            _hasBeenInitialized = false;
        }

        private void OnLogout(int type, int code)
        {
            _mediaManager?.CleanSounds();
            ResetStreamValues();
        }

        private void OnMediaError(object sender, MediaError e)
        {
            _pluginLog.Warning(e.Exception, e.Exception?.Message);
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
            } catch (Exception e)
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
            } catch (Exception e)
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

                _pluginLog.Info("[Media Player] Windows mixer volume restored.");
            } catch (Exception e)
            {
                _pluginLog.Warning(e, "[Media Player] Failed to fix Windows volume");
            }
        }

        #endregion

        #region UI

        private unsafe void OnDraw()
        {
            // Reset per-frame depth capture flag
            _depthCapture?.BeginFrame();

            if (_uiCapture != null)
            {
                _uiCapture.CaptureFrame();
            }

            // Decode frames every tick, even if the video window is closed,
            // so the world-space renderer always has fresh textures.
            _videoWindow.UpdateFrame();

            _windowSystem.Draw();

            // World-space video rendering
            if (_worldRenderer?.IsActive == true)
            {
                // Only read depth to CPU when occlusion is on
                if (_depthCapture != null)
                    _depthCapture.ReadDepthEnabled = _worldRenderer.UseDepthOcclusion;

                var textureWrap = _videoWindow.GetCurrentTextureWrap();
                if (textureWrap != null)
                {
                    // Get camera info for depth occlusion
                    System.Numerics.Vector3? cameraPos = null;
                    System.Numerics.Vector3? cameraForward = null;
                    float nearPlane = 0.1f, farPlane = 10000f;

                    if (_worldRenderer.UseDepthOcclusion && _camera != null)
                    {
                        try
                        {
                            var sceneCamera = _camera->CameraBase.SceneCamera;
                            var camPos = sceneCamera.Object.Position;
                            cameraPos = new System.Numerics.Vector3(camPos.X, camPos.Y, camPos.Z);
                            nearPlane = sceneCamera.RenderCamera->NearPlane;
                            farPlane = sceneCamera.RenderCamera->FarPlane;

                            // Extract camera forward from view matrix (3rd column = forward in view space)
                            var rawView = sceneCamera.ViewMatrix;
                            var view = System.Runtime.CompilerServices.Unsafe.As<
                              FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4,
                              System.Numerics.Matrix4x4>(ref rawView);
                            cameraForward = System.Numerics.Vector3.Normalize(
                              new System.Numerics.Vector3(view.M13, view.M23, view.M33));
                        } catch { }
                    }

                    System.Numerics.Vector2? hoverUV = null;
                    float progress = 0f;
                    bool isPlaying = false;

                    var activeStream = _mediaManager?.ActiveStream;
                    if (activeStream != null && activeStream.Length > 0)
                    {
                        progress = activeStream.Time / (float)activeStream.Length;
                        isPlaying = activeStream.PlaybackState == NAudio.Wave.PlaybackState.Playing;
                    }

                    var mousePos = ImGui.GetIO().MousePos;
                    var (tl, tr, br, bl) = _worldRenderer.Transform.Corners;

                    if (_gameGui.WorldToScreen(tl, out var sTL) &&
                        _gameGui.WorldToScreen(tr, out var sTR) &&
                        _gameGui.WorldToScreen(br, out var sBR) &&
                        _gameGui.WorldToScreen(bl, out var sBL))
                    {

                        var uv = MathUtils.InverseBilinear(mousePos, sTL, sTR, sBR, sBL);
                        if (uv.X >= 0 && uv.X <= 1 && uv.Y >= 0 && uv.Y <= 1)
                        {
                            hoverUV = uv;

                            // Handle clicks on media controls
                            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                            {
                                _pluginLog.Information($"Media Control Clicked at UV: {uv.X:F2}, {uv.Y:F2}");
                                if (uv.Y > 0.85f && activeStream != null)
                                {
                                    if (uv.X > 0.05f && uv.X < 0.10f && uv.Y > 0.88f && uv.Y < 0.94f)
                                    {
                                        _pluginLog.Information("Toggling Play/Pause!");
                                        activeStream.Pause(); // Toggles play/pause
                                    } else if (uv.Y > 0.90f && uv.Y < 0.92f && uv.X > 0.15f && uv.X < 0.95f)
                                    {
                                        float seekProgress = (uv.X - 0.15f) / 0.80f;
                                        _pluginLog.Information($"Seeking to {seekProgress * 100}%");
                                        activeStream.Time = (long)(seekProgress * activeStream.Length);
                                    }
                                }
                            }
                        }
                    }


                    _worldRenderer.Render(textureWrap, _depthCapture, cameraPos, cameraForward, _uiCapture, nearPlane, farPlane, hoverUV, progress, isPlaying);
                }
            }
        }

        /// <summary>
        /// Computes the game's combined View * Projection matrix from the active camera.
        /// Reads both matrices directly from FFXIV to guarantee perfect sync.
        /// </summary>
        private unsafe System.Numerics.Matrix4x4? GetViewProjectionMatrix()
        {
            if (_camera == null) return null;

            try
            {
                var sceneCamera = _camera->CameraBase.SceneCamera;

                var rawView = sceneCamera.ViewMatrix;
                var view = System.Runtime.CompilerServices.Unsafe.As<
                  FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4,
                  System.Numerics.Matrix4x4>(ref rawView);

                if (sceneCamera.RenderCamera == null) return null;

                var rawProj = sceneCamera.RenderCamera->ProjectionMatrix;
                var proj = System.Runtime.CompilerServices.Unsafe.As<
                  FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4,
                  System.Numerics.Matrix4x4>(ref rawProj);

                return System.Numerics.Matrix4x4.Multiply(view, proj);
            } catch
            {
                return null;
            }
        }

        private void OnOpenConfig()
        {
            _settingsWindow.Toggle();
        }

        public void ToggleConfigUi()
        {
            _settingsWindow.Toggle();
        }

        private void OnBrowserPlayRequested(object? sender, MediaCatalogItem item)
        {
            if (_disposed || _playerObject == null) return;

            _lastStreamObject = _playerObject;

            // Route through yt-dlp if available, otherwise direct play
            if (YtDlpManager.IsUrlSupported(item.Url) && _ytDlpManager.IsAvailable())
            {
                PlayViaYtDlp(item.Url, _playerObject);
            } else if (item.Url.Contains("twitch.tv"))
            {
                TuneIntoStream(item.Url, _playerObject, false);
            } else
            {
                TuneIntoStream(item.Url, _playerObject, true);
            }
        }

        #endregion

        #region Utilities

        private static string RemoveSpecialSymbols(string value)
        {
            return Regex.Replace(value, @"[^a-zA-Z0-9:/._\-]", "");
        }

        #endregion

        #region IDisposable

        private unsafe void HandleScreenCommand(string[] args)
        {
            if (args.Length < 2)
            {
                _chat.Print("[Media Player] Screen commands:\n" +
                  " /media screen place — Place screen at your look-at point\n" +
                  " /media screen move <x> <y> <z> — Adjust position\n" +
                  " /media screen rotate <yaw> [pitch] — Set rotation\n" +
                  " /media screen scale <w> <h> — Set size (world units)\n" +
                  " /media screen reset — Return to overlay mode\n" +
                  " /media screen save — Save current placement");
                return;
            }

            switch (args[1].ToLower())
            {
                case "place":
                    PlaceScreenAtCamera();
                    break;

                case "move":
                    if (args.Length >= 5 &&
                      float.TryParse(args[2], out float mx) &&
                      float.TryParse(args[3], out float my) &&
                      float.TryParse(args[4], out float mz))
                    {
                        _worldRenderer.MoveBy(new System.Numerics.Vector3(mx, my, mz));
                        var pos = _worldRenderer.Transform.Position;
                        _chat.Print($"[Media Player] Screen moved to ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})");
                    } else
                    {
                        _chat.PrintError("[Media Player] Usage: /media screen move <x> <y> <z>");
                    }
                    break;

                case "rotate":
                    if (args.Length >= 3 && float.TryParse(args[2], out float yaw))
                    {
                        float pitch = args.Length >= 4 && float.TryParse(args[3], out float p) ? p : 0;
                        _worldRenderer.SetRotation(yaw, pitch);
                        _chat.Print($"[Media Player] Screen rotation: yaw={yaw:F0}° pitch={pitch:F0}°");
                    } else
                    {
                        _chat.PrintError("[Media Player] Usage: /media screen rotate <yaw> [pitch]");
                    }
                    break;

                case "scale":
                    if (args.Length >= 4 &&
                      float.TryParse(args[2], out float sw) &&
                      float.TryParse(args[3], out float sh))
                    {
                        _worldRenderer.SetScale(sw, sh);
                        _chat.Print($"[Media Player] Screen size: {sw:F1} x {sh:F1} world units");
                    } else
                    {
                        _chat.PrintError("[Media Player] Usage: /media screen scale <width> <height>");
                    }
                    break;

                case "reset":
                    _worldRenderer.Reset();
                    _chat.Print("[Media Player] Screen returned to overlay mode.");
                    break;

                case "save":
                    _config.WorldScreen = _worldRenderer.Transform.Clone();
                    SaveScreenForCurrentLocation();
                    _config.Save();
                    var locKey = GetLocationKey();
                    _chat.Print($"[Media Player] Screen placement saved for {locKey}.");
                    break;

                case "depth":
                    _depthPreviewWindow.IsOpen = !_depthPreviewWindow.IsOpen;
                    _chat.Print($"[Media Player] Depth preview {(_depthPreviewWindow.IsOpen ? "opened" : "closed")}.");
                    break;

                default:
                    _chat.PrintError($"[Media Player] Unknown screen command: {args[1]}");
                    break;
            }
        }

        private unsafe void PlaceScreenAtCamera()
        {
            if (_camera != null)
            {
                var camRawPos = _camera->CameraBase.SceneCamera.Object.Position;
                var camPos = new System.Numerics.Vector3(camRawPos.X, camRawPos.Y, camRawPos.Z);
                var viewMatrix = _camera->CameraBase.SceneCamera.ViewMatrix;
                var forward = new System.Numerics.Vector3(viewMatrix.M13, viewMatrix.M23, viewMatrix.M33);
                var screenPos = camPos - forward * 5.0f;
                _worldRenderer.PlaceAt(screenPos, camPos);
                _chat.Print($"[Media Player] Screen placed at ({screenPos.X:F1}, {screenPos.Y:F1}, {screenPos.Z:F1})");
            } else
            {
                _chat.PrintError("[Media Player] Camera not available.");
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _videoWindow.MarkDisposed();

            _framework.Update -= OnFrameworkUpdate;
            _clientState.TerritoryChanged -= OnTerritoryChanged;
            _clientState.Login -= OnLogin;
            _clientState.Logout -= OnLogout;
            _videoWindow.WindowResized -= OnVideoWindowResized;
            _chat.ChatMessage -= OnChatMessage;

            _pluginInterface.UiBuilder.Draw -= OnDraw;
            _pluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfig;

            _commandManager.RemoveHandler("/media");

            _worldRenderer?.Dispose();
            _depthCapture?.Dispose();
            _uiCapture?.Dispose();
            _depthPreviewWindow?.Dispose();
            _mediaManager?.Dispose();
            _windowSystem?.RemoveAllWindows();
        }

        #endregion
    }
}
