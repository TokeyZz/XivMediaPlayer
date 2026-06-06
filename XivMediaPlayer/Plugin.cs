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
using System.Linq;

namespace XivMediaPlayer
{
    public sealed class Plugin : IDalamudPlugin
    {
        public string Name => "XIV Media Player";

        // Static PluginService properties (following Dalamud SamplePlugin template)
        [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
        [PluginService] internal static IPluginLog Log { get; private set; } = null!;
        [PluginService] internal static IKeyState KeyState { get; private set; } = null!;

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
        internal ScreenSettingsWindow ScreenSettingsWindow => _screenSettingsWindow;
        private WorldVideoRenderer _worldRenderer;
        internal WorldVideoRenderer WorldRenderer => _worldRenderer;
        private DepthPreviewWindow _depthPreviewWindow;

        private string _currentMediaOwnerId = string.Empty;
        private bool _isLocalDj = false;
        private DepthBufferCapture _depthCapture;
        private UILayerCapture _uiCapture;
        private Compositing.TitleTextureManager _titleTextureManager;

        private MediaManager _mediaManager;
        internal MediaManager MediaManager => _mediaManager;
        private YtDlpManager _ytDlpManager;
        private Task _ytDlpInitTask;

        private string _lastLocationKey = "";
        private IMediaGameObject? _playerObject;
        private IMediaGameObject? _lastStreamObject;
        private Queue<string> _mediaQueue = new Queue<string>();
        private Stack<string> _mediaHistory = new Stack<string>();
        private float _preMuteVolume = 0.5f;
        private bool _isMuted = false;
        private Random _shuffleRandom = new Random();
        private MediaCameraObject _playerCamera;
        private unsafe Camera* _camera;

        private string[] _streamURLs;
        private string _lastStreamURL;
        private double? _currentMediaDurationMs;
        private string _currentStreamer = "";
        private string _currentMediaTitle = "";
        private string _potentialStream;
        private bool _streamWasPlaying;
        private bool _disposed;
        private bool _bgmWasMutedByUs;
        private bool _wasHousingMenuOpen = false;

        public Networking.ServerClient ServerClient { get; private set; }
        public Configuration Config => _config;
        public bool IsHousingMenuOpen => _wasHousingMenuOpen;
        public Dalamud.Plugin.Services.IObjectTable ObjectTable => _objectTable;
        public Dalamud.Plugin.Services.IPluginLog PluginLog => _pluginLog;
        public Dalamud.Plugin.Services.IChatGui Chat => _chat;

        private bool _isDisposing;

        private DateTime _lastClipboardCheck = DateTime.MinValue;
        private DateTime _lastServerSyncPush = DateTime.MinValue;
        private DateTime _lastServerSyncFetch = DateTime.MinValue;

        private int _lastCookieHash;
        private bool _hasBeenInitialized;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private Stopwatch _streamSetCooldown = new Stopwatch();

        private string _statusMessage = string.Empty;

        // Current room TV state
        public Networking.Models.TvPlacement? CurrentTvPlacement { get; internal set; }

        // Input tracking
        private bool _wasLeftMousePressed = false;
        private DependencyManager _dependencyManager;

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

            // Initialize dependency manager to download large binaries (libvlc, cef)
            string configDir = _pluginInterface.ConfigDirectory.FullName;
            string pluginDir = System.IO.Path.GetDirectoryName(_pluginInterface.AssemblyLocation.FullName) ?? "";
            string version = this.GetType().Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
            _dependencyManager = new DependencyManager(configDir, pluginDir, version, _pluginLog);

            // Bypass Dalamud's assembly resolver for CefSharp natively (just in case)
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => { return null; };

            System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
            {
                if (assemblyName.Name != null && assemblyName.Name.StartsWith("CefSharp"))
                {
                    string pluginDir = System.IO.Path.GetDirectoryName(_pluginInterface.AssemblyLocation.FullName) ?? "";
                    string cefPath = System.IO.Path.Combine(pluginDir, "cef", assemblyName.Name + ".dll");
                    if (System.IO.File.Exists(cefPath))
                    {
                        return context.LoadFromAssemblyPath(cefPath);
                    }
                }
                return null;
            };

            // Load configuration
            _config = (Configuration)_pluginInterface.GetPluginConfig()
                 ?? new Configuration();
            _config.Initialize(_pluginInterface);

            // Initialize yt-dlp manager
            _ytDlpManager = new YtDlpManager(pluginDir, _config.PreferredQuality);
            _ytDlpManager.OnStatusUpdate += (s, msg) => _pluginLog.Info("[yt-dlp] " + msg);
            _ytDlpManager.OnError += (s, ex) => _pluginLog.Warning(ex, "[yt-dlp] " + ex.Message);

            // Auto-download if missing, then always self-update
            _ytDlpInitTask = Task.Run(async () => await _ytDlpManager.EnsureAvailableAsync());

            // Initialize world-space video renderer
            _worldRenderer = new WorldVideoRenderer(_config.WorldScreen, _gameGui);

            ServerClient = new Networking.ServerClient(_config.ServerUrl, _pluginLog);
            _config.OnConfigurationChanged += (s, e) =>
            {
                // Only recreate ServerClient if the ServerUrl actually changed!
                // Otherwise we constantly dispose the HttpClient while requests are in flight!
                if (ServerClient.BaseUrl != _config.ServerUrl)
                {
                    ServerClient?.Dispose();
                    ServerClient = new Networking.ServerClient(_config.ServerUrl, _pluginLog);
                }
            };

            _uiCapture = new UILayerCapture();
            _uiCapture.Initialize();
            _titleTextureManager = new Compositing.TitleTextureManager(_textureProvider);

            // Create windows
            _windowSystem = new WindowSystem("XivMediaPlayer");
            _videoWindow = new VideoWindow(this, _pluginInterface, _textureProvider, _pluginLog);
            _settingsWindow = new SettingsWindow(this, FixWindowsVolume);
            _browserWindow = new MediaBrowserWindow();
            _screenSettingsWindow = new ScreenSettingsWindow(
              this,
              _gameGui,
              _config.WorldScreen,
              _worldRenderer,
              onSave: () =>
              {
                  _config.Save();
                  SaveScreenForCurrentLocation();
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

            // Run initial restore (deferred to allow housing data to load if logging in directly to a house)
            Task.Run(async () =>
            {
                await Task.Delay(3000);
                RestoreScreenForCurrentLocation();
                RestoreMediaForCurrentLocation();
            });
        }

        #region Framework / Initialization

        private unsafe void OnFrameworkUpdate(IFramework framework)
        {
            if (_disposed) return;

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
                bool isHousingMenuOpen = (housingGoods != IntPtr.Zero);

                if (isHousingMenuOpen && !_wasHousingMenuOpen)
                {
                    _wasHousingMenuOpen = isHousingMenuOpen;
                    _screenSettingsWindow.IsOpen = true;
                    _screenSettingsWindow.SyncFromTransform();

                    if (CurrentTvPlacement != null && CurrentTvPlacement.OwnerId != _config.OwnerId && !string.IsNullOrEmpty(LocationKey))
                    {
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

            // Sync Polling Loop
            if (!string.IsNullOrEmpty(LocationKey) && LocationKey.StartsWith("house_"))
            {
                bool isMediaOwner = _isLocalDj;

                // Only push if actively playing or loading.
                // Paused media halts pushing to allow DataAgeMs to increment.
                if (isMediaOwner && ((_mediaManager?.ActiveStream != null && !_isIntentionallyPaused) || !string.IsNullOrEmpty(_lastStreamURL)))
                {
                    if ((DateTime.UtcNow - _lastServerSyncPush).TotalSeconds >= 5)
                    {
                        _lastServerSyncPush = DateTime.UtcNow;
                        _pluginLog.Information($"[Social] Executing PushMediaToServerAsync. ActiveStream Time: {_mediaManager?.ActiveStream?.Time ?? 0}");
                        _ = PushMediaToServerAsync(isBackgroundSync: true);
                    }
                }
                else if ((DateTime.UtcNow - _lastServerSyncPush).TotalSeconds >= 5)
                {
                    _lastServerSyncPush = DateTime.UtcNow;
                    _pluginLog.Information($"[Social] Skipping Push. isMediaOwner={isMediaOwner} ({_currentMediaOwnerId} vs {_config.OwnerId}), ActiveStream={_mediaManager?.ActiveStream != null}");
                }

                // Polling interval.
                // Facilitates DJ handoff on media change.
                if ((DateTime.UtcNow - _lastServerSyncFetch).TotalSeconds >= 10)
                {
                    _lastServerSyncFetch = DateTime.UtcNow;
                    _ = FetchServerDataForCurrentLocationAsync();
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
                    _pluginLog.Debug($"[Media Player] Found LocalPlayer from ObjectTable: {player.Name}");
                }
                return player;
            }
            catch (Exception e)
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
                _settingsWindow.IsOpen = true;
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
                            PlayViaYtDlp(splitArgs[1], _playerObject, 0);
                        }
                    }
                    else
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
                            }
                            catch (Exception e)
                            {
                                _pluginLog.Warning(e, e.Message);
                            }
                        }
                        else
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
                            TuneIntoStream(splitArgs[1], _playerObject, 0);
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
                        if (YtDlpManager.IsUrlSupported(url))
                        {
                            // Invoke yt-dlp resolution
                            _chat.Print("[Media Player] Resolving URL via yt-dlp...");
                            PlayViaYtDlp(url, _playerObject);
                        }
                        else
                        {
                            // Fallback — direct URL to VLC
                            TuneIntoStream(url, _playerObject, 0);
                        }
                    }
                    else
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
                    }
                    else
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
                    _videoWindow.IsOpen = !_videoWindow.IsOpen;
                    break;
                case "browse":
                    _browserWindow.IsOpen = true;
                    break;


