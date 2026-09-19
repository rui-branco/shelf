using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Shelf
{
    /// <summary>
    /// Attaches a drag image to a data object, so dragging a file out of the
    /// stack carries that file's icon under the cursor instead of the bare
    /// move/copy arrow.
    ///
    /// This is the shell's own mechanism - the same one Explorer uses - rather
    /// than a window we drag around ourselves. IDragSourceHelper takes an
    /// HBITMAP and the IDataObject about to be dragged, and from then on the
    /// shell renders and moves the image for us, including the drop-description
    /// text ("Move to Desktop") that target applications supply.
    ///
    /// The bitmap must be 32-bit premultiplied ARGB. Handing it straight-alpha
    /// pixels leaves a dark halo around every antialiased edge, which is the
    /// usual reason this ends up looking worse than no drag image at all.
    /// </summary>
    public static class DragImage
    {
        static readonly Guid CLSID_DragDropHelper =
            new Guid("4657278A-411B-11D2-839A-00C04FD918D0");

        [ComImport]
        [Guid("DE5BF786-477A-11D2-839D-00C04FD918D0")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDragSourceHelper
        {
            void InitializeFromBitmap(ref SHDRAGIMAGE pshdi,
                [MarshalAs(UnmanagedType.Interface)] object pDataObject);
            void InitializeFromWindow(IntPtr hwnd, ref POINT ppt,
                [MarshalAs(UnmanagedType.Interface)] object pDataObject);
        }

        [ComImport]
        [Guid("83E07D0D-0C5F-4163-BF1A-60B274051E40")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDragSourceHelper2
        {
            void InitializeFromBitmap(ref SHDRAGIMAGE pshdi,
                [MarshalAs(UnmanagedType.Interface)] object pDataObject);
            void InitializeFromWindow(IntPtr hwnd, ref POINT ppt,
                [MarshalAs(UnmanagedType.Interface)] object pDataObject);
            void SetFlags(int dwFlags);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SHDRAGIMAGE
        {
            public SIZE sizeDragImage;
            public POINT ptOffset;
            public IntPtr hbmpDragImage;
            public int crColorKey;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int x, y; }

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, int usage,
                                              out IntPtr bits, IntPtr section, int offset);

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
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
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public int bmiColors;
        }

        const int BI_RGB = 0;
        const int DIB_RGB_COLORS = 0;

        /// <summary>
        /// Builds a 32-bit top-down DIB section holding premultiplied ARGB.
        ///
        /// Bitmap.GetHbitmap cannot be used here: it hands back a
        /// device-dependent bitmap with no alpha channel at all, so the shell
        /// composites whatever happens to be in the unused byte and the drag
        /// image comes out transparent or garbled. A DIB section is the only
        /// way to give it real per-pixel alpha.
        /// </summary>
        static IntPtr CreateAlphaBitmap(Bitmap src)
        {
            int w = src.Width;
            int h = src.Height;

            BITMAPINFO bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h;      // negative: top-down, matching GDI+
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = BI_RGB;

            IntPtr bits;
            IntPtr hbmp = CreateDIBSection(IntPtr.Zero, ref bmi, DIB_RGB_COLORS,
                                           out bits, IntPtr.Zero, 0);
            if (hbmp == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;

            Rectangle rect = new Rectangle(0, 0, w, h);
            BitmapData data = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = w * 4;
                byte[] row = new byte[rowBytes];
                for (int y = 0; y < h; y++)
                {
                    IntPtr srcRow = new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride);
                    Marshal.Copy(srcRow, row, 0, rowBytes);

                    // Premultiply in place. Straight alpha here is what leaves a
                    // dark ring around every antialiased edge.
                    for (int i = 0; i < rowBytes; i += 4)
                    {
                        int a = row[i + 3];
                        if (a == 255) continue;
                        row[i] = (byte)(row[i] * a / 255);
                        row[i + 1] = (byte)(row[i + 1] * a / 255);
                        row[i + 2] = (byte)(row[i + 2] * a / 255);
                    }

                    IntPtr dstRow = new IntPtr(bits.ToInt64() + (long)y * rowBytes);
                    Marshal.Copy(row, 0, dstRow, rowBytes);
                }
            }
            finally { src.UnlockBits(data); }

            return hbmp;
        }

        const int DSH_ALLOWDROPDESCRIPTIONTEXT = 0x0001;

        /// <summary>
        /// Attaches <paramref name="icon"/> to <paramref name="dataObject"/> as
        /// the drag image. Best effort: any failure just leaves the default
        /// cursor, which is what we had before. The shell takes ownership of the
        /// HBITMAP once initialization succeeds, so it is only deleted on the
        /// failure paths.
        /// </summary>
        public static void Attach(object dataObject, Bitmap icon, int cursorOffsetX, int cursorOffsetY)
        {
            if (dataObject == null || icon == null) return;

            IntPtr hbmp = IntPtr.Zero;
            object helper = null;

            try
            {
                using (Bitmap normalized = Normalize(icon))
                {
                    hbmp = CreateAlphaBitmap(normalized);
                    if (hbmp == IntPtr.Zero) return;

                    SHDRAGIMAGE shdi = new SHDRAGIMAGE();
                    shdi.sizeDragImage.cx = normalized.Width;
                    shdi.sizeDragImage.cy = normalized.Height;
                    shdi.ptOffset.x = cursorOffsetX;
                    shdi.ptOffset.y = cursorOffsetY;
                    shdi.hbmpDragImage = hbmp;
                    shdi.crColorKey = -1;   // CLR_NONE: alpha decides, not a key colour

                    Type t = Type.GetTypeFromCLSID(CLSID_DragDropHelper);
                    if (t == null) { DeleteObject(hbmp); return; }
                    helper = Activator.CreateInstance(t);

                    // Prefer the v2 interface purely for the drop-description
                    // text; fall back when it is unavailable.
                    IDragSourceHelper2 h2 = helper as IDragSourceHelper2;
                    if (h2 != null)
                    {
                        try { h2.SetFlags(DSH_ALLOWDROPDESCRIPTIONTEXT); }
                        catch { }
                        h2.InitializeFromBitmap(ref shdi, dataObject);
                    }
                    else
                    {
                        IDragSourceHelper h1 = helper as IDragSourceHelper;
                        if (h1 == null) { DeleteObject(hbmp); return; }
                        h1.InitializeFromBitmap(ref shdi, dataObject);
                    }
                }
            }
            catch
            {
                // Losing the drag image is not worth losing the drag over.
                if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            }
            finally
            {
                if (helper != null && Marshal.IsComObject(helper))
                    Marshal.ReleaseComObject(helper);
            }
        }

        /// <summary>
        /// Redraws the icon into a known 32bpp ARGB surface. Shell thumbnails
        /// come back in assorted pixel formats, and CreateAlphaBitmap locks for
        /// Format32bppArgb - handing it something else makes GDI+ convert
        /// behind our back, which is where stray alpha comes from.
        /// </summary>
        static Bitmap Normalize(Bitmap src)
        {
            Bitmap dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.Clear(Color.Transparent);
                g.DrawImageUnscaled(src, 0, 0);
            }
            return dst;
        }
    }
}
