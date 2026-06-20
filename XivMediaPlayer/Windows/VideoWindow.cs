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
    private Direct3D11VideoTexture _videoTexture;
    private IDalamudTextureWrap _blackFrame;
    private ulong _lastLoadedFrameCount = 0;
    private byte[] _lastLoadedFrame;
    private bool _disposed;
    private readonly object _textureLock = new object();
    
    private bool _isDraggingSeek;
    public bool IsDraggingSeek => _isDraggingSeek;
    private float _seekDragProgress;

    public VideoWindow(Plugin plugin, IDalamudPluginInterface pluginInterface, ITextureProvider textureProvider, IPluginLog pluginLog) :
      base("Media Player", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoFocusOnAppearing, false) {
      windowSize = Size = new Vector2(640, 360 + 60);
      this.SizeCondition = ImGuiCond.FirstUseEver;
      this.SizeConstraints = new WindowSizeConstraints() { MinimumSize = new Vector2(360, 260), MaximumSize = new Vector2(3840, 2160) };
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
        private byte[] _localFrameBuffer = Array.Empty<byte>();

        public void UpdateFrame() {
      if (_disposed || _mediaManager == null) return;

      try {
        bool needsUpdate = false;
        ulong frameCount = 0;
        int frameWidth = 0;
        int frameHeight = 0;

        lock (_mediaManager.FrameLock) {
          frameCount = _mediaManager.LastFrameCount;
          frameWidth = _mediaManager.LastFrameWidth;
          frameHeight = _mediaManager.LastFrameHeight;

          if (frameWidth == 0 || frameHeight == 0 || _mediaManager.LastFrame == null || _mediaManager.LastFrame.Length == 0) {
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

          wasStreaming = true;
          if (deadStreamTimer.IsRunning) {
            deadStreamTimer.Stop();
            deadStreamTimer.Reset();
          }

          if (_lastLoadedFrameCount != frameCount) {
             if (_localFrameBuffer.Length != _mediaManager.LastFrame.Length) {
                 _localFrameBuffer = new byte[_mediaManager.LastFrame.Length];
             }
             // Zero-copy pointer swap!
             var temp = _localFrameBuffer;
             _localFrameBuffer = _mediaManager.LastFrame;
             _mediaManager.LastFrame = temp;
             needsUpdate = true;
          }
        }

        if (needsUpdate) {
            lock (_textureLock) {
              if (_disposed) return;
              if (_videoTexture == null || _videoTexture.Width != frameWidth || _videoTexture.Height != frameHeight) {
                _videoTexture?.Dispose();
                _videoTexture = new Direct3D11VideoTexture(frameWidth, frameHeight);
              }
              _videoTexture.Update(_localFrameBuffer, frameWidth, frameHeight);
              _lastLoadedFrameCount = frameCount;
            }
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
        
        Vector2 avail = ImGui.GetContentRegionAvail();
        IntPtr currentSrv = IntPtr.Zero;
        Dalamud.Bindings.ImGui.ImTextureID currentId = default;
        lock (_textureLock) {
            if (_videoTexture != null && _videoTexture.ImGuiHandle != IntPtr.Zero) {
                currentSrv = _videoTexture.ImGuiHandle;
                currentId = System.Runtime.CompilerServices.Unsafe.As<IntPtr, Dalamud.Bindings.ImGui.ImTextureID>(ref currentSrv);
            } else if (_blackFrame != null) {
                var handle = _blackFrame.Handle;
                currentId = handle;
                currentSrv = System.Runtime.CompilerServices.Unsafe.As<Dalamud.Bindings.ImGui.ImTextureID, IntPtr>(ref handle);
            }
        }
        
        if (currentSrv != IntPtr.Zero) {
          Vector2 p0 = ImGui.GetCursorScreenPos();
          
          float maxVidWidth = avail.X;
          float maxVidHeight = Math.Max(10f, avail.Y - uiHeight);

          float targetWidth = maxVidWidth;
          float targetHeight = targetWidth * 0.5625f;

          if (targetHeight > maxVidHeight) {
              // Pillarbox: video is too tall for the available width, shrink width to fit height
              targetHeight = maxVidHeight;
              targetWidth = targetHeight / 0.5625f;
          }

          float offsetX = (maxVidWidth - targetWidth) / 2.0f;
          if (offsetX > 0) {
              ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);
          }

          Vector2 imageSize = new Vector2(targetWidth, targetHeight);
          ImGui.Image(currentId, imageSize);

          if (ImGui.IsItemHovered()) {
              Vector2 mouse = ImGui.GetMousePos();
              float normX = Math.Clamp((mouse.X - p0.X) / imageSize.X, 0f, 1f);
              float normY = Math.Clamp((mouse.Y - p0.Y) / imageSize.Y, 0f, 1f);
              
              bool lmb = ImGui.IsMouseDown(Dalamud.Bindings.ImGui.ImGuiMouseButton.Left);
              bool rmb = ImGui.IsMouseDown(Dalamud.Bindings.ImGui.ImGuiMouseButton.Right);
              float scroll = ImGui.GetIO().MouseWheel;
              
              _plugin.SendEmulationMouseState(normX, normY, scroll, lmb, rmb);
          }
        }

        //  Seek Slider (VODs only) 
        if (_mediaManager != null) {
          var activeStream = _mediaManager.GetActiveStream();
          if (activeStream != null && activeStream.Length > 0) {
            float progress;
            if (!_isDraggingSeek) {
                _seekDragProgress = (float)activeStream.Time / (float)activeStream.Length;
            }
            progress = _seekDragProgress;

            ImGui.SetNextItemWidth(-1);
            
            // Format timecode based on drag progress if dragging, else use actual time
            long displayTime = _isDraggingSeek ? (long)(progress * activeStream.Length) : activeStream.Time;
            
            if (ImGui.SliderFloat("##seek", ref progress, 0f, 1f, 
                FormatTimeCode(displayTime) + " / " + FormatTimeCode(activeStream.Length))) {
              _seekDragProgress = progress;
            }

            if (ImGui.IsItemActivated()) {
                _isDraggingSeek = true;
            }
            
            if (ImGui.IsItemDeactivatedAfterEdit() || (ImGui.IsItemDeactivated() && _isDraggingSeek)) {
                _isDraggingSeek = false;
                activeStream.Time = (long)(_seekDragProgress * activeStream.Length);
            }
          }
        }

        //  Transport Controls 
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
          if (ImGui.IsItemHovered()) ImGui.SetTooltip(isPaused ? "继续" : "暂停");

          ImGui.SameLine();

          // Fast Forward
          if (ImGui.Button(">>", btnSize)) {
            _plugin.SeekRelative(_plugin.Config.SeekIncrementSeconds);
          }
          if (ImGui.IsItemHovered()) ImGui.SetTooltip($"快进 {_plugin.Config.SeekIncrementSeconds}秒");

          ImGui.SameLine();
          ImGui.Dummy(new Vector2(8, 0));
          ImGui.SameLine();

          // Previous
          if (ImGui.Button("|<", btnSize)) {
            _plugin.PlayPrevious();
          }
          if (ImGui.IsItemHovered()) ImGui.SetTooltip("上一首");

          ImGui.SameLine();

          // Next
          if (ImGui.Button(">|", btnSize)) {
            _plugin.PlayNext();
          }
          if (ImGui.IsItemHovered()) ImGui.SetTooltip("下一首");

          ImGui.SameLine();
          ImGui.Dummy(new Vector2(8, 0));
          ImGui.SameLine();

          // 静音
          string muteLabel = _plugin.IsMuted ? "取消静音" : "静音";
          if (ImGui.Button(muteLabel, wideBtnSize)) {
            _plugin.ToggleMute();
          }

          ImGui.SameLine();

          // 循环
          bool loopOn = _plugin.Config.LoopEnabled;
          if (loopOn) ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.2f, 0.6f, 0.9f, 1f));
          if (ImGui.Button("循环", wideBtnSize)) {
            _plugin.Config.LoopEnabled = !_plugin.Config.LoopEnabled;
            _plugin.Config.Save();
          }
          if (loopOn) ImGui.PopStyleColor();
          if (ImGui.IsItemHovered()) ImGui.SetTooltip(loopOn ? "循环: 开" : "循环: 关");

          ImGui.SameLine();

          // 随机
          bool shuffleOn = _plugin.Config.ShuffleEnabled;
          if (shuffleOn) ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.2f, 0.6f, 0.9f, 1f));
          if (ImGui.Button("随机", wideBtnSize)) {
            _plugin.Config.ShuffleEnabled = !_plugin.Config.ShuffleEnabled;
            _plugin.Config.Save();
          }
          if (shuffleOn) ImGui.PopStyleColor();
          if (ImGui.IsItemHovered()) ImGui.SetTooltip(shuffleOn ? "随机: 开" : "随机: 关");

          ImGui.SameLine();

          // 刷新
          if (ImGui.Button("刷新", new Vector2(56, btnH))) {
            _plugin.RefreshCurrentMedia();
          }
          if (ImGui.IsItemHovered()) ImGui.SetTooltip("重新解析并播放当前媒体");
          ImGui.SameLine();

          // DMCA
          if (ImGui.Button("DMCA", wideBtnSize)) {
              string url = _plugin.LastStreamURL;
              if (!string.IsNullOrEmpty(url)) {
                  string domain = "the site administrator";
                  try {
                      Uri uri = new Uri(url);
                      domain = uri.Host;
                  } catch { }
                  
                  string dmcaText = $"Content URL: {url}\n\nPlease contact {domain} to report this content.";
                  ImGui.SetClipboardText(dmcaText);
                  _plugin.PrintChat("[媒体播放器] DMCA 联系信息和链接已复制到剪贴板", Plugin.ChatSeverity.Info);
              } else {
                  _plugin.PrintChatError("[媒体播放器] 没有可复制的媒体链接");
              }
          }
          if (ImGui.IsItemHovered()) ImGui.SetTooltip("复制 DMCA 信息和媒体链接到剪贴板");
          ImGui.SameLine();

          // 重启
          ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.7f, 0.15f, 0.15f, 1f));
          ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0.9f, 0.2f, 0.2f, 1f));
          if (ImGui.Button("重启", wideBtnSize)) {
            _plugin.RequestKillAndRestart();
          }
          ImGui.PopStyleColor(2);
          if (ImGui.IsItemHovered()) ImGui.SetTooltip("终止并重启媒体流程");
        }

        //  Volume Slider 
        if (_mediaManager != null) {
          ImGui.SetNextItemWidth(-(ImGui.CalcTextSize("音量").X + ImGui.GetStyle().ItemInnerSpacing.X));
          int vol = (int)(_mediaManager.LiveStreamVolume * 100f);
          if (ImGui.SliderInt("音量", ref vol, 0, 300)) {
              _mediaManager.LiveStreamVolume = vol / 100f;
          }
        }
        
        //  Emulation Controller 
        if (_plugin.CurrentStreamer == "Emulation" && _plugin.ControllerService != null) {
          ImGui.Separator();
          ImGui.SetNextItemWidth(100f);
          int comboIdx = _plugin.ControllerService.PlayerSlot == 255 ? 4 : _plugin.ControllerService.PlayerSlot;
          if (ImGui.Combo("手柄位置", ref comboIdx, "玩家 1\0玩家 2\0玩家 3\0玩家 4\0无\0")) {
              _plugin.ControllerService.PlayerSlot = comboIdx == 4 ? (byte)255 : (byte)comboIdx;
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
              if (Size.Value.X >= 360 && Size.Value.X < 480) {
                FeedType = TwitchFeedType._360p;
              }
              if (Size.Value.X >= 480 && Size.Value.X < 720) {
                FeedType = TwitchFeedType._480p;
              }
              if (Size.Value.X >= 720 && Size.Value.X < 1080) {
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
      lock (_textureLock) {
        _disposed = true;
        _videoTexture?.Dispose();
        _videoTexture = null;
        _blackFrame?.Dispose();
        _blackFrame = null;
      }
    }

    /// <summary>
    /// Returns the current video frame texture pointer and dimensions,
    /// or a black 16:9 placeholder if no video is playing.
    /// </summary>
    public void GetCurrentVideoTexture(out IntPtr srv, out int width, out int height) {
      lock (_textureLock) {
        if (_videoTexture != null && _videoTexture.ImGuiHandle != IntPtr.Zero) {
            srv = _videoTexture.ImGuiHandle;
            width = _videoTexture.Width;
            height = _videoTexture.Height;
        } else if (_blackFrame != null) {
            var handle = _blackFrame.Handle;
            srv = System.Runtime.CompilerServices.Unsafe.As<Dalamud.Bindings.ImGui.ImTextureID, IntPtr>(ref handle);
            width = _blackFrame.Width;
            height = _blackFrame.Height;
        } else {
            srv = IntPtr.Zero;
            width = 16;
            height = 9;
        }
      }
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
      if (_videoTexture != null) {
        return new Vector2(_videoTexture.Width, _videoTexture.Height);
      }
      return Vector2.Zero;
    }
  }
}
