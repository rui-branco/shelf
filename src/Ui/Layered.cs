using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Shelf
{
    /// <summary>
    /// The drawing surface behind a layered window: a 32-bit premultiplied DIB
    /// that GDI+ draws straight into and UpdateLayeredWindow presents as is.
    ///
    /// The windows used to build a fresh Bitmap for every frame and convert it
    /// with GetHbitmap, which allocates and copies the whole image again. For
    /// the pill that was waste; for the panel, re-rendered on every hover
    /// change, it was the lag. The buffer here lives as long as its size does,
    /// and premultiplied ARGB is already the format UpdateLayeredWindow wants,
    /// so presenting a frame copies nothing.
    /// </summary>
    public sealed class LayeredSurface : IDisposable
    {
        IntPtr _memDC;
        IntPtr _hBmp;
        IntPtr _oldBmp;
        Bitmap _bitmap;
        int _w, _h;

        public int Width { get { return _w; } }
        public int Height { get { return _h; } }

        /// <summary>
        /// Starts a frame of the given size, cleared to transparent. The caller
        /// disposes the Graphics before calling Present.
        /// </summary>
        public Graphics Begin(int w, int h)
        {
            Ensure(Math.Max(1, w), Math.Max(1, h));
            Graphics g = Graphics.FromImage(_bitmap);
            g.Clear(Color.Transparent);
            return g;
        }

        /// <summary>
        /// Puts the current frame on screen at <paramref name="pos"/>. Note that
        /// this also moves the window there: UpdateLayeredWindow positions as
        /// well as paints.
        /// </summary>
        public bool Present(IntPtr hwnd, Point pos, byte alpha)
        {
            return Present(hwnd, pos, new Size(_w, _h), alpha);
        }

        /// <summary>
        /// Presents only the top-left <paramref name="size"/> of the frame.
        /// An animation that changes size every frame draws into one buffer
        /// sized for its largest frame instead of reallocating each time.
        /// </summary>
        public bool Present(IntPtr hwnd, Point pos, Size size, byte alpha)
        {
            if (_bitmap == null || hwnd == IntPtr.Zero) return false;

            GdiFlush();

            POINT dst;
            dst.X = pos.X;
            dst.Y = pos.Y;
            POINT src;
            src.X = 0;
            src.Y = 0;
            SIZE sz;
            sz.cx = Math.Min(size.Width, _w);
            sz.cy = Math.Min(size.Height, _h);

            BLENDFUNCTION blend;
            blend.BlendOp = 0;                 // AC_SRC_OVER
            blend.BlendFlags = 0;
            blend.SourceConstantAlpha = alpha;
            blend.AlphaFormat = 1;             // AC_SRC_ALPHA

            // A null destination DC means the default palette, which is all a
            // 32-bit desktop has ever needed.
            return UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dst, ref sz,
                                       _memDC, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }

        void Ensure(int w, int h)
        {
            if (_bitmap != null && w == _w && h == _h) return;
            Free();

            BITMAPINFO bmi = new BITMAPINFO();
            bmi.biSize = 40;                   // sizeof(BITMAPINFOHEADER), without the colour table
            bmi.biWidth = w;
            bmi.biHeight = -h;                 // top-down, the way GDI+ lays rows out
            bmi.biPlanes = 1;
            bmi.biBitCount = 32;

            IntPtr bits;
            _hBmp = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out bits, IntPtr.Zero, 0);
            if (_hBmp == IntPtr.Zero || bits == IntPtr.Zero)
                throw new OutOfMemoryException("CreateDIBSection failed for " + w + "x" + h);

            _memDC = CreateCompatibleDC(IntPtr.Zero);
            _oldBmp = SelectObject(_memDC, _hBmp);
            _bitmap = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
            _w = w;
            _h = h;
        }

        void Free()
        {
            if (_bitmap != null) { _bitmap.Dispose(); _bitmap = null; }
            if (_memDC != IntPtr.Zero)
            {
                if (_oldBmp != IntPtr.Zero) SelectObject(_memDC, _oldBmp);
                DeleteDC(_memDC);
                _memDC = IntPtr.Zero;
                _oldBmp = IntPtr.Zero;
            }
            if (_hBmp != IntPtr.Zero) { DeleteObject(_hBmp); _hBmp = IntPtr.Zero; }
            _w = _h = 0;
        }

        public void Dispose()
        {
            Free();
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
            public int bmiColors;
        }

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, int usage,
                                              out IntPtr bits, IntPtr section, int offset);

        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool GdiFlush();

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT dst,
            ref SIZE size, IntPtr hdcSrc, ref POINT src, int key,
            ref BLENDFUNCTION blend, int flags);
    }
}
