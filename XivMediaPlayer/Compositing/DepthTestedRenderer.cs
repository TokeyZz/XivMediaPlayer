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
    private ID3D11Buffer _uiRectBuffer;

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
      
      // New fields appended at the end
      public Vector2 HoverUV;
      public float Progress;
      public float IsPlaying;
      public float DynamicMinDepth;
      public float DynamicMaxDepth;
      public float HasBackBuffer;
      public float IsLockedTV;
      public float Volume;
      public Vector2 RenderResolution;
      public float _pad4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UIConstants {
      public fixed float UIRects[256]; // 64 * 4 (x, y, w, h)
      public int UIRectCount;
      public float _pad0;
      public float _pad1;
      public float _pad2;
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
  float2 HoverUV;
  float Progress;
  float IsPlaying;
  float DynamicMinDepth;
  float DynamicMaxDepth;
  float HasBackBuffer;
  float IsLockedTV;
  float Volume;
  float2 RenderResolution;
  float _padEnd;
};

cbuffer UIConsts : register(b1) {
  float4 UIRects[64];
  int UIRectCount;
  float3 _uiPadEnd;
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

  bool isInside = (uv.x >= 0 && uv.x <= 1 && uv.y >= 0 && uv.y <= 1);
  float2 screenUV = pixelPos / ScreenSize;
  
  // Dynamic Resolution scaling: the depth buffer texture size might be larger than the actual rendered area
  float2 renderScale = float2(1.0, 1.0);
  if (RenderResolution.x > 0 && RenderResolution.y > 0) {
      renderScale = RenderResolution / ScreenSize;
  }
  float2 depthUV = screenUV * renderScale;
  
  float gameDepth = DepthTexture.Sample(DepthSampler, depthUV).r;
  float4 color = float4(0, 0, 0, 0);

  bool occluded = false;
  if (isInside) {
      float depthTop = lerp(CornerDepths.x, CornerDepths.y, uv.x);
      float depthBot = lerp(CornerDepths.w, CornerDepths.z, uv.x);
      float quadDepth = lerp(depthTop, depthBot, uv.y);
      if (gameDepth > quadDepth) {
          occluded = true;
      }
  }

  if (isInside && !occluded) {
      // Draw unoccluded TV
      color = VideoTexture.Sample(VideoSampler, uv);
  } else {
      float depthMask = 1.0;
      if (gameDepth < 0.0001) depthMask = 0; // Ignore skybox
      
      // Calculate TV dimensions in screen pixels
      float tvWidthPixels = length(CornerTR - CornerTL);
      float tvHeightPixels = length(CornerBL - CornerTL);
      float tvPixelSize = max(tvWidthPixels, tvHeightPixels);
      
      // Calculate distance from the center of the TV in true screen pixels!
      // By using true screen distance, we completely bypass UV space perspective distortion and the mathematical vanishing point.
      // This guarantees a perfectly smooth radial glow everywhere on screen.
      float2 tvCenter = (CornerTL + CornerTR + CornerBL + CornerBR) * 0.25;
      float distInPixels = distance(pixelPos, tvCenter);
      
      // Subtract half the TV size so the glow starts fading from the edges, not the center
      distInPixels = max(0.0, distInPixels - tvPixelSize * 0.5);
      
      // Removing the 2D in-front shadow blocker!
      // Because we use Color Dodge, the light naturally wraps around 3D objects and beautifully illuminates them.
      // Trying to fake shadows with a 2D screen-space cutout creates blocky rectangular lines on characters!
      
      // Physical light dissipation based on screen pixels!
      // This ensures the light reaches the same distance regardless of perspective.
      float maxGlowRadiusPixels = max(200.0, tvPixelSize * 1.5);
      float distanceFade = saturate(1.0 - (distInPixels / maxGlowRadiusPixels)); 
      depthMask *= pow(distanceFade, 2.5); // Non-linear falloff for realism
      
      if (depthMask > 0.001) {
          // We replace the 9-point sample with a massive 144-point (12x12 grid) average!
          // Because all screen pixels read the exact same UVs, this is a 100% texture cache hit
          // and costs almost zero performance, but it creates an incredibly stable color.
          // This completely eliminates the epilepsy flicker caused by moving objects!
          float3 prominentColor = float3(0, 0, 0);
          for (float x = 0.05; x < 1.0; x += 0.0833) {
              for (float y = 0.05; y < 1.0; y += 0.0833) {
                  prominentColor += VideoTexture.Sample(VideoSampler, float2(x, y)).rgb;
              }
          }
          prominentColor /= 144.0;
          
          // Brighter video pixels get more alpha.
          float luminance = dot(prominentColor, float3(0.299, 0.587, 0.114));
          float alpha = saturate(depthMask * luminance * 3.5); 
          
          // We clamp the ceiling to prevent extreme blowout on bright pixels.
          // alpha = 0.75 -> Scene / 0.25 = 4.0x brightness max.
          // This keeps the light vibrant without reaching the 10x multiplier of alpha 0.9.
          alpha = clamp(alpha, 0.0, 0.75); 
          
          // TRUE LIGHTING BLEND
          // Instead of using ImGui's standard alpha blend (which washes out the background like fog),
          // we sample the actual game pixel and ADD the light to it!
          float3 light = prominentColor * alpha;
          
          if (HasBackBuffer > 0.5) {
              float3 sceneColor = BackBufferTexture.Sample(VideoSampler, screenUV).rgb;
              
              // Color Dodge Blend: Final = Scene / (1.0 - Light)
              // Screen blending raised the black floor, causing the grey fog over the shadows.
              // Color Dodge preserves pure black shadows (0 / X = 0) while powerfully illuminating the textures!
              float3 finalColor = saturate(sceneColor / max(0.001, 1.0 - light));
              
              color = float4(finalColor, 1.0); // Output opaque because we already blended the scene!
          } else {
              // Fallback to standard fog overlay if backbuffer is missing
              color = float4(prominentColor, alpha);
          }
      }
  }

  // Check if this pixel is inside any UI bounding box
  bool insideUI = false;
  for (int i = 0; i < UIRectCount; i++) {
      float4 r = UIRects[i];
      if (pixelPos.x >= r.x && pixelPos.x <= r.x + r.z &&
          pixelPos.y >= r.y && pixelPos.y <= r.y + r.w) {
          insideUI = true;
          break;
      }
  }
  
  if (insideUI) {
      // Use the BackBuffer Alpha channel for perfect UI masking!
      float bbAlpha = BackBufferTexture.Sample(DepthSampler, screenUV).a;
      
      // Smoothly blend out the video behind UI drop shadows and gradients
      color.a *= saturate(1.0 - bbAlpha);
  }
  
  // Media Controls UI overlay
  if (isInside && !occluded && HoverUV.x >= 0.0 && HoverUV.y >= 0.0) {
    if (uv.y > 0.85) {
      // Draw bottom bar background (semi-transparent black)
      color.rgb = lerp(color.rgb, float3(0.05, 0.05, 0.05), 0.7);
      
      // Draw Seek Bar track and progress fill
      if (uv.y > 0.90 && uv.y < 0.92 && uv.x > 0.15 && uv.x < 0.72) {
        float barProgress = (uv.x - 0.15) / 0.57;
        if (barProgress < Progress) {
           color.rgb = float3(0.8, 0.2, 0.2); // FFXIV-style red progress
        } else {
           color.rgb = float3(0.3, 0.3, 0.3); // Grey track
        }
      }
      
      // Draw Play/Pause icon at bottom left
      if (uv.x > 0.05 && uv.x < 0.10 && uv.y > 0.88 && uv.y < 0.94) {
         if (IsPlaying > 0.5) {
            float px = (uv.x - 0.05) / 0.05;
            if ((px > 0.2 && px < 0.4) || (px > 0.6 && px < 0.8)) {
               color.rgb = float3(1, 1, 1);
            }
         } else {
            float px = (uv.x - 0.05) / 0.05;
            float py = (uv.y - 0.88) / 0.06;
            if (px < 1.0 - abs(py - 0.5) * 2.0) {
               color.rgb = float3(1, 1, 1);
            }
         }
      }
      
      // Draw Lock Icon
      if (uv.x > 0.74 && uv.x < 0.80 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.74) / 0.06;
         float py = (uv.y - 0.88) / 0.06;
         
         // Draw Padlock body
         if (px > 0.2 && px < 0.8 && py > 0.4 && py < 0.9) {
             if (IsLockedTV > 0.5) {
                 color.rgb = float3(0.9, 0.7, 0.2); // Golden lock
             } else {
                 color.rgb = float3(0.6, 0.6, 0.6); // Grey unlocked
             }
             // Keyhole
             if (px > 0.45 && px < 0.55 && py > 0.6 && py < 0.8) {
                 color.rgb = float3(0.1, 0.1, 0.1);
             }
         }
         // Draw Padlock shackle
         if (py > 0.1 && py <= 0.4) {
             // For unlocked, only draw the left side of the shackle!
             if (IsLockedTV < 0.5 && px > 0.5) {
                 // Skip drawing right side of shackle if unlocked
             } else if (px > 0.3 && px < 0.7 && py < 0.2) {
                 color.rgb = float3(0.8, 0.8, 0.8);
             } else if ((px > 0.3 && px < 0.4) || (px > 0.6 && px < 0.7)) {
                 color.rgb = float3(0.8, 0.8, 0.8);
             }
         }
      }
      
      // Draw Paste Icon (Clipboard shape) at right
      if (uv.x > 0.82 && uv.x < 0.88 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.82) / 0.06;
         float py = (uv.y - 0.88) / 0.06;
         // Draw clipboard board
         if (px > 0.2 && px < 0.8 && py > 0.1 && py < 0.9) {
             // Draw clip at top
             if (py < 0.3 && px > 0.4 && px < 0.6) {
                 color.rgb = float3(0.9, 0.9, 0.9);
             } else if (py > 0.3) {
                 // Draw paper lines
                 if ((py > 0.45 && py < 0.55) || (py > 0.65 && py < 0.75)) {
                     color.rgb = float3(0.5, 0.5, 0.5); // Text lines
                 } else {
                     color.rgb = float3(0.8, 0.8, 0.8); // Paper
                 }
             } else {
                 color.rgb = float3(0.4, 0.3, 0.2); // Board
             }
         }
      }
      
      // Draw Queue Icon (Plus + Paper) at far right
      if (uv.x > 0.90 && uv.x < 0.96 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.90) / 0.06;
         float py = (uv.y - 0.88) / 0.06;
         
         // Draw plus sign
         if ((px > 0.4 && px < 0.6 && py > 0.2 && py < 0.8) ||
             (py > 0.4 && py < 0.6 && px > 0.2 && px < 0.8)) {
             color.rgb = float3(0.2, 0.8, 0.3); // Green plus
         }
      }
      
      // Draw Volume Slider (thinner bar below the seek bar)
      if (uv.y > 0.95 && uv.y < 0.97 && uv.x > 0.15 && uv.x < 0.72) {
         float volProgress = (uv.x - 0.15) / 0.57;
         if (volProgress < Volume / 3.0) {
            color.rgb = float3(0.2, 0.6, 0.8); // Blue volume bar
         } else {
            color.rgb = float3(0.3, 0.3, 0.3); // Grey track
         }
      }
    }
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

        _uiRectBuffer = _device.CreateBuffer(new BufferDescription {
          ByteWidth = Marshal.SizeOf<UIConstants>(),
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
      ID3D11ShaderResourceView backBufferSRV,
      Vector2? hoverUV, float progress, bool isPlaying, bool isLocked,
      float minDepth, float maxDepth, float volume,
      float renderWidth, float renderHeight,
      List<(int X, int Y, int W, int H, string Name)> uiRects) {

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
          HoverUV = hoverUV ?? new Vector2(-1, -1),
          CornerDepths = cornerDepths,
          Progress = progress,
          IsPlaying = isPlaying ? 1.0f : 0.0f,
          DynamicMinDepth = minDepth,
          DynamicMaxDepth = maxDepth,
          HasBackBuffer = backBufferSRV != null ? 1.0f : 0.0f,
          IsLockedTV = isLocked ? 1.0f : 0.0f,
          Volume = volume,
          RenderResolution = new Vector2(renderWidth, renderHeight),
        };
        _context.UpdateSubresource(constants, _constantBuffer);

        var uiConsts = new UIConstants {
            UIRectCount = Math.Min(64, uiRects?.Count ?? 0)
        };
        for (int i = 0; i < uiConsts.UIRectCount; i++) {
            var r = uiRects[i];
            uiConsts.UIRects[i * 4 + 0] = r.X;
            uiConsts.UIRects[i * 4 + 1] = r.Y;
            uiConsts.UIRects[i * 4 + 2] = r.W;
            uiConsts.UIRects[i * 4 + 3] = r.H;
        }
        _context.UpdateSubresource(uiConsts, _uiRectBuffer);

        // Set pipeline
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);

        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);

        _context.PSSetShader(_pixelShader);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetConstantBuffer(1, _uiRectBuffer);
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
      _uiRectBuffer?.Dispose();
      _pixelShader?.Dispose();
      _vertexShader?.Dispose();
    }
  }
}
