using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Shelf
{
    /// <summary>Whether something fullscreen is in front, for the pill to get out of its way.</summary>
    public static class Fullscreen
    {
        /// <summary>
        /// True when the window in front on the taskbar's monitor fills that
        /// monitor: a game, a video, a browser in F11, a presentation.
        ///
        /// Only that monitor counts. The first version hid the pill for any
        /// fullscreen window anywhere, so a video playing fullscreen on a second
        /// screen took the pill off the main one.
        ///
        /// "In front" is the top of the z-order on that monitor, not the
        /// foreground window. The foreground is wherever the user last clicked,
        /// so going to work on the other screen used to make the video stop
        /// counting and put the pill back over it - while its taskbar, which
        /// Windows tracks per monitor, stayed out of the way.
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

                if (monitorOwner == IntPtr.Zero) return false;
                IntPtr mon = MonitorFromWindow(monitorOwner, MONITOR_DEFAULTTONEAREST);

                IntPtr fg = GetForegroundWindow();
                if (fg != IntPtr.Zero && !IsIgnored(fg, ignore)
                    && MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) == mon)
                {
                    // Exclusive fullscreen Direct3D, on our monitor.
                    if (state == QUNS_RUNNING_D3D_FULL_SCREEN) return true;

                    // Start, Task View or the taskbar itself in front on this
                    // monitor bring the taskbar up over anything, so the pill
                    // comes up with it.
                    if (IsShellSurface(ClassOf(fg))) return false;
                }

                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (!GetMonitorInfo(mon, ref mi)) return false;
                RECT m = mi.rcMonitor;

                // Top down to the first window on this monitor the user can
                // actually see. The cap is only a guard: the z-order can change
                // under the walk, and GetWindow can then go round in circles.
                IntPtr h = GetTopWindow(IntPtr.Zero);
                for (int i = 0; h != IntPtr.Zero && i < MaxWalk; i++, h = GetWindow(h, GW_HWNDNEXT))
                {
                    if (!IsWindowVisible(h) || IsIconic(h) || IsIgnored(h, ignore)) continue;

                    // DEFAULTTONULL: windows parked off every screen belong to none.
                    if (MonitorFromWindow(h, MONITOR_DEFAULTTONULL) != mon) continue;
                    if (IsCloaked(h) || IsOverlayWindow(h)) continue;

                    string cls = ClassOf(h);
                    if (IsDesktop(cls) || IsShellSurface(cls)) continue;

                    RECT r;
                    if (!GetWindowRect(h, out r)) continue;
                    if (FillsMonitor(h, r, m)) return true;

                    // Something always on top that is not fullscreen - a
                    // picture-in-picture, a toast, our own pills - floats over a
                    // fullscreen video without ending it. Look underneath.
                    if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0) continue;

                    return false;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        static bool FillsMonitor(IntPtr hwnd, RECT r, RECT m)
        {
            bool covers = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
            if (!covers) return false;

            // With an auto-hiding taskbar an ordinary maximized window
            // covers the whole monitor too. That is not fullscreen, and the
            // taskbar still slides up over it, so the pill should as well.
            int style = GetWindowLong(hwnd, GWL_STYLE);
            return !(IsZoomed(hwnd) && (style & WS_CAPTION) == WS_CAPTION);
        }

        static bool IsIgnored(IntPtr hwnd, IntPtr[] ignore)
        {
            foreach (IntPtr own in ignore)
            {
                if (hwnd == own) return true;
            }
            return false;
        }

        /// <summary>
        /// Windows on another virtual desktop, and suspended Store apps, are
        /// "visible" but cloaked: DWM draws nothing of them.
        /// </summary>
        static bool IsCloaked(IntPtr hwnd)
        {
            int cloaked;
            return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0;
        }

        const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
        const int QUNS_PRESENTATION_MODE = 4;
        const int GWL_STYLE = -16;
        const int GWL_EXSTYLE = -20;
        const int WS_CAPTION = 0x00C00000;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TRANSPARENT = 0x20;
        const int WS_EX_TOPMOST = 0x8;
        const int MONITOR_DEFAULTTONULL = 0;
        const int MONITOR_DEFAULTTONEAREST = 2;
        const uint GW_HWNDNEXT = 2;
        const int DWMWA_CLOAKED = 14;
        const int MaxWalk = 4096;

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

        static string ClassOf(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder(64);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>
        /// The desktop is always monitor-sized and always at the bottom. It is
        /// never fullscreen content, and never in front of any.
        /// </summary>
        static bool IsDesktop(string cls)
        {
            return cls == "Progman" || cls == "WorkerW";
        }

        /// <summary>
        /// The taskbar, Start, Task View and the other shell surfaces that sit
        /// over everything with the taskbar showing. Monitor-sized some of
        /// them, but none of them is fullscreen content.
        /// </summary>
        static bool IsShellSurface(string cls)
        {
            switch (cls)
            {
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

        [DllImport("dwmapi.dll")]
        static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern IntPtr GetTopWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

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
