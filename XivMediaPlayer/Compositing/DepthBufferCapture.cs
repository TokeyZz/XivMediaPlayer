using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace XivMediaPlayer.Compositing {
  /// <summary>
  /// Captures the game's depth buffer from the RenderTargetManager every frame.
  /// The RTM depth buffer retains valid scene data even during the ImGui pass,
  /// so a direct CopyResource from the game's depth texture works without hooking.
  /// </summary>
  internal unsafe class DepthBufferCapture : IDisposable {
    private ID3D11DeviceContext _context;
    private ID3D11Device _device;
    private ID3D11Texture2D _depthCopy;       // Our persistent copy of the depth buffer
    private ID3D11Texture2D _stagingTexture;   // CPU-readable copy for preview
    private ID3D11DepthStencilView _depthCopyDSV; // DSV created from our copy for rendering
    private ID3D11ShaderResourceView _depthCopySRV; // SRV for shader-based depth sampling
    private int _texWidth, _texHeight;
    private Format _texFormat;
    private int _sampleCount;
    private int _sampleQuality;
    private bool _disposed;
    private bool _initialized;
    private string _debugInfo = "Not initialized";
    private IntPtr _gameDepthTexturePtr; // The game's depth texture to copy from

    // Preview output
    private byte[] _lastRgbaData;
    private int _captureWidth, _captureHeight;

    // Per-frame depth array for occlusion queries
    private float[] _depthData;
    private bool _readDepthEnabled;

    /// <summary>
    /// Enable/disable per-frame CPU depth readback. Only enable when occlusion is active.
    /// </summary>
    public bool ReadDepthEnabled { get => _readDepthEnabled; set => _readDepthEnabled = value; }

    public string DebugInfo => _debugInfo;
    public byte[] LastRgbaData => _lastRgbaData;
    public float[] LastDepthData { get; private set; }
    public int CaptureWidth => _captureWidth;
    public int CaptureHeight => _captureHeight;
    public bool IsInitialized => _initialized;
    public int DepthWidth => _texWidth;
    public int DepthHeight => _texHeight;
    public float RenderWidth { get; private set; }
    public float RenderHeight { get; private set; }

    /// <summary>
    /// Returns the DSV of our captured depth copy, for use in depth-tested rendering.
    /// </summary>
    public ID3D11DepthStencilView CapturedDSV => _depthCopyDSV;

    /// <summary>
    /// Returns the SRV of our captured depth copy, for sampling in a pixel shader.
    /// </summary>
    public ID3D11ShaderResourceView CapturedSRV => _depthCopySRV;

    public bool Initialize() {
      if (_initialized || _disposed) return _initialized;

      try {
        var ffxivDevice = Device.Instance();
        if (ffxivDevice == null || ffxivDevice->D3D11DeviceContext == null) {
          _debugInfo = "FFXIV D3D11 device context not available.";
          return false;
        }

        var contextPtr = (IntPtr)ffxivDevice->D3D11DeviceContext;
        System.Runtime.InteropServices.Marshal.AddRef(contextPtr);
        _context = new ID3D11DeviceContext(contextPtr);
        _device = _context.Device;

        // We now fetch the DepthStencil pointer dynamically every frame in BeginFrame()
        // to prevent access violations when the user resizes the window and the
        // SwapChain/RTM destroys the old texture.
        _initialized = true;
        return true;
      } catch (Exception ex) {
        _debugInfo = $"Init failed: {ex.Message}";
        return false;
      }
    }

    /// <summary>
    /// Copy the depth texture to our persistent copy.
    /// </summary>
    private void CopyDepthBuffer(ID3D11Texture2D depthTexture) {
      var texDesc = depthTexture.Description;

      // Create or recreate our copy texture if needed
      if (_depthCopy == null || 
          _texWidth != (int)texDesc.Width || 
          _texHeight != (int)texDesc.Height ||
          _texFormat != texDesc.Format ||
          _sampleCount != texDesc.SampleDescription.Count ||
          _sampleQuality != texDesc.SampleDescription.Quality) {
          
        _depthCopy?.Dispose();
        _depthCopyDSV?.Dispose();
        _depthCopySRV?.Dispose();
        _stagingTexture?.Dispose();
        _depthCopyDSV = null;
        _depthCopySRV = null;
        _stagingTexture = null;

        _texWidth = (int)texDesc.Width;
        _texHeight = (int)texDesc.Height;
        _texFormat = texDesc.Format;
        _sampleCount = texDesc.SampleDescription.Count;
        _sampleQuality = texDesc.SampleDescription.Quality;

        // Create copy with both DepthStencil + ShaderResource bind flags
        _depthCopy = _device.CreateTexture2D(new Texture2DDescription {
          Width = texDesc.Width,
          Height = texDesc.Height,
          MipLevels = 1,
          ArraySize = 1,
          Format = texDesc.Format,
          SampleDescription = new SampleDescription(_sampleCount, _sampleQuality),
          Usage = ResourceUsage.Default,
          BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
          CPUAccessFlags = CpuAccessFlags.None,
        });

        // Create DSV view
        Format dsvFormat = texDesc.Format switch {
          Format.R24G8_Typeless => Format.D24_UNorm_S8_UInt,
          Format.R32_Typeless => Format.D32_Float,
          Format.R32G8X24_Typeless => Format.D32_Float_S8X24_UInt,
          _ => texDesc.Format,
        };
        _depthCopyDSV = _device.CreateDepthStencilView(_depthCopy, new DepthStencilViewDescription {
          Format = dsvFormat,
          ViewDimension = _sampleCount > 1 ? DepthStencilViewDimension.Texture2DMultisampled : DepthStencilViewDimension.Texture2D,
        });

        // Create SRV view for shader sampling
        Format srvFormat = texDesc.Format switch {
          Format.R24G8_Typeless => Format.R24_UNorm_X8_Typeless,
          Format.R32_Typeless => Format.R32_Float,
          Format.R32G8X24_Typeless => Format.R32_Float_X8X24_Typeless,
          _ => texDesc.Format,
        };
        _depthCopySRV = _device.CreateShaderResourceView(_depthCopy, new ShaderResourceViewDescription {
          Format = srvFormat,
          ViewDimension = _sampleCount > 1 ? Vortice.Direct3D.ShaderResourceViewDimension.Texture2DMultisampled : Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
          Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        });

        // Staging texture for CPU readback (preview)
        _stagingTexture = _device.CreateTexture2D(new Texture2DDescription {
          Width = texDesc.Width,
          Height = texDesc.Height,
          MipLevels = 1,
          ArraySize = 1,
          Format = texDesc.Format,
          SampleDescription = new SampleDescription(_sampleCount, _sampleQuality),
          Usage = ResourceUsage.Staging,
          BindFlags = BindFlags.None,
          CPUAccessFlags = CpuAccessFlags.Read,
        });
      }

      // Copy game's depth buffer to our copy
      _context.CopyResource(_depthCopy, depthTexture);
    }

    /// <summary>
    /// Call at the very start of OnDraw. Copies the game's depth buffer.
    /// </summary>
    public void BeginFrame() {
      if (!_initialized || _disposed) return;

      try {
        var rtm = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager.Instance();
        if (rtm != null && rtm->DepthStencil != null && rtm->DepthStencil->D3D11Texture2D != null) {
          _gameDepthTexturePtr = (IntPtr)rtm->DepthStencil->D3D11Texture2D;
          RenderWidth = rtm->Resolution_Width;
          RenderHeight = rtm->Resolution_Height;
        } else {
          var ffxivDevice = Device.Instance();
          if (ffxivDevice != null && ffxivDevice->SwapChain != null && ffxivDevice->SwapChain->DepthStencil != null && ffxivDevice->SwapChain->DepthStencil->D3D11Texture2D != null) {
            _gameDepthTexturePtr = (IntPtr)ffxivDevice->SwapChain->DepthStencil->D3D11Texture2D;
          } else {
            _gameDepthTexturePtr = IntPtr.Zero;
          }
        }

        if (_gameDepthTexturePtr == IntPtr.Zero) return;

        System.Runtime.InteropServices.Marshal.AddRef(_gameDepthTexturePtr);
        using var depthTexture = new ID3D11Texture2D(_gameDepthTexturePtr);
        CopyDepthBuffer(depthTexture);
        ReadDepthToArray();
      } catch (Exception e) {
        _debugInfo = $"BeginFrame Error: {e.Message}";
      }
    }

    /// <summary>
    /// Reads depth buffer from staging texture into a cached float array.
    /// Uses unsafe pointer access for performance (2M pixels per frame).
    /// </summary>
    private void ReadDepthToArray() {
      if (_depthCopy == null || _stagingTexture == null || _context == null) return;
      if (!_readDepthEnabled) return;

      try {
        _context.CopyResource(_stagingTexture, _depthCopy);
        var mapped = _context.Map(_stagingTexture, 0, MapMode.Read);
        try {
          if (_depthData == null || _depthData.Length != _texWidth * _texHeight) {
            _depthData = new float[_texWidth * _texHeight];
          }

          bool isD24 = _texFormat == Format.R24G8_Typeless || _texFormat == Format.D24_UNorm_S8_UInt;
          const float inv24 = 1.0f / 0x00FFFFFF;

          for (int y = 0; y < _texHeight; y++) {
            byte* rowPtr = (byte*)mapped.DataPointer + y * (int)mapped.RowPitch;
            uint* row32 = (uint*)rowPtr;
            int offset = y * _texWidth;

            if (isD24) {
              for (int x = 0; x < _texWidth; x++) {
                _depthData[offset + x] = (row32[x] & 0x00FFFFFF) * inv24;
              }
            } else {
              // D32_Float / R32_Float
              float* rowF = (float*)rowPtr;
              for (int x = 0; x < _texWidth; x++) {
                _depthData[offset + x] = rowF[x];
              }
            }
          }
        } finally {
          _context.Unmap(_stagingTexture, 0);
        }
      } catch (Exception e) {
        _debugInfo = $"ReadDepth Error: {e.Message}";
      }
    }

    /// <summary>
    /// Get the depth value at a screen coordinate. Returns 0 if out of bounds.
    /// </summary>
    public float GetDepthAt(int screenX, int screenY) {
      if (_depthData == null || screenX < 0 || screenY < 0 || screenX >= _texWidth || screenY >= _texHeight)
        return 0;
      return _depthData[screenY * _texWidth + screenX];
    }

    /// <summary>
    /// Quickly finds the min and max depth in the current depth buffer, ignoring the skybox.
    /// Used for auto-ranging the ambilight shader.
    /// </summary>
    public void GetMinMaxDepth(out float minDepth, out float maxDepth) {
        minDepth = 0.001f;
        maxDepth = 1.0f;
        
        if (_depthData == null) return;
        
        float min = 1.0f;
        float max = 0.001f;
        
        // Scan a grid of points instead of every pixel to save CPU
        int step = 16;
        for (int i = 0; i < _depthData.Length; i += step) {
            float d = _depthData[i];
            if (d > 0.0001f) {
                if (d < min) min = d;
                if (d > max) max = d;
            }
        }
        
        if (max >= min) {
            minDepth = min;
            maxDepth = max;
        }
    }

    /// <summary>
    /// Screen quad corners in screen space, for depth preview overlay.
    /// </summary>
    public (Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl)? ScreenQuadCorners { get; set; }

    /// <summary>
    /// Per-corner depth thresholds for the screen quad.
    /// </summary>
    public Vector4? ScreenQuadDepths { get; set; }

    /// <summary>
    /// Generate the preview image from the captured depth copy.
    /// </summary>
    public void GeneratePreview() {
      if (_disposed || _depthCopy == null || _stagingTexture == null) return;

      try {
        _context.CopyResource(_stagingTexture, _depthCopy);
        var mapped = _context.Map(_stagingTexture, 0, MapMode.Read);
        try {
          int captureW = Math.Min(480, _texWidth);
          int captureH = Math.Min(270, _texHeight);
          int stepX = _texWidth / captureW;
          int stepY = _texHeight / captureH;
          _captureWidth = captureW;
          _captureHeight = captureH;

          if (_lastRgbaData == null || _lastRgbaData.Length != captureW * captureH * 4) {
            _lastRgbaData = new byte[captureW * captureH * 4];
          }
          if (LastDepthData == null || LastDepthData.Length != captureW * captureH) {
            LastDepthData = new float[captureW * captureH];
          }

          float minDepth = float.MaxValue, maxDepth = float.MinValue;
          int nonZeroCount = 0;

          for (int y = 0; y < captureH; y++) {
            for (int x = 0; x < captureW; x++) {
              int srcX = x * stepX;
              int srcY = y * stepY;
              IntPtr rowPtr = mapped.DataPointer + srcY * (int)mapped.RowPitch;

              float depth = 0;
              switch (_texFormat) {
                case Format.R32_Typeless:
                case Format.D32_Float:
                case Format.R32_Float:
                  depth = Marshal.PtrToStructure<float>(rowPtr + srcX * 4);
                  break;
                case Format.R24G8_Typeless:
                case Format.D24_UNorm_S8_UInt: {
                    uint raw = (uint)Marshal.PtrToStructure<int>(rowPtr + srcX * 4);
                    depth = (raw & 0x00FFFFFF) / (float)0x00FFFFFF;
                    break;
                  }
                case Format.R32G8X24_Typeless:
                case Format.D32_Float_S8X24_UInt:
                  depth = Marshal.PtrToStructure<float>(rowPtr + srcX * 8);
                  break;
              }

              LastDepthData[y * captureW + x] = depth;
              if (depth > 0.0001f) nonZeroCount++;
              if (depth < minDepth) minDepth = depth;
              if (depth > maxDepth) maxDepth = depth;
            }
          }

          // Precompute screen quad overlay
          bool hasQuad = ScreenQuadCorners.HasValue && ScreenQuadDepths.HasValue;
          Vector2 qTL = default, qTR = default, qBR = default, qBL = default;
          Vector4 qDepths = default;
          if (hasQuad) {
            var corners = ScreenQuadCorners.Value;
            qDepths = ScreenQuadDepths.Value;
            float sx = (float)captureW / _texWidth;
            float sy = (float)captureH / _texHeight;
            qTL = corners.tl * new Vector2(sx, sy);
            qTR = corners.tr * new Vector2(sx, sy);
            qBR = corners.br * new Vector2(sx, sy);
            qBL = corners.bl * new Vector2(sx, sy);
          }

          float range = maxDepth - minDepth;
          if (range < 0.0001f) range = 1f;

          for (int i = 0; i < captureW * captureH; i++) {
            int px = i % captureW;
            int py = i / captureW;
            float normalized = (LastDepthData[i] - minDepth) / range;
            byte val = (byte)(Math.Clamp(1f - normalized, 0f, 1f) * 255f);
            int idx = i * 4;

            if (hasQuad) {
              var p = new Vector2(px, py);
              if (InverseBilerp(p, qTL, qTR, qBR, qBL, out float u, out float v)
                  && u >= 0 && u <= 1 && v >= 0 && v <= 1) {
                float dTop = qDepths.X + (qDepths.Y - qDepths.X) * u;
                float dBot = qDepths.W + (qDepths.Z - qDepths.W) * u;
                float threshold = dTop + (dBot - dTop) * v;

                bool isEdge = u < 0.02f || u > 0.98f || v < 0.02f || v > 0.98f;
                if (isEdge) {
                  _lastRgbaData[idx + 0] = 255;
                  _lastRgbaData[idx + 1] = 255;
                  _lastRgbaData[idx + 2] = 0;
                  _lastRgbaData[idx + 3] = 255;
                  continue;
                }

                bool occluded = LastDepthData[i] > threshold;
                if (occluded) {
                  _lastRgbaData[idx + 0] = (byte)Math.Min(255, val / 2 + 128);
                  _lastRgbaData[idx + 1] = (byte)(val / 3);
                  _lastRgbaData[idx + 2] = (byte)(val / 3);
                  _lastRgbaData[idx + 3] = 255;
                } else {
                  _lastRgbaData[idx + 0] = (byte)(val / 3);
                  _lastRgbaData[idx + 1] = (byte)Math.Min(255, val / 2 + 80);
                  _lastRgbaData[idx + 2] = (byte)(val / 3);
                  _lastRgbaData[idx + 3] = 255;
                }
                continue;
              }
            }

            _lastRgbaData[idx + 0] = val;
            _lastRgbaData[idx + 1] = val;
            _lastRgbaData[idx + 2] = val;
            _lastRgbaData[idx + 3] = 255;
          }

          _debugInfo = $"Depth: {_texWidth}x{_texHeight}, Format={_texFormat}, " +
                       $"depth=[{minDepth:F6}, {maxDepth:F6}], nonZero={nonZeroCount}/{captureW * captureH}";
        } finally {
          _context.Unmap(_stagingTexture, 0);
        }
      } catch (Exception ex) {
        _debugInfo = $"Preview error: {ex.Message}";
      }
    }

    private static bool InverseBilerp(Vector2 p, Vector2 a, Vector2 b, Vector2 c, Vector2 d,
      out float u, out float v) {
      u = v = -1;
      var e = b - a;
      var f = d - a;
      var g = a - b + c - d;
      var h = p - a;

      float k2 = Cross2D(g, f);
      float k1 = Cross2D(e, f) + Cross2D(h, g);
      float k0 = Cross2D(h, e);

      if (MathF.Abs(k2) < 0.0001f) {
        if (MathF.Abs(k1) < 0.0001f) return false;
        v = -k0 / k1;
      } else {
        float disc = k1 * k1 - 4f * k0 * k2;
        if (disc < 0) return false;
        disc = MathF.Sqrt(disc);
        float v0 = (-k1 - disc) / (2f * k2);
        float v1 = (-k1 + disc) / (2f * k2);
        v = (v0 >= -0.01f && v0 <= 1.01f) ? v0 : v1;
      }

      var denom = e + v * g;
      if (MathF.Abs(denom.X) > MathF.Abs(denom.Y))
        u = (h.X - v * f.X) / denom.X;
      else
        u = (h.Y - v * f.Y) / denom.Y;

      return true;
    }

    private static float Cross2D(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    public void Dispose() {
      if (_disposed) return;
      _disposed = true;

      _depthCopyDSV?.Dispose();
      _depthCopySRV?.Dispose();
      _depthCopy?.Dispose();
      _stagingTexture?.Dispose();

      _device?.Dispose();
      _context?.Dispose();
      _device = null;
      _context = null;
    }
  }
}
