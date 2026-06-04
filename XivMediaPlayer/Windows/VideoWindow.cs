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
    private byte[] _lastLoadedFrame;
    private bool taskAlreadyRunning;
    private bool _disposed;

    public VideoWindow(IDalamudPluginInterface pluginInterface, ITextureProvider textureProvider, IPluginLog pluginLog) :
      base("Media Player", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar, false) {
      windowSize = Size = new Vector2(640, 360);
      this.SizeCondition = ImGuiCond.Always;
      initialSize = Size;
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
            taskAlreadyRunning = true;
            ReadOnlyMemory<byte> bytes = new byte[0];
            lock (_mediaManager.LastFrame) {
              bytes = _mediaManager.LastFrame;
            }

            if (bytes.Length > 0) {
              if (_lastLoadedFrame != _mediaManager.LastFrame) {
                var newTexture = await _textureProvider.CreateFromImageAsync(bytes);
                var oldTexture = _frameToLoad;
                _frameToLoad = newTexture;
                _lastLoadedFrame = _mediaManager.LastFrame;
                oldTexture?.Dispose();
              }
            }
            taskAlreadyRunning = false;
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
        Size = new Vector2(ImGui.GetWindowSize().X, ImGui.GetWindowSize().X * 0.5625f);
        SizeConstraints = new WindowSizeConstraints() { MaximumSize = ImGui.GetMainViewport().Size, MinimumSize = new Vector2(360, 480) };
        if (_frameToLoad != null) {
          ImGui.Image(_frameToLoad.Handle, new Vector2(Size.Value.X, Size.Value.X * 0.5625f));
        }
        if (eventTriggerCooldown.ElapsedMilliseconds > 10000) {
          CheckWindowSize(true);
          eventTriggerCooldown.Restart();
          _lastWindowSize = Size;
        }
      }
    }

    public void CheckWindowSize(bool triggerEvent) {
      if (_lastWindowSize != null) {
        if (_lastWindowSize.Value.X != Size.Value.X || _wasNotOpen) {
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
