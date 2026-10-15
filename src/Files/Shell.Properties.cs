using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Shelf
{
    public static partial class Shell
    {
        /// <summary>
        /// Windows' own Properties sheet, exactly as Explorer shows it: the
        /// usual sheet for one file, and for several in the same folder the
        /// single combined sheet Explorer shows for a multi-selection. True if
        /// a sheet went up. The sheet is modeless and outlives the panel.
        /// </summary>
        public static bool ShowProperties(string[] paths)
        {
            if (paths == null || paths.Length == 0) return false;
            if (paths.Length > 1 && ShowCombinedProperties(paths)) return true;
            return ShowFileProperties(paths[0]);
        }

        static bool ShowFileProperties(string path)
        {
            try
            {
                SHELLEXECUTEINFO sei = new SHELLEXECUTEINFO();
                sei.cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO));
                sei.fMask = SEE_MASK_INVOKEIDLIST;
                sei.lpVerb = "properties";
                sei.lpFile = path;
                sei.nShow = 1;      // SW_SHOWNORMAL
                return ShellExecuteEx(ref sei);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// SHMultiFileProperties wants a data object of item IDs relative to
        /// their common folder, which is the one thing a plain path list cannot
        /// give it - hence the PIDL work.
        /// </summary>
        static bool ShowCombinedProperties(string[] paths)
        {
            string dir = Path.GetDirectoryName(paths[0]);
            foreach (string p in paths)
            {
                if (!string.Equals(Path.GetDirectoryName(p), dir, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            IntPtr folder = IntPtr.Zero;
            IntPtr[] full = new IntPtr[paths.Length];
            IntPtr dataObject = IntPtr.Zero;
            try
            {
                uint attrs;
                if (SHParseDisplayName(dir, IntPtr.Zero, out folder, 0, out attrs) != 0) return false;

                IntPtr[] children = new IntPtr[paths.Length];
                for (int i = 0; i < paths.Length; i++)
                {
                    if (SHParseDisplayName(paths[i], IntPtr.Zero, out full[i], 0, out attrs) != 0) return false;
                    children[i] = ILFindLastID(full[i]);    // points into full[i], freed with it
                }

                Guid iid = IID_IDataObject;
                if (SHCreateDataObject(folder, (uint)children.Length, children, IntPtr.Zero, ref iid, out dataObject) != 0)
                    return false;
                return SHMultiFileProperties(dataObject, 0) == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (dataObject != IntPtr.Zero) Marshal.Release(dataObject);
                foreach (IntPtr p in full)
                    if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
                if (folder != IntPtr.Zero) Marshal.FreeCoTaskMem(folder);
            }
        }

        static readonly Guid IID_IDataObject = new Guid("0000010e-0000-0000-C000-000000000046");
        const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public string lpVerb;
            public string lpFile;
            public string lpParameters;
            public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl,
                                             uint sfgaoIn, out uint sfgaoOut);

        [DllImport("shell32.dll")]
        static extern IntPtr ILFindLastID(IntPtr pidl);

        [DllImport("shell32.dll")]
        static extern int SHCreateDataObject(IntPtr pidlFolder, uint cidl, IntPtr[] apidl,
                                             IntPtr pdtInner, ref Guid riid, out IntPtr ppv);

        [DllImport("shell32.dll")]
        static extern int SHMultiFileProperties(IntPtr pdtobj, uint flags);
    }
}
