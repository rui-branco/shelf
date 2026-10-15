using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Shelf
{
    /// <summary>Whether something fullscreen is in front, for the pill to get out of its way.</summary>
    public static class Fullscreen
    {
        /// <summary>
        /// True when the window the user is looking at fills the monitor the
        /// taskbar is on: a game, a video, a browser in F11, a presentation.
        ///
        /// Only that monitor counts. The first version hid the pill for any
        /// fullscreen window anywhere, so a video playing fullscreen on a second
        /// screen took the pill off the main one.
        ///
        /// Coordinates are real pixels throughout (the process is per-monitor
        /// DPI aware). Before, this ran DPI-unaware, so on a scaled monitor the
        /// window and monitor sizes were compared in two different units.
        /// </summary>
        public static bool IsActive(IntPtr monitorOwner, params IntPtr[] ignore)
        {
            try
            {
                int state;
                if (SHQueryUserNotificationState(out state) != 0) state = 0;

                // Presentation mode is a request to keep everything quiet.
                if (state == QUNS_PRESENTATION_MODE) return true;

                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                foreach (IntPtr own in ignore)
                {
                    if (fg == own) return false;
                }
                if (!IsWindowVisible(fg) || IsIconic(fg)) return false;
                if (IsShellWindow(fg) || IsOverlayWindow(fg)) return false;

                if (monitorOwner == IntPtr.Zero) return false;
                IntPtr mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
                if (mon != MonitorFromWindow(monitorOwner, MONITOR_DEFAULTTONEAREST)) return false;

                // Exclusive fullscreen Direct3D, on our monitor.
                if (state == QUNS_RUNNING_D3D_FULL_SCREEN) return true;

                RECT r;
                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (!GetWindowRect(fg, out r) || !GetMonitorInfo(mon, ref mi)) return false;

                RECT m = mi.rcMonitor;
                bool covers = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
                if (!covers) return false;

                // With an auto-hiding taskbar an ordinary maximized window
                // covers the whole monitor too. That is not fullscreen, and the
                // taskbar still slides up over it, so the pill should as well.
                int style = GetWindowLong(fg, GWL_STYLE);
                if (IsZoomed(fg) && (style & WS_CAPTION) == WS_CAPTION) return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
        const int QUNS_PRESENTATION_MODE = 4;
        const int GWL_STYLE = -16;
        const int GWL_EXSTYLE = -20;
        const int WS_CAPTION = 0x00C00000;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TRANSPARENT = 0x20;
        const int MONITOR_DEFAULTTONEAREST = 2;

        /// <summary>
        /// True for a click-through overlay stretched across the screen -
        /// annotation layers, screenshot tools, Electron apps with a
        /// full-screen transparent surface. Counting one as fullscreen hid the
        /// pill permanently while nothing was actually fullscreen. A window
        /// that cannot be clicked or focused is not something the user is
        /// watching.
        /// </summary>
        static bool IsOverlayWindow(IntPtr hwnd)
        {
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            return (ex & WS_EX_TRANSPARENT) != 0 || (ex & WS_EX_NOACTIVATE) != 0;
        }

        /// <summary>
        /// The desktop and the taskbar are always monitor-sized, and so are
        /// Task View and the other shell surfaces that sit over the desktop
        /// with the taskbar still showing. None of them is fullscreen content.
        /// </summary>
        static bool IsShellWindow(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder(64);
            GetClassName(hwnd, sb, sb.Capacity);
            switch (sb.ToString())
            {
                case "Progman":
                case "WorkerW":
                case "Shell_TrayWnd":
                case "Shell_SecondaryTrayWnd":
                case "XamlExplorerHostIslandWindow":
                case "MultitaskingViewFrame":
                case "ForegroundStaging":
                case "Windows.UI.Core.CoreWindow":
                    return true;
            }
            return false;
        }

        // ----------------------------------------------------------
        // Interop
        // ----------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [DllImport("shell32.dll")]
        static extern int SHQueryUserNotificationState(out int state);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    }
}
