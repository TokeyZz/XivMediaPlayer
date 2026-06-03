using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MediaPlayerCore.Compositing;
using XivMediaPlayer.Compositing;
using System;
using System.Numerics;

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

    private Vector3 _position;
    private Vector2 _rotation; // yaw, pitch
    private Vector2 _scale;
    private bool _enabled;

    // Drag state for world-space interaction
    private bool _isDragging;
    private Vector2 _dragStartMouse;
    private Vector3 _dragStartPosition;

    public ScreenSettingsWindow(
        WorldScreenTransform transform,
        WorldVideoRenderer renderer,
        Action onSave,
        Action onPlaceAtCamera) :
      base("Screen Placement###ScreenPlacement",
        ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize,
        false) {
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
      // Enable toggle 
      if (ImGui.Checkbox("Render in World", ref _enabled)) {
        _transform.Enabled = _enabled;
      }

      if (!_enabled) {
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f),
          "Enable to place the video in the game world.");
        return;
      }

      ImGui.Separator();

      // Quick actions 
      if (ImGui.Button("Place at Camera")) {
        _onPlaceAtCamera?.Invoke();
        SyncFromTransform();
      }
      ImGui.SameLine();
      if (ImGui.Button("Save")) {
        SyncToTransform();
        _onSave?.Invoke();
      }
      ImGui.SameLine();
      if (ImGui.Button("Reset")) {
        _transform.Enabled = false;
        _enabled = false;
        SyncFromTransform();
      }

      ImGui.Spacing();
      ImGui.Separator();

      // Position 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "Position");

      bool posChanged = false;
      posChanged |= ImGui.DragFloat("X##pos", ref _position.X, 0.05f, -1000f, 1000f, "%.2f");
      posChanged |= ImGui.DragFloat("Y##pos", ref _position.Y, 0.05f, -1000f, 1000f, "%.2f");
      posChanged |= ImGui.DragFloat("Z##pos", ref _position.Z, 0.05f, -1000f, 1000f, "%.2f");
      if (posChanged) {
        _transform.Position = _position;
      }

      // Nudge buttons
      float nudge = 0.25f;
      if (ImGui.Button("\u2190##posX")) { _position.X -= nudge; _transform.Position = _position; }
      ImGui.SameLine();
      if (ImGui.Button("\u2192##posX")) { _position.X += nudge; _transform.Position = _position; }
      ImGui.SameLine();
      if (ImGui.Button("\u2193##posY")) { _position.Y -= nudge; _transform.Position = _position; }
      ImGui.SameLine();
      if (ImGui.Button("\u2191##posY")) { _position.Y += nudge; _transform.Position = _position; }
      ImGui.SameLine();
      if (ImGui.Button("Near##posZ")) { _position.Z -= nudge; _transform.Position = _position; }
      ImGui.SameLine();
      if (ImGui.Button("Far##posZ")) { _position.Z += nudge; _transform.Position = _position; }

      ImGui.Spacing();
      ImGui.Separator();

      // Rotation 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "Rotation");

      bool rotChanged = false;
      rotChanged |= ImGui.SliderFloat("Yaw##rot", ref _rotation.X, -180f, 180f, "%.1f\u00b0");
      rotChanged |= ImGui.SliderFloat("Pitch##rot", ref _rotation.Y, -90f, 90f, "%.1f\u00b0");
      if (rotChanged) {
        _transform.RotationDegrees = new Vector3(_rotation.Y, _rotation.X, 0);
      }

      // Quick rotation presets
      if (ImGui.Button("Face North")) { _rotation.X = 0; _transform.RotationDegrees = new Vector3(_rotation.Y, 0, 0); }
      ImGui.SameLine();
      if (ImGui.Button("Face East")) { _rotation.X = 90; _transform.RotationDegrees = new Vector3(_rotation.Y, 90, 0); }
      ImGui.SameLine();
      if (ImGui.Button("Face South")) { _rotation.X = 180; _transform.RotationDegrees = new Vector3(_rotation.Y, 180, 0); }
      ImGui.SameLine();
      if (ImGui.Button("Face West")) { _rotation.X = -90; _transform.RotationDegrees = new Vector3(_rotation.Y, -90, 0); }

      ImGui.Spacing();
      ImGui.Separator();

      // Scale 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "Size (world units)");

      bool scaleChanged = false;
      scaleChanged |= ImGui.DragFloat("Width##scale", ref _scale.X, 0.1f, 0.5f, 50f, "%.1f");
      scaleChanged |= ImGui.DragFloat("Height##scale", ref _scale.Y, 0.1f, 0.3f, 30f, "%.1f");
      if (scaleChanged) {
        _transform.Scale = _scale;
      }

      // Preset sizes
      if (ImGui.Button("Small (2m)")) { _scale = new Vector2(2f, 1.125f); _transform.Scale = _scale; }
      ImGui.SameLine();
      if (ImGui.Button("Medium (4m)")) { _scale = new Vector2(4f, 2.25f); _transform.Scale = _scale; }
      ImGui.SameLine();
      if (ImGui.Button("Large (8m)")) { _scale = new Vector2(8f, 4.5f); _transform.Scale = _scale; }
      ImGui.SameLine();
      if (ImGui.Button("Cinema (12m)")) { _scale = new Vector2(12f, 6.75f); _transform.Scale = _scale; }

      ImGui.Spacing();
      ImGui.Separator();

      // Depth Occlusion 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "Rendering");

      bool depthOcclusion = _renderer?.UseDepthOcclusion ?? false;
      if (ImGui.Checkbox("Enable Depth Occlusion", ref depthOcclusion)) {
        if (_renderer != null) _renderer.UseDepthOcclusion = depthOcclusion;
      }
      if (ImGui.IsItemHovered()) {
        ImGui.SetTooltip("When enabled, game geometry (walls, characters)\n" +
          "will appear in front of the video screen.\n" +
          "Uses the game's depth buffer for per-pixel occlusion.");
      }

      if (depthOcclusion && _renderer?.DepthRendererError != null) {
        ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f),
          _renderer.DepthRendererError);
      }

      ImGui.Spacing();
      ImGui.Separator();

      // Info 
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        $"Screen: {_scale.X:F1}m x {_scale.Y:F1}m at ({_position.X:F1}, {_position.Y:F1}, {_position.Z:F1})");
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        $"Mode: {(depthOcclusion ? "D3D11 Depth-Tested" : "ImGui Overlay")}");
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
          _isDragging = false;
        }
      }

      return false;
    }
  }
}
