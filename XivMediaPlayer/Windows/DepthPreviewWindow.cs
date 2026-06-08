using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using System;
using System.Numerics;
using XivMediaPlayer.Compositing;

namespace XivMediaPlayer.Windows {
  /// <summary>
  /// Debug window that shows the game's depth buffer as a grayscale image.
  /// White = near (depth 1.0 in reverse-Z), Black = far (depth 0.0).
  /// </summary>
  internal class DepthPreviewWindow : Window, IDisposable {
    private readonly ITextureProvider _textureProvider;
    private readonly IPluginLog _pluginLog;
    private IDalamudTextureWrap _previewTexture;
    private IDalamudTextureWrap _uiPreviewTexture;
    private int _lastDataHash;
    private int _lastUiDataHash;
    private bool _disposed;
    private string _rtmProbeResult;
    private string _sceneProbeResult;

    /// <summary>
    /// The shared depth capture instance. Initialized externally.
    /// </summary>
    public DepthBufferCapture Capture { get; set; }
    public UILayerCapture UICapture { get; set; }

    public DepthPreviewWindow(ITextureProvider textureProvider, IPluginLog pluginLog)
      : base("Depth Buffer Preview", ImGuiWindowFlags.None, false) {
      _textureProvider = textureProvider;
      _pluginLog = pluginLog;
      Size = new Vector2(640, 400);
      SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw() {
      if (_disposed) return;

      // Show capture debug info
      if (Capture != null) {
        Capture.GeneratePreview();
        ImGui.TextWrapped(Capture.DebugInfo);
      }

      ImGui.Separator();

      // RTM probe section
      if (ImGui.CollapsingHeader("Depth Texture Scan", ImGuiTreeNodeFlags.DefaultOpen)) {
        if (_rtmProbeResult == null) {
          _rtmProbeResult = DepthBufferProbe.ProbeAllDepthTextures();
        }
        if (ImGui.Button("Rescan")) {
          _rtmProbeResult = DepthBufferProbe.ProbeAllDepthTextures();
        }
        ImGui.TextWrapped(_rtmProbeResult);
      }

      // Scene color probe
      if (ImGui.CollapsingHeader("Scene Color RTs")) {
        if (_sceneProbeResult == null) {
          _sceneProbeResult = SceneColorProbe.ProbeAllColorTextures();
        }
        if (ImGui.Button("Rescan Color RTs")) {
          _sceneProbeResult = SceneColorProbe.ProbeAllColorTextures();
        }
        ImGui.TextWrapped(_sceneProbeResult);
      }

      ImGui.Separator();

      // Update the preview texture from captured data
      var data = Capture?.LastRgbaData;
      if (data != null && data.Length > 0) {
        try {
          int hash = data.Length;
          int step = Math.Max(4, data.Length / 64);
          for (int i = 0; i < data.Length; i += step) {
            hash = hash * 31 + data[i];
          }

          if (hash != _lastDataHash) {
            var oldTex = _previewTexture;
            _previewTexture = _textureProvider.CreateFromRaw(
              new RawImageSpecification(Capture.CaptureWidth, Capture.CaptureHeight, 28), data);
            _lastDataHash = hash;
            oldTex?.Dispose();
          }
        } catch (Exception e) {
          _pluginLog.Warning(e, "[Depth Preview] Failed to create preview texture.");
        }

        if (_previewTexture != null) {
          var avail = ImGui.GetContentRegionAvail();
          // We want the image to fit half the window height to leave room for the UI preview
          float drawH = avail.Y * 0.5f; 
          float aspect = (float)Capture.CaptureWidth / Capture.CaptureHeight;
          float drawW = drawH * aspect;
          if (drawW > avail.X) {
            drawW = avail.X;
            drawH = drawW / aspect;
          }
          ImGui.Image(_previewTexture.Handle, new Vector2(drawW, drawH));
        }
      } else {
        ImGui.TextColored(new Vector4(1, 1, 0, 1), "No depth data captured yet.");
      }

      // UI Capture debug section
      if (UICapture != null) {
        ImGui.Separator();
        UICapture.GeneratePreview();
        ImGui.TextColored(new Vector4(0, 1, 1, 1), $"UI Capture: {UICapture.DebugInfo}");
        
        var uiData = UICapture.LastAlphaData;
        if (uiData != null && uiData.Length > 0) {
          try {
            int hash = uiData.Length;
            int step = Math.Max(4, uiData.Length / 64);
            for (int i = 0; i < uiData.Length; i += step) {
              hash = hash * 31 + uiData[i];
            }

            if (hash != _lastUiDataHash) {
              var oldTex = _uiPreviewTexture;
              _uiPreviewTexture = _textureProvider.CreateFromRaw(
                new RawImageSpecification(UICapture.CaptureWidth, UICapture.CaptureHeight, 28), uiData);
              _lastUiDataHash = hash;
              oldTex?.Dispose();
            }
          } catch (Exception e) {
            _pluginLog.Warning(e, "[Depth Preview] Failed to create UI preview texture.");
          }

          if (_uiPreviewTexture != null) {
            var avail = ImGui.GetContentRegionAvail();
            float drawH = avail.Y; 
            float aspect = (float)UICapture.CaptureWidth / UICapture.CaptureHeight;
            float drawW = drawH * aspect;
            if (drawW > avail.X) {
              drawW = avail.X;
              drawH = drawW / aspect;
            }
            ImGui.Image(_uiPreviewTexture.Handle, new Vector2(drawW, drawH));
          }
        }

        ImGui.Text($"Addon rects: {UICapture.LastAddonRects.Count}");
        if (ImGui.CollapsingHeader("Detected Addons")) {
          for (int i = 0; i < UICapture.LastAddonRects.Count; i++) {
            var r = UICapture.LastAddonRects[i];
            ImGui.Text($"  [{i}] {r.Name}: ({r.X},{r.Y}) {r.W}x{r.H}");
          }
        }
      }
    }

    public void Dispose() {
      if (_disposed) return;
      _disposed = true;
      _previewTexture?.Dispose();
      _uiPreviewTexture?.Dispose();
    }
  }
}
