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
      public float HasTitleTexture;
      public float IsLooping;
      public float IsShuffle;
      public float _pad2;
      public float _pad3;
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
  float HasTitleTexture;
  float IsLooping;
  float IsShuffle;
  float _pad2;
  float _pad3;
};

cbuffer UIConsts : register(b1) {
  float4 UIRects[64];
  int UIRectCount;
  float3 _uiPadEnd;
};

Texture2D VideoTexture : register(t0);
Texture2D DepthTexture : register(t1);
Texture2D BackBufferTexture : register(t2);
Texture2D TitleTexture : register(t3);
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
      
      // Blend title texture perfectly flush onto the TV frame!
      if (HasTitleTexture > 0.5 && HoverUV.x >= 0.0 && HoverUV.y >= 0.0) {
          float4 titleColor = TitleTexture.Sample(VideoSampler, uv);
          // Standard alpha blend
          color.rgb = lerp(color.rgb, titleColor.rgb, titleColor.a);
      }
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
      // Background
      color.rgb = lerp(color.rgb, float3(0.05, 0.05, 0.05), 0.7);
      
      // Prev (0.02 - 0.06)
      if (uv.x > 0.02 && uv.x < 0.06 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.02) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         if (px < 0.2) color.rgb = float3(1, 1, 1);
         else if (px > 0.3 && px > abs(py - 0.5) * 2.0) color.rgb = float3(1, 1, 1);
      }
      
      // Rewind (0.07 - 0.11)
      if (uv.x > 0.07 && uv.x < 0.11 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.07) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         float px1 = frac(px * 2.0);
         if (px1 > abs(py - 0.5) * 2.0) color.rgb = float3(1, 1, 1);
      }

      // Play/Pause (0.12 - 0.16)
      if (uv.x > 0.12 && uv.x < 0.16 && uv.y > 0.88 && uv.y < 0.94) {
         if (IsPlaying > 0.5) {
            float px = (uv.x - 0.12) / 0.04;
            if ((px > 0.2 && px < 0.4) || (px > 0.6 && px < 0.8)) color.rgb = float3(1, 1, 1);
         } else {
            float px = (uv.x - 0.12) / 0.04;
            float py = (uv.y - 0.88) / 0.06;
            if (px < 1.0 - abs(py - 0.5) * 2.0) color.rgb = float3(1, 1, 1);
         }
      }

      // Fast Forward (0.17 - 0.21)
      if (uv.x > 0.17 && uv.x < 0.21 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.17) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         float px1 = frac(px * 2.0);
         if (px1 < 1.0 - abs(py - 0.5) * 2.0) color.rgb = float3(1, 1, 1);
      }
      
      // Next (0.22 - 0.26)
      if (uv.x > 0.22 && uv.x < 0.26 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.22) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         if (px > 0.8) color.rgb = float3(1, 1, 1);
         else if (px < 0.7 && px < 1.0 - abs(py - 0.5) * 2.0) color.rgb = float3(1, 1, 1);
      }
      
      // Seek Bar & Volume Track
      if (uv.y > 0.90 && uv.y < 0.92 && uv.x > 0.28 && uv.x < 0.58) {
         float barProgress = (uv.x - 0.28) / 0.30;
         if (barProgress < Progress) color.rgb = float3(0.8, 0.2, 0.2);
         else color.rgb = float3(0.3, 0.3, 0.3);
      }
      if (uv.y > 0.95 && uv.y < 0.97 && uv.x > 0.28 && uv.x < 0.58) {
         float volProgress = (uv.x - 0.28) / 0.30;
         if (volProgress < Volume / 3.0) color.rgb = float3(0.2, 0.6, 0.8);
         else color.rgb = float3(0.3, 0.3, 0.3);
      }
      
      // Loop (0.62 - 0.66)
      if (uv.x > 0.62 && uv.x < 0.66 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.62) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         bool draw = false;
         if (px < 0.4 && py < 0.6 && abs(distance(float2(px, py), float2(0.4, 0.5)) - 0.25) < 0.05) draw = true;
         if (px >= 0.4 && px <= 0.6 && abs(py - 0.25) < 0.05) draw = true;
         if (px >= 0.6 && px <= 0.8 && py >= 0.1 && py <= 0.4) {
             float lx = (px - 0.6) * 5.0;
             float ly = (py - 0.1) * 3.333;
             if (lx < 1.0 - abs(ly - 0.5) * 2.0) draw = true;
         }
         if (px > 0.6 && py > 0.4 && abs(distance(float2(px, py), float2(0.6, 0.5)) - 0.25) < 0.05) draw = true;
         if (px >= 0.4 && px <= 0.6 && abs(py - 0.75) < 0.05) draw = true;
         if (px >= 0.2 && px <= 0.4 && py >= 0.6 && py <= 0.9) {
             float lx = (px - 0.2) * 5.0;
             float ly = (py - 0.6) * 3.333;
             if (lx > abs(ly - 0.5) * 2.0) draw = true;
         }
         if (draw) {
             if (IsLooping > 0.5) color.rgb = float3(0.2, 0.8, 0.3);
             else color.rgb = float3(1, 1, 1);
         }
      }

      // Shuffle (0.68 - 0.72)
      if (uv.x > 0.68 && uv.x < 0.72 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.68) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         bool draw = false;
         float sy1 = lerp(0.25, 0.75, smoothstep(0.35, 0.65, px));
         float sy2 = lerp(0.75, 0.25, smoothstep(0.35, 0.65, px));
         if (px > 0.15 && px < 0.65 && abs(py - sy1) < 0.05) draw = true;
         if (px > 0.15 && px < 0.65 && abs(py - sy2) < 0.05 && abs(px - 0.5) > 0.06) draw = true;
         if (px >= 0.65 && px <= 0.85 && py >= 0.6 && py <= 0.9) {
             float lx = (px - 0.65) * 5.0;
             float ly = (py - 0.6) * 3.333;
             if (lx < 1.0 - abs(ly - 0.5) * 2.0) draw = true;
         }
         if (px >= 0.65 && px <= 0.85 && py >= 0.1 && py <= 0.4) {
             float lx = (px - 0.65) * 5.0;
             float ly = (py - 0.1) * 3.333;
             if (lx < 1.0 - abs(ly - 0.5) * 2.0) draw = true;
         }
         if (draw) {
             if (IsShuffle > 0.5) color.rgb = float3(0.2, 0.8, 0.3);
             else color.rgb = float3(1, 1, 1);
         }
      }

      // Refresh (0.74 - 0.78)
      if (uv.x > 0.74 && uv.x < 0.78 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.74) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         bool draw = false;
         float dist = distance(float2(px, py), float2(0.5, 0.5));
         if (abs(dist - 0.25) < 0.05) {
             if (!(px > 0.5 && py < 0.4)) draw = true;
         }
         if (px >= 0.4 && px <= 0.6 && py >= 0.1 && py <= 0.4) {
             float lx = (px - 0.4) * 5.0;
             float ly = (py - 0.1) * 3.333;
             if (lx < 1.0 - abs(ly - 0.5) * 2.0) draw = true;
         }
         if (draw) color.rgb = float3(1, 1, 1);
      }
      
      // Lock Icon (0.80 - 0.84)
      if (uv.x > 0.80 && uv.x < 0.84 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.80) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         if (px > 0.2 && px < 0.8 && py > 0.4 && py < 0.9) {
             if (IsLockedTV > 0.5) color.rgb = float3(0.9, 0.7, 0.2);
             else color.rgb = float3(0.6, 0.6, 0.6);
             if (px > 0.45 && px < 0.55 && py > 0.6 && py < 0.8) color.rgb = float3(0.1, 0.1, 0.1);
         }
         if (py > 0.1 && py <= 0.4) {
             if (IsLockedTV < 0.5 && px > 0.5) { }
             else if (px > 0.3 && px < 0.7 && py < 0.2) color.rgb = float3(0.8, 0.8, 0.8);
             else if ((px > 0.3 && px < 0.4) || (px > 0.6 && px < 0.7)) color.rgb = float3(0.8, 0.8, 0.8);
         }
      }
      
      // Paste Icon (0.85 - 0.89)
      if (uv.x > 0.85 && uv.x < 0.89 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.85) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         if (px > 0.2 && px < 0.8 && py > 0.1 && py < 0.9) {
             if (py < 0.3 && px > 0.4 && px < 0.6) color.rgb = float3(0.9, 0.9, 0.9);
             else if (py > 0.3) {
                 if ((py > 0.45 && py < 0.55) || (py > 0.65 && py < 0.75)) color.rgb = float3(0.5, 0.5, 0.5);
                 else color.rgb = float3(0.8, 0.8, 0.8);
             } else color.rgb = float3(0.4, 0.3, 0.2);
         }
      }
      
      // Queue Icon (0.90 - 0.94)
      if (uv.x > 0.90 && uv.x < 0.94 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.90) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         if ((px > 0.4 && px < 0.6 && py > 0.2 && py < 0.8) || (py > 0.4 && py < 0.6 && px > 0.2 && px < 0.8)) color.rgb = float3(0.2, 0.8, 0.3);
      }
      
      // Kill Icon (0.95 - 0.99)
      if (uv.x > 0.95 && uv.x < 0.99 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.95) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         // Draw X
         float d1 = abs(px - py);
         float d2 = abs(px - (1.0 - py));
         if (d1 < 0.15 || d2 < 0.15) {
             color.rgb = float3(0.9, 0.2, 0.2);
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
      IntPtr videoSrvPtr,
      ID3D11ShaderResourceView depthSrv,
      Vector4 cornerDepths,
      int screenWidth, int screenHeight,
      ID3D11ShaderResourceView uiLayerSrv,
      Vector2? hoverUV, float progress, bool isPlaying, bool isLocked,
      float minDepth, float maxDepth, float volume,
      float renderWidth, float renderHeight,
      List<(int X, int Y, int W, int H, string Name)> uiRects, IntPtr titleSrvPtr = default,
      bool isLooping = false, bool isShuffle = false) {

      if (!_initialized || _disposed || videoSrvPtr == IntPtr.Zero || depthSrv == null) return false;

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
          HasBackBuffer = uiLayerSrv != null ? 1.0f : 0.0f,
          IsLockedTV = isLocked ? 1.0f : 0.0f,
          Volume = volume,
          RenderResolution = new Vector2(renderWidth, renderHeight),
          HasTitleTexture = titleSrvPtr != IntPtr.Zero ? 1.0f : 0.0f,
          IsLooping = isLooping ? 1.0f : 0.0f,
          IsShuffle = isShuffle ? 1.0f : 0.0f
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
        var srvs = new ID3D11ShaderResourceView[4];
        srvs[0] = new ID3D11ShaderResourceView(videoSrvPtr);
        srvs[1] = depthSrv;
        srvs[2] = uiLayerSrv;
        srvs[3] = titleSrvPtr != IntPtr.Zero ? new ID3D11ShaderResourceView(titleSrvPtr) : null;
        
        _context.PSSetShaderResources(0, 4, srvs);
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
