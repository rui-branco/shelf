using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Shelf
{
    /// <summary>
    /// The pixel work behind the panel's material: a frosted copy of the screen
    /// behind it, the soft shadow around it, and a little grain.
    ///
    /// The frost is a snapshot taken before the window is shown, so it will
    /// not track content moving behind the panel. For something open a few
    /// seconds at a time that is fine, and it costs nothing while it is open.
    /// </summary>
    public static class Blur
    {
        [DllImport("gdi32.dll")]
        static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height,
                                  IntPtr hdcSrc, int xSrc, int ySrc, int rop);

        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

        const int SRCCOPY = 0x00CC0020;

        /// <summary>
        /// Captures a rectangle from the screen. Call this BEFORE the panel is
        /// shown so the panel is not in its own capture.
        /// </summary>
        public static Bitmap CaptureBehind(Rectangle screenRect)
        {
            if (screenRect.Width <= 0 || screenRect.Height <= 0)
                return null;

            Bitmap bmp = new Bitmap(screenRect.Width, screenRect.Height, PixelFormat.Format32bppArgb);
            IntPtr screenDC = GetDC(IntPtr.Zero);
            try
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try
                    {
                        BitBlt(hdc, 0, 0, screenRect.Width, screenRect.Height,
                               screenDC, screenRect.X, screenRect.Y, SRCCOPY);
                    }
                    finally { g.ReleaseHdc(hdc); }
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDC);
            }
            return bmp;
        }

        /// <summary>
        /// A frosted copy of <paramref name="src"/>: blurred hard enough that
        /// text behind it becomes colour and light, and a little desaturated,
        /// the way Windows' acrylic is.
        ///
        /// The blur runs on a quarter-size copy (a proper three-pass box blur,
        /// which is close to gaussian) and is scaled back up from there. The
        /// old version only downscaled and upscaled with nothing in between,
        /// which left blocky, diamond-shaped artifacts at the sizes it needed.
        /// </summary>
        public static Bitmap Frost(Bitmap src)
        {
            if (src == null) return null;

            int w = src.Width, h = src.Height;
            int sw = Math.Max(1, w / 4), sh = Math.Max(1, h / 4);

            using (Bitmap small = new Bitmap(sw, sh, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(src, new Rectangle(0, 0, sw, sh));
                }

                int[] px = Read(small);
                int[] r = new int[px.Length], gr = new int[px.Length], b = new int[px.Length];
                for (int i = 0; i < px.Length; i++)
                {
                    int c = px[i];
                    r[i] = (c >> 16) & 0xFF;
                    gr[i] = (c >> 8) & 0xFF;
                    b[i] = c & 0xFF;
                }

                int radius = 5;
                BoxBlur(r, sw, sh, radius);
                BoxBlur(gr, sw, sh, radius);
                BoxBlur(b, sw, sh, radius);

                for (int i = 0; i < px.Length; i++)
                {
                    // 25% towards grey: colour still comes through, but a
                    // saturated window behind does not shout through the panel.
                    int gray = (r[i] * 77 + gr[i] * 150 + b[i] * 29) >> 8;
                    int rr = r[i] + (gray - r[i]) / 4;
                    int gg = gr[i] + (gray - gr[i]) / 4;
                    int bb = b[i] + (gray - b[i]) / 4;
                    px[i] = (255 << 24) | (rr << 16) | (gg << 8) | bb;
                }
                Write(small, px);

                Bitmap large = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(large))
                using (ImageAttributes ia = new ImageAttributes())
                {
                    // Mirror at the edges so the bilinear upscale has real
                    // pixels to blend with there instead of fading to nothing.
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(small, new Rectangle(0, 0, w, h), 0, 0, sw, sh, GraphicsUnit.Pixel, ia);
                }
                return large;
            }
        }

        /// <summary>
        /// A soft drop shadow for a rounded rectangle, as a bitmap the size of
        /// the whole window (panel plus margin). Worked out at half resolution:
        /// a shadow has no detail to lose, and the blur costs a quarter as much.
        /// </summary>
        public static Bitmap Shadow(Size window, RectangleF panel, float radius, float blur, float offsetY, int alpha)
        {
            int w = Math.Max(1, window.Width / 2), h = Math.Max(1, window.Height / 2);

            using (Bitmap mask = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(mask))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    RectangleF r = new RectangleF(panel.X / 2, (panel.Y + offsetY) / 2, panel.Width / 2, panel.Height / 2);
                    Theme.FillRounded(g, Color.Black, r, radius / 2);
                }

                int[] px = Read(mask);
                int[] a = new int[px.Length];
                for (int i = 0; i < px.Length; i++) a[i] = (px[i] >> 24) & 0xFF;

                BoxBlur(a, w, h, Math.Max(1, (int)Math.Round(blur / 2 / 2)));

                for (int i = 0; i < px.Length; i++)
                    px[i] = (a[i] * alpha / 255) << 24;   // black, varying alpha
                Write(mask, px);

                Bitmap full = new Bitmap(window.Width, window.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(full))
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(mask, new Rectangle(0, 0, window.Width, window.Height), 0, 0, w, h, GraphicsUnit.Pixel, ia);
                }
                return full;
            }
        }

        static Bitmap _noise;

        /// <summary>
        /// A tile of faint grain. Acrylic has it for a reason: over a smooth
        /// blur it breaks up the banding a 32-bit gradient otherwise shows.
        /// </summary>
        public static Bitmap Noise()
        {
            if (_noise != null) return _noise;

            const int n = 96;
            Random rnd = new Random(7);
            int[] px = new int[n * n];
            for (int i = 0; i < px.Length; i++)
            {
                int v = rnd.Next(256);
                px[i] = (7 << 24) | (v << 16) | (v << 8) | v;
            }
            _noise = new Bitmap(n, n, PixelFormat.Format32bppArgb);
            Write(_noise, px);
            return _noise;
        }

        /// <summary>Three passes of a running box blur, each way: close enough to gaussian.</summary>
        static void BoxBlur(int[] ch, int w, int h, int r)
        {
            int[] tmp = new int[ch.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                BoxH(ch, tmp, w, h, r);
                BoxV(tmp, ch, w, h, r);
            }
        }

        static void BoxH(int[] src, int[] dst, int w, int h, int r)
        {
            int div = r * 2 + 1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int sum = 0;
                for (int i = -r; i <= r; i++) sum += src[row + Clamp(i, w)];
                for (int x = 0; x < w; x++)
                {
                    dst[row + x] = sum / div;
                    sum += src[row + Clamp(x + r + 1, w)] - src[row + Clamp(x - r, w)];
                }
            }
        }

        static void BoxV(int[] src, int[] dst, int w, int h, int r)
        {
            int div = r * 2 + 1;
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int i = -r; i <= r; i++) sum += src[Clamp(i, h) * w + x];
                for (int y = 0; y < h; y++)
                {
                    dst[y * w + x] = sum / div;
                    sum += src[Clamp(y + r + 1, h) * w + x] - src[Clamp(y - r, h) * w + x];
                }
            }
        }

        static int Clamp(int v, int n)
        {
            return v < 0 ? 0 : (v >= n ? n - 1 : v);
        }

        static int[] Read(Bitmap bmp)
        {
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int[] px = new int[bmp.Width * bmp.Height];
                for (int y = 0; y < bmp.Height; y++)
                    Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), px, y * bmp.Width, bmp.Width);
                return px;
            }
            finally { bmp.UnlockBits(d); }
        }

        static void Write(Bitmap bmp, int[] px)
        {
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < bmp.Height; y++)
                    Marshal.Copy(px, y * bmp.Width, new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), bmp.Width);
            }
            finally { bmp.UnlockBits(d); }
        }
    }
}
