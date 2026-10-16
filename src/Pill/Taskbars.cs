using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Shelf
{
    /// <summary>
    /// Every taskbar on the desktop, and the one thread that measures them.
    ///
    /// Measuring is expensive in a way that does not show up in Shelf: the cost
    /// lands on explorer. The first version measured every half second and
    /// again on every location change anywhere in explorer - including every
    /// cursor move over the desktop - from whichever pool thread was free. Left
    /// running for three days that had explorer at 2.4 GB and lagging, and
    /// Shelf at 2.8 GB.
    ///
    /// So measurement runs on one dedicated thread, only when something asks
    /// for it (a taskbar button appeared or vanished, the display changed) plus
    /// a slow safety-net refresh, never closer together than MinGapMs.
    /// </summary>
    public static class Taskbars
    {
        /// <summary>Shortest pause between two measuring passes, so a burst of requests costs one.</summary>
        const int MinGapMs = 150;

        /// <summary>
        /// The safety net for relayouts nothing told us about (a pin, a settings
        /// change). Slow on purpose: everything common arrives as a request.
        /// A pass over two taskbars costs about 13 ms of our CPU and more
        /// inside explorer, so this is worth keeping rare.
        /// </summary>
        const int IdleRefreshMs = 20000;

        /// <summary>
        /// After a taskbar button appears or goes, the bar animates the change,
        /// so a few follow-up passes catch where it ends up.
        /// </summary>
        const int SettleMs = 1000;
        const int SettleStepMs = 250;

        static readonly object _lock = new object();
        static readonly List<Taskbar> _bars = new List<Taskbar>();

        static Thread _worker;
        static readonly AutoResetEvent _wake = new AutoResetEvent(false);
        static readonly ManualResetEvent _first = new ManualResetEvent(false);
        static volatile bool _reset;
        static long _settleUntil;      // DateTime.UtcNow ticks; follow-up passes until then

        /// <summary>
        /// Raised on the worker thread, with the Taskbar as sender, when a
        /// measurement moved its slot. Subscribers marshal to their own thread.
        /// </summary>
        public static event EventHandler Measured;

        /// <summary>Starts the measuring thread. Safe to call more than once.</summary>
        public static void Start()
        {
            if (_worker != null) return;

            All();     // find the bars before the first pass

            _worker = new Thread(Loop);
            _worker.IsBackground = true;
            _worker.Name = "Shelf taskbar";
            // UIA clients are meant to live in the MTA; an STA client can
            // deadlock against providers that call back into it.
            _worker.SetApartmentState(ApartmentState.MTA);
            _worker.Start();
        }

        /// <summary>
        /// Blocks until the first measuring pass is done, or the timeout passes.
        /// Startup waits on this so the pills appear in their slots rather than
        /// flashing up in a corner first.
        /// </summary>
        public static bool WaitForFirstMeasurement(int timeoutMs)
        {
            return _first.WaitOne(timeoutMs);
        }

        /// <summary>Asks for a fresh measurement soon. Cheap; bursts coalesce.</summary>
        public static void Request()
        {
            _wake.Set();
        }

        /// <summary>
        /// A measurement now and a few more over the next second, while the
        /// taskbar animates a button in or out. Every pill hears the same shell
        /// notification and asks; they share this one schedule instead of each
        /// running its own.
        /// </summary>
        public static void Settle()
        {
            Interlocked.Exchange(ref _settleUntil, DateTime.UtcNow.AddMilliseconds(SettleMs).Ticks);
            _wake.Set();
        }

        /// <summary>
        /// Forgets every cached taskbar element, for when explorer restarted and
        /// the ones we hold belong to a process that no longer exists.
        /// </summary>
        public static void Reset()
        {
            _reset = true;
            _wake.Set();
        }

        /// <summary>
        /// The taskbars as they are now: the main one first, then one per
        /// extra monitor that has its own. Bars seen before keep their
        /// measurements; bars that are gone are dropped.
        /// </summary>
        public static List<Taskbar> All()
        {
            List<IntPtr> found = new List<IntPtr>();
            IntPtr primary = IntPtr.Zero;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                StringBuilder sb = new StringBuilder(32);
                GetClassName(h, sb, sb.Capacity);
                string cls = sb.ToString();
                if (cls == "Shell_TrayWnd") primary = h;
                else if (cls == "Shell_SecondaryTrayWnd") found.Add(h);
                return true;
            }, IntPtr.Zero);
            if (primary != IntPtr.Zero) found.Insert(0, primary);

            lock (_lock)
            {
                List<Taskbar> next = new List<Taskbar>();
                foreach (IntPtr h in found)
                {
                    Taskbar bar = _bars.Find(delegate(Taskbar b) { return b.Handle == h; });
                    next.Add(bar ?? new Taskbar(h, h == primary));
                }
                _bars.Clear();
                _bars.AddRange(next);
                return new List<Taskbar>(_bars);
            }
        }

        /// <summary>The main taskbar, or null while there is none (explorer restarting).</summary>
        public static Taskbar Primary
        {
            get
            {
                lock (_lock)
                {
                    foreach (Taskbar b in _bars)
                        if (b.IsPrimary && b.IsAlive) return b;
                }
                return null;
            }
        }

        /// <summary>The taskbars' process (explorer.exe), or 0.</summary>
        public static uint ShellProcessId()
        {
            Taskbar bar = Primary;
            if (bar == null) return 0;
            uint pid;
            GetWindowThreadProcessId(bar.Handle, out pid);
            return pid;
        }

        static void Loop()
        {
            while (true)
            {
                bool settling = DateTime.UtcNow.Ticks < Interlocked.Read(ref _settleUntil);
                bool asked = _wake.WaitOne(settling ? SettleStepMs : IdleRefreshMs) || settling;

                List<Taskbar> bars;
                lock (_lock) bars = new List<Taskbar>(_bars);

                bool reset = _reset;
                _reset = false;

                foreach (Taskbar bar in bars)
                {
                    if (reset) bar.Forget();
                    if (!asked && bar.Paused && bar.HasMeasurement) continue;
                    if (!bar.IsAlive) continue;

                    bool moved = false;
                    try { moved = bar.Measure(); }
                    catch { }

                    if (moved)
                    {
                        EventHandler h = Measured;
                        if (h != null)
                        {
                            try { h(bar, EventArgs.Empty); }
                            catch { }
                        }
                    }
                }

                _first.Set();
                Thread.Sleep(MinGapMs);
            }
        }

        delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumProc cb, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }
}
