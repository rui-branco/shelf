using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Shelf
{
    /// <summary>
    /// One pill per taskbar, kept in step as monitors and taskbars come and go.
    ///
    /// Windows gives every monitor its own taskbar when "show my taskbar on all
    /// displays" is on, and the user reaches for whichever is in front of them,
    /// so each one gets a pill. Taskbars appear and vanish with monitors being
    /// plugged in and out and with explorer restarting, so the set is checked
    /// again whenever that might have happened, and slowly in the background
    /// in case nothing said so.
    /// </summary>
    public static class Docks
    {
        static AppConfig _config;
        static Downloads _downloads;
        static readonly List<DockForm> _pills = new List<DockForm>();

        static Timer _sync;
        static int _quickChecks;
        const int SlowCheckMs = 3000;
        const int QuickCheckMs = 500;

        public static void Start(AppConfig config, Downloads downloads)
        {
            _config = config;
            _downloads = downloads;

            Sync();

            _sync = new Timer();
            _sync.Interval = SlowCheckMs;
            _sync.Tick += delegate
            {
                Sync();
                if (_quickChecks > 0 && --_quickChecks == 0) _sync.Interval = SlowCheckMs;
            };
            _sync.Start();
        }

        /// <summary>
        /// Checks again a few times over the next seconds. A display change or
        /// an explorer restart is announced before the new taskbars exist.
        /// </summary>
        public static void RequestSync()
        {
            if (_sync == null) return;
            _quickChecks = 6;
            _sync.Stop();
            _sync.Interval = QuickCheckMs;
            _sync.Start();
        }

        /// <summary>The pills as they are now.</summary>
        public static List<DockForm> Pills
        {
            get { return new List<DockForm>(_pills); }
        }

        /// <summary>Closes every open stack but the one belonging to <paramref name="except"/>.</summary>
        public static void CloseStacks(DockForm except)
        {
            foreach (DockForm p in _pills)
            {
                if (p != except && !p.IsDisposed) p.CloseStack();
            }
        }

        static void Sync()
        {
            List<Taskbar> bars = Taskbars.All();

            // Retire pills whose taskbar has gone.
            for (int i = _pills.Count - 1; i >= 0; i--)
            {
                DockForm p = _pills[i];
                if (p.IsDisposed || !p.Bar.IsAlive || !bars.Contains(p.Bar))
                {
                    _pills.RemoveAt(i);
                    if (!p.IsDisposed) p.Close();
                }
            }

            bool added = false;
            foreach (Taskbar bar in bars)
            {
                if (!bar.IsAlive) continue;
                Taskbar b = bar;
                if (_pills.Exists(delegate(DockForm p) { return p.Bar == b; })) continue;

                DockForm pill = new DockForm(_config, _downloads, bar);
                // A handle, not a Show: the pill shows itself once its bar has
                // been measured, so it never appears anywhere but its slot.
                IntPtr handle = pill.Handle;
                _pills.Add(pill);
                added = true;
            }

            if (added) Taskbars.Request();
        }
    }
}
