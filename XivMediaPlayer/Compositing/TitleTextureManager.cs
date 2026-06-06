using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace XivMediaPlayer.Compositing
{
    internal class TitleTextureManager : IDisposable
    {
        private readonly ITextureProvider _textureProvider;
        private IDalamudTextureWrap _textureWrap;
        private string _lastTitle = "";
        private string _lastStreamer = "";
        private bool _disposed = false;

        public unsafe IntPtr TextureHandle
        {
            get
            {
                if (_textureWrap == null) return IntPtr.Zero;
                var handle = _textureWrap.Handle;
                return *(IntPtr*)&handle;
            }
        }

        public TitleTextureManager(ITextureProvider textureProvider)
        {
            _textureProvider = textureProvider;
        }

        public void UpdateText(string title, string streamer)
        {
            if (_disposed) return;
            if (title == _lastTitle && streamer == _lastStreamer) return;

            _lastTitle = title ?? "";
            _lastStreamer = streamer ?? "";

            // Free the old texture if it exists
            _textureWrap?.Dispose();
            _textureWrap = null;

            if (string.IsNullOrEmpty(_lastTitle)) return;

            string displayText = _lastTitle;
            if (!string.IsNullOrEmpty(_lastStreamer) && _lastStreamer != _lastTitle)
            {
                displayText += $" - {_lastStreamer}";
            }

            // We render to a 1920x1080 canvas to match standard 16:9 ratio. 
            // This ensures it maps perfectly 1:1 with the VideoTexture UVs!
            int width = 1920;
            int height = 1080;

            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var gfx = Graphics.FromImage(bmp);
            
            // High quality text rendering
            gfx.SmoothingMode = SmoothingMode.HighQuality;
            gfx.PixelOffsetMode = PixelOffsetMode.HighQuality;
            gfx.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            
            // Fully transparent background
            gfx.Clear(Color.Transparent);

            // Use a clean, modern font
            using var font = new Font("Arial", 48, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.White);
            using var shadowBrush = new SolidBrush(Color.FromArgb(200, 0, 0, 0));

            // Measure text to center it at the top
            var stringFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Near
            };

            // Draw at the top, slightly padded
            var rect = new RectangleF(0, 40, width, height);
            
            // Draw a subtle dark shadow/outline for readability against bright videos
            gfx.DrawString(displayText, font, shadowBrush, new RectangleF(2, 42, width, height), stringFormat);
            gfx.DrawString(displayText, font, shadowBrush, new RectangleF(-2, 38, width, height), stringFormat);
            gfx.DrawString(displayText, font, shadowBrush, new RectangleF(2, 38, width, height), stringFormat);
            gfx.DrawString(displayText, font, shadowBrush, new RectangleF(-2, 42, width, height), stringFormat);
            
            // Draw the white text
            gfx.DrawString(displayText, font, brush, rect, stringFormat);

            // Extract the BGRA bytes
            var bmpData = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int bytes = Math.Abs(bmpData.Stride) * bmp.Height;
                byte[] rawData = new byte[bytes];
                Marshal.Copy(bmpData.Scan0, rawData, 0, bytes);

                // We can just use the BGRA byte array directly!
                _textureWrap = _textureProvider.CreateFromRaw(
                    Dalamud.Interface.Textures.RawImageSpecification.Bgra32(width, height),
                    rawData);
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _textureWrap?.Dispose();
            _textureWrap = null;
        }
    }
}
