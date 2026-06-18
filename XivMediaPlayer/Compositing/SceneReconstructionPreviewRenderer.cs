using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace XivMediaPlayer.Compositing {
  /// <summary>
  /// Renders a reconstructed scene from FFXIV's GBuffers for preview in the UI.
  /// </summary>
  internal unsafe class SceneReconstructionPreviewRenderer : IDisposable {
    private ID3D11Device _device;
    private ID3D11DeviceContext _context;

    // Shader pipeline
    private ID3D11VertexShader _vertexShader;
    private ID3D11PixelShader _pixelShader;
    private ID3D11SamplerState _sampler;
    private ID3D11BlendState _blendState;

    // Output texture
    private ID3D11Texture2D _previewTexture;
    private ID3D11ShaderResourceView _previewSRV;
    private ID3D11RenderTargetView _previewRTV;
    private int _width;
    private int _height;

    private bool _initialized;
    private bool _disposed;

    private const string PreviewShaderCode = @"
Texture2D GBuffer0 : register(t0); // Normal
Texture2D GBuffer1 : register(t1); // Unknown/Material
Texture2D GBuffer2 : register(t2); // Albedo
Texture2D GBuffer3 : register(t3); // Unknown/Specular

SamplerState LinearSampler : register(s0);

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

float4 PS(VS_OUT input) : SV_TARGET {
  float4 albedo = GBuffer2.Sample(LinearSampler, input.uv);
  float4 normalMap = GBuffer0.Sample(LinearSampler, input.uv);
  
  // Reconstruct normal
  float3 normal = normalize(normalMap.xyz * 2.0 - 1.0);
  
  // Basic sun-like directional light
  float3 lightDir = normalize(float3(0.5, 1.0, -0.5));
  float NdotL = max(dot(normal, lightDir), 0.2); // ambient 0.2
  
  // Add some fake rim lighting based on normal Z
  float rim = 1.0 - max(normal.z, 0.0);
  rim = smoothstep(0.6, 1.0, rim);
  
  float3 color = albedo.rgb * NdotL + (albedo.rgb * rim * 0.3);
  return float4(color, 1.0);
}
";

    public IntPtr PreviewTextureHandle => _previewSRV?.NativePointer ?? IntPtr.Zero;
    public bool IsInitialized => _initialized;

    public bool Initialize(int width = 800, int height = 450) {
      if (_initialized || _disposed) return _initialized;

      try {
        var ffxivDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        if (ffxivDevice == null || ffxivDevice->D3D11DeviceContext == null)
          return false;

        var contextPtr = (IntPtr)ffxivDevice->D3D11DeviceContext;
        System.Runtime.InteropServices.Marshal.AddRef(contextPtr);
        _context = new ID3D11DeviceContext(contextPtr);
        _device = _context.Device;
        _width = width;
        _height = height;

        // Compile shaders
        var vsBytecode = Compiler.Compile(PreviewShaderCode, "VS", "", "vs_5_0");
        _vertexShader = _device.CreateVertexShader(vsBytecode.Span);

        var psBytecode = Compiler.Compile(PreviewShaderCode, "PS", "", "ps_5_0");
        _pixelShader = _device.CreatePixelShader(psBytecode.Span);

        _sampler = _device.CreateSamplerState(new SamplerDescription {
          Filter = Filter.MinMagMipLinear,
          AddressU = TextureAddressMode.Clamp,
          AddressV = TextureAddressMode.Clamp,
          AddressW = TextureAddressMode.Clamp,
        });

        var blendDesc = new BlendDescription();
        blendDesc.RenderTarget[0] = new RenderTargetBlendDescription {
          BlendEnable = false,
          RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blendState = _device.CreateBlendState(blendDesc);

        _previewTexture = _device.CreateTexture2D(new Texture2DDescription {
          Width = _width,
          Height = _height,
          MipLevels = 1,
          ArraySize = 1,
          Format = Format.R8G8B8A8_UNorm,
          SampleDescription = new SampleDescription(1, 0),
          Usage = ResourceUsage.Default,
          BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
          CPUAccessFlags = CpuAccessFlags.None,
        });

        _previewRTV = _device.CreateRenderTargetView(_previewTexture);
        _previewSRV = _device.CreateShaderResourceView(_previewTexture);

        _initialized = true;
        return true;
      } catch {
        return false;
      }
    }

    public unsafe bool Update(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* gb0, FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* gb1, FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* gb2, FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* gb3) {
      if (!_initialized || _disposed || gb0 == null || gb1 == null || gb2 == null || gb3 == null || 
          gb0->D3D11Texture2D == null || gb1->D3D11Texture2D == null || gb2->D3D11Texture2D == null || gb3->D3D11Texture2D == null) return false;

      ID3D11ShaderResourceView srv0 = null;
      ID3D11ShaderResourceView srv1 = null;
      ID3D11ShaderResourceView srv2 = null;
      ID3D11ShaderResourceView srv3 = null;

      try {
        var texPtr0 = (IntPtr)gb0->D3D11Texture2D;
        var texPtr1 = (IntPtr)gb1->D3D11Texture2D;
        var texPtr2 = (IntPtr)gb2->D3D11Texture2D;
        var texPtr3 = (IntPtr)gb3->D3D11Texture2D;
        
        System.Runtime.InteropServices.Marshal.AddRef(texPtr0);
        using var tex0 = new ID3D11Texture2D(texPtr0);
        System.Runtime.InteropServices.Marshal.AddRef(texPtr1);
        using var tex1 = new ID3D11Texture2D(texPtr1);
        System.Runtime.InteropServices.Marshal.AddRef(texPtr2);
        using var tex2 = new ID3D11Texture2D(texPtr2);
        System.Runtime.InteropServices.Marshal.AddRef(texPtr3);
        using var tex3 = new ID3D11Texture2D(texPtr3);

        srv0 = _device.CreateShaderResourceView(tex0);
        srv1 = _device.CreateShaderResourceView(tex1);
        srv2 = _device.CreateShaderResourceView(tex2);
        srv3 = _device.CreateShaderResourceView(tex3);

        var savedRTVs = new ID3D11RenderTargetView[1];
        ID3D11DepthStencilView savedDSV;
        _context.OMGetRenderTargets(1, savedRTVs, out savedDSV);

        try {
          _context.ClearRenderTargetView(_previewRTV, new Vortice.Mathematics.Color4(0, 0, 0, 0));
          _context.OMSetRenderTargets(_previewRTV);
          _context.RSSetViewport(0, 0, _width, _height);
          _context.OMSetBlendState(_blendState);

          _context.PSSetShaderResource(0, srv0);
          _context.PSSetShaderResource(1, srv1);
          _context.PSSetShaderResource(2, srv2);
          _context.PSSetShaderResource(3, srv3);
          _context.PSSetSampler(0, _sampler);

          _context.VSSetShader(_vertexShader);
          _context.PSSetShader(_pixelShader);

          _context.IASetInputLayout(null);
          _context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
          _context.Draw(3, 0);

          return true;
        } finally {
          _context.OMSetRenderTargets(savedRTVs, savedDSV);
          _context.PSSetShaderResource(0, (ID3D11ShaderResourceView)null);
          _context.PSSetShaderResource(1, (ID3D11ShaderResourceView)null);
          _context.PSSetShaderResource(2, (ID3D11ShaderResourceView)null);
          _context.PSSetShaderResource(3, (ID3D11ShaderResourceView)null);
        }
      } catch (Exception ex) {
        System.IO.File.WriteAllText(@"C:\Users\stel9\Documents\UpdateError.txt", ex.ToString());
        return false;
      } finally {
        srv0?.Dispose();
        srv1?.Dispose();
        srv2?.Dispose();
        srv3?.Dispose();
      }
    }

    public void Dispose() {
      if (_disposed) return;
      _disposed = true;

      _previewRTV?.Dispose();
      _previewSRV?.Dispose();
      _previewTexture?.Dispose();
      _blendState?.Dispose();
      _sampler?.Dispose();
      _pixelShader?.Dispose();
      _vertexShader?.Dispose();
      _device?.Dispose();
      _context?.Dispose();
      _device = null;
      _context = null;
    }
  }
}
