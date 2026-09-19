using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace Shelf
{
    /// <summary>
    /// Helper for locating the Windows taskbar and computing where to place the
    /// dock pill so it sits as the last app icon in the centred cluster.
    ///
    /// The Windows 11 taskbar centres its app icons. Opening Start or Search adds
    /// a button, causing the cluster to widen and re-centre instantly. To track
    /// the true last icon we use UI Automation over Shell_TrayWnd, filtering
    /// Button elements to those left of TrayNotifyWnd and excluding "Show Desktop".
    ///
    /// UIA queries block the calling thread, so we run them on a pool thread and
    /// cache the result. The main thread reads the cache synchronously and never
    /// blocks on a pending query. If a query fails or returns no buttons, we keep
    /// the last good measurement rather than jumping to a fallback.
    /// </summary>
    public static class Taskbar
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter,
                                          string lpszClass, string lpszWindow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        // Cached measurement from the last successful UIA query.
        static int _cachedLastRight;
        static int _cachedButtonWidth = 44;
        static int _cachedBarTop;
        static int _cachedBarHeight;
        static int _cachedTrayLeft;
        static bool _cacheValid;
        static readonly object _cacheLock = new object();

        // Guard against overlapping background queries.
        static int _queryInFlight;

        // Rate-limit: minimum interval between query kicks (milliseconds).
        const int MinQueryIntervalMs = 200;
        static DateTime _lastQueryKick = DateTime.MinValue;

        /// <summary>
        /// Returns the Shell_TrayWnd handle, or IntPtr.Zero if not found.
        /// </summary>
        public static IntPtr GetTaskbarHandle()
        {
            return FindWindow("Shell_TrayWnd", null);
        }

        /// <summary>
        /// Returns the process ID of the taskbar (explorer.exe), or 0 on failure.
        /// </summary>
        public static uint GetTaskbarProcessId()
        {
            IntPtr bar = GetTaskbarHandle();
            if (bar == IntPtr.Zero)
                return 0;
            uint pid;
            GetWindowThreadProcessId(bar, out pid);
            return pid;
        }

        /// <summary>
        /// Returns the taskbar's screen rectangle, or Rectangle.Empty on failure.
        /// </summary>
        public static Rectangle GetTaskbarRect()
        {
            IntPtr bar = GetTaskbarHandle();
            if (bar == IntPtr.Zero)
                return Rectangle.Empty;
            RECT r;
            if (!GetWindowRect(bar, out r))
                return Rectangle.Empty;
            return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }

        /// <summary>
        /// Kicks a background UIA query if none is already in flight and we have
        /// not queried too recently. Call this from WinEvent callbacks to react
        /// quickly to taskbar relayouts.
        /// </summary>
        public static void KickQuery()
        {
            DateTime now = DateTime.UtcNow;
            lock (_cacheLock)
            {
                if ((now - _lastQueryKick).TotalMilliseconds < MinQueryIntervalMs)
                    return;
                _lastQueryKick = now;
            }
            RefreshCacheAsync();
        }

        /// <summary>
        /// Computes the rectangle where the dock pill should sit on the taskbar,
        /// slotted as the last app icon in the centred cluster.
        ///
        /// Returns false if the taskbar is unavailable, not visible, vertical,
        /// or UIA has not yet produced a valid measurement. On failure the caller
        /// should keep the last applied position rather than jumping elsewhere.
        /// </summary>
        /// <param name="gap">Horizontal spacing in device pixels.</param>
        /// <param name="slot">The computed rectangle for the pill (screen coords).</param>
        public static bool TryGetSlot(int gap, out Rectangle slot)
        {
            slot = Rectangle.Empty;

            try
            {
                IntPtr tray = FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero || !IsWindowVisible(tray))
                    return false;

                RECT trayRect;
                if (!GetWindowRect(tray, out trayRect))
                    return false;

                int barW = trayRect.Right - trayRect.Left;
                int barH = trayRect.Bottom - trayRect.Top;

                // Only handle horizontal taskbar (width > height).
                if (barW <= barH)
                    return false;

                // Ensure a background query is in flight or recently completed.
                RefreshCacheAsync();

                int lastRight, buttonWidth, barTop, trayLeft;
                bool valid;
                lock (_cacheLock)
                {
                    valid = _cacheValid;
                    lastRight = _cachedLastRight;
                    buttonWidth = _cachedButtonWidth;
                    barTop = _cachedBarTop;
                    trayLeft = _cachedTrayLeft;
                    // Update bar height from live measurement
                    _cachedBarHeight = barH;
                }

                if (!valid)
                    return false;

                // Pill size matches the measured button width (typically 44).
                int pillSize = buttonWidth;
                if (pillSize < 24)
                    pillSize = 24;

                // Position just right of the last icon.
                int x = lastRight + gap;
                int y = barTop + (barH - pillSize) / 2;

                // Clamp so we never cross TrayNotifyWnd.
                if (x + pillSize > trayLeft - gap)
                    x = trayLeft - gap - pillSize;

                slot = new Rectangle(x, y, pillSize, pillSize);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns the taskbar height in device pixels, or 0 if unknown.
        /// </summary>
        public static int BarHeight()
        {
            lock (_cacheLock)
            {
                if (_cachedBarHeight > 0)
                    return _cachedBarHeight;
            }

            try
            {
                IntPtr tray = FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero)
                    return 0;

                RECT rect;
                if (!GetWindowRect(tray, out rect))
                    return 0;

                int h = rect.Bottom - rect.Top;
                lock (_cacheLock) { _cachedBarHeight = h; }
                return h;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Starts a background thread to query UIA for the last app icon position.
        /// Only one query runs at a time; additional calls are no-ops until it
        /// completes.
        /// </summary>
        static void RefreshCacheAsync()
        {
            // Interlocked compare-exchange to ensure only one query in flight.
            if (Interlocked.CompareExchange(ref _queryInFlight, 1, 0) != 0)
                return;

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                try
                {
                    QueryUia();
                }
                finally
                {
                    Interlocked.Exchange(ref _queryInFlight, 0);
                }
            });
        }

        /// <summary>
        /// Performs the actual UIA query. Runs on a pool thread. Finds all Button
        /// elements under Shell_TrayWnd, filters to those left of TrayNotifyWnd
        /// and vertically within the bar, excludes "Show Desktop", and computes
        /// the rightmost edge as the last icon position.
        /// </summary>
        static void QueryUia()
        {
            try
            {
                IntPtr barHwnd = FindWindow("Shell_TrayWnd", null);
                if (barHwnd == IntPtr.Zero)
                    return;

                RECT barRect;
                if (!GetWindowRect(barHwnd, out barRect))
                    return;

                int barTop = barRect.Top;
                int barBottom = barRect.Bottom;

                // Find TrayNotifyWnd to know where the system tray starts.
                IntPtr notifyHwnd = FindWindowEx(barHwnd, IntPtr.Zero, "TrayNotifyWnd", null);
                int trayLeft = barRect.Right;
                if (notifyHwnd != IntPtr.Zero)
                {
                    RECT notifyRect;
                    if (GetWindowRect(notifyHwnd, out notifyRect))
                        trayLeft = notifyRect.Left;
                }

                // Get the AutomationElement for the taskbar.
                AutomationElement bar = AutomationElement.FromHandle(barHwnd);
                if (bar == null)
                    return;

                // Find all Button descendants.
                Condition buttonCondition = new PropertyCondition(
                    AutomationElement.ControlTypeProperty, ControlType.Button);
                AutomationElementCollection buttons = bar.FindAll(
                    TreeScope.Descendants, buttonCondition);

                int maxRight = 0;
                int measuredWidth = 44;
                int foundCount = 0;

                foreach (AutomationElement btn in buttons)
                {
                    try
                    {
                        // Skip "Show Desktop" which sits at the far right.
                        string name = btn.Current.Name;
                        if (!string.IsNullOrEmpty(name) &&
                            name.IndexOf("Show Desktop", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;

                        System.Windows.Rect bounds = btn.Current.BoundingRectangle;

                        // Skip elements with no valid bounds.
                        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
                            continue;

                        // Filter to buttons within the bar's vertical range.
                        if (bounds.Top < barTop || bounds.Bottom > barBottom + 2)
                            continue;

                        // Filter to buttons left of the system tray.
                        if (bounds.Left >= trayLeft)
                            continue;

                        foundCount++;
                        int right = (int)bounds.Right;
                        if (right > maxRight)
                        {
                            maxRight = right;
                            measuredWidth = (int)bounds.Width;
                        }
                    }
                    catch
                    {
                        // Individual element access can fail; skip it.
                    }
                }

                // Only update the cache if we found at least one button.
                if (foundCount > 0 && maxRight > 0)
                {
                    lock (_cacheLock)
                    {
                        _cachedLastRight = maxRight;
                        _cachedButtonWidth = measuredWidth > 0 ? measuredWidth : 44;
                        _cachedBarTop = barTop;
                        _cachedTrayLeft = trayLeft;
                        _cachedBarHeight = barBottom - barTop;
                        _cacheValid = true;
                    }
                }
            }
            catch
            {
                // UIA query failed; keep existing cache.
            }
        }
    }
}
