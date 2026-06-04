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
    private int _texWidth, _texHeight;
    private Format _texFormat;
    private bool _disposed;
    private bool _initialized;
    private string _debugInfo = "Not initialized";
    private IntPtr _gameDepthTexturePtr; // The game's depth texture to copy from

    // Preview output
    private byte[] _lastRgbaData;
    private int _captureWidth, _captureHeight;

    public string DebugInfo => _debugInfo;
    public byte[] LastRgbaData => _lastRgbaData;
    public int CaptureWidth => _captureWidth;
    public int CaptureHeight => _captureHeight;
    public bool IsInitialized => _initialized;

    /// <summary>
    /// Returns the DSV of our captured depth copy, for use in depth-tested rendering.
    /// Returns null if no capture has been made.
    /// </summary>
    public ID3D11DepthStencilView CapturedDSV => _depthCopyDSV;

    public bool Initialize() {
      if (_initialized || _disposed) return _initialized;

      try {
        var ffxivDevice = Device.Instance();
        if (ffxivDevice == null || ffxivDevice->D3D11DeviceContext == null) {
          _debugInfo = "FFXIV D3D11 device context not available.";
          return false;
        }

        var contextPtr = (IntPtr)ffxivDevice->D3D11DeviceContext;
        _context = new ID3D11DeviceContext(contextPtr);
        _device = _context.Device;

        // Find the game's depth texture pointer
        // Use RenderTargetManager.DepthStencil — this is the scene depth buffer
        // and retains valid data even during the ImGui/Present pass.
        var rtm = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager.Instance();
        if (rtm != null && rtm->DepthStencil != null && rtm->DepthStencil->D3D11Texture2D != null) {
          _gameDepthTexturePtr = (IntPtr)rtm->DepthStencil->D3D11Texture2D;
          _debugInfo = $"Using RTM DepthStencil: {_gameDepthTexturePtr:X}";
        } else {
          // Fallback to SwapChain depth (may not have scene data)
          var swapChain = ffxivDevice->SwapChain;
          if (swapChain != null && swapChain->DepthStencil != null && swapChain->DepthStencil->D3D11Texture2D != null) {
            _gameDepthTexturePtr = (IntPtr)swapChain->DepthStencil->D3D11Texture2D;
            _debugInfo = $"Using SwapChain DepthStencil (fallback): {_gameDepthTexturePtr:X}";
          }
        }

        if (_gameDepthTexturePtr == IntPtr.Zero) {
          _debugInfo = "No depth texture found.";
          return false;
        }

        // Do initial depth copy to set up textures
        var depthTexture = new ID3D11Texture2D(_gameDepthTexturePtr);
        CopyDepthBuffer(depthTexture);

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
      if (_depthCopy == null || _texWidth != (int)texDesc.Width || _texHeight != (int)texDesc.Height) {
        _depthCopy?.Dispose();
        _depthCopyDSV?.Dispose();
        _stagingTexture?.Dispose();
        _depthCopyDSV = null;
        _stagingTexture = null;

        _texWidth = (int)texDesc.Width;
        _texHeight = (int)texDesc.Height;
        _texFormat = texDesc.Format;

        // Create a non-MSAA copy with DepthStencil bind flag so we can create a DSV
        _depthCopy = _device.CreateTexture2D(new Texture2DDescription {
          Width = texDesc.Width,
          Height = texDesc.Height,
          MipLevels = 1,
          ArraySize = 1,
          Format = texDesc.Format,
          SampleDescription = new SampleDescription(1, 0),
          Usage = ResourceUsage.Default,
          BindFlags = BindFlags.DepthStencil,
          CPUAccessFlags = CpuAccessFlags.None,
        });

        // Create DSV from our copy for rendering
        Format dsvFormat = texDesc.Format switch {
          Format.R24G8_Typeless => Format.D24_UNorm_S8_UInt,
          Format.R32_Typeless => Format.D32_Float,
          Format.R32G8X24_Typeless => Format.D32_Float_S8X24_UInt,
          _ => texDesc.Format,
        };
        _depthCopyDSV = _device.CreateDepthStencilView(_depthCopy, new DepthStencilViewDescription {
          Format = dsvFormat,
          ViewDimension = DepthStencilViewDimension.Texture2D,
        });

        // Staging texture for CPU readback (preview)
        _stagingTexture = _device.CreateTexture2D(new Texture2DDescription {
          Width = texDesc.Width,
          Height = texDesc.Height,
          MipLevels = 1,
          ArraySize = 1,
          Format = texDesc.Format,
          SampleDescription = new SampleDescription(1, 0),
          Usage = ResourceUsage.Staging,
          BindFlags = BindFlags.None,
          CPUAccessFlags = CpuAccessFlags.Read,
        });
      }

      // Copy game's depth buffer to our copy
      _context.CopyResource(_depthCopy, depthTexture);
    }

    /// <summary>
    /// Call each frame to copy the latest depth data from the game.
    /// </summary>
    public void BeginFrame() {
      if (_disposed || _gameDepthTexturePtr == IntPtr.Zero) return;

      try {
        var depthTexture = new ID3D11Texture2D(_gameDepthTexturePtr);
        CopyDepthBuffer(depthTexture);
      } catch {
        // ignore — texture may become invalid during shutdown
      }
    }

    /// <summary>
    /// Generate the preview image from the captured depth copy.
    /// Call this during ImGui draw when the preview window is open.
    /// </summary>
    public void GeneratePreview() {
      if (_disposed || _depthCopy == null || _stagingTexture == null) return;

      try {
        // Copy depth to staging for CPU read
        _context.CopyResource(_stagingTexture, _depthCopy);
        var mapped = _context.Map(_stagingTexture, 0, MapMode.Read);
        try {
          // Downscale to reasonable preview size
          int captureW = Math.Min(480, _texWidth);
          int captureH = Math.Min(270, _texHeight);
          int stepX = _texWidth / captureW;
          int stepY = _texHeight / captureH;
          _captureWidth = captureW;
          _captureHeight = captureH;

          if (_lastRgbaData == null || _lastRgbaData.Length != captureW * captureH * 4) {
            _lastRgbaData = new byte[captureW * captureH * 4];
          }

          float minDepth = float.MaxValue, maxDepth = float.MinValue;
          int nonZeroCount = 0;
          
          // Temp buffer for raw depth values
          var depthValues = new float[captureW * captureH];

          // First pass: read depth values and find range
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

              depthValues[y * captureW + x] = depth;
              if (depth > 0.0001f) nonZeroCount++;
              if (depth < minDepth) minDepth = depth;
              if (depth > maxDepth) maxDepth = depth;
            }
          }

          // Second pass: map depth to grayscale with auto-contrast
          // Closer objects = brighter (inverted)
          float range = maxDepth - minDepth;
          if (range < 0.0001f) range = 1f;

          for (int i = 0; i < captureW * captureH; i++) {
            float normalized = (depthValues[i] - minDepth) / range;
            byte val = (byte)(Math.Clamp(1f - normalized, 0f, 1f) * 255f);
            int idx = i * 4;
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

    public void Dispose() {
      if (_disposed) return;
      _disposed = true;

      _depthCopyDSV?.Dispose();
      _depthCopy?.Dispose();
      _stagingTexture?.Dispose();

      _device = null;
      _context = null;
    }
  }
}
