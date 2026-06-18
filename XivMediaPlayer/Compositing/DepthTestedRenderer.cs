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
      public float NearPlane;
      public float FarPlane;
      public Vector4 CornerDepths; // TL, TR, BR, BL depths

      public Vector3 CameraPos;
      public float FovY;
      public Vector3 CameraForward;
      public float AspectRatio;
      public Vector3 CameraRight;
      public float _pad2;
      public Vector3 CameraUp;
      public float _pad3;

      public Vector3 CornerTL3D;
      public float _pad4;
      public Vector3 CornerTR3D;
      public float _pad5;
      public Vector3 CornerBL3D;
      public float VideoAspectRatio;
      
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
      public float Time;
      public float ShowScreensaver;
      public float HasPreUI;
      public float _pad7;
      public float _pad8;
      public float _pad9;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UIConstants {
      public fixed float UIRects[256]; // 64 * 4 (x, y, w, h)
      public fixed float UIRectTypes[64]; // 0 = standard, 1 = MJI
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
  float NearPlane;
  float FarPlane;
  float4 CornerDepths; // x=TL, y=TR, z=BR, w=BL

  float3 CameraPos;
  float FovY;
  float3 CameraForward;
  float AspectRatio;
  float3 CameraRight;
  float _pad2;
  float3 CameraUp;
  float _pad3;

  float3 CornerTL3D;
  float _pad4;
  float3 CornerTR3D;
  float _pad5;
  float3 CornerBL3D;
  float VideoAspectRatio;

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
  float Time;
  float ShowScreensaver;
  float HasPreUI;
  float _pad7;
  float _pad8;
  float _pad9;
};

cbuffer UIConsts : register(b1) {
  float4 UIRects[64];
  float4 UIRectTypes[16]; // 64 floats packed into 16 vectors
  int UIRectCount;
  float3 _padUI;
};

Texture2D VideoTexture : register(t0);
Texture2D DepthTexture : register(t1);
Texture2D BackBufferTexture : register(t2);
Texture2D TitleTexture : register(t3);
Texture2D PreUITexture : register(t4);
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



