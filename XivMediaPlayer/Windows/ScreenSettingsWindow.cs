using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MediaPlayerCore.Compositing;
using XivMediaPlayer.Compositing;
using System;
using System.Numerics;
using Dalamud.Plugin.Services;

namespace XivMediaPlayer.Windows {
  /// <summary>
  /// ImGui settings window for interactively positioning the world-space video screen.
  /// Provides drag controls for position, rotation, and scale, plus quick-action buttons.
  /// </summary>
  internal class ScreenSettingsWindow : Window {
    private readonly WorldScreenTransform _transform;
    private readonly WorldVideoRenderer _renderer;
    private readonly Action _onSave;
    private readonly Action _onPlaceAtCamera;
    private readonly Plugin _plugin;
    private readonly IGameGui _gameGui;

    private string _statusMessage = "";
    private Vector4 _statusColor = new Vector4(1, 1, 1, 1);

    private Vector3 _position;
    private Vector2 _rotation; // yaw, pitch
    private Vector2 _scale;
    private bool _enabled;
    private bool _wasShiftPressed;
    private int _aspectRatio = 0; // 0 = 16:9, 1 = 4:3

    private float _opacity = 1.0f;
    private bool _isProjectorMode = false;
    private Vector3 _screensaverColor = new Vector3(0.0f, 0.0f, 0.0f);
    private int _screensaverStyle = 0;

    // Drag state for world-space interaction
    private bool _isDragging;
    private Vector2 _dragStartMouse;
    private Vector3 _dragStartPosition;

    public ScreenSettingsWindow(
        Plugin plugin,
        IGameGui gameGui,
        WorldScreenTransform transform,
        WorldVideoRenderer renderer,
        Action onSave,
        Action onPlaceAtCamera) :
      base("Screen Placement###ScreenPlacement",
        ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize,
        false) {
      _plugin = plugin;
      _gameGui = gameGui;
      _transform = transform;
      _renderer = renderer;
      _onSave = onSave;
      _onPlaceAtCamera = onPlaceAtCamera;

      Size = new Vector2(340, 0);
      SizeCondition = ImGuiCond.FirstUseEver;

      SyncFromTransform();
    }

    public void SyncFromTransform() {
      _position = _transform.Position;
      _rotation = new Vector2(_transform.RotationDegrees.Y, _transform.RotationDegrees.X); // yaw, pitch
      _scale = _transform.Scale;
      _enabled = _transform.Enabled;
      _opacity = _transform.Opacity;
      _isProjectorMode = _transform.IsProjectorMode;
      _screensaverColor = _transform.ScreensaverColor;
      _screensaverStyle = _transform.ScreensaverStyle;
    }

    private void SyncToTransform() {
      _transform.Position = _position;
      _transform.RotationDegrees = new Vector3(_rotation.Y, _rotation.X, 0); // pitch, yaw, roll
      _transform.Scale = _scale;
      _transform.Opacity = _opacity;
      _transform.IsProjectorMode = _isProjectorMode;
      _transform.ScreensaverColor = _screensaverColor;
      _transform.ScreensaverStyle = _screensaverStyle;
    }

