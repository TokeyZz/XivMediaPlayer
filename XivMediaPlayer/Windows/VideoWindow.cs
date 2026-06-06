using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Bindings.ImGui;

using MediaPlayerCore;
using MediaPlayerCore.Twitch;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Vector2 = System.Numerics.Vector2;

namespace XivMediaPlayer.Windows {
  internal class VideoWindow : Window {
    private System.Numerics.Vector2? windowSize;
    private Vector2? initialSize;
    IDalamudTextureWrap textureWrap;
    MediaManager _mediaManager;
    private Plugin _plugin;
    private IDalamudPluginInterface _pluginInterface;
    private ITextureProvider _textureProvider;
    private IPluginLog _pluginLog;
    Stopwatch deadStreamTimer = new Stopwatch();
    private string fpsCount = "";
    int countedFrames = 0;
    private bool wasStreaming;
    private Vector2? _lastWindowSize;
    public event EventHandler WindowResized;
    public TwitchFeedType FeedType = TwitchFeedType._360p;
    private bool _wasNotOpen;
    Stopwatch eventTriggerCooldown = new Stopwatch();
    private IDalamudTextureWrap _frameToLoad;
    private IDalamudTextureWrap _blackFrame;
    private ulong _lastLoadedFrameCount = 0;
    private byte[] _lastLoadedFrame;
    private bool taskAlreadyRunning;
    private bool _disposed;

    public VideoWindow(Plugin plugin, IDalamudPluginInterface pluginInterface, ITextureProvider textureProvider, IPluginLog pluginLog) :
      base("Media Player", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoFocusOnAppearing, false) {
      windowSize = Size = new Vector2(640, 360);
      this.SizeCondition = ImGuiCond.Always;
      initialSize = Size;
      _plugin = plugin;
      _pluginInterface = pluginInterface;
      _textureProvider = textureProvider;
      _pluginLog = pluginLog;
      Position = new Vector2(0, 0);
      PositionCondition = ImGuiCond.Once;
      eventTriggerCooldown.Start();
      CreateBlackFrame();
    }

    public MediaManager MediaManager { get => _mediaManager; set => _mediaManager = value; }

    /// <summary>
    /// Decodes the latest VLC frame into a texture.
    /// Called every frame from Plugin.OnDraw(), regardless of window visibility,
    /// so the world renderer can get fresh frames even when the ImGui window is closed.
    /// </summary>
    public void UpdateFrame() {
      if (_disposed || _mediaManager == null) return;
      if (_mediaManager.LastFrame == null || _mediaManager.LastFrame.Length == 0) {
        if (wasStreaming) {
          if (!deadStreamTimer.IsRunning) {
            deadStreamTimer.Start();
          }
          if (deadStreamTimer.ElapsedMilliseconds > 10000) {
            deadStreamTimer.Stop();
            deadStreamTimer.Reset();
            IsOpen = false;
            wasStreaming = false;
          }
        }
        return;
      }

      // We have frame data — decode it into a texture
      wasStreaming = true;
      if (deadStreamTimer.IsRunning) {
        deadStreamTimer.Stop();
        deadStreamTimer.Reset();
      }

      try {
        if (!taskAlreadyRunning) {
          _ = Task.Run(async () => {
            try {
              taskAlreadyRunning = true;
              ReadOnlyMemory<byte> bytes = new byte[0];
              lock (_mediaManager.LastFrame) {
                bytes = _mediaManager.LastFrame;
              }

              if (bytes.Length > 0 && _mediaManager.LastFrameWidth > 0 && _mediaManager.LastFrameHeight > 0) {
                if (_lastLoadedFrameCount != _mediaManager.LastFrameCount) {
                  var newTexture = _textureProvider.CreateFromRaw(Dalamud.Interface.Textures.RawImageSpecification.Bgra32(_mediaManager.LastFrameWidth, _mediaManager.LastFrameHeight), bytes.Span, "VideoWindowTexture");
                  var oldTexture = _frameToLoad;
                  _frameToLoad = newTexture;
                  _lastLoadedFrameCount = _mediaManager.LastFrameCount;
                  oldTexture?.Dispose();
                }
              }
            } finally {
              taskAlreadyRunning = false;
            }
          });
        }
      } catch (Exception e) {
        _pluginLog.Warning(e, e.Message);
      }
    }

