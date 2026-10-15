using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Shelf
{
    /// <summary>
    /// The process is per-monitor DPI aware (see Program), so every coordinate
    /// in Shelf is a real device pixel and windows draw at the monitor's true
    /// resolution. This only answers "how big is a logical pixel here".
    /// </summary>
    public static class Dpi
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

        [DllImport("shcore.dll")]
        static extern int SetProcessDpiAwareness(int value);

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [DllImport("shcore.dll")]
        static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dx, out uint dy);

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromPoint(POINT pt, int flags);

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        const int MONITOR_DEFAULTTONEAREST = 2;

        /// <summary>
        /// Opts the whole process into per-monitor (v2) awareness. Has to run
        /// before the first window exists. This replaces flipping the thread's
        /// awareness around individual calls, which left windows created in one
        /// mode being measured in the other - harmless at 100%, and wrong on
        /// any monitor that is scaled.
        /// </summary>
        public static void MakeProcessAware()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; }
            catch { }
            try { if (SetProcessDpiAwareness(2) == 0) return; }
            catch { }
            try { SetProcessDPIAware(); }
            catch { }
        }

        /// <summary>The scale factor for the monitor a window is on.</summary>
        public static float ScaleForWindow(IntPtr hwnd)
        {
            return ScaleFor(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));
        }

        /// <summary>The scale factor for the monitor at a screen point.</summary>
        public static float ScaleAt(Point p)
        {
            POINT pt;
            pt.X = p.X;
            pt.Y = p.Y;
            return ScaleFor(MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST));
        }

        static float ScaleFor(IntPtr mon)
        {
            if (mon != IntPtr.Zero)
            {
                try
                {
                    uint dx, dy;
                    if (GetDpiForMonitor(mon, 0 /* MDT_EFFECTIVE_DPI */, out dx, out dy) == 0 && dx > 0)
                        return dx / 96f;
                }
                catch { }
            }
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                return g.DpiX / 96f;
        }
    }
}
