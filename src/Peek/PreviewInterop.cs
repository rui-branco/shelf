using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Shelf
{
    /// <summary>
    /// The COM side of Windows preview handlers: the same components Explorer's
    /// preview pane hosts for PDFs, Office documents, text, code and media.
    /// Hosting them is what lets Quick Look show nearly anything without
    /// knowing a single file format itself.
    /// </summary>
    static class PreviewInterop
    {
        const string PreviewHandlerIid = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
        public static readonly Guid IID_IShellItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");

        public const int STGM_READ = 0x0;
        public const int STGM_SHARE_DENY_NONE = 0x40;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;

            public RECT(Rectangle r)
            {
                Left = r.Left;
                Top = r.Top;
                Right = r.Right;
                Bottom = r.Bottom;
            }
        }

        [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPreviewHandler
        {
            void SetWindow(IntPtr hwnd, ref RECT rect);
            void SetRect(ref RECT rect);
            void DoPreview();
            void Unload();
            void SetFocus();
            void QueryFocus(out IntPtr phwnd);
            [PreserveSig] int TranslateAccelerator(IntPtr pmsg);
        }

        [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IInitializeWithFile
        {
            void Initialize([MarshalAs(UnmanagedType.LPWStr)] string path, int mode);
        }

        [ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IInitializeWithItem
        {
            void Initialize([MarshalAs(UnmanagedType.IUnknown)] object item, int mode);
        }

        [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IInitializeWithStream
        {
            void Initialize(IStream stream, int mode);
        }

        [ComImport, Guid("196bf9a5-b346-4ef0-aa1e-5dcdb76768b1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPreviewHandlerVisuals
        {
            void SetBackgroundColor(int color);
            void SetFont(IntPtr logfont);
            void SetTextColor(int color);
        }

        /// <summary>
        /// The preview handler Windows has registered for a file, the same one
        /// Explorer's preview pane would use. False when there is none.
        /// </summary>
        public static bool TryGetHandler(string path, out Guid clsid)
        {
            clsid = Guid.Empty;
            string ext = System.IO.Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return false;

            StringBuilder sb = new StringBuilder(64);
            int len = sb.Capacity;
            // ASSOCF_INIT_DEFAULTTOSTAR, ASSOCSTR_SHELLEXTENSION
            if (AssocQueryString(0x4, 16, ext, PreviewHandlerIid, sb, ref len) != 0) return false;

            try
            {
                clsid = new Guid(sb.ToString());
                return clsid != Guid.Empty;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>A COLORREF, the 0x00BBGGRR the handlers take.</summary>
        public static int ColorRef(Color c)
        {
            return c.R | (c.G << 8) | (c.B << 16);
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int AssocQueryString(int flags, int str, string assoc, string extra,
                                           StringBuilder outBuf, ref int outLen);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        public static extern void SHCreateItemFromParsingName(string path, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object item);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        public static extern void SHCreateStreamOnFileEx(string file, int mode, int attributes,
            bool create, IntPtr template, out IStream stream);
    }
}