    public override void Draw() {
      bool betweenAreas = false;
      unsafe {
        betweenAreas = !Conditions.Instance()->BetweenAreas;
      }
      if (IsOpen && betweenAreas && !_disposed) {
        float uiHeight = ImGui.GetTextLineHeightWithSpacing() * 3 + 24; // Allocate space for controls
        Size = new Vector2(ImGui.GetWindowSize().X, ImGui.GetWindowSize().X * 0.5625f + uiHeight);
        SizeConstraints = new WindowSizeConstraints() { MaximumSize = ImGui.GetMainViewport().Size, MinimumSize = new Vector2(360, 480) };
        
        if (_frameToLoad != null) {
          ImGui.Image(_frameToLoad.Handle, new Vector2(Size.Value.X, Size.Value.X * 0.5625f));
        }

        // --- Seek Slider (VODs only) ---
        if (_mediaManager != null) {
          var activeStream = _mediaManager.ActiveStream;
          if (activeStream != null && activeStream.Length > 0) {
            float progress = (float)activeStream.Time / (float)activeStream.Length;
            ImGui.SetNextItemWidth(Size.Value.X);
            if (ImGui.SliderFloat("##seek", ref progress, 0f, 1f, 
                FormatTimeCode(activeStream.Time) + " / " + FormatTimeCode(activeStream.Length))) {
              activeStream.Time = (long)(progress * activeStream.Length);
            }
          }
        }

        // --- Transport Controls ---
        if (_plugin != null) {
          float btnW = 36;
          float btnH = 24;
          var btnSize = new Vector2(btnW, btnH);
          var wideBtnSize = new Vector2(48, btnH);

          // Rewind
          if (ImGui.Button("<<", btnSize)) {
            _plugin.SeekRelative(-_plugin.Config.SeekIncrementSeconds);
          }
          if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Rewind {_plugin.Config.SeekIncrementSeconds}s");

          ImGui.SameLine();

          // Play/Pause
          bool isPaused = _plugin.IsIntentionallyPaused;
          string playPauseLabel = isPaused ? " > ##pp" : " || ##pp";
          if (ImGui.Button(playPauseLabel, btnSize)) {
            _plugin.TogglePlayPause();
          }
          if (ImGui.IsItemHovered()) ImGui.SetTooltip(isPaused ? "Resume" : "Pause");

          ImGui.SameLine();

          // Fast Forward
          if (ImGui.Button(">>", btnSize)) {
            _plugin.SeekRelative(_plugin.Config.SeekIncrementSeconds);
          }
          if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Fast Forward {_plugin.Config.SeekIncrementSeconds}s");

          ImGui.SameLine();
          ImGui.Dummy(new Vector2(8, 0));
          ImGui.SameLine();

          // Previous
          if (ImGui.Button("|<", btnSize)) {
            _plugin.PlayPrevious();
          }
          if (ImGui.IsItemHovered()) ImGui.SetTooltip("Previous Track");

          ImGui.SameLine();

          // Next
          if (ImGui.Button(">|", btnSize)) {
            _plugin.PlayNext();
          }
          if (ImGui.IsItemHovered()) ImGui.SetTooltip("Next Track");

          ImGui.SameLine();
          ImGui.Dummy(new Vector2(8, 0));
          ImGui.SameLine();

          // Mute
          string muteLabel = _plugin.IsMuted ? "Unmute" : "Mute";
          if (ImGui.Button(muteLabel, wideBtnSize)) {
            _plugin.ToggleMute();
          }

          ImGui.SameLine();

          // Loop
          bool loopOn = _plugin.Config.LoopEnabled;
          if (loopOn) ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.2f, 0.6f, 0.9f, 1f));
          if (ImGui.Button("Loop", wideBtnSize)) {
            _plugin.Config.LoopEnabled = !_plugin.Config.LoopEnabled;
            _plugin.Config.Save();
          }
          if (loopOn) ImGui.PopStyleColor();
          if (ImGui.IsItemHovered()) ImGui.SetTooltip(loopOn ? "Loop: ON" : "Loop: OFF");

          ImGui.SameLine();

          // Shuffle
          bool shuffleOn = _plugin.Config.ShuffleEnabled;
          if (shuffleOn) ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.2f, 0.6f, 0.9f, 1f));
          if (ImGui.Button("Shuf", wideBtnSize)) {
            _plugin.Config.ShuffleEnabled = !_plugin.Config.ShuffleEnabled;
            _plugin.Config.Save();
          }
          if (shuffleOn) ImGui.PopStyleColor();
          if (ImGui.IsItemHovered()) ImGui.SetTooltip(shuffleOn ? "Shuffle: ON" : "Shuffle: OFF");

