using System;
using System.Numerics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace XivMediaPlayer.Compositing {
  /// <summary>
  /// Creates a tiny downsampled copy of the video texture on the GPU.
  /// When drawn scaled up via ImGui, bilinear filtering produces a
  /// natural blur effect — perfect for screen glow/backlight.
  /// </summary>
  internal class GlowRenderer : IDisposable {
    private ID3D11Device _device;
    private ID3D11DeviceContext _context;

    // Downsampled glow texture
    private ID3D11Texture2D _glowTexture;
    private ID3D11ShaderResourceView _glowSRV;
    private ID3D11RenderTargetView _glowRTV;
    private int _glowSize;

    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// The SRV pointer for use with ImGui.AddImageQuad.
    /// </summary>
    public IntPtr GlowTextureHandle => _glowSRV?.NativePointer ?? IntPtr.Zero;
    public bool IsInitialized => _initialized;

    public unsafe bool Initialize(int glowSize = 32) {
      if (_initialized || _disposed) return _initialized;

      try {
        var ffxivDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        if (ffxivDevice == null || ffxivDevice->D3D11DeviceContext == null)
          return false;

        _context = new ID3D11DeviceContext((IntPtr)ffxivDevice->D3D11DeviceContext);
        _device = _context.Device;
        _glowSize = glowSize;

        CreateGlowTexture();
        _initialized = true;
        return true;
      } catch {
        return false;
      }
    }

    private unsafe void CreateGlowTexture() {
      _glowTexture = _device.CreateTexture2D(new Texture2DDescription {
        Width = _glowSize,
        Height = _glowSize,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm, // Match common Dalamud/ImGui format
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        CPUAccessFlags = CpuAccessFlags.None,
      });

      _glowSRV = _device.CreateShaderResourceView(_glowTexture);
      _glowRTV = _device.CreateRenderTargetView(_glowTexture);
    }

    /// <summary>
    /// Updates the glow texture by downsampling the video texture.
    /// Call once per frame before rendering the glow quad.
    /// </summary>
    public unsafe bool UpdateFromVideoTexture(IntPtr videoSrvPtr) {
      if (!_initialized || _disposed || videoSrvPtr == IntPtr.Zero) return false;

      try {
        // Get the source texture from the SRV
        var videoSRV = new ID3D11ShaderResourceView(videoSrvPtr);
        var videoResource = videoSRV.Resource;
        var videoTexture = videoResource.QueryInterface<ID3D11Texture2D>();
        var desc = videoTexture.Description;

        // Use GenerateMips approach: copy a sub-region to our tiny texture
        // D3D11 doesn't allow direct CopySubresourceRegion with different sizes,
        // so we'll use a full-screen quad blit via the device context.

        // Save current state
        var savedRTVs = new ID3D11RenderTargetView[1];
        ID3D11DepthStencilView savedDSV;
        _context.OMGetRenderTargets(1, savedRTVs, out savedDSV);

        try {
          // Set our tiny render target
          _context.OMSetRenderTargets(_glowRTV);
          _context.RSSetViewport(0, 0, _glowSize, _glowSize);

          // Use the video SRV as input — we need a simple passthrough shader
          // to blit/downsample. Since we don't have one, use CopySubresourceRegion
          // with a staging texture approach instead.
          //
          // Actually, the simplest approach: create a mipchain texture, copy the
          // video to mip0, generate mips, then copy the smallest mip to our texture.
          //
          // Even simpler: just use CopySubresourceRegion with source box.
          // This picks a sub-region but doesn't resample... we need actual downsampling.
          //
          // Simplest correct approach: create an intermediate texture with full mipchain.
          DownsampleViaMips(videoTexture, desc);
        } finally {
          _context.OMSetRenderTargets(savedRTVs, savedDSV);
        }

        return true;
      } catch {
        return false;
      }
    }

    private unsafe void DownsampleViaMips(ID3D11Texture2D source, Texture2DDescription srcDesc) {
      // Create a temporary texture with full mipchain for downsampling
      int mipLevels = 1;
      int w = (int)srcDesc.Width, h = (int)srcDesc.Height;
      while (w > _glowSize || h > _glowSize) {
        w /= 2;
        h /= 2;
        mipLevels++;
      }
      if (mipLevels < 1) mipLevels = 1;

      // Create a temp texture with auto-generated mips
      using var mipTexture = _device.CreateTexture2D(new Texture2DDescription {
        Width = srcDesc.Width,
        Height = srcDesc.Height,
        MipLevels = 0, // full mipchain
        ArraySize = 1,
        Format = srcDesc.Format,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        CPUAccessFlags = CpuAccessFlags.None,
        MiscFlags = ResourceOptionFlags.GenerateMips,
      });

      using var mipSRV = _device.CreateShaderResourceView(mipTexture);

      // Copy source into mip level 0
      _context.CopySubresourceRegion(mipTexture, 0, 0, 0, 0, source, 0);

      // Generate mips — GPU downsamples through the chain
      _context.GenerateMips(mipSRV);

      // Find the mip level closest to our glow size
      var mipDesc = mipTexture.Description;
      int targetMip = 0;
      int mw = (int)mipDesc.Width, mh = (int)mipDesc.Height;
      while (mw > _glowSize * 2 && mh > _glowSize * 2 && targetMip < (int)mipDesc.MipLevels - 1) {
        mw /= 2;
        mh /= 2;
        targetMip++;
      }

      // Copy that mip level to our glow texture
      _context.CopySubresourceRegion(
        _glowTexture, 0, 0, 0, 0,
        mipTexture, targetMip);
    }

    public void Dispose() {
      if (_disposed) return;
      _disposed = true;

      _glowRTV?.Dispose();
      _glowSRV?.Dispose();
      _glowTexture?.Dispose();
    }
  }
}
