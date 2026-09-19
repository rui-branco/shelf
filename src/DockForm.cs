using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Shelf
{
    /// <summary>
    /// The dock pill: a small rounded button that sits on the taskbar as a
    /// layered window, shows a macOS-style Downloads folder icon, and toggles
    /// the StackForm on click.
    ///
    /// It is a *layered* window drawn from a 32-bit ARGB bitmap, not a normal
    /// window with a clipped region or transparency key. A region is hard-edged
    /// and a transparency key blends antialiased edges toward the key colour,
    /// leaving visible fringing. Per-pixel alpha composites the curves smoothly
    /// against whatever is behind it.
    ///
    /// WS_EX_NOACTIVATE means it never takes focus.
    ///
    /// Position is computed via UI Automation to find the rightmost app icon in
    /// the centred cluster. A WinEvent hook for EVENT_OBJECT_LOCATIONCHANGE on
    /// the shell process triggers immediate re-measurement when the taskbar
    /// relayouts (e.g. when Start opens and adds a button).
    ///
    /// The shell raises its own windows above us when the Start menu opens, so
    /// we reassert topmost in a short burst rather than once. This is a race we
    /// can only win by being persistent - the shell animation takes time, and
    /// we keep pushing back until it settles.
    /// </summary>
    public class DockForm : Form
    {
        const int WS_EX_TOOLWINDOW = 0x80;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_LAYERED = 0x80000;

        // Fallback size when taskbar is unavailable (e.g. vertical taskbar).
        const int FallbackSize = 44;

        /// <summary>
        /// Artwork size as a fraction of the taskbar cell. Windows 11 uses a
        /// 24px icon in a 44px button; 24/44 is a hair under 0.55, and matching
        /// it is what makes the pill read as one of the row rather than a
        /// visitor standing too close.
        /// </summary>
        const float TaskbarIconRatio = 0.545f;

        // Taskbar mode metrics
        const int TaskbarHoverRadius = 6;
        const int TaskbarHoverInset = 3;

        // SetWindowPos constants
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOACTIVATE = 0x0010;

        // WinEventHook constants
        const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        const uint EVENT_OBJECT_SHOW = 0x8002;
        const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        AppConfig _config;
        Downloads _downloads;
        StackForm _stack;
        DateTime _stackClosedAt = DateTime.MinValue;
        float _scale = 1f;

        // Bounce animation when a new file arrives
        Timer _bounce;
        int _bounceFrame;
        int _bounceOffset;

        // Taskbar anchor state
        Timer _anchorTimer;
        Rectangle _lastTaskbarSlot;

        // Hover state
        bool _isHovered;

        // WinEventHook delegates. MUST be stored in fields - if collected the hook
        // crashes the process, which is the classic bug with this API.
        delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
        WinEventDelegate _foregroundProc;
        WinEventDelegate _locationChangeProc;
        WinEventDelegate _showProc;
        IntPtr _foregroundHook;
        IntPtr _locationChangeHook;
        IntPtr _showHook;

        // Topmost burst timer: reasserts topmost repeatedly for a short window
        // after shell events. The shell's Start menu animation raises windows
        // over time, so a single reassertion loses the race. This is a workaround
        // rather than a fix - we keep pushing back until the animation settles.
        Timer _topmostBurst;
        DateTime _burstEnd;
        const int BurstDurationMs = 700;
        const int BurstIntervalMs = 16;

        public DockForm(AppConfig config, Downloads downloads)
        {
            _config = config;
            _downloads = downloads;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            // The whole pill is one button, so the pointer says so the moment
            // the cursor is over it.
            Cursor = Cursors.Hand;

            _downloads.Changed += delegate { Render(); };
            _downloads.NewFileArrived += delegate { StartBounce(); };

            _bounce = new Timer();
            _bounce.Interval = 8;
            _bounce.Tick += delegate(object s, EventArgs e) { StepBounce(); };

            _anchorTimer = new Timer();
            _anchorTimer.Interval = 500;
            _anchorTimer.Tick += delegate(object s, EventArgs e) { OnAnchorTick(); };

            // Single reusable timer for the topmost burst.
            _topmostBurst = new Timer();
            _topmostBurst.Interval = BurstIntervalMs;
            _topmostBurst.Tick += delegate(object s, EventArgs e) { OnTopmostBurstTick(); };

            // Install a WinEventHook for foreground changes to reassert topmost.
            _foregroundProc = new WinEventDelegate(OnForegroundEvent);
            _foregroundHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);

            // Install a WinEventHook for location changes scoped to the shell's
            // process. This fires when the taskbar relayouts (e.g. Start opens),
            // letting us reposition immediately rather than waiting for the poll.
            uint shellPid = Taskbar.GetTaskbarProcessId();
            if (shellPid != 0)
            {
                _locationChangeProc = new WinEventDelegate(OnLocationChangeEvent);
                _locationChangeHook = SetWinEventHook(
                    EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
                    IntPtr.Zero, _locationChangeProc, shellPid, 0, WINEVENT_OUTOFCONTEXT);

                // Hook EVENT_OBJECT_SHOW for the shell process as well - the Start
                // menu appearing is a show event, not always a foreground change.
                _showProc = new WinEventDelegate(OnShowEvent);
                _showHook = SetWinEventHook(
                    EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW,
                    IntPtr.Zero, _showProc, shellPid, 0, WINEVENT_OUTOFCONTEXT);
            }

            ApplyScale();
            PositionOnScreen();

            _anchorTimer.Start();
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
                // LAYERED enables per-pixel alpha via UpdateLayeredWindow. This
                // replaces the old TransparencyKey approach which left magenta
                // fringing on antialiased edges.
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        /// <summary>
        /// A layered window shows nothing at all until UpdateLayeredWindow has
        /// been called once - there is no WM_PAINT to fall back on. The
        /// constructor lays the window out before the handle exists, so the
        /// Render() calls in there quietly no-op, and the anchor timer only
        /// renders when the slot moved. Left to itself the pill would sit at
        /// the right place, report itself visible, and draw absolutely nothing.
        /// Render once the handle is real, and again on first show.
        /// </summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Render();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Render();
        }

        void ApplyScale()
        {
            IntPtr ctx = Dpi.EnterDpi();
            try
            {
                // Use the scale of the monitor where the taskbar lives.
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                _scale = Dpi.ScaleAt(wa.Right - 80, wa.Bottom - 80);

                // Size comes from the taskbar slot; set a default here that
                // PositionOnScreen will override once it has a measurement.
                int sz = FallbackSize;
                int barH = Taskbar.BarHeight();
                if (barH > 0)
                {
                    sz = Math.Max(barH - 8, 24);
                }
                Size = new Size(sz, sz);
            }
            finally { Dpi.LeaveDpi(ctx); }
        }

        void PositionOnScreen()
        {
            IntPtr ctx = Dpi.EnterDpi();
            try
            {
                // Zero, not a visual guess. The taskbar lays its buttons out on a flat
            // 44px pitch with the cells touching, so any gap at all puts us one
            // notch further out than every neighbour and reads as detached.
            int gap = 0;

                Rectangle slot;
                if (Taskbar.TryGetSlot(gap, out slot))
                {
                    _lastTaskbarSlot = slot;
                    Size = new Size(slot.Width, slot.Height);
                    Location = new Point(slot.X, slot.Y);
                    AssertTopmost();
                    Render();
                    return;
                }

                // Fallback: bottom-right of the work area, non-interactive position.
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                int sz = FallbackSize;
                int fbGap = (int)Math.Round(16 * _scale);
                Size = new Size(sz, sz);
                Location = new Point(wa.Right - sz - fbGap, wa.Bottom - sz - fbGap);
                AssertTopmost();
                Render();
            }
            finally { Dpi.LeaveDpi(ctx); }
        }

        void OnAnchorTick()
        {
            UpdateFullscreenVisibility();
            if (!Visible) return;      // nothing to place while we are out of the way
            SyncHoverToCursor();
            UpdateTaskbarPosition();
        }

        /// <summary>
        /// An overlay has to get out of the way of fullscreen. Two signals,
        /// because neither catches everything: SHQueryUserNotificationState
        /// reports exclusive-fullscreen D3D and presentation mode, while
        /// borderless-fullscreen video - a browser at full screen, VLC - looks
        /// like an ordinary window to it and is only caught by comparing the
        /// foreground window against its monitor.
        ///
        /// The shell's own windows are excluded deliberately: the desktop fills
        /// the monitor by definition, and counting it would hide the pill for
        /// good the moment the user clicked the wallpaper.
        /// </summary>
        void UpdateFullscreenVisibility()
        {
            bool fullscreen = false;

            try
            {
                int state;
                if (SHQueryUserNotificationState(out state) == 0)
                {
                    // 3 = QUNS_RUNNING_D3D_FULL_SCREEN, 4 = QUNS_PRESENTATION_MODE
                    if (state == 3 || state == 4) fullscreen = true;
                }
            }
            catch { }

            if (!fullscreen)
            {
                try
                {
                    IntPtr fg = GetForegroundWindow();
                    if (fg != IntPtr.Zero && fg != Handle && !IsShellWindow(fg) && !IsOverlayWindow(fg))
                    {
                        RECT r;
                        if (GetWindowRect(fg, out r))
                        {
                            Rectangle win = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                            Rectangle mon = Screen.FromRectangle(win).Bounds;
                            if (win.Width >= mon.Width && win.Height >= mon.Height)
                                fullscreen = true;
                        }
                    }
                }
                catch { }
            }

            if (fullscreen && Visible)
            {
                CloseStack();
                Hide();
            }
            else if (!fullscreen && !Visible)
            {
                Show();
                AssertTopmost();
                Render();
            }
        }

        /// <summary>
        /// True for a click-through overlay stretched across the screen -
        /// annotation layers, screenshot tools, Electron apps with a
        /// full-screen transparent surface. Measured on this machine: Forge
        /// Deck keeps one at exactly the monitor size, and counting it as
        /// fullscreen hid the pill permanently while nothing was actually
        /// fullscreen. A window that cannot be clicked or focused is not
        /// something the user is watching.
        /// </summary>
        static bool IsOverlayWindow(IntPtr hwnd)
        {
            try
            {
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                return (ex & WS_EX_TRANSPARENT) != 0 || (ex & WS_EX_NOACTIVATE) != 0;
            }
            catch { return false; }
        }

        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x20;

        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        /// <summary>True for the desktop and the taskbar, which are always monitor-sized.</summary>
        static bool IsShellWindow(IntPtr hwnd)
        {
            try
            {
                StringBuilder sb = new StringBuilder(64);
                GetClassName(hwnd, sb, sb.Capacity);
                string cls = sb.ToString();
                return cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd"
                    || cls == "Shell_SecondaryTrayWnd";
            }
            catch { return false; }
        }

        /// <summary>
        /// OnMouseLeave is not dependable on a layered, non-activating window:
        /// move the cursor straight from the pill onto another window and the
        /// leave never arrives, leaving the hover highlight burned on for good.
        /// The timer is already running, so let it settle the truth from where
        /// the cursor actually is.
        /// </summary>
        void SyncHoverToCursor()
        {
            bool inside = false;
            try { inside = Bounds.Contains(Cursor.Position); }
            catch { }

            if (inside != _isHovered)
            {
                _isHovered = inside;
                Render();
            }
        }

        [DllImport("shell32.dll")]
        static extern int SHQueryUserNotificationState(out int state);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        /// <summary>
        /// Recomputes and applies the taskbar slot position. Called from both the
        /// poll timer and the location-change WinEvent hook.
        ///
        /// Parenting into Shell_TrayWnd was tried and abandoned: SetParent succeeds
        /// but the taskbar's XAML content bridge stays above for hit testing, so
        /// the pill becomes visible but unclickable. We stay a floating topmost
        /// window instead.
        /// </summary>
        void UpdateTaskbarPosition()
        {
            IntPtr ctx = Dpi.EnterDpi();
            try
            {
                // Zero, not a visual guess. The taskbar lays its buttons out on a flat
            // 44px pitch with the cells touching, so any gap at all puts us one
            // notch further out than every neighbour and reads as detached.
            int gap = 0;

                Rectangle slot;
                if (!Taskbar.TryGetSlot(gap, out slot))
                {
                    // Measurement failed. Keep current position rather than
                    // jumping to a fallback - the taskbar may be mid-relayout.
                    return;
                }

                bool sizeChanged = slot.Width != Width || slot.Height != Height;
                bool posChanged = slot != _lastTaskbarSlot;

                if (!posChanged && !sizeChanged)
                {
                    return;
                }

                _lastTaskbarSlot = slot;
                Size = new Size(slot.Width, slot.Height);
                Location = new Point(slot.X, slot.Y);
                AssertTopmost();

                // Render on any change. The layered surface is ours to keep
                // current - nothing repaints it for us.
                Render();
            }
            finally { Dpi.LeaveDpi(ctx); }
        }

        void AssertTopmost()
        {
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        /// <summary>
        /// Starts (or restarts) a burst of topmost reassertions. Called from
        /// WinEvent hooks when the shell does something that might cover us.
        /// </summary>
        void StartTopmostBurst()
        {
            _burstEnd = DateTime.UtcNow.AddMilliseconds(BurstDurationMs);
            if (!_topmostBurst.Enabled)
            {
                _topmostBurst.Start();
            }
            // Immediate first reassertion
            AssertTopmost();
        }

        void OnTopmostBurstTick()
        {
            if (DateTime.UtcNow >= _burstEnd)
            {
                _topmostBurst.Stop();
                return;
            }
            AssertTopmost();
        }

        void OnForegroundEvent(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            // Called when any window comes to the foreground. Reassert our
            // topmost status so the shell or other apps cannot bury us.
            try
            {
                if (!IsHandleCreated)
                    return;
                StartTopmostBurst();
            }
            catch
            {
                // Swallow any exception - this runs on the message pump and
                // must not crash the process.
            }
        }

        void OnLocationChangeEvent(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            // Called when any window in the shell process moves or resizes. This
            // includes the taskbar buttons re-laying when Start opens. Kick a
            // cache refresh and reposition immediately.
            try
            {
                if (!IsHandleCreated)
                    return;

                // Kick an async UIA query (rate-limited internally).
                Taskbar.KickQuery();

                // Update position synchronously from the (possibly stale) cache.
                // The next query completion will fix any lag.
                UpdateTaskbarPosition();

                // Also start a topmost burst - shell relayout often covers us.
                StartTopmostBurst();
            }
            catch
            {
                // Swallow any exception.
            }
        }

        void OnShowEvent(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            // Called when a window in the shell process is shown. The Start menu
            // appearing triggers this, and it can cover us before any foreground
            // change fires.
            try
            {
                if (!IsHandleCreated)
                    return;
                StartTopmostBurst();
            }
            catch
            {
                // Swallow any exception.
            }
        }

        /// <summary>
        /// Returns the pill's bounding rectangle in screen coordinates. Used by
        /// StackForm to position itself above the pill.
        /// </summary>
        public Rectangle GetScreenBounds()
        {
            return new Rectangle(Left, Top, Width, Height);
        }

        // ----------------------------------------------------------
        // Layered window rendering
        // ----------------------------------------------------------

        /// <summary>
        /// Renders the pill to a 32-bit ARGB bitmap and pushes it to the layered
        /// window via UpdateLayeredWindow. Call this instead of Invalidate()
        /// whenever the visual state changes.
        /// </summary>
        void Render()
        {
            if (!IsHandleCreated)
                return;

            IntPtr ctx = Dpi.EnterDpi();
            try
            {
                int w = Width;
                int h = Height;
                if (w < 1 || h < 1)
                    return;

                using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    bmp.SetResolution(96f, 96f);
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        PaintContent(g, w, h);
                    }
                    Blit(bmp);
                }
            }
            finally { Dpi.LeaveDpi(ctx); }
        }

        /// <summary>
        /// Paints the pill content (highlight + icon) into the given Graphics.
        /// Transparent background with icon only; matches taskbar button style.
        /// </summary>
        void PaintContent(Graphics g, int w, int h)
        {
            int offsetY = _bounceOffset;

            // Hover/open highlight
            bool showHighlight = _isHovered || IsStackOpen;
            if (showHighlight)
            {
                int inset = TaskbarHoverInset;
                RectangleF highlightRect = new RectangleF(
                    inset, inset + offsetY,
                    w - inset * 2, h - inset * 2 - Math.Abs(offsetY));

                // White at 10% for hover, 18% for open
                int alpha = IsStackOpen ? 46 : 26;
                Color highlightColor = Color.FromArgb(alpha, 255, 255, 255);

                using (GraphicsPath path = Theme.Rounded(highlightRect, TaskbarHoverRadius))
                using (SolidBrush b = new SolidBrush(highlightColor))
                {
                    g.FillPath(b, path);
                }
            }

            // Windows 11 draws a 24px icon inside a 44px taskbar button, so
            // the artwork is a little over half the cell and the rest is
            // breathing room. Filling the cell the way we used to made the
            // folder tower over every neighbour.
            float iconSize = w * TaskbarIconRatio;
            float iconX = (w - iconSize) / 2f;
            float iconY = (h - iconSize) / 2f + offsetY;

            // Always the folder logo. Showing the newest download's own icon was
            // tried and dropped: the pill is the app's identity in the taskbar,
            // and a face that changes with every download is not one.
            Ui.DrawDownloadsIcon(g, new RectangleF(iconX, iconY, iconSize, iconSize));
        }


        /// <summary>
        /// Pushes an ARGB bitmap to the layered window, preserving per-pixel alpha.
        /// </summary>
        void Blit(Bitmap bmp)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr mem = CreateCompatibleDC(screen);
            IntPtr hBmp = IntPtr.Zero;
            IntPtr old = IntPtr.Zero;
            try
            {
                hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(mem, hBmp);

                SIZE size;
                size.cx = bmp.Width;
                size.cy = bmp.Height;
                BLITPOINT src;
                src.X = 0;
                src.Y = 0;
                BLITPOINT dst;
                dst.X = Left;
                dst.Y = Top;

                BLENDFUNCTION blend;
                blend.BlendOp = 0;               // AC_SRC_OVER
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = 1;           // AC_SRC_ALPHA

                UpdateLayeredWindow(Handle, screen, ref dst, ref size,
                                    mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(mem, old);
                if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BLITPOINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr obj);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref BLITPOINT dst,
            ref SIZE size, IntPtr hdcSrc, ref BLITPOINT src, int key,
            ref BLENDFUNCTION blend, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                        int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
            IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
            uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);

            if (_foregroundHook != IntPtr.Zero)
            {
                UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
            }
            if (_locationChangeHook != IntPtr.Zero)
            {
                UnhookWinEvent(_locationChangeHook);
                _locationChangeHook = IntPtr.Zero;
            }
            if (_showHook != IntPtr.Zero)
            {
                UnhookWinEvent(_showHook);
                _showHook = IntPtr.Zero;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (_foregroundHook != IntPtr.Zero)
            {
                UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
            }
            if (_locationChangeHook != IntPtr.Zero)
            {
                UnhookWinEvent(_locationChangeHook);
                _locationChangeHook = IntPtr.Zero;
            }
            if (_showHook != IntPtr.Zero)
            {
                UnhookWinEvent(_showHook);
                _showHook = IntPtr.Zero;
            }
            if (_topmostBurst != null)
            {
                _topmostBurst.Stop();
                _topmostBurst.Dispose();
                _topmostBurst = null;
            }
            base.Dispose(disposing);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (!_isHovered)
            {
                _isHovered = true;
                Render();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_isHovered)
            {
                _isHovered = false;
                Render();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                ToggleStack();
            }
            else if (e.Button == MouseButtons.Right)
            {
                // Everything the tray icon used to offer. The pill is already
                // on the taskbar; a second icon in the notification area just
                // to quit this one was one icon too many.
                CloseStack();
                ContextMenuStrip menu = Program.BuildPillMenu();
                if (menu != null)
                {
                    menu.Show(PointToScreen(e.Location));

                    // The pill is WS_EX_NOACTIVATE, so a menu it owns never
                    // takes the foreground - and a popup menu that was never
                    // activated never receives the click-elsewhere that would
                    // dismiss it, so it sat there until something was picked.
                    // This is the same trick tray menus have always needed.
                    SetForegroundWindow(menu.Handle);
                }
            }
        }

        void ToggleStack()
        {
            if (_stack != null && !_stack.IsDisposed && _stack.Visible)
            {
                _stack.Close();
                _stack = null;
                Render(); // Redraw to update highlight state
                return;
            }

            // A click that landed just after the stack closed itself is the
            // same gesture that closed it - the user reaching for the pill to
            // dismiss what was open - so swallow it rather than reopening.
            // NOACTIVATE should already stop the stack deactivating on a pill
            // click, but any other path that closes it leaves the same trap.
            if ((DateTime.UtcNow - _stackClosedAt).TotalMilliseconds < 250) return;

            {
                _stack = new StackForm(_config, _downloads, this);
                _stack.Closed += delegate { _stack = null; _stackClosedAt = DateTime.UtcNow; Render(); };

                // Position above the dock pill. Use GetScreenBounds() which handles
                // the attached vs floating case.
                IntPtr ctx = Dpi.EnterDpi();
                try
                {
                    Rectangle pillBounds = GetScreenBounds();
                    int x = pillBounds.Left + (pillBounds.Width - _stack.Width) / 2;
                    int y = pillBounds.Top - _stack.Height - (int)Math.Round(16 * _scale);

                    // Keep on screen
                    Rectangle wa = Screen.FromRectangle(pillBounds).WorkingArea;
                    if (x < wa.Left) x = wa.Left;
                    if (x + _stack.Width > wa.Right) x = wa.Right - _stack.Width;
                    if (y < wa.Top) y = wa.Top;

                    _stack.Location = new Point(x, y);
                }
                finally { Dpi.LeaveDpi(ctx); }

                // Grab what is behind the popup while there is still nothing
                // there. This has to happen after Location is final and before
                // Show, or we either capture the wrong part of the screen or
                // photograph the popup on top of itself.
                _stack.CaptureBackdrop();

                _stack.Show();

                // A layered window shows nothing until UpdateLayeredWindow has
                // run once - there is no WM_PAINT to fall back on.
                _stack.Render();
                _stack.StartOpenAnimation();

                // The pill cannot be activated, so showing from it does not
                // hand the popup the foreground on its own. Ask explicitly -
                // Windows grants it because we are handling the click that got
                // us here. Without this the popup paints but never has focus,
                // which costs it both Esc and close-on-click-away.
                _stack.Activate();
                SetForegroundWindow(_stack.Handle);

                Render(); // Redraw to show open highlight
            }
        }

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>
        /// Closes the stack popup if it is open. Called when starting a drag from
        /// StackForm so the popup does not vanish mid-drag.
        /// </summary>
        public void CloseStack()
        {
            if (_stack != null && !_stack.IsDisposed)
            {
                _stack.Close();
                _stack = null;
            }
        }

        public bool IsStackOpen
        {
            get { return _stack != null && !_stack.IsDisposed && _stack.Visible; }
        }

        void StartBounce()
        {
            _bounceFrame = 0;
            _bounce.Start();
        }

        void StepBounce()
        {
            // Simple bounce: rise up then settle back down, eased
            _bounceFrame++;

            // ~20 frames total at 8ms = 160ms
            if (_bounceFrame > 20)
            {
                _bounce.Stop();
                _bounceOffset = 0;
                Render();
                return;
            }

            // Sine-ish ease: peak at frame 5, settle by frame 20
            double t = _bounceFrame / 20.0;
            double ease = Math.Sin(t * Math.PI) * (1 - t);
            _bounceOffset = -(int)Math.Round(8 * _scale * ease);
            Render();
        }
    }
}
