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
    private int _lastDataHash;
    private bool _disposed;
    private string _rtmProbeResult;

    /// <summary>
    /// The shared depth capture instance. Initialized externally.
    /// </summary>
    public DepthBufferCapture Capture { get; set; }

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

      ImGui.Separator();

      // Update the preview texture from captured data
      var data = Capture?.LastRgbaData;
      if (data != null && data.Length > 0) {
        try {
          int hash = data.Length;
          for (int i = 0; i < Math.Min(64, data.Length); i += 4) {
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
          float aspect = (float)Capture.CaptureWidth / Capture.CaptureHeight;
          float drawW = avail.X;
          float drawH = drawW / aspect;
          if (drawH > avail.Y) {
            drawH = avail.Y;
            drawW = drawH * aspect;
          }
          ImGui.Image(_previewTexture.Handle, new Vector2(drawW, drawH));
        }
      } else {
        ImGui.TextColored(new Vector4(1, 1, 0, 1), "No depth data captured yet.");
      }
    }

    public void Dispose() {
      if (_disposed) return;
      _disposed = true;
      _previewTexture?.Dispose();
    }
  }
}
