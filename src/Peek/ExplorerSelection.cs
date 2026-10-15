using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Shelf
{
    /// <summary>
    /// What is selected in an Explorer window or on the desktop, read through
    /// the shell's own automation (ShellWindows, then each window's folder
    /// view), the documented way for another program to ask.
    /// </summary>
    static class ExplorerSelection
    {
        static readonly Guid CLSID_ShellWindows = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
        static readonly Guid SID_STopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        const int CSIDL_DESKTOP = 0;
        const int SWC_DESKTOP = 8;
        const int SWFO_NEEDDISPATCH = 1;

        /// <summary>
        /// The selected files and folders in <paramref name="window"/> (an
        /// Explorer frame, or the desktop), in the order the view gives them.
        /// Empty when nothing real is selected - virtual items such as This PC
        /// have no path to preview.
        /// </summary>
        public static List<string> Get(IntPtr window, bool desktop)
        {
            List<string> paths = new List<string>();
            object windows = null;
            object view = null;
            try
            {
                Type t = Type.GetTypeFromCLSID(CLSID_ShellWindows);
                windows = Activator.CreateInstance(t);
                IShellWindows sw = (IShellWindows)windows;

                view = desktop ? DesktopView(sw) : FrameView(sw, window);
                if (view == null) return paths;

                object items = Call(view, "SelectedItems");
                try
                {
                    int count = Convert.ToInt32(Prop(items, "Count"));
                    for (int i = 0; i < count && i < 500; i++)
                    {
                        object item = Call(items, "Item", i);
                        try
                        {
                            string p = Prop(item, "Path") as string;
                            if (!string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p))) paths.Add(p);
                        }
                        finally { Release(item); }
                    }
                }
                finally { Release(items); }
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
            finally
            {
                Release(view);
                Release(windows);
            }
            return paths;
        }

        /// <summary>
        /// Where the focused item's icon is on screen, from UI Automation: the
        /// list item the keyboard is on. Its rectangle covers icon and label,
        /// so this keeps the icon's square - the top of a tile in the icon
        /// views, the left end of a row in Details.
        /// </summary>
        public static Rectangle FocusedItemIcon()
        {
            try
            {
                AutomationElement el = AutomationElement.FocusedElement;
                if (el == null) return Rectangle.Empty;
                ControlType ct = el.Current.ControlType;
                if (ct != ControlType.ListItem && ct != ControlType.DataItem) return Rectangle.Empty;

                System.Windows.Rect b = el.Current.BoundingRectangle;
                if (b.IsEmpty || b.Width < 4 || b.Height < 4) return Rectangle.Empty;

                int x = (int)b.X, y = (int)b.Y, w = (int)b.Width, h = (int)b.Height;
                if (w > h * 1.6) return new Rectangle(x, y, h, h);            // a Details row
                if (h > w) return new Rectangle(x, y, w, w);                  // an icon over its label
                return new Rectangle(x, y, w, h);
            }
            catch
            {
                return Rectangle.Empty;
            }
        }

        static object DesktopView(IShellWindows sw)
        {
            object loc = CSIDL_DESKTOP;
            object root = null;
            int hwnd;
            object browser = sw.FindWindowSW(ref loc, ref root, SWC_DESKTOP, out hwnd, SWFO_NEEDDISPATCH);
            try { return browser == null ? null : Prop(browser, "Document"); }
            finally { Release(browser); }
        }

        /// <summary>
        /// The folder view of an Explorer frame. Windows 11 puts several tabs
        /// in one frame, each its own entry with the same window handle; the
        /// one that counts is the tab on show, whose ShellTabWindowClass is the
        /// first child of the frame.
        /// </summary>
        static object FrameView(IShellWindows sw, IntPtr frame)
        {
            IntPtr activeTab = FindWindowEx(frame, IntPtr.Zero, "ShellTabWindowClass", null);
            int n = sw.Count;
            for (int i = 0; i < n; i++)
            {
                object browser = null;
                try
                {
                    browser = sw.Item(i);
                    if (browser == null) continue;
                    if (new IntPtr(Convert.ToInt64(Prop(browser, "HWND"))) != frame) continue;
                    if (activeTab != IntPtr.Zero && !IsTab(browser, activeTab)) continue;
                    return Prop(browser, "Document");
                }
                catch
                {
                    // Windows come and go while we look; skip the one that went.
                }
                finally { Release(browser); }
            }
            return null;
        }

        static bool IsTab(object browser, IntPtr tab)
        {
            IComServiceProvider sp = browser as IComServiceProvider;
            if (sp == null) return true;

            Guid sid = SID_STopLevelBrowser;
            Guid iid = typeof(IShellBrowser).GUID;
            object shellBrowser;
            if (sp.QueryService(ref sid, ref iid, out shellBrowser) != 0 || shellBrowser == null) return true;
            try
            {
                IntPtr hwnd;
                return ((IShellBrowser)shellBrowser).GetWindow(out hwnd) != 0 || hwnd == tab;
            }
            finally { Release(shellBrowser); }
        }

        static object Prop(object o, string name)
        {
            return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null);
        }

        static object Call(object o, string name, params object[] args)
        {
            return o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args);
        }

        static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o))
            {
                try { Marshal.ReleaseComObject(o); }
                catch { }
            }
        }

        // ----------------------------------------------------------
        // Interop
        // ----------------------------------------------------------

        /// <summary>
        /// Dual interface, bound through its vtable. The middle methods are
        /// placeholders that only hold their slots so FindWindowSW lands on the
        /// right one; they are never called.
        /// </summary>
        [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        interface IShellWindows
        {
            int Count { get; }
            [return: MarshalAs(UnmanagedType.IDispatch)] object Item([In] object index);
            [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
            void Register();
            void RegisterPending();
            void Revoke();
            void OnNavigate();
            void OnActivated();
            [return: MarshalAs(UnmanagedType.IDispatch)]
            object FindWindowSW([In] ref object loc, [In] ref object locRoot, int swClass, out int hwnd, int options);
        }

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IComServiceProvider
        {
            [PreserveSig]
            int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
        }

        /// <summary>Only GetWindow, the first method after IUnknown, is ever called.</summary>
        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellBrowser
        {
            [PreserveSig]
            int GetWindow(out IntPtr hwnd);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
    }
}
