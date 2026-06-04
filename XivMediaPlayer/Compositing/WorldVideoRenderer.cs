using Dalamud.Interface.Textures.TextureWraps;
using MediaPlayerCore.Compositing;
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using System.Runtime.CompilerServices;

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
    private GlowRenderer? _glowRenderer;
    private bool _disposed;
    private bool _useDepthOcclusion;
    private bool _enableGlow = true;

    /// <summary>
    /// Whether to render a backlit glow effect around the screen.
    /// </summary>
    public bool EnableGlow { get => _enableGlow; set => _enableGlow = value; }

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
    public void Render(IDalamudTextureWrap textureWrap,
      DepthBufferCapture depthCapture = null,
      Vector3? cameraPos = null,
      float nearPlane = 0.1f, float farPlane = 10000f) {
      if (_disposed || !IsActive || textureWrap == null) return;

      if (_useDepthOcclusion && depthCapture != null && cameraPos.HasValue) {
        RenderWithOcclusion(textureWrap, depthCapture, cameraPos.Value, nearPlane, farPlane);
      } else {
        RenderScreenSpace(textureWrap);
      }
    }

    /// <summary>
    /// Renders as a tessellated ImGui grid with per-vertex alpha from depth buffer.
    /// </summary>
    private void RenderWithOcclusion(IDalamudTextureWrap textureWrap, DepthBufferCapture depthCapture,
      Vector3 cameraPos, float nearPlane, float farPlane) {
      var (tl, tr, br, bl) = _transform.Corners;

      // Project all corners to screen space (never cull)
      WorldToScreenClamped(tl, out var sTL, out _);
      WorldToScreenClamped(tr, out var sTR, out _);
      WorldToScreenClamped(br, out var sBR, out _);
      WorldToScreenClamped(bl, out var sBL, out _);

      // Compute the quad's actual depth from camera distance
      var quadCenter = (tl + tr + br + bl) * 0.25f;
      float distance = Vector3.Distance(cameraPos, quadCenter);

      // Reverse-Z depth formula: near maps to 1.0, far maps to 0.0
      // depth = near * (far - distance) / (distance * (far - near))
      float quadDepth = nearPlane * (farPlane - distance) / (distance * (farPlane - nearPlane));
      quadDepth = Math.Clamp(quadDepth, 0f, 1f);

      // Threshold: anything with higher depth (closer in reverse-Z) occludes the quad
      float threshold = quadDepth;

      var drawList = ImGui.GetBackgroundDrawList();

      // Draw backlit glow layers behind the video
      if (_enableGlow) {
        // Compute screen visibility for glow attenuation
        float visibility = ComputeVisibility(depthCapture, sTL, sTR, sBR, sBL, threshold);
        RenderGlow(drawList, textureWrap, sTL, sTR, sBR, sBL, visibility);
      }

      const int gridSize = 512;

      // For each grid cell, draw a textured quad with depth-based alpha
      for (int gy = 0; gy < gridSize; gy++) {
        for (int gx = 0; gx < gridSize; gx++) {
          float u0 = gx / (float)gridSize;
          float v0 = gy / (float)gridSize;
          float u1 = (gx + 1) / (float)gridSize;
          float v1 = (gy + 1) / (float)gridSize;

          // Bilinear interpolation from quad corners to get screen positions
          var p00 = Bilerp(sTL, sTR, sBL, sBR, u0, v0);
          var p10 = Bilerp(sTL, sTR, sBL, sBR, u1, v0);
          var p01 = Bilerp(sTL, sTR, sBL, sBR, u0, v1);
          var p11 = Bilerp(sTL, sTR, sBL, sBR, u1, v1);

          // Sample depth at cell center
          var center = Bilerp(sTL, sTR, sBL, sBR, (u0 + u1) * 0.5f, (v0 + v1) * 0.5f);
          uint color = DepthToColor(depthCapture, center, threshold);

          drawList.AddImageQuad(
            textureWrap.Handle,
            p00, p10, p11, p01,
            new Vector2(u0, v0), new Vector2(u1, v0),
            new Vector2(u1, v1), new Vector2(u0, v1),
            color);
        }
      }
    }

    private static Vector2 Bilerp(Vector2 tl, Vector2 tr, Vector2 bl, Vector2 br, float u, float v) {
      var top = Vector2.Lerp(tl, tr, u);
      var bot = Vector2.Lerp(bl, br, u);
      return Vector2.Lerp(top, bot, v);
    }

    private static uint DepthToColor(DepthBufferCapture depthCapture, Vector2 screenPos, float threshold) {
      float depth = depthCapture.GetDepthAt((int)screenPos.X, (int)screenPos.Y);
      // In reverse-Z: higher depth = closer. If game geometry is closer (depth > threshold),
      // the quad should be occluded (alpha = 0).
      byte alpha = depth > threshold ? (byte)0 : (byte)255;
      return (uint)(alpha << 24) | 0x00FFFFFF; // ABGR format
    }

    /// <summary>
    /// Multi-sample depth within a cell area for anti-aliased occlusion edges.
    /// Returns an alpha-modulated white color.
    /// </summary>
    private static uint DepthToColorAA(DepthBufferCapture depthCapture,
      Vector2 sTL, Vector2 sTR, Vector2 sBL, Vector2 sBR,
      float u0, float v0, float u1, float v1, float threshold) {
      const int subSamples = 3; // 3x3 = 9 samples per cell
      int passing = 0;
      int total = subSamples * subSamples;

      for (int sy = 0; sy < subSamples; sy++) {
        for (int sx = 0; sx < subSamples; sx++) {
          float su = u0 + (u1 - u0) * (sx + 0.5f) / subSamples;
          float sv = v0 + (v1 - v0) * (sy + 0.5f) / subSamples;
          var samplePos = Bilerp(sTL, sTR, sBL, sBR, su, sv);
          float depth = depthCapture.GetDepthAt((int)samplePos.X, (int)samplePos.Y);
          if (depth <= threshold) passing++;
        }
      }

      byte alpha = (byte)(passing * 255 / total);
      return (uint)(alpha << 24) | 0x00FFFFFF;
    }

    /// <summary>
    /// Renders using ImGui screen-space projection (no occlusion).
    /// </summary>
    private void RenderScreenSpace(IDalamudTextureWrap textureWrap) {
      var (tl, tr, br, bl) = _transform.Corners;

      WorldToScreenClamped(tl, out var sTL, out _);
      WorldToScreenClamped(tr, out var sTR, out _);
      WorldToScreenClamped(br, out var sBR, out _);
      WorldToScreenClamped(bl, out var sBL, out _);

      var drawList = ImGui.GetBackgroundDrawList();

      // Draw backlit glow layers behind the video
      if (_enableGlow) {
        RenderGlow(drawList, textureWrap, sTL, sTR, sBR, sBL, 1f); // no occlusion = full glow
      }

      drawList.AddImageQuad(
        textureWrap.Handle,
        sTL, sTR, sBR, sBL,
        new Vector2(0, 0), new Vector2(1, 0),
        new Vector2(1, 1), new Vector2(0, 1),
        0xFFFFFFFF);
    }

    /// <summary>
    /// Draws soft glow layers behind the video quad to simulate screen illumination.
    /// Uses the video texture itself so the glow color naturally matches the content.
    /// </summary>
    private void RenderGlow(ImDrawListPtr drawList, IDalamudTextureWrap textureWrap,
      Vector2 sTL, Vector2 sTR, Vector2 sBR, Vector2 sBL, float visibility) {
      if (visibility <= 0.01f) return; // fully occluded — no glow

      // Lazy-init the GPU glow renderer
      if (_glowRenderer == null) {
        _glowRenderer = new GlowRenderer();
        _glowRenderer.Initialize(64); // 64x64 downsample with shader vignette
      }
      if (!_glowRenderer.IsInitialized) return;

      // Update the glow texture (GPU downsample + vignette in one shader pass)
      var texId = textureWrap.Handle;
      var texPtr = Unsafe.As<ImTextureID, IntPtr>(ref texId);
      if (!_glowRenderer.UpdateFromVideoTexture(texPtr)) return;
      var glowPtr = _glowRenderer.GlowTextureHandle;
      if (glowPtr == IntPtr.Zero) return;
      var glowId = Unsafe.As<IntPtr, ImTextureID>(ref glowPtr);

      // Single draw call — vignette is baked into the texture so edges fade naturally
      var center = (sTL + sTR + sBR + sBL) * 0.25f;
      float scale = 1.45f;
      var gTL = center + (sTL - center) * scale;
      var gTR = center + (sTR - center) * scale;
      var gBR = center + (sBR - center) * scale;
      var gBL = center + (sBL - center) * scale;

      // Soften visibility: glow ranges from 50% (fully occluded) to 100% (fully visible)
      // This simulates light bleeding around/through occluding objects
      float glowStrength = 0.5f + visibility * 0.5f;
      byte alpha = (byte)(144 * glowStrength); // base ~56% scaled by softened visibility
      uint color = (uint)(alpha << 24) | 0x00FFFFFF;

      drawList.AddImageQuad(
        glowId,
        gTL, gTR, gBR, gBL,
        new Vector2(0, 0), new Vector2(1, 0),
        new Vector2(1, 1), new Vector2(0, 1),
        color);
    }

    /// <summary>
    /// Samples a sparse grid across the screen quad area to determine
    /// what fraction of the screen is visible (not occluded by depth).
    /// </summary>
    private float ComputeVisibility(DepthBufferCapture depthCapture,
      Vector2 sTL, Vector2 sTR, Vector2 sBR, Vector2 sBL, float threshold) {
      const int sampleGrid = 8; // 8x8 = 64 samples (cheap)
      int passing = 0;
      int total = 0;

      for (int sy = 0; sy < sampleGrid; sy++) {
        for (int sx = 0; sx < sampleGrid; sx++) {
          float u = (sx + 0.5f) / sampleGrid;
          float v = (sy + 0.5f) / sampleGrid;
          var pos = Bilerp(sTL, sTR, sBL, sBR, u, v);

          float depth = depthCapture.GetDepthAt((int)pos.X, (int)pos.Y);
          if (depth <= threshold) passing++; // not occluded
          total++;
        }
      }

      return total > 0 ? passing / (float)total : 1f;
    }

    private bool WorldToScreen(Vector3 worldPos, out Vector2 screenPos) {
      if (_gameGui != null) {
        return _gameGui.WorldToScreen(worldPos, out screenPos);
      }
      screenPos = Vector2.Zero;
      return false;
    }

    /// <summary>
    /// Projects a world position to screen with coordinate clamping to prevent
    /// extreme values from causing ImGui rendering artifacts.
    /// </summary>
    private bool WorldToScreenClamped(Vector3 worldPos, out Vector2 screenPos, out bool isBehind) {
      screenPos = Vector2.Zero;
      isBehind = false;

      if (_gameGui == null) return false;

      bool onScreen = _gameGui.WorldToScreen(worldPos, out screenPos);
      isBehind = !onScreen;

      // Clamp to prevent extreme coordinates
      var viewport = ImGui.GetMainViewport();
      float maxRange = MathF.Max(viewport.Size.X, viewport.Size.Y) * 2f;
      screenPos.X = Math.Clamp(screenPos.X, -maxRange, maxRange);
      screenPos.Y = Math.Clamp(screenPos.Y, -maxRange, maxRange);

      return true;
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
      _glowRenderer?.Dispose();
    }
  }
}
