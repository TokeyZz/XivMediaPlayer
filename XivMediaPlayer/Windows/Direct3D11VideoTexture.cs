using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace XivMediaPlayer.Windows {
    public unsafe class Direct3D11VideoTexture : IDisposable {
        private ID3D11Device _device;
        private ID3D11DeviceContext _context;
        private ID3D11Texture2D _texture;
        private ID3D11ShaderResourceView _srv;
        private bool _disposed;
        
        public int Width { get; private set; }
        public int Height { get; private set; }
        
        // This is exactly what ImGui.Image and WorldVideoRenderer expect
        public IntPtr ImGuiHandle => _srv != null ? _srv.NativePointer : IntPtr.Zero;

        public Direct3D11VideoTexture(int width, int height) {
            Width = width;
            Height = height;
            
            var ffxivDevice = Device.Instance();
            if (ffxivDevice == null || ffxivDevice->D3D11DeviceContext == null) return;
            
            var contextPtr = (IntPtr)ffxivDevice->D3D11DeviceContext;
            System.Runtime.InteropServices.Marshal.AddRef(contextPtr);
            _context = new ID3D11DeviceContext(contextPtr);
            System.Runtime.InteropServices.Marshal.AddRef(_context.Device.NativePointer);
            _device = _context.Device;
            
            var desc = new Texture2DDescription {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };
            
            _texture = _device.CreateTexture2D(desc);
            _srv = _device.CreateShaderResourceView(_texture);
        }

        public void Update(byte[] rawData, int width, int height) {
            if (_disposed || _context == null || _texture == null || rawData == null) return;
            
            unsafe {
                fixed (byte* ptr = rawData) {
                    _context.UpdateSubresource(_texture, 0, null, (IntPtr)ptr, width * 4, width * height * 4);
                }
            }
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            
            _srv?.Dispose();
            _texture?.Dispose();
            _context?.Dispose();
            
            _device = null;
            _context = null;
            _srv = null;
            _texture = null;
        }
    }
}
