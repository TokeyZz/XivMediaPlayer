using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace XivMediaPlayer.Compositing {
  /// <summary>
  /// Renders a textured quad with per-pixel depth occlusion using a fullscreen
  /// shader pass.
  /// </summary>
  internal unsafe class DepthTestedRenderer : IDisposable {
    private ID3D11Device _device;
    private ID3D11DeviceContext _context;

    // Shaders
    private ID3D11VertexShader _vertexShader;
    private ID3D11PixelShader _pixelShader;
    private ID3D11BlendState _blendState;
    private ID3D11SamplerState _videoSampler;
    private ID3D11SamplerState _depthSampler;

    // Buffers
    private ID3D11Buffer _constantBuffer;

    // Offscreen render target
    private ID3D11Texture2D _renderTarget;
    private ID3D11RenderTargetView _renderTargetView;
    private ID3D11ShaderResourceView _renderTargetSRV;
    private int _rtWidth, _rtHeight;

    private bool _initialized;
    private bool _disposed;
    private string _initError;

    [StructLayout(LayoutKind.Sequential)]
    private struct PSConstants {
      public Vector2 CornerTL;
      public Vector2 CornerTR;
      public Vector2 CornerBR;
      public Vector2 CornerBL;
      public Vector2 ScreenSize;
      public float _pad0;
      public float _pad1;
      public Vector4 CornerDepths; // TL, TR, BR, BL depths
    }

    private const string ShaderCode = @"
cbuffer Constants : register(b0) {
  float2 CornerTL;
  float2 CornerTR;
  float2 CornerBR;
  float2 CornerBL;
  float2 ScreenSize;
  float _pad0;
  float _pad1;
  float4 CornerDepths; // x=TL, y=TR, z=BR, w=BL
};

Texture2D VideoTexture : register(t0);
Texture2D DepthTexture : register(t1);
Texture2D BackBufferTexture : register(t2);
SamplerState VideoSampler : register(s0);
SamplerState DepthSampler : register(s1);

struct VS_OUT {
  float4 pos : SV_POSITION;
  float2 uv : TEXCOORD;
};

VS_OUT VS(uint id : SV_VertexID) {
  VS_OUT o;
  o.uv = float2((id << 1) & 2, id & 2);
  o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
  return o;
}

float cross2d(float2 a, float2 b) {
  return a.x * b.y - a.y * b.x;
}

float2 InverseBilinear(float2 p, float2 a, float2 b, float2 c, float2 d) {
  float2 e = b - a;
  float2 f = d - a;
  float2 g = a - b + c - d;
  float2 h = p - a;

  float k2 = cross2d(g, f);
  float k1 = cross2d(e, f) + cross2d(h, g);
  float k0 = cross2d(h, e);

  float v;
  if (abs(k2) < 0.0001) {
    v = -k0 / k1;
  } else {
    float disc = k1 * k1 - 4.0 * k0 * k2;
    if (disc < 0) return float2(-1, -1);
    disc = sqrt(disc);
    float v0 = (-k1 - disc) / (2.0 * k2);
    float v1 = (-k1 + disc) / (2.0 * k2);
    v = (v0 >= -0.001 && v0 <= 1.001) ? v0 : v1;
  }

  float2 denom = e + v * g;
  float u;
  if (abs(denom.x) > abs(denom.y)) {
    u = (h.x - v * f.x) / denom.x;
  } else {
    u = (h.y - v * f.y) / denom.y;
  }

  return float2(u, v);
}

float4 PS(VS_OUT input) : SV_TARGET {
  float2 pixelPos = input.pos.xy;
  float2 uv = InverseBilinear(pixelPos, CornerTL, CornerTR, CornerBR, CornerBL);

  if (uv.x < 0 || uv.x > 1 || uv.y < 0 || uv.y > 1) {
    return float4(0, 0, 0, 0);
  }

  float4 color = VideoTexture.Sample(VideoSampler, uv);

  // Depth occlusion
  float depthTop = lerp(CornerDepths.x, CornerDepths.y, uv.x);
  float depthBot = lerp(CornerDepths.w, CornerDepths.z, uv.x);
  float quadDepth = lerp(depthTop, depthBot, uv.y);

  float2 screenUV = pixelPos / ScreenSize;
  float gameDepth = DepthTexture.Sample(DepthSampler, screenUV).r;
  if (gameDepth > quadDepth) {
    color.a = 0;
  }

  // Use the BackBuffer Alpha channel for perfect UI masking!
  float bbAlpha = BackBufferTexture.Sample(DepthSampler, screenUV).a;
  
  if (bbAlpha > 0.1) {
    color.a = 0; // Hide video pixels behind UI
  }
  
  return color;
}
";

    public bool IsInitialized => _initialized;
    public string InitError => _initError;
    public ID3D11ShaderResourceView OutputSRV => _renderTargetSRV;

    public bool Initialize() {
      if (_initialized || _disposed) return _initialized;

      try {
        var ffxivDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        if (ffxivDevice == null || ffxivDevice->D3D11DeviceContext == null) {
          _initError = "FFXIV D3D11 device context not available.";
          return false;
        }

        var contextPtr = (IntPtr)ffxivDevice->D3D11DeviceContext;
        System.Runtime.InteropServices.Marshal.AddRef(contextPtr);
        _context = new ID3D11DeviceContext(contextPtr);
        System.Runtime.InteropServices.Marshal.AddRef(_context.Device.NativePointer);
        _device = _context.Device;

        // Compile shaders
        var vsBytecode = Compiler.Compile(ShaderCode, "VS", "", "vs_5_0");
        _vertexShader = _device.CreateVertexShader(vsBytecode.Span);

        var psBytecode = Compiler.Compile(ShaderCode, "PS", "", "ps_5_0");
        _pixelShader = _device.CreatePixelShader(psBytecode.Span);

        // Constant buffers
        _constantBuffer = _device.CreateBuffer(new BufferDescription {
          ByteWidth = Marshal.SizeOf<PSConstants>(),
          Usage = ResourceUsage.Default,
          BindFlags = BindFlags.ConstantBuffer,
          CPUAccessFlags = CpuAccessFlags.None,
        });

        // Blend state: write-through
        var blendDesc = new BlendDescription();
        blendDesc.RenderTarget[0] = new RenderTargetBlendDescription {
          BlendEnable = false,
          RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blendState = _device.CreateBlendState(blendDesc);

        // Samplers
        _videoSampler = _device.CreateSamplerState(new SamplerDescription {
          Filter = Filter.MinMagMipLinear,
          AddressU = TextureAddressMode.Clamp,
          AddressV = TextureAddressMode.Clamp,
          AddressW = TextureAddressMode.Clamp,
        });
        _depthSampler = _device.CreateSamplerState(new SamplerDescription {
          Filter = Filter.MinMagMipPoint,
          AddressU = TextureAddressMode.Clamp,
          AddressV = TextureAddressMode.Clamp,
          AddressW = TextureAddressMode.Clamp,
        });

        _initialized = true;
        return true;
      } catch (Exception ex) {
        _initError = $"DepthTestedRenderer init failed: {ex.Message}";
        return false;
      }
    }

    private void EnsureRenderTarget(int width, int height) {
      if (_renderTarget != null && _rtWidth == width && _rtHeight == height) return;

      _renderTargetView?.Dispose();
      _renderTargetSRV?.Dispose();
      _renderTarget?.Dispose();

      _rtWidth = width;
      _rtHeight = height;

      var texDesc = new Texture2DDescription {
        Width = width,
        Height = height,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.R8G8B8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        CPUAccessFlags = CpuAccessFlags.None,
      };

      _renderTarget = _device.CreateTexture2D(texDesc);
      _renderTargetView = _device.CreateRenderTargetView(_renderTarget);
      _renderTargetSRV = _device.CreateShaderResourceView(_renderTarget);
    }

    public unsafe bool Render(
      (Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl) screenCorners,
      IntPtr videoTextureSRV,
      ID3D11ShaderResourceView depthSRV,
      Vector4 cornerDepths,
      int screenWidth, int screenHeight,
      ID3D11ShaderResourceView backBufferSRV) {

      if (!_initialized || _disposed || videoTextureSRV == IntPtr.Zero || depthSRV == null) return false;

      EnsureRenderTarget(screenWidth, screenHeight);

      var savedRTVs = new ID3D11RenderTargetView[1];
      ID3D11DepthStencilView savedDSV;
      _context.OMGetRenderTargets(1, savedRTVs, out savedDSV);

      try {
        _context.ClearRenderTargetView(_renderTargetView, new Vortice.Mathematics.Color4(0, 0, 0, 0));

        // Update constants
        var constants = new PSConstants {
          CornerTL = screenCorners.tl,
          CornerTR = screenCorners.tr,
          CornerBR = screenCorners.br,
          CornerBL = screenCorners.bl,
          ScreenSize = new Vector2(screenWidth, screenHeight),
          CornerDepths = cornerDepths,
        };
        _context.UpdateSubresource(constants, _constantBuffer);

        // Set pipeline
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);

        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);

        _context.PSSetShader(_pixelShader);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        var videoSRV = new ID3D11ShaderResourceView(videoTextureSRV);
        _context.PSSetShaderResource(0, videoSRV);
        _context.PSSetShaderResource(1, depthSRV);
        _context.PSSetShaderResource(2, backBufferSRV);
        _context.PSSetSampler(0, _videoSampler);
        _context.PSSetSampler(1, _depthSampler);

        _context.OMSetBlendState(_blendState);
        _context.RSSetViewport(0, 0, screenWidth, screenHeight);
        _context.OMSetRenderTargets(_renderTargetView);

        _context.Draw(3, 0);

        return true;
      } finally {
        _context.OMSetRenderTargets(savedRTVs, savedDSV);
        _context.PSSetShaderResource(0, (ID3D11ShaderResourceView)null);
        _context.PSSetShaderResource(1, (ID3D11ShaderResourceView)null);
        _context.PSSetShaderResource(2, (ID3D11ShaderResourceView)null);
        
        savedRTVs[0]?.Dispose();
        savedDSV?.Dispose();
      }
    }

    public void Dispose() {
      if (_disposed) return;
      _disposed = true;

      _renderTargetView?.Dispose();
      _renderTargetSRV?.Dispose();
      _renderTarget?.Dispose();
      _depthSampler?.Dispose();
      _videoSampler?.Dispose();
      _blendState?.Dispose();
      _constantBuffer?.Dispose();
      _pixelShader?.Dispose();
      _vertexShader?.Dispose();
    }
  }
}