                case "listen":
                    if (!string.IsNullOrEmpty(_potentialStream) && _playerObject != null)
                    {
                        PlayViaYtDlp(_potentialStream, _playerObject, 0);
                    }
                    break;

                case "screen":
                    if (!IsHousingMenuOpen)
                    {
                        _chat.PrintError("[Media Player] The screen settings menu can only be accessed while the 'Edit Furnishings' housing menu is open.");
                        break;
                    }

                    if (splitArgs.Length < 2)
                    {
                        // No subcommand: toggle the settings window
                        _screenSettingsWindow.Toggle();
                    }
                    else
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

        private void TuneIntoStream(string url, MediaPlayerCore.IMediaGameObject audioGameObject, int startTimeMs = 0, Dictionary<string, string>? httpHeaders = null, bool isAutoSync = false)
        {
            if (!isAutoSync && CurrentTvPlacement?.IsLocked == true && CurrentTvPlacement?.OwnerId != _config.OwnerId && !IsHousingMenuOpen)
            {
                _chat.PrintError("[Media Player] Cannot play stream: The TV in this room is locked by its owner.");
                return;
            }

            Task.Run(() =>
            {
                string cleanedURL = RemoveSpecialSymbols(url);
                _streamURLs = new string[] { url };
                _videoWindow.IsOpen = _config.DefaultVideoOpen == 0;
                if (_streamURLs.Length > 0)
                {
                    _mediaManager.PlayStream(audioGameObject, _streamURLs[(int)_videoWindow.FeedType], startTimeMs, httpHeaders);
                    _lastStreamURL = cleanedURL;
                    _currentStreamer = "Stream";
                    _chat.Print(@"[Media Player] Playing stream!" +
                      "\r\nUse \"/media video\" to toggle the video feed." +
                      "\r\nUse \"/media stop\" to stop the stream.");
                }

                if (!isAutoSync)
                {
                    _ = PushMediaToServerAsync(isBackgroundSync: false);
                }
            });
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

        private void PlayViaYtDlp(string url, IMediaGameObject audioGameObject, int startTimeMs = 0, bool isAutoSync = false)
        {
            _isIntentionallyPaused = false;
            _lastUrlLoadTime = DateTime.UtcNow;

            if (url != _lastStreamURL) _mediaErrorCount = 0;

            // If it's an auto-sync and we're already resolving something, ignore it to prevent spam.
            // But if it's a manual play, we ALLOW it to interrupt the current resolution!
            if (isAutoSync && _isResolvingMedia) return;

            if (!isAutoSync && CurrentTvPlacement?.IsLocked == true && CurrentTvPlacement?.OwnerId != _config.OwnerId && !IsHousingMenuOpen)
            {
                _chat.PrintError("[Media Player] Cannot play: The TV in this room is locked by its owner.");
                return;
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
                urlWithoutQuery.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                _lastStreamURL = url;
                _lastStreamIsLive = urlWithoutQuery.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
                _lastStreamObject = audioGameObject;
                _streamURLs = new string[] { url };
                _videoWindow.IsOpen = _config.DefaultVideoOpen == 0;

                _mediaManager.PlayStream(audioGameObject, url, startTimeMs, null);

                _currentMediaDurationMs = null;
                _currentStreamer = "Direct Stream";
                _currentMediaTitle = "Direct Stream";

                _chat.Print($"[Media Player] Playing direct stream!\r\nUse \"/media video\" to toggle the video feed.\r\nUse \"/media stop\" to stop.");

                if (!isAutoSync) _ = PushMediaToServerAsync(isBackgroundSync: false);
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
                    _chat.Print("[Media Player] Waiting for yt-dlp download/update to finish...");
                    await _ytDlpInitTask;
                }
                if (resolutionId != _currentResolutionId) return;

                try
                {
                    _lastStreamURL = url; // Save the original requested URL so PushMediaToServerAsync pushes it instead of the raw .m3u8

                    // Try to get metadata for a nice chat message
                    var metadataTask = _ytDlpManager.GetMetadata(url);
                    var resolveTask = _ytDlpManager.ResolveStreamUrl(url);

                    string? streamUrl = null;
                    try
                    {
                        streamUrl = await resolveTask;
                        if (resolutionId != _currentResolutionId) return;
                    }
                    catch (Exception resolveEx)
                    {
                        _pluginLog.Warning(resolveEx, "[yt-dlp] Failed to resolve stream URL.");
                    }

                    MediaPlayerCore.YtDlp.YtDlpMetadata? metadata = null;
                    try
                    {
                        metadata = await metadataTask;
                    }
                    catch (Exception metadataEx)
                    {
                        _pluginLog.Warning(metadataEx, "[yt-dlp] Failed to get metadata.");
                    }

                    if (string.IsNullOrEmpty(streamUrl))
                    {
                        // Fallback to CefSharp for heavily protected sites
                        _chat.Print("[Media Player] yt-dlp failed. Falling back to embedded browser resolver...");

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
                            streamUrl = cefResult.Url;
                            metadata = new MediaPlayerCore.YtDlp.YtDlpMetadata { HttpHeaders = cefResult.Headers };

                            if (!_isLocalDj && url != streamUrl && startTimeMs < 5000)
                            {
                                _pluginLog.Information("[Social] Guest successfully resolved a raw Cef URL to a direct stream. Rescuing the host by pushing the .m3u8 back to the server!");
                                _lastStreamURL = streamUrl;
                                _isLocalDj = true;
                                _ = PushMediaToServerAsync(isBackgroundSync: false);
                            }

                            _chat.Print("[Media Player] Embedded browser successfully found stream URL.");

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
                                streamUrl = MediaPlayerCore.StreamProxy.Instance.RegisterStream(cefResult.Url, metadata.HttpHeaders, cefResult.M3u8Content);
                                _pluginLog.Info($"[Media Player] Proxying stream URL: {streamUrl}");
                            }
                            catch (Exception proxyEx)
                            {
                                _pluginLog.Warning(proxyEx, "[Media Player] Failed to proxy stream URL, falling back to direct.");
                            }
                        }
                        else
                        {
                            _chat.PrintError("[Media Player] Failed to resolve URL natively. Trying direct playback...");
                            TuneIntoStream(url, audioGameObject, startTimeMs, metadata?.HttpHeaders);
                            return;
                        }
                    }
                    string title = metadata?.Title ?? "Unknown";
                    string uploader = metadata?.Uploader ?? "";

                    // Twitch streams often don't explicitly return is_live=true, but they lack a duration!
                    // Also explicitly check if it's a twitch channel URL (not a video)
                    bool isTwitchLive = url.Contains("twitch.tv") && !url.Contains("/videos/");
                    bool isLive = (metadata?.IsLive == true) || (metadata != null && metadata.Duration == null) || isTwitchLive;
                    _lastStreamIsLive = isLive;

                    _lastStreamObject = audioGameObject;
                    _streamURLs = new string[] { streamUrl };
                    _videoWindow.IsOpen = _config.DefaultVideoOpen == 0;

                    if (resolutionId != _currentResolutionId) return;
                    _mediaManager.PlayStream(audioGameObject, streamUrl, startTimeMs, metadata?.HttpHeaders);
                    if (resolutionId != _currentResolutionId) return;
                    _lastStreamURL = url;
                    _currentMediaDurationMs = metadata?.Duration * 1000.0;
                    _currentStreamer = !string.IsNullOrEmpty(uploader) ? uploader : title;
                    _currentMediaTitle = title;

                    string statusMsg = isLive ? "LIVE" : (metadata?.Duration.HasValue == true
                      ? TimeSpan.FromSeconds(metadata.Duration.Value).ToString(@"mm\:ss") : "");

                    _chat.Print($"[Media Player] Now playing: {title}" +
                      (!string.IsNullOrEmpty(uploader) ? $" by {uploader}" : "") +
                      (!string.IsNullOrEmpty(statusMsg) ? $" [{statusMsg}]" : "") +
                      "\r\nUse \"/media video\" to toggle the video feed." +
                      "\r\nUse \"/media stop\" to stop.");

                    if (!isAutoSync)
                    {
                        _ = PushMediaToServerAsync(isBackgroundSync: false);
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
                }
                finally
                {
                    _isResolvingMedia = false;
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
                                }
                                catch (Exception e)
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

        private void _mediaManager_OnNewMediaTriggered(object sender, EventArgs e)
        {
            _chat.Print("[Media Player] Starting Stream...");
        }

        private void _mediaManager_OnPlaybackFinished(object sender, string e)
        {
            if (!_isLocalDj) return;

            // Replay current track
            if (_config.LoopEnabled && !string.IsNullOrEmpty(_lastStreamURL) && _playerObject != null)
            {
                _chat.Print("[Media Player] Looping current track...");
                PlayViaYtDlp(_lastStreamURL, _playerObject, 0);
                return;
            }

            // Advance queue (with shuffle support)
            if (_mediaQueue.Count > 0 && _playerObject != null)
            {
                // Record history
                if (!string.IsNullOrEmpty(_lastStreamURL))
                {
                    _mediaHistory.Push(_lastStreamURL);
                }

                string nextUrl;
                if (_config.ShuffleEnabled && _mediaQueue.Count > 1)
                {
                    var list = _mediaQueue.ToList();
                    int idx = _shuffleRandom.Next(list.Count);
                    nextUrl = list[idx];
                    list.RemoveAt(idx);
                    _mediaQueue = new Queue<string>(list);
                }
                else
                {
                    nextUrl = _mediaQueue.Dequeue();
                }

                _chat.Print($"[Media Player] Playing Next in Queue: {nextUrl}");
                PlayViaYtDlp(nextUrl, _playerObject);
            }
        }

        private unsafe void ResetStreamValues()
        {
            _lastStreamObject = null;
            _streamURLs = null;
            _potentialStream = "";
            _lastStreamURL = "";
            _currentMediaDurationMs = null;
            _currentStreamer = "";
            _currentMediaTitle = "";
            _videoWindow.IsOpen = false;

            bool wasPlaying = _streamWasPlaying;
            _streamWasPlaying = false;
            _streamSetCooldown.Stop();
            _streamSetCooldown.Reset();

            if (wasPlaying)
            {
                Task.Run(async () =>
                {
                    Thread.Sleep(1000);
                    while (Conditions.Instance()->BetweenAreas)
                    {
                        Thread.Sleep(500);
                    }
                    try
                    {
                        RestoreBgm();
                    }
                    catch (Exception e)
                    {
                        _pluginLog.Warning(e, e.Message);
                    }
                });
            }
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
                                    PlayViaYtDlp(value
                                      .Trim('(').Trim(')')
                                      .Trim('[').Trim(']')
                                      .Trim('!').Trim('@'), audioGameObject, 0);
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
            SaveMediaStateForCurrentLocation();
            _videoWindow.IsOpen = false;
            _mediaManager?.CleanSounds();
            ResetStreamValues();

            // Auto-restore screen placement for the new location (deferred — housing data isn't ready yet)
            Task.Run(async () =>
            {
                await Task.Delay(3000); // Wait for housing data to be available
                RestoreScreenForCurrentLocation();
                RestoreMediaForCurrentLocation();

                await FetchServerDataForCurrentLocationAsync();
            });
        }

        private async Task FetchServerDataForCurrentLocationAsync()
        {
            // Fetch public TVs from the server
            string locationKey = GetLocationKey();
            if (!string.IsNullOrEmpty(locationKey) && locationKey.StartsWith("house_"))
            {
                var tvs = await ServerClient.GetTvsForRoomAsync(locationKey);
                if (tvs.Count > 0)
                {
                    var tv = tvs[0];
                    CurrentTvPlacement = tv;

                    // Apply to the ACTIVE renderer transform ONLY if we aren't actively editing it
                    if (_worldRenderer != null && !IsHousingMenuOpen)
                    {
                        _worldRenderer.Transform.Enabled = true;
                        _worldRenderer.Transform.Position = new System.Numerics.Vector3(tv.PositionX, tv.PositionY, tv.PositionZ);
                        _worldRenderer.Transform.RotationDegrees = new System.Numerics.Vector3(tv.RotationX, tv.RotationY, tv.RotationZ);
                        _worldRenderer.Transform.Scale = new System.Numerics.Vector2(tv.ScaleX, tv.ScaleY);

                        // Sync back to config for saving
                        _config.WorldScreen = _worldRenderer.Transform.Clone();
                        _config.ScreenPlacements[locationKey] = _worldRenderer.Transform.Clone();
                        _config.Save();
                    }

                    _pluginLog.Info($"[Social] Loaded public TV placement for room {locationKey}.");
                }
                else
                {
                    CurrentTvPlacement = null;

                    // Auto-restore local TV configuration to the server if one exists and is enabled locally
                    if (_config.ScreenPlacements.TryGetValue(locationKey, out var saved) && saved.Enabled)
                    {
                        var tvToUpload = new Networking.Models.TvPlacement
                        {
                            LocationKey = locationKey,
                            PositionX = saved.Position.X,
                            PositionY = saved.Position.Y,
                            PositionZ = saved.Position.Z,
                            RotationX = saved.RotationDegrees.X,
                            RotationY = saved.RotationDegrees.Y,
                            RotationZ = saved.RotationDegrees.Z,
                            ScaleX = saved.Scale.X,
                            ScaleY = saved.Scale.Y,
                            OwnerId = _config.OwnerId,
                            IsLocked = false
                        };

                        CurrentTvPlacement = await ServerClient.RegisterTvAsync(locationKey, tvToUpload);
                        if (CurrentTvPlacement != null)
                        {
                            _pluginLog.Info($"[Social] Restored local TV placement to server for room {locationKey}.");
                        }
                    }
                }

                // Automatically sync the media playback from the server upon entering the room
                await FetchMediaFromServerAsync();
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
                _worldRenderer.Transform.Enabled = false; // Turn off 3D screen in new zones by default
            }
        }

        /// <summary>
        /// Saves the current media URL, queue, and timecode for the current location.
        /// </summary>
        private void SaveMediaStateForCurrentLocation()
        {
            var key = _lastLocationKey;
            if (string.IsNullOrEmpty(key)) return;

            var state = new RoomMediaState();

            var activeStream = _mediaManager?.ActiveStream;
            if (activeStream != null && !string.IsNullOrEmpty(activeStream.SoundPath))
            {
                // We use _lastStreamURL to save the original un-resolved YouTube/Twitch URL
                // so we can re-resolve it via yt-dlp upon entering the room next time!
                state.CurrentUrl = !string.IsNullOrEmpty(_lastStreamURL) ? _lastStreamURL : activeStream.SoundPath;
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
            var key = GetLocationKey();
            if (string.IsNullOrEmpty(key)) return;

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

                // Start playback if there was a URL
                if (!string.IsNullOrEmpty(state.CurrentUrl) && _playerObject != null)
                {
                    _chat.Print($"[Media Player] Resuming playback in this room...");
                    _lastStreamObject = _playerObject;
                    PlayViaYtDlp(state.CurrentUrl, _playerObject, (int)state.TimecodeMs, isAutoSync: true);
                }
            }
            else
            {
                // Track location for future saving
                _lastLocationKey = key;
                return;
            }

            _lastLocationKey = key;
        }

        private async Task PushMediaToServerAsync(bool isBackgroundSync = false)
        {
            var key = GetLocationKey();
            if (string.IsNullOrEmpty(key) || !key.StartsWith("house_")) return;

            var activeStream = _mediaManager?.ActiveStream;

            // Don't push if there's literally no stream URL and we aren't loading one
            if (string.IsNullOrEmpty(_lastStreamURL) && (activeStream == null || string.IsNullOrEmpty(activeStream.SoundPath))) return;

            var sync = new Networking.Models.RoomMediaStateSync
            {
                LocationKey = key,
                CurrentUrl = !string.IsNullOrEmpty(_lastStreamURL) ? _lastStreamURL : activeStream?.SoundPath ?? "",
                TimecodeMs = activeStream?.Time ?? 0,
                // Only push "Paused" if the DJ explicitly pressed the pause button!
                // Otherwise, random network buffering on the DJ's client will accidentally force-pause the entire room!
                IsPlaying = !_isIntentionallyPaused,
                OwnerId = _config.OwnerId,
                PlaylistJson = System.Text.Json.JsonSerializer.Serialize(_mediaQueue.ToArray()),
                BypassLock = IsHousingMenuOpen,
                DurationMs = _currentMediaDurationMs,
                IsBackgroundSync = isBackgroundSync
            };

            try
            {
                await ServerClient.UpdateMediaStateAsync(key, sync);
                _currentMediaOwnerId = _config.OwnerId;

                // If we successfully pushed a foreground sync, we are definitely the DJ now.
                if (!isBackgroundSync)
                {
                    _isLocalDj = true;
                    _currentMediaOwnerId = _config.OwnerId;
                }
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
                _chat.PrintError("[Media Player] Cannot share media: The TV in this room is locked by its owner.");
                await FetchMediaFromServerAsync();
            }
        }

        public async Task FetchMediaFromServerAsync()
        {
            var key = GetLocationKey();
            if (string.IsNullOrEmpty(key) || !key.StartsWith("house_")) return;

            var sync = await ServerClient.GetMediaStateAsync(key);
            if (sync == null || string.IsNullOrEmpty(sync.CurrentUrl)) return;

            _currentMediaOwnerId = sync.OwnerId;

            if (_isLocalDj) return;

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
            var activeStream = _mediaManager?.ActiveStream;
            bool isLocalEnded = activeStream != null && activeStream.VlcState == LibVLCSharp.Shared.VLCState.Ended;
            bool isDifferentUrl = activeStream == null || (!string.IsNullOrEmpty(_lastStreamURL) && _lastStreamURL != sync.CurrentUrl) || (isLocalEnded && sync.IsPlaying);
            // Only sync VODs. Live streams cannot be reliably timecode-synced.
            bool isOutofSync = !_lastStreamIsLive && activeStream != null && activeStream.Length > 0 && Math.Abs(activeStream.Time - targetTimeMs) > 5000;
            bool localIsPlaying = activeStream != null && activeStream.PlaybackState == NAudio.Wave.PlaybackState.Playing;

            if (isDifferentUrl)
            {
                _pluginLog.Information($"[Social] Syncing NEW media from server: {sync.CurrentUrl} at {targetTimeMs}ms (Playing: {sync.IsPlaying})");

                _mediaQueue.Clear();
                foreach (var url in state.Playlist) _mediaQueue.Enqueue(url);

                if (_playerObject != null)
                {
                    // Starts the stream. If sync.IsPlaying is false, we should pause it immediately after it loads...
                    // But yt-dlp might take a while, so we just let it start and the next poll will pause it.
                    PlayViaYtDlp(state.CurrentUrl, _playerObject, (int)targetTimeMs, isAutoSync: true);
                }
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
                        _pluginLog.Information($"[Social] Server says play, but we are paused. Resuming!");
                        activeStream.Resume();
                    }
                    else if (!sync.IsPlaying && localIsPlaying)
                    {
                        bool isNewlyLoaded = (DateTime.UtcNow - _lastUrlLoadTime).TotalSeconds < 20;

                        // Check sync staleness or new stream status
                        if (sync.DataAgeMs < 15000 || isNewlyLoaded)
                        {
                            _pluginLog.Information($"[Social] Server says paused (NewlyLoaded: {isNewlyLoaded}). Pausing!");
                            activeStream.Pause();
                        }
                        else
                        {
                            _pluginLog.Information($"[Social] Server says paused, but it is {sync.DataAgeMs}ms old. Ignoring!");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Generates a unique key for the current location.
        public string LocationKey => GetLocationKey();

        /// <summary>
        /// Generates a unique string identifier for the current in-game location.
        /// Regular zones: "zone_{territoryId}"
        /// Housing: "house_{worldId}_{territoryId}_{ward}_{plot}_{room}"
        /// </summary>
        public unsafe string GetLocationKey()
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
                                worldId = localChar->CurrentWorld;
                            }
                        }
                    }
                    catch { }

                    short ward = housingMgr->GetCurrentWard();
                    short plot = housingMgr->GetCurrentPlot();
                    short room = housingMgr->GetCurrentRoom();
                    return $"house_{worldId}_{territoryId}_{ward}_{plot}_{room}";
                }

