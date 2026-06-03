using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace XivMediaPlayer.Compositing {
  /// <summary>
  /// Renders a textured quad as native D3D11 geometry with depth testing
  /// against the game's depth buffer. This gives per-pixel occlusion by
  /// walls, characters, and other game geometry.
  ///
  /// The game uses reverse-Z depth (near=1.0, far=0.0), so our depth
  /// comparison function is GREATER — our pixel is only drawn when its
  /// depth is greater (closer) than the game's depth.
  /// </summary>
  internal unsafe class DepthTestedRenderer : IDisposable {
    private ID3D11Device _device;
    private ID3D11DeviceContext _context;

    // Pipeline state
    private ID3D11VertexShader _vertexShader;
    private ID3D11PixelShader _pixelShader;
    private ID3D11InputLayout _inputLayout;
    private ID3D11DepthStencilState _depthStencilState;
    private ID3D11RasterizerState _rasterizerState;
    private ID3D11BlendState _blendState;
    private ID3D11SamplerState _samplerState;

    // Buffers
    private ID3D11Buffer _vertexBuffer;
    private ID3D11Buffer _indexBuffer;
    private ID3D11Buffer _constantBuffer;

    private bool _initialized;
    private bool _disposed;
    private string _initError;

    [StructLayout(LayoutKind.Sequential)]
    private struct QuadVertex {
      public Vector3 Position;
      public Vector2 TexCoord;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VSConstants {
      public Matrix4x4 ViewProjection;
    }

    private static readonly ushort[] QuadIndices = { 0, 1, 2, 2, 3, 0 };

    private const string ShaderCode = @"
cbuffer Constants : register(b0) {
  matrix ViewProjection;
};

Texture2D VideoTexture : register(t0);
SamplerState VideoSampler : register(s0);

struct VS_IN {
  float3 pos : POSITION;
  float2 uv : TEXCOORD;
};

struct PS_IN {
  float4 pos : SV_POSITION;
  float2 uv : TEXCOORD;
};

PS_IN VS(VS_IN input) {
  PS_IN output = (PS_IN)0;
  output.pos = mul(float4(input.pos, 1.0f), ViewProjection);
  output.uv = input.uv;
  return output;
}

float4 PS(PS_IN input) : SV_TARGET {
  return VideoTexture.Sample(VideoSampler, input.uv);
}
";

    public bool IsInitialized => _initialized;
    public string InitError => _initError;

    /// <summary>
    /// Initialize D3D11 resources using the game's device context.
    /// </summary>
    public bool Initialize() {
      if (_initialized || _disposed) return _initialized;

      try {
        var ffxivDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        if (ffxivDevice == null || ffxivDevice->D3D11DeviceContext == null) {
          _initError = "FFXIV D3D11 device context not available.";
          return false;
        }

        _context = new ID3D11DeviceContext((IntPtr)ffxivDevice->D3D11DeviceContext);
        _device = _context.Device;

        // Compile shaders
        var vsBytecode = Compiler.Compile(ShaderCode, "VS", "", "vs_5_0");
        _vertexShader = _device.CreateVertexShader(vsBytecode.Span);

        var inputElements = new[] {
          new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
          new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 12, 0),
        };
        _inputLayout = _device.CreateInputLayout(inputElements, vsBytecode.Span);

        var psBytecode = Compiler.Compile(ShaderCode, "PS", "", "ps_5_0");
        _pixelShader = _device.CreatePixelShader(psBytecode.Span);

        // Constant buffer (ViewProjection matrix = 64 bytes)
        _constantBuffer = _device.CreateBuffer(new BufferDescription {
          ByteWidth = 64,
          Usage = ResourceUsage.Default,
          BindFlags = BindFlags.ConstantBuffer,
          CPUAccessFlags = CpuAccessFlags.None,
        });

        // Vertex buffer (4 vertices, updated each frame)
        _vertexBuffer = _device.CreateBuffer(new BufferDescription {
          ByteWidth = Marshal.SizeOf<QuadVertex>() * 4,
          Usage = ResourceUsage.Default,
          BindFlags = BindFlags.VertexBuffer,
          CPUAccessFlags = CpuAccessFlags.None,
        });

        // Index buffer (6 indices, static)
        _indexBuffer = _device.CreateBuffer(QuadIndices, BindFlags.IndexBuffer);

        // Depth stencil: GREATER for reverse-Z, read-only (no writes to game depth)
        _depthStencilState = _device.CreateDepthStencilState(new DepthStencilDescription {
          DepthEnable = true,
          DepthWriteMask = DepthWriteMask.Zero,
          DepthFunc = ComparisonFunction.Greater,
        });

        // Blend state: standard alpha blending
        var blendDesc = new BlendDescription();
        blendDesc.RenderTarget[0] = new RenderTargetBlendDescription {
          BlendEnable = true,
          SourceBlend = Blend.SourceAlpha,
          DestinationBlend = Blend.InverseSourceAlpha,
          BlendOperation = BlendOperation.Add,
          SourceBlendAlpha = Blend.One,
          DestinationBlendAlpha = Blend.InverseSourceAlpha,
          BlendOperationAlpha = BlendOperation.Add,
          RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blendState = _device.CreateBlendState(blendDesc);

        // Rasterizer: no culling (screen visible from both sides)
        _rasterizerState = _device.CreateRasterizerState(new RasterizerDescription {
          FillMode = FillMode.Solid,
          CullMode = CullMode.None,
          FrontCounterClockwise = false,
          DepthClipEnable = true,
        });

        // Sampler for the video texture
        _samplerState = _device.CreateSamplerState(new SamplerDescription {
          Filter = Filter.MinMagMipLinear,
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

    /// <summary>
    /// Render the video quad with depth testing against the game's depth buffer.
    /// </summary>
    public void Render(
      (Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl) corners,
      IntPtr videoTextureSRV,
      Matrix4x4 viewProjection) {

      if (!_initialized || _disposed || videoTextureSRV == IntPtr.Zero) return;

      // Save the game's current render targets and depth stencil
      var savedRTVs = new ID3D11RenderTargetView[1];
      ID3D11DepthStencilView savedDSV;
      _context.OMGetRenderTargets(1, savedRTVs, out savedDSV);

      try {
        // Update vertex buffer with current quad corners
        var vertices = new QuadVertex[] {
          new() { Position = corners.tl, TexCoord = new Vector2(0, 0) },
          new() { Position = corners.tr, TexCoord = new Vector2(1, 0) },
          new() { Position = corners.br, TexCoord = new Vector2(1, 1) },
          new() { Position = corners.bl, TexCoord = new Vector2(0, 1) },
        };
        _context.UpdateSubresource(vertices, _vertexBuffer);

        // Update constant buffer
        var constants = new VSConstants { ViewProjection = viewProjection };
        _context.UpdateSubresource(constants, _constantBuffer);

        // The savedDSV from OMGetRenderTargets IS the game's depth buffer
        if (savedDSV == null) return;

        // Set pipeline state
        _context.IASetInputLayout(_inputLayout);
        _context.IASetVertexBuffer(0, _vertexBuffer, Marshal.SizeOf<QuadVertex>());
        _context.IASetIndexBuffer(_indexBuffer, Format.R16_UInt, 0);
        _context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);

        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);

        _context.PSSetShader(_pixelShader);
        var videoSRV = new ID3D11ShaderResourceView(videoTextureSRV);
        _context.PSSetShaderResource(0, videoSRV);
        _context.PSSetSampler(0, _samplerState);

        _context.RSSetState(_rasterizerState);
        _context.OMSetDepthStencilState(_depthStencilState);
        _context.OMSetBlendState(_blendState);

        // Bind game's render target + depth stencil
        _context.OMSetRenderTargets(savedRTVs, savedDSV);

        // Draw the quad
        _context.DrawIndexed(6, 0, 0);
      } finally {
        // Restore original render targets and depth stencil
        _context.OMSetRenderTargets(savedRTVs, savedDSV);
      }
    }

    public void Dispose() {
      if (_disposed) return;
      _disposed = true;

      _samplerState?.Dispose();
      _blendState?.Dispose();
      _rasterizerState?.Dispose();
      _depthStencilState?.Dispose();
      _indexBuffer?.Dispose();
      _vertexBuffer?.Dispose();
      _constantBuffer?.Dispose();
      _inputLayout?.Dispose();
      _pixelShader?.Dispose();
      _vertexShader?.Dispose();
    }
  }
}
