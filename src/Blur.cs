using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Shelf
{
    /// <summary>
    /// Captures and blurs a screen region for use as a popup backdrop.
    /// The blur is a snapshot taken before the window is shown, so it will
    /// not track content moving behind the popup. This is fine and expected
    /// for a transient popup.
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

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hdc);

        const int SRCCOPY = 0x00CC0020;

        /// <summary>
        /// Captures a rectangle from the screen using BitBlt.
        /// Call this BEFORE the popup is shown so the popup is not in its own capture.
        /// </summary>
        public static Bitmap CaptureBehind(Rectangle screenRect)
        {
            if (screenRect.Width <= 0 || screenRect.Height <= 0)
                return null;

            Bitmap bmp = new Bitmap(screenRect.Width, screenRect.Height, PixelFormat.Format32bppArgb);
            IntPtr screenDC = GetDC(IntPtr.Zero);
            IntPtr memDC = CreateCompatibleDC(screenDC);
            IntPtr hBmp = CreateCompatibleBitmap(screenDC, screenRect.Width, screenRect.Height);
            IntPtr oldBmp = SelectObject(memDC, hBmp);

            try
            {
                BitBlt(memDC, 0, 0, screenRect.Width, screenRect.Height,
                       screenDC, screenRect.X, screenRect.Y, SRCCOPY);

                // Copy from the HBITMAP to our managed Bitmap
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    BitBlt(hdc, 0, 0, screenRect.Width, screenRect.Height, memDC, 0, 0, SRCCOPY);
                    g.ReleaseHdc(hdc);
                }
            }
            finally
            {
                SelectObject(memDC, oldBmp);
                DeleteObject(hBmp);
                DeleteDC(memDC);
                ReleaseDC(IntPtr.Zero, screenDC);
            }

            return bmp;
        }

        /// <summary>
        /// Produces a blur by downscaling and upscaling the image multiple times.
        /// This is cheap and gives a genuine gaussian-ish blur effect.
        /// </summary>
        /// <param name="src">Source bitmap to blur</param>
        /// <param name="passes">Number of down/up passes (2 is plenty)</param>
        public static Bitmap BoxBlur(Bitmap src, int passes)
        {
            if (src == null) return null;

            int w = src.Width;
            int h = src.Height;
            if (w <= 0 || h <= 0) return src;

            Bitmap current = src;
            bool ownsSource = false;

            for (int i = 0; i < passes; i++)
            {
                // Downscale hard. The smaller this intermediate, the wider the
                // effective blur radius when it is scaled back up - 1/8 still
                // left edges and text readable through the panel.
                int smallW = Math.Max(1, w / 16);
                int smallH = Math.Max(1, h / 16);

                Bitmap small = new Bitmap(smallW, smallH, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(current, 0, 0, smallW, smallH);
                }

                // Upscale back to full size
                Bitmap large = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(large))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(small, 0, 0, w, h);
                }

                small.Dispose();

                if (ownsSource && current != null)
                {
                    current.Dispose();
                }

                current = large;
                ownsSource = true;
            }

            return current;
        }
    }
}