                return $"zone_{territoryId}";
            }
            catch
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
            if (_videoWindow != null) _videoWindow.IsOpen = false;
            if (_screenSettingsWindow != null) _screenSettingsWindow.IsOpen = false;
            if (_settingsWindow != null) _settingsWindow.IsOpen = false;
            _mediaManager?.CleanSounds();
            ResetStreamValues();
        }

        private int _mediaErrorCount = 0;

        private void OnMediaError(object sender, MediaError e)
        {
            _pluginLog.Warning(e.Exception, e.Exception?.Message);

            // Auto-retry VLC playback if it crashes shortly after starting (e.g. dropped TLS connection)
            // Ensure we ONLY retry if VLC actually threw a FATAL error, completely ignoring internal VLC logger spam
            if (!e.Exception?.Message?.Contains("Failed to create demuxer") == true) return;

            // If the player is actually successfully playing right now, then this is just a minor background stream error
            // (e.g. dropping a secondary audio track) and not a fatal playback crash. Ignore it.
            if (_mediaManager?.ActiveStream?.PlaybackState == NAudio.Wave.PlaybackState.Playing) return;

            if (!string.IsNullOrEmpty(_lastStreamURL) && _lastStreamObject != null)
            {
                if ((DateTime.UtcNow - _lastUrlLoadTime).TotalSeconds < 10)
                {
                    if (_mediaErrorCount < 5)
                    {
                        _mediaErrorCount++;
                        _chat.Print($"[Media Player] VLC encountered an error. Retrying playback... (Attempt {_mediaErrorCount}/5)");
                        Task.Run(() =>
                        {
                            Thread.Sleep(1000);
                            PlayViaYtDlp(_lastStreamURL, _lastStreamObject, (int)(_mediaManager?.ActiveStream?.Time ?? 0), isAutoSync: true);
                        });
                    }
                    else
                    {
                        _chat.PrintError("[Media Player] VLC failed to play the media after multiple attempts. The format might be unsupported or the server is dropping the connection.");
                    }
                }
            }
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

        #endregion

        #region UI

        private unsafe void OnDraw()
        {
            // Reset per-frame depth capture flag
            _depthCapture?.BeginFrame();

            if (!_dependencyManager.IsReady)
            {
                if (_dependencyManager.IsDownloading || _dependencyManager.HasError)
                {
                    ImGui.SetNextWindowPos(new System.Numerics.Vector2(ImGui.GetIO().DisplaySize.X / 2 - 200, ImGui.GetIO().DisplaySize.Y / 2 - 50));
                    ImGui.SetNextWindowSize(new System.Numerics.Vector2(400, 100));
                    if (ImGui.Begin("XivMediaPlayer - Initial Setup", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove))
                    {
                        ImGui.TextWrapped(_dependencyManager.Status);
                        if (_dependencyManager.IsDownloading)
                        {
                            ImGui.ProgressBar(_dependencyManager.DownloadProgress, new System.Numerics.Vector2(-1, 0));
                        }
                        if (_dependencyManager.HasError)
                        {
                            ImGui.TextColored(new System.Numerics.Vector4(1, 0, 0, 1), _dependencyManager.ErrorMessage);
                            if (ImGui.Button("Retry Download"))
                            {
                                _ = _dependencyManager.DownloadDependenciesAsync();
                            }
                        }
                        ImGui.End();
                    }
                }
                return;
            }

            if (_uiCapture != null)
            {
                _uiCapture.CaptureFrame();
            }

            // Decode frames every tick, even if the video window is closed,
            // so the world-space renderer always has fresh textures.
            _videoWindow.UpdateFrame();

            _windowSystem.Draw();

            // World-space video rendering
            if (_worldRenderer?.IsActive == true && _clientState.IsLoggedIn)
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
                        }
                        catch { }
                    }

                    System.Numerics.Vector2? hoverUV = null;
                    float progress = 0f;
                    bool isPlaying = false;

                    var activeStream = _mediaManager?.ActiveStream;
                    if (activeStream != null)
                    {
                        if (activeStream.Length > 0)
                            progress = activeStream.Time / (float)activeStream.Length;

                        isPlaying = activeStream.PlaybackState == NAudio.Wave.PlaybackState.Playing;
                    }

                    var mousePos = ImGui.GetIO().MousePos;
                    var (tl, tr, br, bl) = _worldRenderer.Transform.Corners;

                    _gameGui.WorldToScreen(tl, out var sTL);
                    _gameGui.WorldToScreen(tr, out var sTR);
                    _gameGui.WorldToScreen(br, out var sBR);
                    _gameGui.WorldToScreen(bl, out var sBL);

                    var uv = MathUtils.InverseBilinear(mousePos, sTL, sTR, sBR, sBL);

                    // We must calculate mouse state unconditionally every frame so that holding the mouse
                    // and dragging it OVER the window doesn't falsely trigger a "Click" event!
                    bool isLeftMousePressed = (GetAsyncKeyState(0x01) & 0x8000) != 0; // VK_LBUTTON
                    bool isMouseClicked = isLeftMousePressed && !_wasLeftMousePressed;
                    bool isMouseReleased = !isLeftMousePressed && _wasLeftMousePressed;
                    _wasLeftMousePressed = isLeftMousePressed;

                    if (uv.X >= 0 && uv.X <= 1 && uv.Y >= 0 && uv.Y <= 1)
                    {
                        hoverUV = uv;

                        if (isMouseClicked)
                        {
                            _pluginLog.Information($"Media Control Clicked at UV: {uv.X:F2}, {uv.Y:F2}");

                            // Handle Volume Slider Click
                            if (uv.Y > 0.95f && uv.Y < 0.97f && uv.X > 0.15f && uv.X < 0.72f)
                            {
                                if (_mediaManager != null)
                                {
                                    float volProgress = (uv.X - 0.15f) / 0.57f;
                                    _mediaManager.LiveStreamVolume = Math.Clamp(volProgress * 3f, 0f, 3f);
                                    _config.LivestreamVolume = _mediaManager.LiveStreamVolume;
                                    _config.Save();
                                }
                            }
                            // Handle Transport Controls (Y between 0.85 and 0.95)
                            else if (uv.Y > 0.85f && uv.Y < 0.95f)
                            {
                                // Prev (0.02 - 0.06)
                                if (uv.X >= 0.02f && uv.X <= 0.06f)
                                {
                                    PlayPrevious();
                                }
                                // Rewind (0.07 - 0.11)
                                else if (uv.X >= 0.07f && uv.X <= 0.11f)
                                {
                                    SeekRelative(-_config.SeekIncrementSeconds);
                                }
                                // Play/Pause (0.12 - 0.16)
                                else if (uv.X >= 0.12f && uv.X <= 0.16f)
                                {
                                    TogglePlayPause();
                                }
                                // Fast Forward (0.17 - 0.21)
                                else if (uv.X >= 0.17f && uv.X <= 0.21f)
                                {
                                    SeekRelative(_config.SeekIncrementSeconds);
                                }
                                // Next (0.22 - 0.26)
                                else if (uv.X >= 0.22f && uv.X <= 0.26f)
                                {
                                    PlayNext();
                                }
                                // Seek Bar (0.28 - 0.58)
                                else if (uv.X >= 0.28f && uv.X <= 0.58f)
                                {
                                    if (activeStream != null)
                                    {
                                        float seekProgress = (uv.X - 0.28f) / 0.30f;
                                        activeStream.Time = (long)(seekProgress * activeStream.Length);
                                        _isLocalDj = true;
                                        _ = PushMediaToServerAsync(isBackgroundSync: false);
                                    }
                                }
                                // Loop (0.62 - 0.66)
                                else if (uv.X >= 0.62f && uv.X <= 0.66f)
                                {
                                    _config.LoopEnabled = !_config.LoopEnabled;
                                    _config.Save();
                                    _chat.Print($"[Media Player] Loop: {(_config.LoopEnabled ? "ON" : "OFF")}");
                                }
                                // Shuffle (0.68 - 0.72)
                                else if (uv.X >= 0.68f && uv.X <= 0.72f)
                                {
                                    _config.ShuffleEnabled = !_config.ShuffleEnabled;
                                    _config.Save();
                                    _chat.Print($"[Media Player] Shuffle: {(_config.ShuffleEnabled ? "ON" : "OFF")}");
                                }
                                // Refresh (0.74 - 0.78)
                                else if (uv.X >= 0.74f && uv.X <= 0.78f)
                                {
                                    RefreshCurrentMedia();
                                }
                                // Lock (0.80 - 0.84)
                                else if (uv.X >= 0.80f && uv.X <= 0.84f)
                                {
                                    if (CurrentTvPlacement != null && CurrentTvPlacement.OwnerId == _config.OwnerId)
                                    {
                                        CurrentTvPlacement.IsLocked = !CurrentTvPlacement.IsLocked;
                                        if (!string.IsNullOrEmpty(LocationKey) && LocationKey.StartsWith("house_"))
                                        {
                                            _screenSettingsWindow.RegisterTvAsync(LocationKey);
                                            _chat.Print($"[Media Player] TV is now {(CurrentTvPlacement.IsLocked ? "Locked" : "Unlocked")}.");
                                        }
                                    }
                                    else if (CurrentTvPlacement == null)
                                    {
                                        CurrentTvPlacement = new Networking.Models.TvPlacement { OwnerId = _config.OwnerId, IsLocked = false };
                                        _screenSettingsWindow.RegisterTvAsync(LocationKey);
                                        _chat.Print("[Media Player] TV registered and Unlocked.");
                                    }
                                    else { _chat.Print("[Media Player] You do not own this TV."); }
                                }
                                // Paste (0.85 - 0.89)
                                else if (uv.X >= 0.85f && uv.X <= 0.89f)
                                {
                                    string clip = ImGui.GetClipboardText();
                                    if (!string.IsNullOrEmpty(clip) && _playerObject != null)
                                    {
                                        _chat.Print("[Media Player] Loading URL from clipboard...");
                                        PlayViaYtDlp(clip, _playerObject);
                                    }
                                }
                                // Queue (0.90 - 0.94)
                                else if (uv.X >= 0.90f && uv.X <= 0.94f)
                                {
                                    string clip = ImGui.GetClipboardText();
                                    if (!string.IsNullOrEmpty(clip))
                                    {
                                        _mediaQueue.Enqueue(clip);
                                        _chat.Print($"[Media Player] Queued ({_mediaQueue.Count}): {clip}");
                                        if (activeStream == null || activeStream.PlaybackState == NAudio.Wave.PlaybackState.Stopped)
                                            if (_playerObject != null) PlayViaYtDlp(_mediaQueue.Dequeue(), _playerObject);
                                        else _ = PushMediaToServerAsync(false);
                                    }
                                }
                                // Kill (0.95 - 0.99)
                                else if (uv.X >= 0.95f && uv.X <= 0.99f)
                                {
                                    KillAndRestart();
                                }
                            }
                        }
                    }

                    // Update dynamic 3D text texture
                    if (_titleTextureManager != null)
                    {
                        _titleTextureManager.UpdateText(_currentMediaTitle, _currentStreamer);
                    }

                    bool isLocked = CurrentTvPlacement?.IsLocked ?? true;
                    float volume = _mediaManager != null ? _mediaManager.LiveStreamVolume : 1f;
                    _worldRenderer.Render(textureWrap, _depthCapture, cameraPos, cameraForward, _uiCapture, nearPlane, farPlane, hoverUV, progress, isPlaying, isLocked, volume, _titleTextureManager?.TextureHandle ?? IntPtr.Zero, _config.LoopEnabled, _config.ShuffleEnabled);
                }
            }
        }

        private System.Numerics.Matrix4x4? _lastStabilizedVP;

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

                var vp = System.Numerics.Matrix4x4.Multiply(view, proj);

                if (_lastStabilizedVP.HasValue)
                {
                    float diff = 0;
                    diff += Math.Abs(vp.M11 - _lastStabilizedVP.Value.M11);
                    diff += Math.Abs(vp.M12 - _lastStabilizedVP.Value.M12);
                    diff += Math.Abs(vp.M13 - _lastStabilizedVP.Value.M13);
                    diff += Math.Abs(vp.M21 - _lastStabilizedVP.Value.M21);
                    diff += Math.Abs(vp.M22 - _lastStabilizedVP.Value.M22);
                    diff += Math.Abs(vp.M23 - _lastStabilizedVP.Value.M23);
                    diff += Math.Abs(vp.M31 - _lastStabilizedVP.Value.M31);
                    diff += Math.Abs(vp.M32 - _lastStabilizedVP.Value.M32);
                    diff += Math.Abs(vp.M33 - _lastStabilizedVP.Value.M33);
                    diff += Math.Abs(vp.M41 - _lastStabilizedVP.Value.M41);
                    diff += Math.Abs(vp.M42 - _lastStabilizedVP.Value.M42);
                    diff += Math.Abs(vp.M43 - _lastStabilizedVP.Value.M43);

                    // Stabilize the combined ViewProjection matrix to filter out both
                    // camera float drift AND TAA/DLSS/FSR projection sub-pixel jitter.
                    if (diff < 0.1f)
                    {
                        vp = _lastStabilizedVP.Value;
                    }
                }
                _lastStabilizedVP = vp;

                return vp;
            }
            catch
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
                PlayViaYtDlp(item.Url, _playerObject, 0);
            }
            else
            {
                TuneIntoStream(item.Url, _playerObject, 0);
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
                    }
                    else
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
                    }
                    else
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
                    }
                    else
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
            }
            else
            {
                _chat.PrintError("[Media Player] Camera not available.");
            }
        }
        #region Playback Controls

        /// <summary>
        /// Seeks the current stream forward or backward by the given number of seconds.
        /// </summary>
        public void SeekRelative(int seconds)
        {
            var activeStream = _mediaManager?.ActiveStream;
            if (activeStream == null || activeStream.Length <= 0) return;

            long newTime = activeStream.Time + (seconds * 1000L);
            newTime = Math.Clamp(newTime, 0, activeStream.Length);
            activeStream.Time = newTime;

            if (_isLocalDj)
            {
                _ = PushMediaToServerAsync(isBackgroundSync: false);
            }
        }

        /// <summary>
        /// Toggles play/pause on the current stream.
        /// </summary>
        public void TogglePlayPause()
        {
            var activeStream = _mediaManager?.ActiveStream;
            if (activeStream == null) return;

            if (activeStream.PlaybackState == NAudio.Wave.PlaybackState.Playing)
            {
                activeStream.Pause();
                _isIntentionallyPaused = true;
            }
            else
            {
                activeStream.Resume();
                _isIntentionallyPaused = false;
            }

            if (_isLocalDj)
            {
                _ = PushMediaToServerAsync(isBackgroundSync: false);
            }
        }

        /// <summary>
        /// Returns true if the stream is currently intentionally paused by the user.
        /// </summary>
        public bool IsIntentionallyPaused => _isIntentionallyPaused;

        /// <summary>
        /// Plays the next track from the media queue.
        /// If shuffle is enabled, picks a random track from the queue.
        /// </summary>
        public void PlayNext()
        {
            if (_mediaQueue.Count == 0 || _playerObject == null) return;

            // Record history
            if (!string.IsNullOrEmpty(_lastStreamURL))
            {
                _mediaHistory.Push(_lastStreamURL);
            }

            string nextUrl;
            if (_config.ShuffleEnabled && _mediaQueue.Count > 1)
            {
                // Shuffle queue logic
                var list = _mediaQueue.ToList();
                int idx = _shuffleRandom.Next(list.Count);
                nextUrl = list[idx];
                list.RemoveAt(idx);
                _mediaQueue = new Queue<string>(list);
            }
            else
            {
                nextUrl = _mediaQueue.Dequeue();
            }

            _chat.Print($"[Media Player] Playing next: {nextUrl}");
            PlayViaYtDlp(nextUrl, _playerObject);
        }

        /// <summary>
        /// Plays the previous track from the media history stack.
        /// Pushes the current track back onto the front of the queue.
        /// </summary>
        public void PlayPrevious()
        {
            if (_mediaHistory.Count == 0 || _playerObject == null) return;

            // Requeue current media
            if (!string.IsNullOrEmpty(_lastStreamURL))
            {
                var list = _mediaQueue.ToList();
                list.Insert(0, _lastStreamURL);
                _mediaQueue = new Queue<string>(list);
            }

            string prevUrl = _mediaHistory.Pop();
            _chat.Print($"[Media Player] Playing previous: {prevUrl}");
            PlayViaYtDlp(prevUrl, _playerObject);
        }

        /// <summary>
        /// Toggles mute on/off. Stores the pre-mute volume and restores it when unmuting.
        /// </summary>
        public void ToggleMute()
        {
            if (_mediaManager == null) return;

            if (_isMuted)
            {
                _mediaManager.LiveStreamVolume = _preMuteVolume;
                _config.LivestreamVolume = _preMuteVolume;
                _isMuted = false;
            }
            else
            {
                _preMuteVolume = _mediaManager.LiveStreamVolume;
                _mediaManager.LiveStreamVolume = 0;
                _isMuted = true;
            }
        }

        /// <summary>
        /// Whether the media player is currently muted.
        /// </summary>
        public bool IsMuted => _isMuted;

        /// <summary>
        /// Re-resolves and replays the current media URL at the current timecode.
        /// Useful when the 2D/3D screen fails to load.
        /// </summary>
        public void RefreshCurrentMedia()
        {
            if (string.IsNullOrEmpty(_lastStreamURL) || _playerObject == null) return;

            var activeStream = _mediaManager?.ActiveStream;
            int currentTimeMs = activeStream != null ? (int)activeStream.Time : 0;

            _chat.Print("[Media Player] Refreshing media...");
            _mediaManager?.StopStream();
            PlayViaYtDlp(_lastStreamURL, _playerObject, currentTimeMs);
        }

        /// <summary>
        /// Kills the media manager and restarts it, then resumes the current media.
        /// Recovers from locked-up VLC states.
        /// </summary>
        public void KillAndRestart()
        {
            _chat.Print("[Media Player] Killing media pipeline and restarting...");

            // Save what we were playing
            string savedUrl = _lastStreamURL;
            var activeStream = _mediaManager?.ActiveStream;
            int savedTimeMs = activeStream != null ? (int)activeStream.Time : 0;

            // Tear down
            _mediaManager?.Dispose();
            _mediaManager = null;
            _videoWindow.MediaManager = null;

            // Reinitialize
            try
            {
                InitializeMediaManager();
            }
            catch (Exception e)
            {
                _pluginLog.Warning(e, "[Media Player] Failed to reinitialize MediaManager during kill.");
                _chat.PrintError("[Media Player] Failed to restart media pipeline.");
                return;
            }

            // Resume playback
            if (!string.IsNullOrEmpty(savedUrl) && _playerObject != null)
            {
                _chat.Print("[Media Player] Resuming playback...");
                PlayViaYtDlp(savedUrl, _playerObject, savedTimeMs);
            }
            else
            {
                _chat.Print("[Media Player] Media pipeline restarted.");
            }
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            SaveScreenForCurrentLocation();
            SaveMediaStateForCurrentLocation();
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

            _uiCapture?.Dispose();
            _titleTextureManager?.Dispose();
            _worldRenderer?.Dispose();
            _depthCapture?.Dispose();
            _depthPreviewWindow?.Dispose();
            ServerClient?.Dispose();
            _mediaManager?.Dispose();
            _ytDlpManager?.Dispose();
            MediaPlayerCore.StreamProxy.Instance.Dispose();
            _windowSystem?.RemoveAllWindows();
        }

        #endregion
    }
}
