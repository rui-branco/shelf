using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Shelf
{
    /// <summary>
    /// The pill: one extra taskbar button, sitting after the last app icon,
    /// that opens the stack.
    ///
    /// It is a *layered* window drawn from a 32-bit ARGB bitmap, not a normal
    /// window with a clipped region or transparency key. A region is hard-edged
    /// and a transparency key blends antialiased edges toward the key colour,
    /// leaving visible fringing. Per-pixel alpha composites the curves smoothly
    /// against whatever is behind it.
    ///
    /// WS_EX_NOACTIVATE means it never takes focus.
    ///
    /// There is one pill per taskbar (see Docks), so a second monitor with its
    /// own taskbar gets its own pill, sized and scaled for that screen.
    ///
    /// Where it goes comes from its Taskbar, measured with UI Automation on
    /// Taskbars' own thread. This window only asks for a measurement
    /// when something that moves the row happens - a window with a taskbar
    /// button appearing or going (the shell hook says so), the taskbar itself
    /// relaying out, the display changing - and otherwise just reads the cache.
    ///
    /// The shell raises its own windows above us when the Start menu opens, so
    /// we reassert topmost in a short burst rather than once. That is a race we
    /// can only win by being persistent; it is never run while something is
    /// fullscreen, where the right answer is to be gone.
    /// </summary>
    public partial class DockForm : Form
    {
        const int WS_EX_TOOLWINDOW = 0x80;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_LAYERED = 0x80000;
        const int WS_EX_TOPMOST = 0x8;

        /// <summary>Windows 11 draws 24px icons in its taskbar buttons; so do we.</summary>
        const int IconPx = 24;

        /// <summary>
        /// The visible button plate inside the cell. Windows 11 shows a 40px
        /// rounded plate in a 44px cell, so 2px of air each side.
        /// </summary>
        const int PlateInset = 2;
        const int PlateRadius = 4;

        // SetWindowPos constants
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_NOOWNERZORDER = 0x0200;

        // WinEventHook constants
        const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        const int OBJID_CARET = -8;
        const int OBJID_CURSOR = -9;

        // Shell hook codes (RegisterShellHookWindow)
        const int HSHELL_WINDOWCREATED = 1;
        const int HSHELL_WINDOWDESTROYED = 2;
        const int HSHELL_WINDOWACTIVATED = 4;      // | 0x8000 when the window is fullscreen ("rude")
        const int HSHELL_WINDOWREPLACED = 13;
        const int HSHELL_MONITORCHANGED = 16;

        const int WM_SETTINGCHANGE = 0x001A;
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_MOUSEACTIVATE = 0x0021;
        const int WM_DPICHANGED = 0x02E0;
        const int MA_NOACTIVATE = 3;

        AppConfig _config;
        Downloads _downloads;
        Taskbar _bar;
        StackForm _stack;
        DateTime _stackClosedAt = DateTime.MinValue;
        float _scale = 1f;

        readonly LayeredSurface _surface = new LayeredSurface();
        Bitmap _logo;   // the mark, rendered once per size

        bool _hover;
        bool _pressed;
        bool _dropHover;

        // Bounce when a new file arrives
        Timer _anim;
        readonly Stopwatch _bounce = new Stopwatch();
        float _bounceOffset;
        const int BounceMs = 650;

        // The heartbeat: fullscreen, auto-hide, hover and position, four times a
        // second. Every check in it is a handful of cheap Win32 calls.
        Timer _tick;

        // A few follow-up measurements after a taskbar button appears or goes:
        // the bar animates the change, so the first measurement is too early.
        Timer _settle;
        int _settleLeft;

        // Topmost burst: reasserts topmost repeatedly for a short window after
        // shell events. A single reassertion loses to the Start menu animation.
        Timer _topmostBurst;
        DateTime _burstEnd;
        const int BurstDurationMs = 500;
        const int BurstIntervalMs = 33;

        // WinEventHook delegates. MUST be stored in fields - if collected the hook
        // crashes the process, which is the classic bug with this API.
        delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
        WinEventDelegate _foregroundProc;
        WinEventDelegate _locationChangeProc;
        IntPtr _foregroundHook;
        IntPtr _locationChangeHook;

        int _shellHookMsg;
        int _taskbarCreatedMsg;
        EventHandler _measured;

        public DockForm(AppConfig config, Downloads downloads, Taskbar bar)
        {
            _config = config;
            _downloads = downloads;
            _bar = bar;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            // The whole pill is one button, so the pointer says so the moment
            // the cursor is over it.
            Cursor = Cursors.Hand;
            // Dropping files on the pill moves them into Downloads, the way
            // dropping on a stack in the macOS Dock does.
            AllowDrop = true;

            _downloads.Changed += delegate { Render(); };
            _downloads.NewFileArrived += delegate { StartBounce(); };

            _anim = new Timer();
            _anim.Interval = 15;
            _anim.Tick += delegate { StepBounce(); };

            _tick = new Timer();
            _tick.Interval = 250;
            _tick.Tick += delegate { OnTick(); };

            _settle = new Timer();
            _settle.Interval = 250;
            _settle.Tick += delegate
            {
                Taskbars.Request();
                if (--_settleLeft <= 0) _settle.Stop();
            };

            _topmostBurst = new Timer();
            _topmostBurst.Interval = BurstIntervalMs;
            _topmostBurst.Tick += delegate { OnTopmostBurstTick(); };

            // Measurements land on the taskbar thread; positions are ours.
            _measured = delegate(object sender, EventArgs e)
            {
                if (sender != _bar) return;
                try
                {
                    if (IsHandleCreated && !IsDisposed)
                        BeginInvoke(new MethodInvoker(OnMeasured));
                }
                catch { }
            };
            Taskbars.Measured += _measured;

            Rectangle slot;
            if (_bar.TryGetSlot(out slot))
            {
                Bounds = slot;
                _scale = Dpi.ScaleAt(new Point(slot.Left + slot.Width / 2, slot.Top + slot.Height / 2));
            }

            InstallHooks();
            _tick.Start();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // NOACTIVATE as well as ShowWithoutActivation: the latter only
                // governs the first Show, so without this the pill would steal
                // activation on every click. That matters because the stack
                // closes when it loses focus - clicking the pill to dismiss it
                // would deactivate the stack, close it, and then find nothing
                // open to toggle, so it would spring straight back.
                //
                // LAYERED enables per-pixel alpha via UpdateLayeredWindow.
                //
                // TOPMOST as a style rather than the TopMost property: WinForms
                // applies the property with a SetWindowPos that activates the
                // window, NOACTIVATE or not, every time it is shown - so each
                // time the pill came back after fullscreen it could take the
                // focus from whatever had it.
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        /// <summary>
        /// A layered window shows nothing at all until UpdateLayeredWindow has
        /// been called once - there is no WM_PAINT to fall back on. Render once
        /// the handle is real, and again on first show.
        /// </summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // The shell hook is how the taskbar itself learns that a window
            // came or went, and so exactly when its button row changes. It also
            // flags fullscreen ("rude") windows as they are activated.
            _shellHookMsg = RegisterWindowMessage("SHELLHOOK");
            _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
            try { RegisterShellHookWindow(Handle); }
            catch { }

            Render();
        }

        /// <summary>The taskbar this pill belongs to.</summary>
        public Taskbar Bar
        {
            get { return _bar; }
        }

        /// <summary>The scale of the monitor the pill is on.</summary>
        public float MonitorScale
        {
            get { return _scale; }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Render();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = new IntPtr(MA_NOACTIVATE);
                return;
            }

            if (_shellHookMsg != 0 && m.Msg == _shellHookMsg)
            {
                int code = m.WParam.ToInt32() & 0x7FFF;
                if (code == HSHELL_WINDOWCREATED || code == HSHELL_WINDOWDESTROYED
                    || code == HSHELL_WINDOWREPLACED)
                {
                    Settle();
                }
                else if (code == HSHELL_WINDOWACTIVATED || code == HSHELL_MONITORCHANGED)
                {
                    UpdateVisibility();
                }
            }
            else if (_taskbarCreatedMsg != 0 && m.Msg == _taskbarCreatedMsg)
            {
                // Explorer restarted. Everything we knew about the old taskbars
                // - their process, their UIA elements, the windows themselves -
                // is gone with it, and this pill's bar with them. Docks finds
                // the new ones and gives each a fresh pill.
                Taskbars.Reset();
                Docks.RequestSync();
            }
            else if (m.Msg == WM_SETTINGCHANGE || m.Msg == WM_DISPLAYCHANGE)
            {
                // A monitor coming or going adds or removes a taskbar.
                Theme.Refresh();
                if (m.Msg == WM_DISPLAYCHANGE) Docks.RequestSync();
                Settle();
                Render();
            }
            else if (m.Msg == WM_DPICHANGED)
            {
                // Sized and placed by the taskbar measurement, not by the
                // rectangle Windows suggests here.
                Settle();
                Render();
                return;
            }

            base.WndProc(ref m);
        }

        // ----------------------------------------------------------
        // Placement
        // ----------------------------------------------------------

        /// <summary>
        /// Moves to the measured slot if it changed. Reads the cache only, so
        /// it is cheap enough for the heartbeat.
        ///
        /// Parenting into Shell_TrayWnd was tried and abandoned: SetParent succeeds
        /// but the taskbar's XAML content bridge stays above for hit testing, so
        /// the pill becomes visible but unclickable. We stay a floating topmost
        /// window instead.
        /// </summary>
        void UpdateTaskbarPosition()
        {
            Rectangle slot;
            if (!_bar.TryGetSlot(out slot)) return;     // keep the last good spot
            if (slot == Bounds) return;

            Bounds = slot;
            // Each monitor has its own scale, and the slot says which one we
            // are on before the window has finished moving there.
            _scale = Dpi.ScaleAt(new Point(slot.Left + slot.Width / 2, slot.Top + slot.Height / 2));
            if (!Visible) return;
            AssertTopmost();
            Render();
        }

        void OnMeasured()
        {
            UpdateTaskbarPosition();
            UpdateVisibility();
        }

        /// <summary>A few measurements over the next second, for an animating taskbar.</summary>
        void Settle()
        {
            Taskbars.Request();
            _settleLeft = 4;
            _settle.Stop();
            _settle.Start();
        }

        void OnTick()
        {
            UpdateVisibility();
            if (!Visible) return;      // nothing to place while we are out of the way
            SyncHoverToCursor();
            UpdateTaskbarPosition();
        }

        /// <summary>
        /// The pill goes when its taskbar goes: while something is fullscreen
        /// on that taskbar's monitor, and while an auto-hiding taskbar is tucked
        /// away. Comes back the moment neither is true. It also waits for its
        /// bar's first measurement, so it never shows up anywhere but its slot.
        /// </summary>
        void UpdateVisibility()
        {
            if (!IsHandleCreated || IsDisposed) return;

            IntPtr stack = _stack != null && !_stack.IsDisposed && _stack.IsHandleCreated ? _stack.Handle : IntPtr.Zero;
            Rectangle slot;
            bool hide = !_bar.TryGetSlot(out slot) || !_bar.IsOnScreen()
                     || Fullscreen.IsActive(_bar.Handle, Handle, stack);

            if (hide && Visible)
            {
                _topmostBurst.Stop();
                CloseStack();
                Hide();
                _bar.Paused = true;
            }
            else if (!hide && !Visible)
            {
                _bar.Paused = false;
                Show();
                Settle();
                UpdateTaskbarPosition();
                AssertTopmost();
                Render();
            }
        }

        /// <summary>
        /// OnMouseLeave is not dependable on a layered, non-activating window:
        /// move the cursor straight from the pill onto another window and the
        /// leave never arrives, leaving the hover highlight burned on for good.
        /// The heartbeat settles the truth from where the cursor actually is.
        /// </summary>
        void SyncHoverToCursor()
        {
            bool inside = Bounds.Contains(Cursor.Position);
            bool changed = false;

            if (inside != _hover)
            {
                _hover = inside;
                changed = true;
            }
            if (_pressed && (Control.MouseButtons & MouseButtons.Left) == 0)
            {
                _pressed = false;
                changed = true;
            }
            if (changed) Render();
        }

        // ----------------------------------------------------------
        // Teardown
        // ----------------------------------------------------------

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            Teardown();
        }

        protected override void Dispose(bool disposing)
        {
            Teardown();
            base.Dispose(disposing);
        }

        void Teardown()
        {
            RemoveHooks();
            if (_measured != null)
            {
                Taskbars.Measured -= _measured;
                _measured = null;
            }
            if (IsHandleCreated)
            {
                try { DeregisterShellHookWindow(Handle); }
                catch { }
            }
            foreach (Timer t in new Timer[] { _anim, _tick, _settle, _topmostBurst })
            {
                if (t != null) { t.Stop(); t.Dispose(); }
            }
            _surface.Dispose();
            if (_logo != null) { _logo.Dispose(); _logo = null; }
        }

        // ----------------------------------------------------------
        // Interop
        // ----------------------------------------------------------

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int RegisterWindowMessage(string name);

        [DllImport("user32.dll")]
        static extern bool RegisterShellHookWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool DeregisterShellHookWindow(IntPtr hwnd);
    }
}
