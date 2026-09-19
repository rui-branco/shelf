using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Shelf
{
    /// <summary>
    /// Shell interop: Downloads folder path, file thumbnails, and Recycle Bin.
    /// </summary>
    public static class Shell
    {
        #region Downloads folder

        static readonly Guid FOLDERID_Downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetKnownFolderPath(
            [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
            uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

        /// <summary>
        /// Returns the user's Downloads folder. Falls back to %USERPROFILE%\Downloads
        /// if the shell call fails.
        /// </summary>
        public static string GetDownloadsFolder()
        {
            IntPtr path = IntPtr.Zero;
            try
            {
                if (SHGetKnownFolderPath(FOLDERID_Downloads, 0, IntPtr.Zero, out path) == 0)
                {
                    return Marshal.PtrToStringUni(path);
                }
            }
            catch { }
            finally
            {
                if (path != IntPtr.Zero) Marshal.FreeCoTaskMem(path);
            }

            // Fallback
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(profile, "Downloads");
        }

        #endregion

        #region Thumbnails

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(SIZE size, int flags, out IntPtr hbmp);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE
        {
            public int cx, cy;
            public SIZE(int w, int h) { cx = w; cy = h; }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(
            string pszPath, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        static extern int GetObject(IntPtr hObject, int nCount, ref BITMAP lpObject);

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAP
        {
            public int bmType, bmWidth, bmHeight, bmWidthBytes;
            public short bmPlanes, bmBitsPixel;
            public IntPtr bmBits;
        }

        // Fallback: SHGetFileInfo for icons
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        const uint SHGFI_ICON = 0x100;
        const uint SHGFI_LARGEICON = 0x0;

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>
        /// Gets a thumbnail for a file. The returned bitmap has proper alpha.
        /// Returns null on failure.
        /// </summary>
        public static Bitmap GetThumbnail(string path, int size)
        {
            IntPtr hbmp = IntPtr.Zero;
            try
            {
                IShellItemImageFactory factory;
                SHCreateItemFromParsingName(path, IntPtr.Zero,
                    typeof(IShellItemImageFactory).GUID, out factory);

                // SIIGBF_RESIZETOFIT = 0
                int hr = factory.GetImage(new SIZE(size, size), 0, out hbmp);
                if (hr == 0 && hbmp != IntPtr.Zero)
                {
                    Bitmap result = BitmapFromHbitmapWithAlpha(hbmp);
                    return result;
                }
            }
            catch { }
            finally
            {
                if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            }

            // Fallback to file icon
            return GetFileIcon(path);
        }

        /// <summary>
        /// Bitmap.FromHbitmap drops the alpha channel, leaving icon corners black.
        /// This copies the bits manually and restores alpha.
        /// </summary>
        static Bitmap BitmapFromHbitmapWithAlpha(IntPtr hbmp)
        {
            BITMAP bm = new BITMAP();
            GetObject(hbmp, Marshal.SizeOf(typeof(BITMAP)), ref bm);

            if (bm.bmWidth == 0 || bm.bmHeight == 0 || bm.bmBitsPixel != 32)
            {
                // Not 32bpp, fall back to simple conversion
                Bitmap simple = Image.FromHbitmap(hbmp);
                return simple;
            }

            int w = bm.bmWidth;
            int h = bm.bmHeight;
            int stride = bm.bmWidthBytes;
            int byteCount = stride * h;

            byte[] bits = new byte[byteCount];
            Marshal.Copy(bm.bmBits, bits, 0, byteCount);

            // Check if all alpha bytes are zero - means the source was really opaque
            // and we should force them to 255.
            bool allZeroAlpha = true;
            for (int i = 3; i < bits.Length; i += 4)
            {
                if (bits[i] != 0)
                {
                    allZeroAlpha = false;
                    break;
                }
            }

            if (allZeroAlpha)
            {
                for (int i = 3; i < bits.Length; i += 4)
                {
                    bits[i] = 255;
                }
            }

            Bitmap result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData bd = result.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            // The HBITMAP is bottom-up, our Bitmap is top-down, so flip rows.
            for (int y = 0; y < h; y++)
            {
                int srcOffset = (h - 1 - y) * stride;
                IntPtr destRow = new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride);
                Marshal.Copy(bits, srcOffset, destRow, w * 4);
            }

            result.UnlockBits(bd);
            return result;
        }

        static Bitmap GetFileIcon(string path)
        {
            SHFILEINFO shfi = new SHFILEINFO();
            IntPtr hr = SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf(shfi),
                SHGFI_ICON | SHGFI_LARGEICON);
            if (hr == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;

            try
            {
                Icon ico = Icon.FromHandle(shfi.hIcon);
                Bitmap bmp = ico.ToBitmap();
                return bmp;
            }
            finally
            {
                DestroyIcon(shfi.hIcon);
            }
        }

        #endregion

        #region Recycle Bin

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        const uint FO_DELETE = 0x0003;
        const ushort FOF_ALLOWUNDO = 0x0040;
        const ushort FOF_NOCONFIRMATION = 0x0010;
        const ushort FOF_SILENT = 0x0004;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        /// <summary>
        /// Sends a file to the Recycle Bin. Returns true on success.
        /// </summary>
        public static bool RecycleFile(string path)
        {
            try
            {
                SHFILEOPSTRUCT op = new SHFILEOPSTRUCT();
                op.wFunc = FO_DELETE;
                // pFrom must be double-null-terminated
                op.pFrom = path + "\0\0";
                op.fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT;
                int result = SHFileOperation(ref op);
                return result == 0 && !op.fAnyOperationsAborted;
            }
            catch
            {
                return false;
            }
        }

        const uint FO_MOVE = 0x0001;
        const uint FO_COPY = 0x0002;
        const ushort FOF_NOCONFIRMMKDIR = 0x0200;

        /// <summary>
        /// Moves or copies files into a destination folder using SHFileOperation.
        /// This lets Windows handle name collisions with its own UI and provides
        /// undo support via FOF_ALLOWUNDO. Returns true on success.
        /// </summary>
        public static bool MoveInto(string[] sources, string destFolder, bool copy)
        {
            if (sources == null || sources.Length == 0) return false;
            if (string.IsNullOrEmpty(destFolder)) return false;

            try
            {
                // Build double-null-terminated source list
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                foreach (string src in sources)
                {
                    sb.Append(src);
                    sb.Append('\0');
                }
                sb.Append('\0');

                SHFILEOPSTRUCT op = new SHFILEOPSTRUCT();
                op.wFunc = copy ? FO_COPY : FO_MOVE;
                op.pFrom = sb.ToString();
                // pTo must also be double-null-terminated
                op.pTo = destFolder + "\0\0";
                // FOF_ALLOWUNDO for undo, FOF_NOCONFIRMMKDIR to auto-create subdirs.
                // Deliberately NOT passing FOF_NOCONFIRMATION so Windows shows its
                // own collision/progress dialogs and provides a proper undo stack.
                op.fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR;

                int result = SHFileOperation(ref op);
                return result == 0 && !op.fAnyOperationsAborted;
            }
            catch
            {
                return false;
            }
        }

        #endregion
    }
}
