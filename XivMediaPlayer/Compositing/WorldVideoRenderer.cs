using Dalamud.Interface.Textures.TextureWraps;
using MediaPlayerCore.Compositing;
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace XivMediaPlayer.Compositing {
  /// <summary>
  /// Renders the VLC video frame as a textured quad in 3D world space.
  /// Supports two render modes:
  ///  1. ImGui mode (default): projects quad corners to screen space via WorldToScreen,
  ///   draws with ImGui.AddImageQuad. No occlusion.
  ///  2. D3D11 depth-tested mode: renders as native D3D11 geometry using the game's
  ///   depth buffer for per-pixel occlusion by walls, characters, etc.
  /// </summary>
  internal class WorldVideoRenderer : IDisposable {
    private readonly WorldScreenTransform _transform;
    private readonly IGameGui? _gameGui;
    private DepthTestedRenderer? _depthRenderer;
    private bool _disposed;
    private bool _useDepthOcclusion;

    public WorldScreenTransform Transform => _transform;

    /// <summary>
    /// Whether world rendering is currently active.
    /// </summary>
    public bool IsActive {
      get => _transform.Enabled;
      set => _transform.Enabled = value;
    }

    /// <summary>
    /// When true, renders via D3D11 with depth testing for occlusion.
    /// When false, renders via ImGui overlay (no occlusion).
    /// </summary>
    public bool UseDepthOcclusion {
      get => _useDepthOcclusion;
      set {
        _useDepthOcclusion = value;
        if (value && _depthRenderer == null) {
          _depthRenderer = new DepthTestedRenderer();
        }
      }
    }

    /// <summary>
    /// Returns initialization error from the depth renderer, if any.
    /// </summary>
    public string? DepthRendererError => _depthRenderer?.InitError;

    public WorldVideoRenderer(WorldScreenTransform transform, IGameGui? gameGui = null) {
      _transform = transform ?? new WorldScreenTransform();
      _gameGui = gameGui;
    }

    /// <summary>
    /// Renders the video texture as a 3D quad in world space.
    /// </summary>
    /// <param name="textureWrap">The texture wrap from ITextureProvider</param>
    /// <param name="viewProjection">Optional VP matrix for depth-tested rendering</param>
    public void Render(IDalamudTextureWrap textureWrap, Matrix4x4? viewProjection = null) {
      if (_disposed || !IsActive || textureWrap == null) return;

      // Try depth-tested D3D11 rendering first
      if (_useDepthOcclusion && viewProjection.HasValue) {
        if (RenderDepthTested(textureWrap, viewProjection.Value)) {
          return; // Success — skip ImGui fallback
        }
      }

      // Fallback: ImGui screen-space rendering (no occlusion)
      RenderScreenSpace(textureWrap);
    }

    /// <summary>
    /// Renders using D3D11 with depth testing. Returns true on success.
    /// </summary>
    private bool RenderDepthTested(IDalamudTextureWrap textureWrap, Matrix4x4 viewProjection) {
      if (_depthRenderer == null) {
        _depthRenderer = new DepthTestedRenderer();
      }

      if (!_depthRenderer.IsInitialized) {
        if (!_depthRenderer.Initialize()) {
          return false; // Init failed — fall back to ImGui
        }
      }

      var corners = _transform.Corners;
      // Extract the native D3D11 SRV pointer from the ImTextureID struct
      var handle = textureWrap.Handle;
      var srvPtr = System.Runtime.CompilerServices.Unsafe.As<Dalamud.Bindings.ImGui.ImTextureID, IntPtr>(ref handle);
      _depthRenderer.Render(corners, srvPtr, viewProjection);
      return true;
    }

    /// <summary>
    /// Renders using ImGui screen-space projection (no occlusion).
    /// </summary>
    private void RenderScreenSpace(IDalamudTextureWrap textureWrap) {
      var (tl, tr, br, bl) = _transform.Corners;

      if (!WorldToScreen(tl, out var sTL) ||
        !WorldToScreen(tr, out var sTR) ||
        !WorldToScreen(br, out var sBR) ||
        !WorldToScreen(bl, out var sBL)) {
        return;
      }

      var drawList = ImGui.GetBackgroundDrawList();
      drawList.AddImageQuad(
        textureWrap.Handle,
        sTL, sTR, sBR, sBL,
        new Vector2(0, 0), new Vector2(1, 0),
        new Vector2(1, 1), new Vector2(0, 1),
        0xFFFFFFFF);
    }

    private bool WorldToScreen(Vector3 worldPos, out Vector2 screenPos) {
      if (_gameGui != null) {
        return _gameGui.WorldToScreen(worldPos, out screenPos);
      }
      screenPos = Vector2.Zero;
      return false;
    }

    public void PlaceAt(Vector3 position, Vector3 lookAt) {
      _transform.PlaceLookingAt(position, lookAt);
      _transform.Enabled = true;
    }

    public void MoveBy(Vector3 offset) {
      _transform.Position += offset;
    }

    public void SetRotation(float yaw, float pitch, float roll = 0) {
      _transform.RotationDegrees = new Vector3(pitch, yaw, roll);
    }

    public void SetScale(float width, float height) {
      _transform.Scale = new Vector2(width, height);
    }

    public void Reset() {
      _transform.Enabled = false;
    }

    public void Dispose() {
      _disposed = true;
      _depthRenderer?.Dispose();
    }
  }
}
