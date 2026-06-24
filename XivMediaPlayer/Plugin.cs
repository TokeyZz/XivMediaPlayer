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
    public sealed partial class Plugin : IDalamudPlugin
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
        private readonly IPartyList _partyList;
        private readonly IGameInteropProvider _gameInterop;

        private readonly Configuration _config;
        private readonly WindowSystem _windowSystem;
        private readonly VideoWindow _videoWindow;
        private readonly SettingsWindow _settingsWindow;
        private readonly ScreenSettingsWindow _screenSettingsWindow;
        internal ScreenSettingsWindow ScreenSettingsWindow => _screenSettingsWindow;
        private WorldVideoRenderer _worldRenderer;
        internal WorldVideoRenderer WorldRenderer => _worldRenderer;
        internal string CurrentStreamer => _currentStreamer;
        internal Networking.ControllerService? ControllerService => _controllerService;
        private DepthPreviewWindow _depthPreviewWindow;

        private Networking.EmulationClient? _emulationClient;
        private Networking.ControllerService? _controllerService;

        private string _currentMediaOwnerId = string.Empty;
        // v2 sync state
        private long _stateVersion = 0;
        private bool _isTransitioning = false;
        private bool _isSyncing = false;
        private CancellationTokenSource _heartbeatCts;
        private CancellationTokenSource _fetchCts;
        private int _consecutiveLocalFailures = 0;
        private int _consecutiveSyncFailures = 0;
        private long _lastSuccessfulTimecode = -1;
        private bool _lastSyncWithRoom = false;
        private int _stalledDetectCount = 0;
        private int _heartbeatGeneration = 0;
        private bool _isLocalDj = false;
        private DepthBufferCapture _depthCapture;
        private UILayerCapture _uiCapture;
        private Compositing.TitleTextureManager _titleTextureManager;
        private Compositing.HistoryMenuTextureManager _historyMenuTextureManager;
        private bool _isHistoryMenuOpen = false;
        private Compositing.QueueMenuTextureManager _queueMenuTextureManager;
        private bool _isQueueMenuOpen = false;

        private MediaManager _mediaManager;
        public MediaManager MediaManager => _mediaManager;
        private YtDlpManager _ytDlpManager;
        private Task _ytDlpInitTask;

        private string _lastLocationKey = "";
        private MediaGameObject? _playerObject;
        private IMediaGameObject? _lastStreamObject;
        private MediaGameObject? _tvAudioObject;
        private IMediaGameObject CurrentAudioSource => (_worldRenderer?.Transform.Enabled == true) ? _tvAudioObject! : _playerObject!;
        private Queue<string> _mediaQueue = new Queue<string>();
        private Stack<string> _mediaHistory = new Stack<string>();
        private float _preMuteVolume = 0.5f;
        private bool _isMuted = false;
        private bool _wasDragging3DSeek = false;
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
        private bool _playbackEverConfirmed;
        private bool _disposed;
        private bool _bgmWasMutedByUs;
        private bool _wasHousingMenuOpen = false;
        private readonly ConcurrentQueue<Action> _frameworkActions = new();
        private DateTime? _deferredBgmRestoreTime = null;
        private bool _killRestartQueued;

        private System.Numerics.Matrix4x4? _prevViewProjMatrix = null;
        private System.Numerics.Vector3? _prevCameraPos = null;
        private System.Numerics.Vector3? _prevCameraForward = null;
        private System.Numerics.Vector3? _prevCameraRight = null;
        private System.Numerics.Vector3? _prevCameraUp = null;
        private bool _refreshQueued;

        public Networking.ServerClient ServerClient { get; private set; }
        public Configuration Config => _config;
        public YtDlpManager YtDlpManager => _ytDlpManager;
        public bool IsHousingMenuOpen => _wasHousingMenuOpen;
        public Dalamud.Plugin.Services.IObjectTable ObjectTable => _objectTable;
        public Dalamud.Plugin.Services.IPluginLog PluginLog => _pluginLog;
        public Dalamud.Plugin.Services.IChatGui Chat => _chat;
        public string LastStreamURL => _lastStreamURL;

        private bool _isDisposing;

        private DateTime _lastClipboardCheck = DateTime.MinValue;
        private DateTime _lastServerSyncPush = DateTime.MinValue;
        private DateTime _lastHistoryUpdate = DateTime.MinValue;
        private DateTime? _deferredTerritoryChangeTime = null;
        private DateTime _lastServerSyncFetch = DateTime.MinValue;
        private string _lastGridLocationKey = string.Empty;
        private DateTime _lastGridChangeTime = DateTime.MinValue;
        private long _serverTimeOffsetMs = 0;
        private bool _hasFetchedServerTime = false;
        private int _cachedRealPlayerCount = 0;
        private System.Numerics.Vector3? _cachedLocalPlayerPosition = null;
        private uint _cachedLocalPlayerWorldId = 0;

        private int _lastCookieHash;
        private bool _hasBeenInitialized;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private IntPtr _mainWindowHandle;
        private IDisposable _cefBrowserHandle;
        private Stopwatch _streamSetCooldown = new Stopwatch();
        private Stopwatch _screensaverTimer = new Stopwatch();

        private string _statusMessage = string.Empty;

        public unsafe bool IsVisitingIslandSanctuary()
        {
            if (_clientState.TerritoryType != 1055) return false;
            var mji = FFXIVClientStructs.FFXIV.Client.Game.MJI.MJIManager.Instance();
            return mji != null && !mji->IsPlayerInSanctuary;
        }

        // Current room TV state
        public TvPlacement? CurrentTvPlacement { get; internal set; }
        private List<TvPlacement> _nearbyTvs = new();

        // Input tracking
        private bool _wasLeftMousePressed = false;
        private bool _clickStartedOnTv = false;
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
          IPartyList partyList,
          IGameInteropProvider gameInterop,
          Dalamud.Plugin.Services.IAddonLifecycle addonLifecycle)
        {
            _pluginInterface = pluginInterface;
            _commandManager = commandManager;
            _chat = chat;
            
            _mainWindowHandle = Process.GetCurrentProcess().MainWindowHandle;
            
            _clientState = clientState;
            _framework = framework;
            _gameConfig = gameConfig;
            _pluginLog = pluginLog;
            _textureProvider = textureProvider;
            _gameGui = gameGui;
            _objectTable = objectTable;
            _partyList = partyList;
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
                // Handle SyncWithRoom toggle (independent of ServerUrl)
                if (_config.SyncWithRoom != _lastSyncWithRoom)
                {
                    _lastSyncWithRoom = _config.SyncWithRoom;
                    if (_config.SyncWithRoom)
                    {
                        if (!_isLocalDj) StartFetchLoop();
                    }
                    else
                    {
                        StopFetchLoop();
                        StopHeartbeatLoop();
                    }
                }

                // Only recreate ServerClient if the ServerUrl actually changed
                if (ServerClient.BaseUrl != _config.ServerUrl)
                {
                    StopHeartbeatLoop();
                    StopFetchLoop();
                    ServerClient?.Dispose();
                    ServerClient = new Networking.ServerClient(_config.ServerUrl, _pluginLog);
                    if (_config.SyncWithRoom && !_isLocalDj) StartFetchLoop();
                    if (_isLocalDj)
                    {
                        _isLocalDj = false;
                        if (_config.SyncWithRoom) StartFetchLoop();
                    }
                }
                ApplyProxySettings();
            };

            // Apply proxy settings at startup
            ApplyProxySettings();

            _uiCapture = new UILayerCapture(addonLifecycle);
            _uiCapture.Initialize();
            _titleTextureManager = new Compositing.TitleTextureManager(_textureProvider);
            _historyMenuTextureManager = new Compositing.HistoryMenuTextureManager(_textureProvider);
            _queueMenuTextureManager = new Compositing.QueueMenuTextureManager(_textureProvider);

            // Create windows
            _windowSystem = new WindowSystem("XivMediaPlayer");
            _videoWindow = new VideoWindow(this, _pluginInterface, _textureProvider, _pluginLog);
            _settingsWindow = new SettingsWindow(this, FixWindowsVolume);
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

            _depthCapture = new DepthBufferCapture();
            _depthCapture.Initialize();

            _depthPreviewWindow = new DepthPreviewWindow(_textureProvider, _pluginLog);
            _depthPreviewWindow.Capture = _depthCapture;
            _depthPreviewWindow.UICapture = _uiCapture;
            _depthPreviewWindow.Config = _config;

            _windowSystem.AddWindow(_videoWindow);
            _windowSystem.AddWindow(_settingsWindow);
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
                " /media emulate <ip> <session> — Connect to emulation server",
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
                EnqueueFrameworkAction(() =>
                {
                    RestoreScreenForCurrentLocation();
                    RestoreMediaForCurrentLocation();
                });
            });

            // Start proxy server for stream routing
            MediaPlayerCore.StreamProxy.Instance.Start();
        }



        private unsafe void HandleScreenCommand(string[] args)
        {
            if (args.Length < 2)
            {
                PrintChat("[媒体播放器] 屏幕命令:\n" +
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
                        PrintChat($"[媒体播放器] 屏幕已移动到 ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})");
                    }
                    else
                    {
                        PrintChatError("[媒体播放器] 用法: /media screen move <x> <y> <z>");
                    }
                    break;

                case "rotate":
                    if (args.Length >= 3 && float.TryParse(args[2], out float yaw))
                    {
                        float pitch = args.Length >= 4 && float.TryParse(args[3], out float p) ? p : 0;
                        _worldRenderer.SetRotation(yaw, pitch);
                        PrintChat($"[媒体播放器] 屏幕旋转: 偏航={yaw:F0}° 俯仰={pitch:F0}°");
                    }
                    else
                    {
                        PrintChatError("[媒体播放器] 用法: /media screen rotate <偏航> [俯仰]");
                    }
                    break;

                case "scale":
                    if (args.Length >= 4 &&
                      float.TryParse(args[2], out float sw) &&
                      float.TryParse(args[3], out float sh))
                    {
                        _worldRenderer.SetScale(sw, sh);
                        PrintChat($"[媒体播放器] 屏幕尺寸: {sw:F1} x {sh:F1} 世界单位");
                    }
                    else
                    {
                        PrintChatError("[媒体播放器] 用法: /media screen scale <宽度> <高度>");
                    }
                    break;

                case "reset":
                    _worldRenderer.Reset();
                    PrintChat("[媒体播放器] 屏幕已返回覆盖模式");
                    break;

                case "save":
                    _config.WorldScreen = _worldRenderer.Transform.Clone();
                    SaveScreenForCurrentLocation();
                    _config.Save();
                    var locKey = GetLocationKey();
                    PrintChat($"[媒体播放器] 屏幕位置已保存: {locKey}", ChatSeverity.Info);
                    break;

                default:
                    PrintChatError($"[媒体播放器] 未知的屏幕命令: {args[1]}");
                    break;
            }
        }

        private unsafe void PlaceScreenAtCamera()
        {
            if (_camera != null)
            {
                var sceneCamera = _camera->CameraBase.SceneCamera;
                var rawView = sceneCamera.RenderCamera != null ? sceneCamera.RenderCamera->ViewMatrix : sceneCamera.ViewMatrix;
                
                var viewMatrix = System.Runtime.CompilerServices.Unsafe.As<
                  FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4,
                  System.Numerics.Matrix4x4>(ref rawView);
                  
                viewMatrix.M14 = 0f;
                viewMatrix.M24 = 0f;
                viewMatrix.M34 = 0f;
                viewMatrix.M44 = 1f;
                
                System.Numerics.Matrix4x4.Invert(viewMatrix, out var invView);
                var camPos = invView.Translation;
                var forward = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(invView.M31, invView.M32, invView.M33));
                
                var screenPos = camPos - forward * 5.0f;
                _worldRenderer.PlaceAt(screenPos, camPos);
                PrintChat($"[媒体播放器] 屏幕已放置在 ({screenPos.X:F1}, {screenPos.Y:F1}, {screenPos.Z:F1})");
            }
            else
            {
                PrintChatError("[媒体播放器] 相机不可用");
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopHeartbeatLoop();
            StopFetchLoop();
            _ = ReleaseDjAsync();

            try
            {
                // Clean up HidSharp's hidden window to prevent RegisterClass crashes on plugin reload
                IntPtr hwnd = FindWindow("HidSharpDeviceMonitor", null);
                if (hwnd != IntPtr.Zero)
                {
                    // Send WM_CLOSE (0x0010) to let the background thread destroy it and exit cleanly
                    SendMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
                }
                
                IntPtr hInst = GetModuleHandle("HidSharp.dll");
                if (hInst == IntPtr.Zero) hInst = GetModuleHandle(null);
                UnregisterClass("HidSharpDeviceMonitor", hInst);
            }
            catch { }

            UpdateWatchHistory();

            SaveScreenForCurrentLocation();
            SaveMediaStateForCurrentLocation();

            _framework.Update -= OnFrameworkUpdate;
            _clientState.TerritoryChanged -= OnTerritoryChanged;
            _clientState.Login -= OnLogin;
            _clientState.Logout -= OnLogout;
            _videoWindow.WindowResized -= OnVideoWindowResized;
            _chat.ChatMessage -= OnChatMessage;
            if (_mediaManager != null)
            {
                _mediaManager.OnErrorReceived -= OnMediaError;
                _mediaManager.OnNewMediaTriggered -= _mediaManager_OnNewMediaTriggered;
                _mediaManager.OnPlaybackFinished -= _mediaManager_OnPlaybackFinished;
            }

            _pluginInterface.UiBuilder.Draw -= OnDraw;
            _pluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfig;

            _commandManager.RemoveHandler("/media");

            while (_frameworkActions.TryDequeue(out _)) { }
            _videoWindow.MarkDisposed();
            _emulationClient?.Dispose();
            _controllerService?.Dispose();
            _uiCapture?.Dispose();
            _titleTextureManager?.Dispose();
            _historyMenuTextureManager?.Dispose();
            _queueMenuTextureManager?.Dispose();
            _worldRenderer?.Dispose();
            _depthCapture?.Dispose();
            _depthPreviewWindow?.Dispose();
            ServerClient?.Dispose();
            _mediaManager?.Dispose();
            _ytDlpManager?.Dispose();
            MediaPlayerCore.StreamProxy.Instance.Dispose();
            _windowSystem?.RemoveAllWindows();
        }

    }
}









