using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Shelf
{
    /// <summary>
    /// One taskbar - the main one, or a second monitor's - and where its pill
    /// goes: as the last button of its centred app cluster.
    ///
    /// The Windows 11 taskbar is XAML, so its buttons have no window handles and
    /// UI Automation is the only way to see where they are. That makes every
    /// measurement a cross-process tree walk inside explorer.exe, which is why
    /// measuring is left to Taskbars' single worker thread and done only when
    /// something asks for it. Readers here only ever see the cache and never
    /// wait.
    /// </summary>
    public sealed class Taskbar
    {
        readonly IntPtr _hwnd;
        readonly bool _primary;

        // Last good measurement. Guarded by _lock; written by the worker only.
        readonly object _lock = new object();
        int _lastRight;
        int _buttonWidth = 44;
        int _trayLeft = int.MaxValue;
        bool _valid;

        // Owned by the worker thread.
        AutomationElement _element;

        volatile bool _paused;

        internal Taskbar(IntPtr hwnd, bool primary)
        {
            _hwnd = hwnd;
            _primary = primary;
        }

        public IntPtr Handle { get { return _hwnd; } }

        /// <summary>The main taskbar, the one with the notification area.</summary>
        public bool IsPrimary { get { return _primary; } }

        /// <summary>
        /// Skips the idle refresh while this bar's pill is out of sight
        /// (fullscreen, auto-hidden bar). Explicit requests still run.
        /// </summary>
        public bool Paused
        {
            get { return _paused; }
            set { _paused = value; }
        }

        /// <summary>False once explorer has destroyed this taskbar (a monitor unplugged, a restart).</summary>
        public bool IsAlive
        {
            get { return IsWindow(_hwnd); }
        }

        internal bool HasMeasurement
        {
            get { lock (_lock) return _valid; }
        }

        /// <summary>The taskbar's screen rectangle, or Rectangle.Empty.</summary>
        public Rectangle Rect
        {
            get
            {
                RECT r;
                if (!GetWindowRect(_hwnd, out r)) return Rectangle.Empty;
                return r.ToRectangle();
            }
        }

        /// <summary>The bounds of the monitor this taskbar is on, or Rectangle.Empty.</summary>
        public Rectangle Monitor
        {
            get
            {
                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (!GetMonitorInfo(MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST), ref mi))
                    return Rectangle.Empty;
                return mi.rcMonitor.ToRectangle();
            }
        }

        /// <summary>
        /// False while an auto-hiding taskbar is tucked away (or sliding), when
        /// the pill should be gone too rather than floating over whatever is
        /// at the bottom of the screen.
        /// </summary>
        public bool IsOnScreen()
        {
            if (!IsWindowVisible(_hwnd)) return false;

            Rectangle r = Rect;
            Rectangle mon = Monitor;
            if (r.IsEmpty || mon.IsEmpty) return false;

            // Hidden, the bar sits almost entirely off the monitor with a sliver
            // showing. Only a bar wholly on screen counts.
            return mon.Contains(r);
        }

        /// <summary>
        /// Where the pill goes: one button-width cell right after the last app
        /// button. Reads the cache only, so it never blocks; false until the
        /// first measurement lands, or for a vertical taskbar, in which case
        /// the caller keeps whatever position it already had.
        /// </summary>
        public bool TryGetSlot(out Rectangle slot)
        {
            slot = Rectangle.Empty;

            RECT r;
            if (!GetWindowRect(_hwnd, out r)) return false;

            int barW = r.Right - r.Left;
            int barH = r.Bottom - r.Top;
            if (barW <= barH) return false;    // vertical taskbars are not handled

            int lastRight, width, trayLeft;
            lock (_lock)
            {
                if (!_valid) return false;
                lastRight = _lastRight;
                width = _buttonWidth;
                trayLeft = _trayLeft;
            }

            int size = Math.Max(24, Math.Min(width, barH));

            // Never run into the notification area or the clock.
            int x = Math.Min(lastRight, Math.Min(trayLeft, r.Right) - size);

            // Height comes from the live bar rather than the cache, so the
            // pill follows the bar without waiting for a measurement.
            int y = r.Top + (barH - size) / 2;

            slot = new Rectangle(x, y, size, size);
            return true;
        }

        /// <summary>Drops the cached UIA element; the next measurement starts afresh.</summary>
        internal void Forget()
        {
            _element = null;
        }

        /// <summary>
        /// One UIA walk: every Button under the taskbar, name, class and bounds
        /// fetched in the same round trip, keeping the rightmost app button.
        /// Returns true when the slot moved. Worker thread only.
        /// </summary>
        internal bool Measure()
        {
            RECT barRect;
            if (!GetWindowRect(_hwnd, out barRect)) return false;

            if (_element == null)
            {
                try { _element = AutomationElement.FromHandle(_hwnd); }
                catch { return false; }
            }

            // Name, class and bounds come back with the walk, and None means the
            // results hold no live reference into explorer - nothing to release
            // later, and nothing for a finalizer to get round to eventually.
            CacheRequest cache = new CacheRequest();
            cache.Add(AutomationElement.NameProperty);
            cache.Add(AutomationElement.ClassNameProperty);
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.AutomationElementMode = AutomationElementMode.None;

            AutomationElementCollection buttons;
            try
            {
                using (cache.Activate())
                {
                    buttons = _element.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                }
            }
            catch
            {
                // Typically ElementNotAvailable: explorer restarted under us.
                _element = null;
                return false;
            }

            // Where the notification area starts. The main taskbar still has a
            // TrayNotifyWnd; a secondary one only has its clock, which shows up
            // as SystemTray buttons like the rest of the tray.
            int trayLeft = barRect.Right;
            IntPtr notify = FindWindowEx(_hwnd, IntPtr.Zero, "TrayNotifyWnd", null);
            RECT nr;
            if (notify != IntPtr.Zero && GetWindowRect(notify, out nr)) trayLeft = nr.Left;

            int maxRight = 0;
            int width = 44;

            foreach (AutomationElement btn in buttons)
            {
                try
                {
                    string cls = btn.Cached.ClassName ?? "";
                    string name = btn.Cached.Name ?? "";
                    System.Windows.Rect b = btn.Cached.BoundingRectangle;
                    if (b.IsEmpty || b.Width <= 0 || b.Height <= 0) continue;
                    if (b.Top < barRect.Top || b.Bottom > barRect.Bottom + 2) continue;

                    // The tray's buttons live under the taskbar too. They are not
                    // app buttons; they only mark where the pill has to stop.
                    if (cls.StartsWith("SystemTray.", StringComparison.Ordinal)
                        || name.IndexOf("Show Desktop", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        trayLeft = Math.Min(trayLeft, (int)Math.Round(b.Left));
                        continue;
                    }

                    int right = (int)Math.Round(b.Right);
                    if (right > maxRight)
                    {
                        maxRight = right;
                        width = (int)Math.Round(b.Width);
                    }
                }
                catch
                {
                    // One unreadable element is not worth the measurement.
                }
            }

            if (maxRight <= 0) return false;   // keep the last good answer

            lock (_lock)
            {
                bool moved = !_valid || maxRight != _lastRight || width != _buttonWidth || trayLeft != _trayLeft;
                _lastRight = maxRight;
                _buttonWidth = width > 0 ? width : 44;
                _trayLeft = trayLeft;
                _valid = true;
                return moved;
            }
        }

        // ----------------------------------------------------------
        // Interop
        // ----------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left, Top, Right, Bottom;
            public Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        const int MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    }
}
