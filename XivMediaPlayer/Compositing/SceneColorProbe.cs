using System;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace XivMediaPlayer.Compositing {
  /// <summary>
  /// Diagnostic probe: enumerates RTM color render targets and SwapChain 
  /// back buffer to find textures usable for scene-vs-UI differencing.
  /// </summary>
  internal static unsafe class SceneColorProbe {
    public static string ProbeAllColorTextures() {
      var info = "";
      try {
        var dev = Device.Instance();
        if (dev == null) return "Device.Instance() is null";

        // SwapChain back buffer
        var sc = dev->SwapChain;
        if (sc != null) {
          info += $"SwapChain: {sc->Width}x{sc->Height}\n";
          
          // Get back buffer from DXGI
          var dxgiPtr = (IntPtr)sc->DXGISwapChain;
          if (dxgiPtr != IntPtr.Zero) {
            var dxgiSC = new Vortice.DXGI.IDXGISwapChain(dxgiPtr);
            try {
              var bb = dxgiSC.GetBuffer<ID3D11Texture2D>(0);
              var bbDesc = bb.Description;
              info += $"  BackBuffer: {bbDesc.Width}x{bbDesc.Height}, {bbDesc.Format}, Bind={bbDesc.BindFlags}\n";
              bb.Dispose();
            } catch (Exception ex) {
              info += $"  BackBuffer error: {ex.Message}\n";
            }
          }

          // SwapChain depth stencil format
          var scDepth = sc->DepthStencil;
          if (scDepth != null && scDepth->D3D11Texture2D != null) {
            var d3d = new ID3D11Texture2D((IntPtr)scDepth->D3D11Texture2D);
            var desc = d3d.Description;
            info += $"  DepthStencil: {desc.Width}x{desc.Height}, {desc.Format}, Bind={desc.BindFlags}\n";
          }
        }

        info += "\n";

        // RenderTargetManager textures
        var rtm = RenderTargetManager.Instance();
        if (rtm == null) {
          info += "RTM: null\n";
          return info;
        }

        // GBuffers (0-4)
        string[] gBufferNames = { "Normal", "GBuffer1", "Diffuse", "GBuffer3", "GBuffer4" };
        for (int i = 0; i < 5; i++) {
          var tex = rtm->GBuffers[i].Value;
          if (tex != null && tex->D3D11Texture2D != null) {
            var d3d = new ID3D11Texture2D((IntPtr)tex->D3D11Texture2D);
            var desc = d3d.Description;
            info += $"GBuffer[{i}] ({gBufferNames[i]}): {desc.Width}x{desc.Height}, {desc.Format}, Bind={desc.BindFlags}\n";
          } else {
            info += $"GBuffer[{i}] ({gBufferNames[i]}): null\n";
          }
        }

        // Named textures
        info += ProbeTexture("DepthStencil", rtm->DepthStencil);

        // SemitransparentGBuffers
        for (int i = 0; i < 5; i++) {
          info += ProbeTexture($"SemiGBuffer[{i}]", rtm->SemitransparentGBuffers[i].Value);
        }

      } catch (Exception ex) {
        info += $"Error: {ex.Message}\n{ex.StackTrace}";
      }
      return info;
    }

    private static string ProbeTexture(string name, Texture* tex) {
      if (tex == null) return $"{name}: null\n";
      if (tex->D3D11Texture2D == null) return $"{name}: no D3D tex\n";
      var d3d = new ID3D11Texture2D((IntPtr)tex->D3D11Texture2D);
      var desc = d3d.Description;
      return $"{name}: {desc.Width}x{desc.Height}, {desc.Format}, Bind={desc.BindFlags}\n";
    }

    public static unsafe IntPtr GetToneAdjustSourceSrvPtr() {
        // ToneAdjustSource is no longer used by Dawntrail and will overwrite the UI mask with zeros!
        return IntPtr.Zero;
    }
  }
}