          ImGui.SameLine();

          // Refresh
          if (ImGui.Button("Refresh", new Vector2(56, btnH))) {
            _plugin.RefreshCurrentMedia();
          }
          if (ImGui.IsItemHovered()) ImGui.SetTooltip("Re-resolve and replay the current media");

          ImGui.SameLine();

          // Kill
          ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.7f, 0.15f, 0.15f, 1f));
          ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0.9f, 0.2f, 0.2f, 1f));
          if (ImGui.Button("Kill", wideBtnSize)) {
            _plugin.KillAndRestart();
          }
          ImGui.PopStyleColor(2);
          if (ImGui.IsItemHovered()) ImGui.SetTooltip("Kill the media pipeline and restart it");
        }

        // --- Volume Slider ---
        if (_mediaManager != null) {
          ImGui.SetNextItemWidth(Size.Value.X - ImGui.CalcTextSize("Volume").X - 20);
          int vol = (int)(_mediaManager.LiveStreamVolume * 100f);
          if (ImGui.SliderInt("Volume", ref vol, 0, 300)) {
              _mediaManager.LiveStreamVolume = vol / 100f;
          }
        }

        if (eventTriggerCooldown.ElapsedMilliseconds > 10000) {
          CheckWindowSize(true);
          eventTriggerCooldown.Restart();
          _lastWindowSize = Size;
        }
      }
    }

    private static string FormatTimeCode(long ms) {
      var ts = TimeSpan.FromMilliseconds(ms);
      return ts.Hours > 0 
        ? string.Format("{0}:{1:D2}:{2:D2}", ts.Hours, ts.Minutes, ts.Seconds)
        : string.Format("{0}:{1:D2}", ts.Minutes, ts.Seconds);
    }

    public void CheckWindowSize(bool triggerEvent) {
      if (_lastWindowSize != null) {
        if (_lastWindowSize.Value.X != Size.Value.X || _wasNotOpen) {
          if (IsOpen || (_plugin.WorldRenderer?.Transform.Enabled ?? false)) {
            if (IsOpen) {
              if (Size.Value.X < 360) {
                FeedType = TwitchFeedType._160p;
              }
              if (Size.Value.X >= 360 || Size.Value.X < 480) {
                FeedType = TwitchFeedType._360p;
              }
              if (Size.Value.X >= 480 || Size.Value.X < 720) {
                FeedType = TwitchFeedType._480p;
              }
              if (Size.Value.X >= 720 || Size.Value.X < 1080) {
                FeedType = TwitchFeedType._720p;
              }
              if (Size.Value.X >= 1080) {
                FeedType = TwitchFeedType._1080p;
              }
            } else {
              // Fallback to 720p when 2D window is hidden but 3D is active.
              FeedType = TwitchFeedType._720p;
            }
          } else {
            FeedType = TwitchFeedType.Audio;
            _wasNotOpen = true;
          }
          if (triggerEvent) {
            WindowResized?.Invoke(this, EventArgs.Empty);
          }
        }
      }
    }

    public void MarkDisposed() {
      _disposed = true;
      _frameToLoad?.Dispose();
      _frameToLoad = null;
      _blackFrame?.Dispose();
      _blackFrame = null;
    }

    /// <summary>
    /// Returns the current video frame texture wrap,
    /// or a black 16:9 placeholder if no video is playing.
    /// </summary>
    public IDalamudTextureWrap? GetCurrentTextureWrap() {
      return _frameToLoad ?? _blackFrame;
    }

    /// <summary>
    /// Creates a small black 16:9 texture as a placeholder.
    /// </summary>
    private void CreateBlackFrame() {
      try {
        int w = 16, h = 9;
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) {
          pixels[i * 4 + 3] = 255;
        }
        _blackFrame = _textureProvider.CreateFromRaw(
          new Dalamud.Interface.Textures.RawImageSpecification(w, h, 28), pixels);
      } catch (Exception e) {
        _pluginLog.Warning(e, "[Media Player] Failed to create black placeholder texture.");
      }
    }

    /// <summary>
    /// Returns the pixel dimensions of the current video frame texture.
    /// </summary>
    public Vector2 GetCurrentTextureSize() {
      if (_frameToLoad != null) {
        return new Vector2(_frameToLoad.Width, _frameToLoad.Height);
      }
      return Vector2.Zero;
    }
  }
}
