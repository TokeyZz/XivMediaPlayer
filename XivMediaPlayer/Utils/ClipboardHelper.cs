using System;
using System.Runtime.InteropServices;

namespace XivMediaPlayer.Utils {
    public static class ClipboardHelper {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll")]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr hMem);

        private const uint CF_DIB = 8;
        private const uint GMEM_MOVEABLE = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        /// <summary>
        /// Copies raw BGRA pixel data to the Windows clipboard as a Device Independent Bitmap (CF_DIB).
        /// </summary>
        /// <param name="width">Image width</param>
        /// <param name="height">Image height</param>
        /// <param name="bgraPixels">Raw byte array in BGRA format. Length must be width * height * 4</param>
        public static bool CopyBgraToClipboard(int width, int height, byte[] bgraPixels) {
            if (bgraPixels == null || bgraPixels.Length != width * height * 4)
                return false;

            int headerSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            int imageSize = width * height * 4;
            int totalSize = headerSize + imageSize;

            IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)totalSize);
            if (hGlobal == IntPtr.Zero)
                return false;

            IntPtr ptr = GlobalLock(hGlobal);
            if (ptr == IntPtr.Zero)
                return false;

            try {
                // Prepare the header
                BITMAPINFOHEADER bih = new BITMAPINFOHEADER {
                    biSize = (uint)headerSize,
                    biWidth = width,
                    biHeight = height, // Note: Positive height means bottom-up DIB. D3D11 is top-down, so we pass negative height. Wait, DIB expects negative for top-down!
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                    biSizeImage = (uint)imageSize,
                    biXPelsPerMeter = 0,
                    biYPelsPerMeter = 0,
                    biClrUsed = 0,
                    biClrImportant = 0
                };
                
                // DIB uses negative height for top-down image (which D3D/byte arrays usually are)
                bih.biHeight = -height;

                Marshal.StructureToPtr(bih, ptr, false);

                // Copy pixels
                IntPtr pixelPtr = new IntPtr(ptr.ToInt64() + headerSize);
                Marshal.Copy(bgraPixels, 0, pixelPtr, imageSize);

                // Set clipboard
                if (!OpenClipboard(IntPtr.Zero))
                    return false;

                EmptyClipboard();
                SetClipboardData(CF_DIB, hGlobal);
                CloseClipboard();

                return true;
            } finally {
                GlobalUnlock(hGlobal);
            }
        }
    }
}
