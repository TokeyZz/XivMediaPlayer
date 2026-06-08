using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace XivMediaPlayer.Compositing {
  /// <summary>
  /// Captures the game's back buffer (which contains Scene + UI) at the start
  /// of each ImGui frame. After the video is blitted to the back buffer,
  /// RestoreUIRegions() copies the original UI addon rectangles back on top.
  /// </summary>
  internal unsafe class UILayerCapture : IDisposable {
    private ID3D11DeviceContext _context;
    private ID3D11Device _device;
    private ID3D11Texture2D _backBufferCopy;
    private ID3D11ShaderResourceView _backBufferSRV;
    private ID3D11Texture2D _sceneDiffuseCopy;
    private ID3D11ShaderResourceView _sceneDiffuseSRV;
    private ID3D11Texture2D _stagingTexture;
    private ID3D11Texture2D _previewStagingTexture;
    private int _width, _height;
    private bool _disposed;
    private bool _initialized;
    private bool _frameCaptured;
    private ulong _lastPreDrawFrame;
    private Dalamud.Plugin.Services.IAddonLifecycle _addonLifecycle;
    private string _debugInfo = "Not initialized";

    public string DebugInfo => _debugInfo;
    public bool IsInitialized => _initialized;
    public ID3D11ShaderResourceView BackBufferSRV => _backBufferSRV;
    public ID3D11ShaderResourceView SceneDiffuseSRV => _sceneDiffuseSRV;
    public byte[] LastAlphaData { get; private set; }
    public int CaptureWidth { get; private set; }
    public int CaptureHeight { get; private set; }
    public bool Initialize() {
      if (_initialized || _disposed) return _initialized;

      try {
        var ffxivDevice = Device.Instance();
        if (ffxivDevice == null || ffxivDevice->D3D11DeviceContext == null) {
          _debugInfo = "FFXIV D3D11 device context not available.";
          return false;
        }

        var contextPtr = (IntPtr)ffxivDevice->D3D11DeviceContext;
        Marshal.AddRef(contextPtr);
        _context = new ID3D11DeviceContext(contextPtr);
        Marshal.AddRef(_context.Device.NativePointer);
        _device = _context.Device;

        _initialized = true;
        _debugInfo = "Initialized, waiting for first capture.";
        return true;
      } catch (Exception ex) {
        _debugInfo = $"Init failed: {ex.Message}";
        return false;
      }
    }

    /// <summary>
    /// Call at the very start of OnDraw, before any ImGui rendering.
    /// Captures the current back buffer (Scene + Game UI). The alpha channel contains the UI mask!
    /// </summary>
    public void CaptureFrame() {
      if (_disposed || !_initialized) return;
      _frameCaptured = false;

      try {
        // Get the back buffer from the DXGI swap chain directly.
        // OMGetRenderTargets returns null at the start of OnDraw,
        // so we go through the FFXIV device's swap chain instead.
        var ffxivDevice = Device.Instance();
        if (ffxivDevice == null || ffxivDevice->SwapChain == null) {
          _debugInfo = "SwapChain not available";
          return;
        }

        // The FFXIV SwapChain wrapper has a DXGISwapChain pointer
        var scPtr = (IntPtr)ffxivDevice->SwapChain->DXGISwapChain;
        if (scPtr == IntPtr.Zero) {
          _debugInfo = "DXGISwapChain ptr is null";
          return;
        }

        var dxgiSwapChain = new Vortice.DXGI.IDXGISwapChain(scPtr);
        // GetBuffer(0) = current back buffer
        var backBuffer = dxgiSwapChain.GetBuffer<ID3D11Texture2D>(0);
        var desc = backBuffer.Description;

        // Create or resize our copy texture
        if (_backBufferCopy == null || _width != (int)desc.Width || _height != (int)desc.Height) {
          _backBufferCopy?.Dispose();
          _backBufferSRV?.Dispose();
          _stagingTexture?.Dispose();
          _backBufferSRV = null;

          _width = (int)desc.Width;
          _height = (int)desc.Height;

          _backBufferCopy = _device.CreateTexture2D(new Texture2DDescription {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
          });

          _stagingTexture = _device.CreateTexture2D(new Texture2DDescription {
            Width = 1,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
          });

          _previewStagingTexture?.Dispose();
          _previewStagingTexture = _device.CreateTexture2D(new Texture2DDescription {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
          });

          _backBufferSRV = _device.CreateShaderResourceView(_backBufferCopy);
        }

        // Copy the back buffer (Scene + UI) to our snapshot
        if (desc.SampleDescription.Count > 1) {
          _context.ResolveSubresource(_backBufferCopy, 0, backBuffer, 0, desc.Format);
        } else {
          _context.CopyResource(_backBufferCopy, backBuffer);
        }

        backBuffer.Dispose();

        // We no longer capture GBuffer[3] here. The Pre-UI scene is captured in OnAddonPreDraw!
        _frameCaptured = true;
        _debugInfo = $"Captured {_width}x{_height}";

        // Also enumerate visible addons for debug
        LastAddonRects = GetVisibleAddonRects();
        _debugInfo = $"Captured {_width}x{_height}, {LastAddonRects.Count} addons";
      } catch (Exception ex) {
        _debugInfo = $"Capture failed: {ex.Message}";
      }
    }

    /// <summary>
    /// Reads the alpha value of a single pixel from the captured backbuffer.
    /// Returns 0 if coordinates are out of bounds or capture failed.
    /// </summary>
    public float GetPixelAlpha(int x, int y) {
      if (_disposed || !_initialized || !_frameCaptured || _backBufferCopy == null || _stagingTexture == null) return 0f;
      if (x < 0 || y < 0 || x >= _width || y >= _height) return 0f;

      try {
        _context.CopySubresourceRegion(_stagingTexture, 0, 0, 0, 0, _backBufferCopy, 0, new Vortice.Mathematics.Box(x, y, 0, x + 1, y + 1, 1));
        var mapped = _context.Map(_stagingTexture, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        
        float alpha = 0f;
        unsafe {
            byte* ptr = (byte*)mapped.DataPointer;
            alpha = ptr[3] / 255f; // BGRA or RGBA, alpha is the 4th byte
        }
        
        _context.Unmap(_stagingTexture, 0);
        return alpha;
      } catch {
        return 0f;
      }
    }

    /// <summary>
    /// After the video has been blitted to the back buffer, this copies
    /// the original UI addon regions from the saved frame back on top.
    /// This pixel-perfectly restores game UI that was overwritten by the video.
    /// </summary>
    public void RestoreUIRegions() {
      if (_disposed || !_initialized || !_frameCaptured || _backBufferCopy == null) return;

      try {
        // Get the current back buffer
        var savedRTVs = new ID3D11RenderTargetView[1];
        ID3D11DepthStencilView savedDSV;
        _context.OMGetRenderTargets(1, savedRTVs, out savedDSV);

        if (savedRTVs[0] == null) {
          _debugInfo = "RestoreUI: No RTV bound";
          return;
        }

        using var rtvResource = savedRTVs[0].Resource;
        using var backBuffer = rtvResource.QueryInterface<ID3D11Texture2D>();

        // Use the addon rects already enumerated during CaptureFrame
        int restored = 0;

        foreach (var rect in LastAddonRects) {
          // Clamp to screen bounds
          int x = Math.Max(0, rect.X);
          int y = Math.Max(0, rect.Y);
          int right = Math.Min(_width, rect.X + rect.W);
          int bottom = Math.Min(_height, rect.Y + rect.H);

          if (right <= x || bottom <= y) continue;

          // CopySubresourceRegion: copy this rectangle from saved frame to back buffer
          var srcBox = new Vortice.Mathematics.Box(x, y, 0, right, bottom, 1);
          _context.CopySubresourceRegion(backBuffer, 0, x, y, 0, _backBufferCopy, 0, srcBox);
          restored++;
        }

        _debugInfo = $"Restored {restored}/{LastAddonRects.Count} UI regions";
      } catch (Exception ex) {
        _debugInfo = $"Restore error: {ex.Message}";
      }
    }

    /// <summary>
    /// The last set of addon rectangles found. Exposed for debug visualization.
    /// </summary>
    public List<(int X, int Y, int W, int H, string Name)> LastAddonRects { get; private set; }
      = new List<(int X, int Y, int W, int H, string Name)>();

    /// <summary>
    /// Enumerates all visible ATK addon units and returns their screen rectangles.
    /// </summary>
    private List<(int X, int Y, int W, int H, string Name)> GetVisibleAddonRects() {
      var rects = new List<(int X, int Y, int W, int H, string Name)>();

      try {
        var unitManager = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkUnitManager.Instance();
        if (unitManager == null) {
          _debugInfo = "RaptureAtkUnitManager is null";
          return rects;
        }

        // Iterate through the AtkUnitList entries
        var unitList = &unitManager->AllLoadedUnitsList;
        for (int i = 0; i < unitList->Count; i++) {
          var unitBase = unitList->Entries[i].Value;
          if (unitBase == null) continue;

          // Only include visible addons with nonzero size
          if (!unitBase->IsVisible || unitBase->RootNode == null) continue;

          var x = (int)unitBase->X;
          var y = (int)unitBase->Y;
          var w = (int)(unitBase->RootNode->Width * unitBase->Scale);
          var h = (int)(unitBase->RootNode->Height * unitBase->Scale);
          var name = unitBase->NameString;

          if (w <= 0 || h <= 0) continue;

          rects.Add((x, y, w, h, name));
        }
      } catch (Exception ex) {
        _debugInfo = $"Addon enum error: {ex.Message}";
      }

      return rects;
    }

    public void GeneratePreview() {
      if (_disposed || !_initialized || !_frameCaptured || _backBufferCopy == null || _previewStagingTexture == null) return;
      try {
        _context.CopyResource(_previewStagingTexture, _backBufferCopy);
        var mapped = _context.Map(_previewStagingTexture, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try {
          int captureW = Math.Min(480, _width);
          int captureH = Math.Min(270, _height);
          int stepX = _width / captureW;
          int stepY = _height / captureH;
          CaptureWidth = captureW;
          CaptureHeight = captureH;

          if (LastAlphaData == null || LastAlphaData.Length != captureW * captureH * 4) {
            LastAlphaData = new byte[captureW * captureH * 4];
          }

          unsafe {
            byte* ptr = (byte*)mapped.DataPointer;
            for (int y = 0; y < captureH; y++) {
              for (int x = 0; x < captureW; x++) {
                int srcX = x * stepX;
                int srcY = y * stepY;
                byte* pixel = ptr + srcY * mapped.RowPitch + srcX * 4;
                
                byte alpha = pixel[3]; // FFXIV backbuffer is usually B8G8R8A8, so 3 is A
                int idx = (y * captureW + x) * 4;
                LastAlphaData[idx + 0] = alpha;
                LastAlphaData[idx + 1] = alpha;
                LastAlphaData[idx + 2] = alpha;
                LastAlphaData[idx + 3] = 255;
              }
            }
          }
        } finally {
          _context.Unmap(_previewStagingTexture, 0);
        }
      } catch (Exception ex) {
        _debugInfo = $"UI Preview error: {ex.Message}";
      }
    }

    public void Dispose() {
      if (_disposed) return;
      _disposed = true;
      _stagingTexture?.Dispose();
      _previewStagingTexture?.Dispose();
      _backBufferSRV?.Dispose();
      _backBufferCopy?.Dispose();
      _sceneDiffuseSRV?.Dispose();
      _sceneDiffuseCopy?.Dispose();
      _context?.Dispose();
      _device?.Dispose();
      _device = null;
      _context = null;
    }
  }
}