    public override void Draw() {
      string locKey = _plugin.LocationKey;
      bool isOutdoors = !string.IsNullOrEmpty(locKey) && locKey.StartsWith("zone_");
      bool isIsland = !string.IsNullOrEmpty(locKey) && locKey.StartsWith("island_");
      bool hasHousingMenuOpen = _plugin.IsHousingMenuOpen;
      bool hasPrivileges = isOutdoors || isIsland || hasHousingMenuOpen;

      if (!hasPrivileges) {
          ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "需要房屋菜单");
          ImGui.TextWrapped("要放置或同步屏幕，请在游戏中打开'室内家具'菜单。");
          ImGui.Spacing();
          if (ImGui.Button("教程视频")) {
              System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                  FileName = "https://www.youtube.com/watch?v=ZgLs2OJQ8ks",
                  UseShellExecute = true
              });
          }
          return;
      }

      if (isOutdoors && !_plugin.Config.EnableOutdoorPublicScreens) {
          ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "室外屏幕已禁用");
          ImGui.TextWrapped("必须在主设置菜单中启用'允许室外公共屏幕'才能将电视放置在室外。");
          return;
      }

      // Enable toggle 
      if (ImGui.Checkbox("在世界中渲染", ref _enabled)) {
        _transform.Enabled = _enabled;
        
        // Auto-delete from server if turning off and we own it or have privileges
        if (!_enabled && !string.IsNullOrEmpty(locKey) &&
            _plugin.CurrentTvPlacement != null && (_plugin.CurrentTvPlacement.OwnerId == _plugin.Config.OwnerId || hasPrivileges)) {
            _ = DeleteTvAsync(locKey, restoreOnFailure: true);
        } else {
            _onSave?.Invoke();
        }
      }

      ImGui.SameLine();
      ImGui.SetCursorPosX(ImGui.GetWindowWidth() - 110);
      if (ImGui.Button("教程视频")) {
          System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
              FileName = "https://www.youtube.com/watch?v=ZgLs2OJQ8ks",
              UseShellExecute = true
          });
      }

      if (!_enabled) {
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f),
          "启用以在游戏世界中放置视频。");
        return;
      }

      ImGui.Separator();

      // Ctrl+Shift quick-snap logic
      bool isSnapKeyPressed = ImGui.GetIO().KeyShift && ImGui.GetIO().KeyCtrl;
      if (isSnapKeyPressed && !_wasShiftPressed) {
          unsafe {
              var hm = FFXIVClientStructs.FFXIV.Client.Game.HousingManager.Instance();
              if (hm != null && hm->IndoorTerritory != null) {
                  var hover = hm->IndoorTerritory->HoveredHousingObject;
                  var target = hm->IndoorTerritory->TargetedHousingObject;
                  var objToSnap = hover != null ? hover : target;

                  if (objToSnap != null) {
                      _position = objToSnap->Position;
                      _rotation.X = objToSnap->Rotation * (180f / (float)Math.PI);
                      _rotation.Y = 0f;
                      SyncToTransform();
                      _onSave?.Invoke();
                  }
              }
          }
      }
      _wasShiftPressed = isSnapKeyPressed;

      // Quick actions 
      if (ImGui.Button("放置在摄像机位置")) {
        _onPlaceAtCamera?.Invoke();
        SyncFromTransform();
        _onSave?.Invoke();
      }
      
      ImGui.Spacing();
      ImGui.TextColored(new Vector4(0.7f, 1f, 0.7f, 1f), "快速吸附:");
      ImGui.TextWrapped("在编辑模式下按住 CTRL + SHIFT 悬停或选中家具，即可将电视吸附到该位置。");
      ImGui.Spacing();
      
      if (ImGui.Button("保存")) {
        SyncToTransform();
        _onSave?.Invoke();
      }
      ImGui.SameLine();
      if (ImGui.Button("重置")) {
        _transform.Enabled = false;
        _enabled = false;
        SyncFromTransform();
        
        string locKey2 = _plugin.LocationKey;
        if (!string.IsNullOrEmpty(locKey2) && _plugin.CurrentTvPlacement != null && (_plugin.CurrentTvPlacement.OwnerId == _plugin.Config.OwnerId || hasPrivileges)) {
            _ = DeleteTvAsync(locKey2, restoreOnFailure: true);
        } else {
            _onSave?.Invoke();
        }
      }

      ImGui.Spacing();
      ImGui.Separator();

      // Position 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "位置");

      bool posChanged = false;
      posChanged |= ImGui.DragFloat("X##pos", ref _position.X, 0.05f, -1000f, 1000f, "%.2f");
      bool savePos = ImGui.IsItemDeactivatedAfterEdit();
      posChanged |= ImGui.DragFloat("Y##pos", ref _position.Y, 0.05f, -1000f, 1000f, "%.2f");
      savePos |= ImGui.IsItemDeactivatedAfterEdit();
      posChanged |= ImGui.DragFloat("Z##pos", ref _position.Z, 0.05f, -1000f, 1000f, "%.2f");
      savePos |= ImGui.IsItemDeactivatedAfterEdit();
      
      if (posChanged) {
        _transform.Position = _position;
      }
      if (savePos) {
        _onSave?.Invoke();
      }

      // Nudge buttons
      float nudge = 0.25f;
      if (ImGui.Button("\u2190##posX")) { _position.X -= nudge; _transform.Position = _position; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("\u2192##posX")) { _position.X += nudge; _transform.Position = _position; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("\u2193##posY")) { _position.Y -= nudge; _transform.Position = _position; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("\u2191##posY")) { _position.Y += nudge; _transform.Position = _position; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("近##posZ")) { _position.Z -= nudge; _transform.Position = _position; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("远##posZ")) { _position.Z += nudge; _transform.Position = _position; _onSave?.Invoke(); }

      ImGui.Spacing();
      ImGui.Separator();

      // Rotation 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "旋转");

      bool rotChanged = false;
      rotChanged |= ImGui.SliderFloat("Yaw##rot", ref _rotation.X, -180f, 180f, "%.1f\u00b0");
      bool saveRot = ImGui.IsItemDeactivatedAfterEdit();
      rotChanged |= ImGui.SliderFloat("Pitch##rot", ref _rotation.Y, -90f, 90f, "%.1f\u00b0");
      saveRot |= ImGui.IsItemDeactivatedAfterEdit();
      if (rotChanged) {
        _transform.RotationDegrees = new Vector3(_rotation.Y, _rotation.X, 0);
      }
      if (saveRot) {
        _onSave?.Invoke();
      }

      // Quick rotation presets
      if (ImGui.Button("朝北")) { _rotation.X = 0; _transform.RotationDegrees = new Vector3(_rotation.Y, 0, 0); _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("朝东")) { _rotation.X = 90; _transform.RotationDegrees = new Vector3(_rotation.Y, 90, 0); _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("朝南")) { _rotation.X = 180; _transform.RotationDegrees = new Vector3(_rotation.Y, 180, 0); _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("朝西")) { _rotation.X = -90; _transform.RotationDegrees = new Vector3(_rotation.Y, -90, 0); _onSave?.Invoke(); }

      ImGui.Spacing();
      ImGui.Separator();

      // Scale 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "大小 (世界单位)");

      bool aspectChanged = false;
      aspectChanged |= ImGui.RadioButton("16:9", ref _aspectRatio, 0);
      ImGui.SameLine();
      aspectChanged |= ImGui.RadioButton("4:3", ref _aspectRatio, 1);
      ImGui.SameLine();
      aspectChanged |= ImGui.RadioButton("自定义 / 自由比例", ref _aspectRatio, 2);

      bool scaleChanged = false;
      if (_aspectRatio != 2) {
          scaleChanged |= ImGui.DragFloat("对角线尺寸##scale", ref _scale.X, 0.1f, 0.5f, 200f, "%.1f");
      } else {
          scaleChanged |= ImGui.DragFloat("宽度##scaleX", ref _scale.X, 0.1f, 0.5f, 200f, "%.1f");
          scaleChanged |= ImGui.DragFloat("高度##scaleY", ref _scale.Y, 0.1f, 0.5f, 200f, "%.1f");
      }
      bool saveScale = ImGui.IsItemDeactivatedAfterEdit();

      if (aspectChanged || scaleChanged) {
        if (_aspectRatio != 2) {
            float ratio = _aspectRatio == 0 ? (9f / 16f) : (3f / 4f);
            _scale.Y = _scale.X * ratio;
        }
        _transform.Scale = _scale;
      }
      if (saveScale || aspectChanged) {
        _onSave?.Invoke();
      }

      // Preset sizes
      if (ImGui.Button("小 (2m)")) { _scale.X = 2f; _scale.Y = _scale.X * (_aspectRatio == 1 ? (3f/4f) : (9f/16f)); _transform.Scale = _scale; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("中 (4m)")) { _scale.X = 4f; _scale.Y = _scale.X * (_aspectRatio == 1 ? (3f/4f) : (9f/16f)); _transform.Scale = _scale; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("大 (8m)")) { _scale.X = 8f; _scale.Y = _scale.X * (_aspectRatio == 1 ? (3f/4f) : (9f/16f)); _transform.Scale = _scale; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("影院 (12m)")) { _scale.X = 12f; _scale.Y = _scale.X * (_aspectRatio == 1 ? (3f/4f) : (9f/16f)); _transform.Scale = _scale; _onSave?.Invoke(); }

      ImGui.Spacing();
      ImGui.Separator();

      // 投影仪与透明度
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "投影仪与透明度");

      bool appearanceChanged = false;
      appearanceChanged |= ImGui.Checkbox("投影仪模式 (加法混合)", ref _isProjectorMode);

      appearanceChanged |= ImGui.SliderFloat("透明度", ref _opacity, 0.05f, 1.0f, "%.2f");
      appearanceChanged |= ImGui.ColorEdit3("屏保颜色", ref _screensaverColor);

      string[] screensaverStyles = new string[] { "弹跳标志", "录像机", "无信号彩条", "雪花噪点", "测试图案", "数字雨" };
      appearanceChanged |= ImGui.Combo("屏保样式", ref _screensaverStyle, screensaverStyles, screensaverStyles.Length);

      bool saveAppearance = ImGui.IsItemDeactivatedAfterEdit() || ImGui.IsItemDeactivated();

      if (appearanceChanged) {
        _transform.Opacity = _opacity;
        _transform.IsProjectorMode = _isProjectorMode;
        _transform.ScreensaverColor = _screensaverColor;
        _transform.ScreensaverStyle = _screensaverStyle;
      }
      if (saveAppearance || appearanceChanged) {
        _onSave?.Invoke();
      }


      ImGui.Spacing();
      ImGui.Separator();

      // Info 
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        $"Screen: {_scale.X:F1}m x {_scale.Y:F1}m at ({_position.X:F1}, {_position.Y:F1}, {_position.Z:F1})");

      var depthDebug = _renderer.DepthDebugInfo;
      if (!string.IsNullOrEmpty(depthDebug)) {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Depth Debug");
        ImGui.TextWrapped(depthDebug);
      }
      var rendererError = _renderer.DepthRendererError;
      if (!string.IsNullOrEmpty(rendererError)) {
        ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), $"GPU Error: {rendererError}");
      }

      ImGui.Spacing();
      ImGui.Separator();

      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "房间同步");
      ImGui.TextWrapped("上述保存仅在本地生效。要使电视对其他玩家可见，必须将其同步到房间。");
      
      string locationKey = _plugin.LocationKey;
      bool isOutdoorsSync = !string.IsNullOrEmpty(locationKey) && locationKey.StartsWith("zone_");
      bool isIslandSync = !string.IsNullOrEmpty(locationKey) && locationKey.StartsWith("island_");
      
      if (string.IsNullOrEmpty(locationKey) || (!locationKey.StartsWith("house_") && !locationKey.StartsWith("zone_") && !locationKey.StartsWith("island_"))) {
          ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "必须在房屋区域或有效的室外区域才能同步电视。");
      } else {
          ImGui.Text($"位置密钥: {locationKey}");
          if (_plugin.CurrentTvPlacement == null || _plugin.CurrentTvPlacement.OwnerId == _plugin.Config.OwnerId) {
              bool isLocked = _plugin.CurrentTvPlacement?.IsLocked ?? !isOutdoorsSync;
              if (!isOutdoorsSync) {
                  if (ImGui.Checkbox("锁定TV仅限所有者", ref isLocked)) {
                      if (_plugin.CurrentTvPlacement != null) {
                          _plugin.CurrentTvPlacement.IsLocked = isLocked;
                      } else {
                          _plugin.CurrentTvPlacement = new TvPlacement {
                              OwnerId = _plugin.Config.OwnerId,
                              IsLocked = isLocked
                          };
                      }
                      RegisterTvAsync(locationKey);
                  }
              }
              
              ImGui.Spacing();
              if (ImGui.Button("同步放置到区域")) {
                  RegisterTvAsync(locationKey);
              }
              ImGui.SameLine();
              if (ImGui.Button("从区域移除电视")) {
                  _ = DeleteTvAsync(locationKey);
              }
          } else {
              if (_plugin.IsHousingMenuOpen || isOutdoorsSync || isIslandSync) {
                  if (ImGui.Button("接管电视所有权")) {
                      RegisterTvAsync(locationKey);
                  }
                  ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "由于你在此处拥有权限，可以覆盖此锁定的电视。");
              } else {
                  if (_plugin.CurrentTvPlacement.IsLocked) {
                      ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "此电视已被其所有者锁定。");
                  }
              }
          }

          if (!string.IsNullOrEmpty(_statusMessage)) {
              ImGui.TextColored(_statusColor, _statusMessage);
          }
      }
    }

    public async System.Threading.Tasks.Task<bool> DeleteTvAsync(string locationKey, bool restoreOnFailure = false) {
        if (_plugin.CurrentTvPlacement == null) return false;
        var currentPlacement = _plugin.CurrentTvPlacement;
        var serverLocationKey = string.IsNullOrEmpty(currentPlacement.LocationKey) ? locationKey : currentPlacement.LocationKey;
        
        _statusMessage = "正在从服务器删除电视...";
        _statusColor = new Vector4(1, 1, 1, 1);
        
        try {
            bool isOutdoorsSync = !string.IsNullOrEmpty(serverLocationKey) && serverLocationKey.StartsWith("zone_");
            bool isIslandSync = !string.IsNullOrEmpty(serverLocationKey) && serverLocationKey.StartsWith("island_");
            bool success = await _plugin.ServerClient.DeleteTvAsync(serverLocationKey, currentPlacement.Id, _plugin.Config.OwnerId, _plugin.IsHousingMenuOpen || isOutdoorsSync || isIslandSync);
            if (success) {
                _plugin.CurrentTvPlacement = null;
                _plugin.Config.ScreenPlacements.Remove(locationKey);
                _plugin.Config.ScreenPlacements.Remove(serverLocationKey);
                _transform.Enabled = false;
                _enabled = false;
                _plugin.Config.Save();
                _statusMessage = "成功移除房间内的电视!";
                _statusColor = new Vector4(0.3f, 1f, 0.3f, 1);
                _plugin.PrintChat("[媒体播放器] " + _statusMessage);
                return true;
            } else {
                RestoreEnabledAfterDeleteFailure(restoreOnFailure);
                _statusMessage = "移除电视失败";
                _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
                _plugin.PrintChatError("[媒体播放器] " + _statusMessage);
                return false;
            }
        } catch (UnauthorizedAccessException) {
            RestoreEnabledAfterDeleteFailure(restoreOnFailure);
            _statusMessage = "无法删除电视: 已被房主锁定";
            _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
            _plugin.PrintChatError("[媒体播放器] " + _statusMessage);
        } catch (Exception) {
            RestoreEnabledAfterDeleteFailure(restoreOnFailure);
            _statusMessage = "删除电视时发生网络错误";
            _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
            _plugin.PrintChatError("[媒体播放器] " + _statusMessage);
        }

        return false;
    }

    private void RestoreEnabledAfterDeleteFailure(bool restoreOnFailure) {
        if (!restoreOnFailure) return;

        _enabled = true;
        _transform.Enabled = true;
    }

    /// <summary>
    /// Handles world-space click-drag interaction on the video quad.
    /// Call this from the main draw loop with the projected screen coordinates
    /// of the quad center. Returns true if drag is active.
    /// </summary>
    public bool HandleWorldDrag(Vector2 screenCenter, float screenRadius) {
      if (!_enabled) return false;

      var mousePos = ImGui.GetMousePos();
      float dist = Vector2.Distance(mousePos, screenCenter);

      if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && dist < screenRadius) {
        _isDragging = true;
        _dragStartMouse = mousePos;
        _dragStartPosition = _transform.Position;
      }

      if (_isDragging) {
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
          var delta = ImGui.GetMousePos() - _dragStartMouse;
          // Convert screen delta to world delta (approximate: 0.01 world units per pixel)
          float sensitivity = 0.01f;
          _transform.Position = _dragStartPosition + new Vector3(
            delta.X * sensitivity,
            -delta.Y * sensitivity,
            0);
          SyncFromTransform();
          return true;
        } else {
          if (_isDragging) {
             _onSave?.Invoke();
          }
          _isDragging = false;
        }
      }

      return false;
    }

    private DateTime _lastRegistrationTime = DateTime.MinValue;

    public async void RegisterTvAsync(string locationKey) {
      if (!_enabled) {
        _statusMessage = "室外屏幕未启用!";
        _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
        return;
      }

      if ((DateTime.UtcNow - _lastRegistrationTime).TotalSeconds < 2) {
          return; // Debounce to prevent double-logs from FFXIV UI flickering
      }
      _lastRegistrationTime = DateTime.UtcNow;

      _statusMessage = "正在向服务器注册电视...";
      _statusColor = new Vector4(1, 1, 1, 1);

      var placement = new TvPlacement {
        LocationKey = locationKey,
        PositionX = _position.X,
        PositionY = _position.Y,
        PositionZ = _position.Z,
        RotationX = _transform.RotationDegrees.X,
        RotationY = _transform.RotationDegrees.Y,
        RotationZ = _transform.RotationDegrees.Z,
        ScaleX = _scale.X,
        ScaleY = _scale.Y,
        Opacity = _opacity,
        IsProjectorMode = _isProjectorMode,
        ScreensaverColorR = _screensaverColor.X,
        ScreensaverColorG = _screensaverColor.Y,
        ScreensaverColorB = _screensaverColor.Z,
        ScreensaverStyle = _screensaverStyle,
        OwnerId = _plugin.Config.OwnerId,
        IsLocked = _plugin.CurrentTvPlacement?.IsLocked ?? (!locationKey.StartsWith("zone_") && !locationKey.StartsWith("island_")),
        BypassLock = _plugin.IsHousingMenuOpen || locationKey.StartsWith("zone_") || locationKey.StartsWith("island_")
      };

      SyncToTransform();
      _onSave?.Invoke();

      try 
      {
        var result = await _plugin.ServerClient.RegisterTvAsync(locationKey, placement);
        if (result != null) {
          _plugin.CurrentTvPlacement = result;
          _statusMessage = "已成功为所有访客注册电视!";
          _statusColor = new Vector4(0.3f, 1f, 0.3f, 1);
          _plugin.PrintChat("[媒体播放器] " + _statusMessage);
        } else {
          _statusMessage = "已本地保存, 但无法连接到同步服务器";
          _statusColor = new Vector4(1, 0.6f, 0.2f, 1);
          _plugin.PrintChatError("[媒体播放器] " + _statusMessage);
        }
      }
      catch (UnauthorizedAccessException)
      {
        _statusMessage = "无法移动电视: 已被房主锁定";
        _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
        _plugin.PrintChatError("[媒体播放器] " + _statusMessage);
      }
      catch (Exception)
      {
        _statusMessage = "同步电视时发生网络错误";
        _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
        _plugin.PrintChatError("[媒体播放器] " + _statusMessage);
      }
    }

  }
}