float4 PS(VS_OUT input) : SV_TARGET {
  float2 pixelPos = input.pos.xy;
  float2 screenUV = pixelPos / ScreenSize;

  float2 ndc = screenUV * 2.0 - 1.0;
  ndc.y = -ndc.y;

  float fovDist = 1.0 / tan(FovY * 0.5);
  float3 rayOrigin = CameraPos;
  // FFXIV uses a coordinate system where CameraForward (3rd column) points backwards from the camera.
  // We must negate it to point the ray towards the screen.
  float3 rayDir = normalize(ndc.x * AspectRatio * CameraRight + ndc.y * CameraUp - fovDist * CameraForward);

  float3 tvRight = CornerTR3D - CornerTL3D;
  float3 tvDown = CornerBL3D - CornerTL3D;
  float3 tvNormal = normalize(cross(tvRight, tvDown));

  float denom = dot(tvNormal, rayDir);
  bool isInside = false;
  float2 uv = float2(-1, -1);
  float t = -1.0;

  float2 sampleUV = float2(-1, -1);
  if (abs(denom) > 1e-6) {
      t = dot(CornerTL3D - rayOrigin, tvNormal) / denom;
      float3 hitPoint = rayOrigin + rayDir * t;
      float3 d = hitPoint - CornerTL3D;
      float u = dot(d, tvRight) / dot(tvRight, tvRight);
      float v = dot(d, tvDown) / dot(tvDown, tvDown);
      
      uv = float2(u, v);
      isInside = (t > 0.0 && u >= 0.0 && u <= 1.0 && v >= 0.0 && v <= 1.0);
      
      sampleUV = uv;
      if (VideoAspectRatio > 0) {
          float tvAspect = length(tvRight) / length(tvDown);
          if (VideoAspectRatio > tvAspect) {
              float scale = tvAspect / VideoAspectRatio;
              sampleUV.y = (sampleUV.y - 0.5) / scale + 0.5;
          } else {
              float scale = VideoAspectRatio / tvAspect;
              sampleUV.x = (sampleUV.x - 0.5) / scale + 0.5;
          }
      }
  }
  
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
      // Calculate exact view-space Z distance of the intersection point
      float viewZ = dot(rayDir * t, -CameraForward);
      
      // Convert to non-linear reversed-Z depth to match FFXIV's depth buffer
      float exactDepth = 0.0;
      if (viewZ > 0.0) {
          exactDepth = NearPlane * (FarPlane - viewZ) / (viewZ * (FarPlane - NearPlane));
      }
      exactDepth = saturate(exactDepth);
      
      // Use exact mathematical depth + a tiny bias to perfectly fix Z-fighting 
      // when the TV is placed completely flush against a wall!
      if (gameDepth > exactDepth + 0.0001) {
          occluded = true;
      }
  }

  if (isInside && !occluded) {
      // Draw unoccluded TV
      if (sampleUV.x < 0 || sampleUV.x > 1 || sampleUV.y < 0 || sampleUV.y > 1) {
          color = float4(0, 0, 0, 1);
      } else {
          color = VideoTexture.Sample(VideoSampler, sampleUV);
          color.a = 1.0;
      }
      
      // XMP Screensaver
      if (ShowScreensaver > 0.5) {
          color.rgb *= 0.2; // Dim background
          float aspect = 16.0 / 9.0;
          if (RenderResolution.y > 0) aspect = RenderResolution.x / RenderResolution.y;
          
          float speedX = 0.1;
          float speedY = 0.075;
          float bx = Time * speedX;
          float by = Time * speedY;
          
          float logoSize = 0.15;
          float logoW = logoSize * 3.0 / aspect;
          float logoH = logoSize * 1.0;
          
          float rangeX = 1.0 - logoW;
          float rangeY = 1.0 - logoH;
          
          float posX = (logoW / 2.0) + abs(fmod(bx, 2.0) - 1.0) * rangeX;
          float posY = (logoH / 2.0) + abs(fmod(by, 2.0) - 1.0) * rangeY;
          
          float2 p = float2((uv.x - posX) * aspect / logoSize, (uv.y - posY) / logoSize);
          float2 pText = float2(p.x + p.y * 0.35, p.y);
          
          bool draw = false;
          if (p.x > -2.0 && p.x < 2.0 && p.y > -0.7 && p.y < 0.7) {
              
              // Top connection line
              if (pText.x > -1.53 && pText.x < 1.25 && pText.y > -0.6 && pText.y < -0.45) draw = true;
              
              // X
              if (pText.x > -1.5 && pText.x < -0.5 && pText.y > -0.5 && pText.y < 0.2) {
                  float dx1 = abs((pText.x + 1.0) - (pText.y + 0.15) * 1.2);
                  float dx2 = abs((pText.x + 1.0) + (pText.y + 0.15) * 1.2);
                  if (dx1 < 0.11 || dx2 < 0.11) draw = true;
              }
              
              // M
              if (pText.x > -0.6 && pText.x < 0.6 && pText.y > -0.5) {
                  if (pText.y < 0.2) {
                      if (abs(pText.x + 0.4) < 0.11) draw = true;
                      if (abs(pText.x - 0.4) < 0.11) draw = true;
                  }
                  float dm1 = abs(pText.x - (pText.y * 0.4 - 0.2));
                  float dm2 = abs(pText.x - (-pText.y * 0.4 + 0.2));
                  if (pText.x <= 0.0 && dm1 < 0.12 && pText.y < 0.55) draw = true;
                  if (pText.x >= 0.0 && dm2 < 0.12 && pText.y < 0.55) draw = true;
              }
              
              // P
              if (pText.x > 0.5 && pText.x < 1.6 && pText.y > -0.61 && pText.y < 0.2) {
                  if (abs(pText.x - 0.8) < 0.11) draw = true;
                  if (pText.x >= 0.8 && pText.x <= 1.25) {
                      if (abs(pText.y - (-0.05)) < 0.11) draw = true; 
                  }
                  if (pText.x > 1.25) {
                      if (distance(float2(pText.x, pText.y), float2(1.25, -0.27)) < 0.33) {
                          if (distance(float2(pText.x, pText.y), float2(1.25, -0.305)) >= 0.145) {
                              draw = true;
                          }
                      }
                  }
              }
              
              // Ellipse (Use original un-slanted p)
              if (distance(float2(p.x * 0.08, p.y - 0.45), float2(0,0)) < 0.12) {
                  float dm1 = abs(pText.x - (pText.y * 0.5 - 0.25));
                  float dm2 = abs(pText.x - (-pText.y * 0.5 + 0.25));
                  float distToV = (pText.x < 0.0) ? dm1 : dm2;
                  
                  if (distToV > 0.16) {
                      draw = true;
                      // Slit for VIDEO parody
                      if (p.x > -0.8 && p.x < 0.8 && abs(p.y - 0.45) < 0.025) draw = false;
                  }
              }
              
              if (p.y < -0.6) draw = false;
          }
          
          if (draw) {
              int colorIdx = (int(floor(bx)) + int(floor(by))) % 6;
              float3 logoColor = float3(1, 1, 1);
              if (colorIdx == 0) logoColor = float3(1.0, 0.3, 0.3);
              else if (colorIdx == 1) logoColor = float3(0.3, 1.0, 0.3);
              else if (colorIdx == 2) logoColor = float3(0.3, 0.6, 1.0);
              else if (colorIdx == 3) logoColor = float3(1.0, 1.0, 0.3);
              else if (colorIdx == 4) logoColor = float3(1.0, 0.3, 1.0);
              else if (colorIdx == 5) logoColor = float3(0.3, 1.0, 1.0);
              
              color.rgb = logoColor;
          }
      }

      // Blend title texture perfectly flush onto the TV frame!
      if (HasTitleTexture > 0.5 && HoverUV.x >= 0.0 && HoverUV.y >= 0.0) {
          float4 titleColor = TitleTexture.Sample(VideoSampler, uv);
          // Standard alpha blend
          color.rgb = lerp(color.rgb, titleColor.rgb, titleColor.a);
      }
  } else {
      float depthMask = 1.0;
      if (gameDepth < 0.0001) depthMask = 0; // Ignore skybox
      
      // Convert gameDepth (reversed Z) to viewZ
      float gameViewZ = (NearPlane * FarPlane) / (gameDepth * (FarPlane - NearPlane) + NearPlane);
      
      // Reconstruct the 3D world position of the game pixel
      float3 gameWorldPos = CameraPos + rayDir * (gameViewZ / dot(rayDir, -CameraForward));
      
      // Calculate TV dimensions in 3D world space
      float3 tvCenter3D = (CornerTR3D + CornerBL3D) * 0.5;
      float tvWidth3D = length(CornerTR3D - CornerTL3D);
      float tvHeight3D = length(CornerBL3D - CornerTL3D);
      float tvSize3D = max(tvWidth3D, tvHeight3D);
      
      // Calculate distance from the center of the TV in 3D world space
      float3 toPixel = gameWorldPos - tvCenter3D;
      float dist3D = length(toPixel);
      
      // Subtract half the TV size so the glow starts fading from the edges, not the center
      dist3D = max(0.0, dist3D - tvSize3D * 0.5);
      
      // Physical light dissipation based on world space distance
      float maxGlowRadius3D = max(4.0, tvSize3D * 1.5);
      float distanceFade = saturate(1.0 - (dist3D / maxGlowRadius3D)); 
      depthMask *= pow(distanceFade, 2.5); // Non-linear falloff for realism
      
      // Backlight directionality fade: shine behind the TV, not on objects in front
      float3 dirToPixel = dist3D > 0.001 ? normalize(toPixel) : float3(0, 0, 0);
      float dotNorm = dot(dirToPixel, tvNormal);
      float directionFade = smoothstep(0.5, -0.2, dotNorm);
      depthMask *= directionFade;
      
      if (depthMask > 0.001) {
          // Use a 144-point (12x12 grid) average texture sample to stabilize the glow color
          // and mitigate potential flicker from on-screen movement.
          float3 prominentColor = float3(0, 0, 0);
          for (float x = 0.05; x < 1.0; x += 0.0833) {
              for (float y = 0.05; y < 1.0; y += 0.0833) {
                  prominentColor += VideoTexture.Sample(VideoSampler, float2(x, y)).rgb;
              }
          }
          prominentColor /= 144.0;
          
          // Scale glow alpha by the computed video luminance.
          float luminance = dot(prominentColor, float3(0.299, 0.587, 0.114));
          float alpha = saturate(depthMask * luminance * 3.5); 
          
          // Clamp intensity ceiling to prevent extreme highlights overexposure.
          alpha = clamp(alpha, 0.0, 0.45); 
          
          // Compute additive backlight intensity.
          float3 light = prominentColor * alpha;
          
          if (HasBackBuffer > 0.5) {
              float3 sceneColor = BackBufferTexture.Sample(VideoSampler, screenUV).rgb;
              float bbAlpha = BackBufferTexture.Sample(DepthSampler, screenUV).a;
              
              // Color Dodge Blend: Final = Scene / (1.0 - Light) to preserve dark shadows
              // while illuminating brighter midtones.
              float3 glowedScene = saturate(sceneColor / max(0.001, 1.0 - light));
              
              // Protect overlapping UI elements from overexposure by blending back original scene color.
              float3 finalColor = lerp(glowedScene, sceneColor, bbAlpha);
              
              color = float4(finalColor, 1.0); // Output pre-blended opaque color
          } else {
              // Fallback to standard transparent fog overlay if backbuffer is missing
              color = float4(prominentColor, alpha);
          }
      }
  }

  // The UI compositing block was moved to the bottom of the shader to draw OVER the media controls.
  
  // Media Controls UI overlay
  if (isInside && !occluded && HoverUV.x >= 0.0 && HoverUV.y >= 0.0 && IsLockedTV >= 0.0) {
    // History Icon Top Left (0.02 - 0.08, 0.04 - 0.12)
    if (uv.x > 0.02 && uv.x < 0.08 && uv.y > 0.04 && uv.y < 0.12) {
       color.rgb = lerp(color.rgb, float3(0.05, 0.05, 0.05), 0.7);
       float px = (uv.x - 0.02) / 0.06;
       float py = (uv.y - 0.04) / 0.08;
       
       // draw clock
       float dist = distance(float2(px, py), float2(0.5, 0.5));
       if (abs(dist - 0.3) < 0.05) color.rgb = float3(1, 1, 1);
       if (px > 0.45 && px < 0.55 && py > 0.25 && py < 0.55) color.rgb = float3(1, 1, 1); // hour hand
       if (px > 0.45 && px < 0.7 && py > 0.45 && py < 0.55) color.rgb = float3(1, 1, 1); // minute hand
    }

    // DMCA Button (Top Right: 0.92 - 0.98, 0.04 - 0.12)
    if (uv.x > 0.92 && uv.x < 0.98 && uv.y > 0.04 && uv.y < 0.12) {
       color.rgb = lerp(color.rgb, float3(0.05, 0.05, 0.05), 0.7);
       float px = (uv.x - 0.92) / 0.06;
       float py = (uv.y - 0.04) / 0.08;
       
       // draw 'i' for info/report
       if (px > 0.4 && px < 0.6) {
           if (py > 0.2 && py < 0.3) color.rgb = float3(1, 1, 1);
           if (py > 0.4 && py < 0.8) color.rgb = float3(1, 1, 1);
       }
    }

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
      
      // Stop (0.27 - 0.31)
      if (uv.x > 0.27 && uv.x < 0.31 && uv.y > 0.88 && uv.y < 0.94) {
         float px = (uv.x - 0.27) / 0.04;
         float py = (uv.y - 0.88) / 0.06;
         if (px > 0.1 && px < 0.9 && py > 0.1 && py < 0.9) color.rgb = float3(1, 1, 1);
      }
      
      // Seek Bar & Volume Track
      if (uv.y > 0.90 && uv.y < 0.92 && uv.x > 0.32 && uv.x < 0.60) {
         float barProgress = (uv.x - 0.32) / 0.28;
         if (barProgress < Progress) color.rgb = float3(0.8, 0.2, 0.2);
         else color.rgb = float3(0.3, 0.3, 0.3);
      }
      if (uv.y > 0.95 && uv.y < 0.97 && uv.x > 0.32 && uv.x < 0.60) {
         float volProgress = (uv.x - 0.32) / 0.28;
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
      if (IsLockedTV >= 0.0 && uv.x > 0.80 && uv.x < 0.84 && uv.y > 0.88 && uv.y < 0.94) {
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
  
  // Check if this pixel is inside any UI bounding box
  bool insideUI = false;
  int rectType = 0; // 0 = Standard, 1 = MJI, 2 = ActionDetail
  for (int i = 0; i < UIRectCount; i++) {
      float4 r = UIRects[i];
      if (pixelPos.x >= r.x && pixelPos.x <= r.x + r.z &&
          pixelPos.y >= r.y && pixelPos.y <= r.y + r.w) {
          insideUI = true;
          
          int vecIdx = i / 4;
          int compIdx = i % 4;
          float typeVal = UIRectTypes[vecIdx][compIdx];
          
          if (typeVal > 3.5) {
              rectType = 4; // _ToDoList takes highest precedence
          } else if (typeVal > 2.5 && rectType < 4) {
              rectType = 3; // MjlHud
          } else if (typeVal > 1.5 && rectType < 3) {
              rectType = 2; // ActionDetail
          } else if (typeVal > 0.5 && rectType < 2) {
              rectType = 1; // MJI
          }
      }
  }
  
  if (insideUI && isInside && !occluded) {
      float4 bbColor = BackBufferTexture.Sample(VideoSampler, screenUV);
      float bbAlpha = BackBufferTexture.Sample(DepthSampler, screenUV).a;
      
      if (HasPreUI > 0.5) {
          float4 uiTex = PreUITexture.Sample(VideoSampler, screenUV);
          bbAlpha = saturate(uiTex.a);
      }
      
      if (color.a > 0.5) {
          if (rectType == 4) {
              // _ToDoList: threshold 90, backdrop #453C26
              float threshold = 90.0 / 255.0;
              float bbLuminance = dot(bbColor.rgb, float3(0.299, 0.587, 0.114));
              float colorBoost = smoothstep(0.3, 0.6, bbLuminance);
              float isPureWhite = max(smoothstep(threshold - 0.02, 1.0, bbAlpha), colorBoost);
              float3 shadowColor = float3(69.0 / 255.0, 60.0 / 255.0, 38.0 / 255.0); // #453C26
              float3 targetColor = lerp(shadowColor, bbColor.rgb, isPureWhite);
              color.rgb = color.rgb * saturate(1.0 - bbAlpha) + targetColor * bbAlpha;
          } else if (rectType == 3) {
              // MjlHud: threshold 233, backdrop #ACA393
              float threshold = 233.0 / 255.0;
              float isPureWhite = smoothstep(threshold - 0.02, 1.0, bbAlpha);
              float3 shadowColor = float3(172.0 / 255.0, 163.0 / 255.0, 147.0 / 255.0); // #ACA393
              float3 targetColor = lerp(shadowColor, bbColor.rgb, isPureWhite);
              color.rgb = color.rgb * saturate(1.0 - bbAlpha) + targetColor * bbAlpha;
          } else if (rectType == 2) {
              // ActionDetail: ultra aggressive threshold, only 255 cuts the TV
              float threshold = 254.0 / 255.0;
              float isPureWhite = smoothstep(threshold - 0.02, 1.0, bbAlpha);
              float3 shadowColor = float3(48.0 / 255.0, 34.0 / 255.0, 21.0 / 255.0); // #302215
              float3 targetColor = lerp(shadowColor, bbColor.rgb, isPureWhite);
              color.rgb = color.rgb * saturate(1.0 - bbAlpha) + targetColor * bbAlpha;
          } else if (rectType == 1) {
              // MJI: threshold 152
              float threshold = 152.0 / 255.0;
              float isPureWhite = smoothstep(threshold - 0.02, threshold, bbAlpha);
              
              float3 blendedBlack = color.rgb * saturate(1.0 - bbAlpha);
              color.rgb = blendedBlack + (bbColor.rgb * bbAlpha * isPureWhite);
          } else {
              // For all other UI (standard game UI)
              // Standard mode with shadow backdrop
              float threshold = 152.0 / 255.0;
              float isPureWhite = smoothstep(threshold - 0.02, 1.0, bbAlpha);
              float3 shadowColor = float3(0.0, 0.0, 0.0);
              float3 targetColor = lerp(shadowColor, bbColor.rgb, isPureWhite);
              color.rgb = color.rgb * saturate(1.0 - bbAlpha) + targetColor * bbAlpha;
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
      (Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl) worldCorners,
      Vector3 cameraPos,
      Vector3 cameraForward, Vector3 cameraRight, Vector3 cameraUp, float fovY, float aspectRatio,
      IntPtr videoSrvPtr,
      ID3D11ShaderResourceView depthSrv,
      Vector4 cornerDepths,
      float nearPlane, float farPlane,
      int screenWidth, int screenHeight,
      ID3D11ShaderResourceView uiLayerSrv,
      Vector2? hoverUV, float progress, bool isPlaying, float lockState,
      float minDepth, float maxDepth, float volume,
      float renderWidth, float renderHeight,
      List<(int X, int Y, int W, int H, string Name)> uiRects, IntPtr titleSrvPtr = default,
      bool isLooping = false, bool isShuffle = false, float time = 0, float showScreensaver = 0,
      float videoAspectRatio = 0, IntPtr gbuffer2SrvPtr = default, IntPtr gbuffer3SrvPtr = default, IntPtr transparentUiSrvPtr = default) {

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
          NearPlane = nearPlane,
          FarPlane = farPlane,
          HoverUV = hoverUV ?? new Vector2(-1, -1),
          CornerDepths = cornerDepths,

          CameraPos = cameraPos,
          FovY = fovY,
          CameraForward = cameraForward,
          AspectRatio = aspectRatio,
          CameraRight = cameraRight,
          CameraUp = cameraUp,

          CornerTL3D = worldCorners.tl,
          CornerTR3D = worldCorners.tr,
          CornerBL3D = worldCorners.bl,
          VideoAspectRatio = videoAspectRatio,

          Progress = progress,
          IsPlaying = isPlaying ? 1.0f : 0.0f,
          DynamicMinDepth = minDepth,
          DynamicMaxDepth = maxDepth,
          HasBackBuffer = uiLayerSrv != null ? 1.0f : 0.0f,
          IsLockedTV = lockState,
          Volume = volume,
          RenderResolution = new Vector2(renderWidth, renderHeight),
          HasTitleTexture = titleSrvPtr != IntPtr.Zero ? 1.0f : 0.0f,
          IsLooping = isLooping ? 1.0f : 0.0f,
          IsShuffle = isShuffle ? 1.0f : 0.0f,
          Time = time,
          ShowScreensaver = showScreensaver,
          HasPreUI = transparentUiSrvPtr != IntPtr.Zero ? 1.0f : 0.0f
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
            
            // Flag addons so the shader treats them differently: 2 = _ActionContents, 3 = MJI (Island Sanctuary HUD), 4 = _ToDoList, 0 = Standard
            if (r.Name != null && r.Name.StartsWith("_ActionContents")) {
                uiConsts.UIRectTypes[i] = 2.0f;
            } else if (r.Name != null && r.Name.StartsWith("MJI")) {
                uiConsts.UIRectTypes[i] = 3.0f;
            } else if (r.Name != null && r.Name.StartsWith("_ToDoList")) {
                uiConsts.UIRectTypes[i] = 4.0f;
            } else {
                uiConsts.UIRectTypes[i] = 0.0f;
            }
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
        var srvs = new ID3D11ShaderResourceView[7];
        if (videoSrvPtr != IntPtr.Zero) System.Runtime.InteropServices.Marshal.AddRef(videoSrvPtr);
        srvs[0] = videoSrvPtr != IntPtr.Zero ? new ID3D11ShaderResourceView(videoSrvPtr) : null;
        srvs[1] = depthSrv;
        srvs[2] = uiLayerSrv;
        if (titleSrvPtr != IntPtr.Zero) System.Runtime.InteropServices.Marshal.AddRef(titleSrvPtr);
        srvs[3] = titleSrvPtr != IntPtr.Zero ? new ID3D11ShaderResourceView(titleSrvPtr) : null;
        if (transparentUiSrvPtr != IntPtr.Zero) System.Runtime.InteropServices.Marshal.AddRef(transparentUiSrvPtr);
        srvs[4] = transparentUiSrvPtr != IntPtr.Zero ? new ID3D11ShaderResourceView(transparentUiSrvPtr) : null;
        if (gbuffer2SrvPtr != IntPtr.Zero) System.Runtime.InteropServices.Marshal.AddRef(gbuffer2SrvPtr);
        srvs[5] = gbuffer2SrvPtr != IntPtr.Zero ? new ID3D11ShaderResourceView(gbuffer2SrvPtr) : null;
        if (gbuffer3SrvPtr != IntPtr.Zero) System.Runtime.InteropServices.Marshal.AddRef(gbuffer3SrvPtr);
        srvs[6] = gbuffer3SrvPtr != IntPtr.Zero ? new ID3D11ShaderResourceView(gbuffer3SrvPtr) : null;
        
        _context.PSSetShaderResources(0, 7, srvs);
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
        _context.PSSetShaderResource(3, (ID3D11ShaderResourceView)null);
        _context.PSSetShaderResource(4, (ID3D11ShaderResourceView)null);
        
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
      _device?.Dispose();
      _context?.Dispose();
      _device = null;
      _context = null;
    }
  }
}

