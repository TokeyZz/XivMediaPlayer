using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MediaPlayerCore.Compositing;
using XivMediaPlayer.Compositing;
using System;
using System.Numerics;
using Dalamud.Plugin.Services;
using XivMediaPlayer.Networking.Models;

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

    private void SyncFromTransform() {
      _position = _transform.Position;
      _rotation = new Vector2(_transform.RotationDegrees.Y, _transform.RotationDegrees.X); // yaw, pitch
      _scale = _transform.Scale;
      _enabled = _transform.Enabled;
    }

    private void SyncToTransform() {
      _transform.Position = _position;
      _transform.RotationDegrees = new Vector3(_rotation.Y, _rotation.X, 0); // pitch, yaw, roll
      _transform.Scale = _scale;
      _transform.Enabled = _enabled;
    }

    public override void Draw() {
      // Check if housing menu is open
      bool hasHousingMenuOpen = _plugin.IsHousingMenuOpen;

      if (!hasHousingMenuOpen) {
          ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "Housing Menu Required");
          ImGui.TextWrapped("To place or sync a screen, please open the 'Indoor Furnishings' menu in-game.");
          return;
      }

      // Enable toggle 
      if (ImGui.Checkbox("Render in World", ref _enabled)) {
        _transform.Enabled = _enabled;
        _onSave?.Invoke();
      }

      if (!_enabled) {
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f),
          "Enable to place the video in the game world.");
        return;
      }

      ImGui.Separator();

      // Shift quick-snap logic
      bool isShiftPressed = ImGui.GetIO().KeyShift;
      if (isShiftPressed && !_wasShiftPressed) {
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
      _wasShiftPressed = isShiftPressed;

      // Quick actions 
      if (ImGui.Button("Place at Camera")) {
        _onPlaceAtCamera?.Invoke();
        SyncFromTransform();
        _onSave?.Invoke();
      }
      
      ImGui.Spacing();
      ImGui.TextColored(new Vector4(0.7f, 1f, 0.7f, 1f), "Quick Snap:");
      ImGui.TextWrapped("Hold SHIFT while hovering over or selecting a furnishing in Edit Mode to instantly snap the TV to it.");
      ImGui.Spacing();
      
      if (ImGui.Button("Save")) {
        SyncToTransform();
        _onSave?.Invoke();
      }
      ImGui.SameLine();
      if (ImGui.Button("Reset")) {
        _transform.Enabled = false;
        _enabled = false;
        SyncFromTransform();
        _onSave?.Invoke();
      }

      ImGui.Spacing();
      ImGui.Separator();

      // Position 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "Position");

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
      if (ImGui.Button("Near##posZ")) { _position.Z -= nudge; _transform.Position = _position; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("Far##posZ")) { _position.Z += nudge; _transform.Position = _position; _onSave?.Invoke(); }

      ImGui.Spacing();
      ImGui.Separator();

      // Rotation 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "Rotation");

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
      if (ImGui.Button("Face North")) { _rotation.X = 0; _transform.RotationDegrees = new Vector3(_rotation.Y, 0, 0); _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("Face East")) { _rotation.X = 90; _transform.RotationDegrees = new Vector3(_rotation.Y, 90, 0); _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("Face South")) { _rotation.X = 180; _transform.RotationDegrees = new Vector3(_rotation.Y, 180, 0); _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("Face West")) { _rotation.X = -90; _transform.RotationDegrees = new Vector3(_rotation.Y, -90, 0); _onSave?.Invoke(); }

      ImGui.Spacing();
      ImGui.Separator();

      // Scale 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "Size (world units)");

      bool aspectChanged = false;
      aspectChanged |= ImGui.RadioButton("16:9", ref _aspectRatio, 0);
      ImGui.SameLine();
      aspectChanged |= ImGui.RadioButton("4:3", ref _aspectRatio, 1);
      
      bool scaleChanged = false;
      scaleChanged |= ImGui.DragFloat("Diagonal Size##scale", ref _scale.X, 0.1f, 0.5f, 200f, "%.1f");
      bool saveScale = ImGui.IsItemDeactivatedAfterEdit();

      if (aspectChanged || scaleChanged) {
        float ratio = _aspectRatio == 0 ? (9f / 16f) : (3f / 4f);
        _scale.Y = _scale.X * ratio;
        _transform.Scale = _scale;
      }
      if (saveScale || aspectChanged) {
        _onSave?.Invoke();
      }

      // Preset sizes
      if (ImGui.Button("Small (2m)")) { _scale.X = 2f; _scale.Y = _scale.X * (_aspectRatio == 0 ? (9f/16f) : (3f/4f)); _transform.Scale = _scale; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("Medium (4m)")) { _scale.X = 4f; _scale.Y = _scale.X * (_aspectRatio == 0 ? (9f/16f) : (3f/4f)); _transform.Scale = _scale; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("Large (8m)")) { _scale.X = 8f; _scale.Y = _scale.X * (_aspectRatio == 0 ? (9f/16f) : (3f/4f)); _transform.Scale = _scale; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("Cinema (12m)")) { _scale.X = 12f; _scale.Y = _scale.X * (_aspectRatio == 0 ? (9f/16f) : (3f/4f)); _transform.Scale = _scale; _onSave?.Invoke(); }


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
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "Room Sync");
      
      string locationKey = _plugin.LocationKey;
      if (string.IsNullOrEmpty(locationKey) || !locationKey.StartsWith("house_")) {
          ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "You must be inside a housing area to sync TVs.");
      } else {
          ImGui.Text($"Location Key: {locationKey}");
          if (_plugin.CurrentTvPlacement == null || _plugin.CurrentTvPlacement.OwnerId == _plugin.Config.OwnerId) {
              bool isLocked = _plugin.CurrentTvPlacement?.IsLocked ?? true;
              if (ImGui.Checkbox("Lock TV to Owner Only", ref isLocked)) {
                  if (_plugin.CurrentTvPlacement != null) {
                      _plugin.CurrentTvPlacement.IsLocked = isLocked;
                  } else {
                      _plugin.CurrentTvPlacement = new Networking.Models.TvPlacement {
                          OwnerId = _plugin.Config.OwnerId,
                          IsLocked = isLocked
                      };
                  }
                  RegisterTvAsync(locationKey);
              }
          } else {
              if (_plugin.IsHousingMenuOpen) {
                  if (ImGui.Button("Take Ownership of TV")) {
                      RegisterTvAsync(locationKey);
                  }
                  ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "You can override this locked TV because you have housing privileges.");
              } else {
                  if (_plugin.CurrentTvPlacement.IsLocked) {
                      ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "This TV is locked by its owner.");
                  }
              }
          }

          if (!string.IsNullOrEmpty(_statusMessage)) {
              ImGui.TextColored(_statusColor, _statusMessage);
          }
      }
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
        _statusMessage = "World screen is not enabled!";
        _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
        return;
      }

      if ((DateTime.UtcNow - _lastRegistrationTime).TotalSeconds < 2) {
          return; // Debounce to prevent double-logs from FFXIV UI flickering
      }
      _lastRegistrationTime = DateTime.UtcNow;

      _statusMessage = "Syncing with server...";
      _statusColor = new Vector4(1, 1, 1, 1);

      var placement = new TvPlacement {
        PositionX = _position.X,
        PositionY = _position.Y,
        PositionZ = _position.Z,
        RotationX = _transform.RotationDegrees.X,
        RotationY = _transform.RotationDegrees.Y,
        RotationZ = _transform.RotationDegrees.Z,
        ScaleX = _scale.X,
        ScaleY = _scale.Y,
        OwnerId = _plugin.Config.OwnerId,
        IsLocked = _plugin.CurrentTvPlacement?.IsLocked ?? true,
        BypassLock = _plugin.IsHousingMenuOpen
      };

      SyncToTransform();
      _onSave?.Invoke();

      try 
      {
        var result = await _plugin.ServerClient.RegisterTvAsync(locationKey, placement);
        if (result != null) {
          _plugin.CurrentTvPlacement = placement;
          _statusMessage = "Successfully registered TV for all visitors!";
          _statusColor = new Vector4(0.3f, 1f, 0.3f, 1);
          _plugin.Chat.Print("[Media Player] " + _statusMessage);
        } else {
          _statusMessage = "Saved locally, but failed to reach the sync server.";
          _statusColor = new Vector4(1, 0.6f, 0.2f, 1);
          _plugin.Chat.PrintError("[Media Player] " + _statusMessage);
        }
      } 
      catch (UnauthorizedAccessException) 
      {
        _statusMessage = "Cannot move TV: It is locked by its owner.";
        _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
        _plugin.Chat.PrintError("[Media Player] " + _statusMessage);
      }
    }

  }
}
