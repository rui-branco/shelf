using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Shelf
{
    /// <summary>
    /// The stack: a Windows 11 style flyout above the pill with the folder's
    /// newest items in a scrolling grid, a header naming the folder and a footer
    /// with Open folder and the Recycle Bin. Tiles select on click and open on
    /// double-click, as in Explorer, preview with Space, drag out to
    /// anywhere that takes files, and take files dragged in.
    ///
    /// This is a layered window (WS_EX_LAYERED + UpdateLayeredWindow). That buys
    /// per-pixel alpha, so the corners are properly antialiased and the panel
    /// can cast its own soft shadow, and the open and close animations are only
    /// re-blits of a finished frame.
    ///
    /// The frosted backdrop is a snapshot taken before the window is shown: it
    /// will not track content moving behind the panel. For a popup that lives a
    /// few seconds that is fine, and it costs nothing while open.
    ///
    /// Everything static - shadow, material, rim, title - is drawn once per
    /// layout into _chrome. A frame copies that and draws only what changes:
    /// tiles, hover, buttons. That is what keeps hover and scrolling smooth.
    /// </summary>
    public partial class StackForm : Form
    {
        const int WS_EX_TOOLWINDOW = 0x80;
        const int WS_EX_LAYERED = 0x80000;

        // Layout, in logical pixels at 100%. Scaled by the monitor's DPI.
        const int ShadowPx = 28;        // transparent border the shadow is drawn into
        const int RadiusPx = 8;         // Windows 11 flyout corner
        const int HeaderPx = 52;
        const int FooterPx = 52;
        const int PadPx = 12;           // grid inset from the panel's sides
        const int TileWPx = 88;
        const int TileHPx = 100;
        const int GapPx = 4;
        const int IconPx = 48;
        const int IconTopPx = 10;
        const int GridBottomPx = 8;
        const int EmptyPx = 172;
        const int MinCols = 4;
        const int MaxCols = 6;
        const int MaxRows = 4;
        const int BarGapPx = 12;        // clearance above the taskbar, as Windows' own flyouts
        const int EdgePx = 8;           // nearest the panel comes to a screen edge
        const int LabelFontPx = 12;
        const int TitleFontPx = 14;

        const int OpenMs = 180;
        const int CloseMs = 110;
        const int RisePx = 14;

        enum Part { None, Tile, OpenFolder, Bin, Outside }

        sealed class Tile
        {
            public DownloadItem Item;
            public string Label;       // fitted to two lines on first paint
            public bool IsNew;
        }

        AppConfig _config;
        Downloads _downloads;
        DockForm _dock;

        float _s = 1f;
        int _m, _header, _footer, _pad, _tileW, _tileH, _gap, _icon, _iconTop, _gridBottom;

        List<Tile> _tiles = new List<Tile>();
        int _cols = MinCols;
        Rectangle _panel;          // client coordinates, inside the shadow margin
        Rectangle _grid;           // the scrolling viewport
        Rectangle _openBtn;
        Rectangle _binBtn;
        Size _window;
        int _contentH;
        int _maxScroll;
        float _scroll;
        float _scrollTarget;

        Rectangle _pill;
        Rectangle _bar;
        Point _anchor;             // where the window sits once settled

        int _hover = -1;
        Part _hoverPart;
        int _press = -1;
        Part _pressPart;
        Point _pressPoint;

        bool _dragging;
        bool _dropHover;
        bool _dropCopy;
        bool _binHover;
        bool _closing;
        bool _modal;
        bool _stale;

        readonly LayeredSurface _surface = new LayeredSurface();
        Bitmap _chrome;
        Bitmap _frost;
        Point _frostOrigin;

        Timer _anim;
        readonly Stopwatch _clock = new Stopwatch();
        byte _alpha;
        int _offset;

        ContextMenuStrip _menu;
        EventHandler _onChanged;
        EventHandler _onThumb;

        public StackForm(AppConfig config, Downloads downloads, DockForm dock)
        {
            _config = config;
            _downloads = downloads;
            _dock = dock;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            AllowDrop = true;

            _anim = new Timer();
            _anim.Interval = 15;
            _anim.Tick += delegate { OnAnimTick(); };

            // Live while open: a download finishing while the panel is up
            // shows up in it, and thumbnails arriving fill themselves in.
            _onChanged = delegate { OnDownloadsChanged(); };
            _onThumb = delegate { if (IsHandleCreated && !_closing) Render(); };
            _downloads.Changed += _onChanged;
            Thumbs.Loaded += _onThumb;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // TOOLWINDOW keeps us out of Alt-Tab. LAYERED enables per-pixel
                // alpha via UpdateLayeredWindow.
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_LAYERED;
                return cp;
            }
        }

        // No ShowWithoutActivation here, deliberately. This popup has to take
        // focus: it is how Esc and the arrow keys reach it, and it is the only
        // way OnDeactivate ever fires - a window that was never activated is
        // never deactivated, so opening it unfocused would leave it on screen
        // with no way to dismiss it by clicking away. The pill is the half of
        // the pair that stays unfocusable.

        /// <summary>True once the panel has started its closing animation.</summary>
        public bool IsClosing
        {
            get { return _closing; }
        }

        /// <summary>The thumbnail size the panel asks for, so the app can fetch ahead.</summary>
        public static int ThumbSizeFor(float scale)
        {
            return (int)Math.Round(IconPx * scale);
        }

        /// <summary>How many tiles the panel shows without scrolling, at most.</summary>
        public static int VisibleCapacity
        {
            get { return MaxCols * MaxRows; }
        }

        int R(float px)
        {
            return (int)Math.Round(px * _s);
        }

        // ----------------------------------------------------------
        // Opening and closing
        // ----------------------------------------------------------

        /// <summary>
        /// Lays the panel out above the pill, photographs what is behind it,
        /// and shows it with a short rise-and-fade.
        /// </summary>
        public void Open(Rectangle pill, Rectangle bar)
        {
            Theme.Refresh();
            Prepare(pill, bar);

            // Grab what is behind the popup while there is still nothing
            // there. This has to happen after the position is final and before
            // Show, or we either capture the wrong part of the screen or
            // photograph the popup on top of itself.
            CaptureBackdrop();
            BuildChrome();

            Bounds = new Rectangle(_anchor, _window);
            _alpha = 0;
            _offset = R(RisePx);
            _clock.Restart();

            Show();
            Render();
            _anim.Start();
            Activate();
        }

        void Prepare(Rectangle pill, Rectangle bar)
        {
            _pill = pill;
            _bar = bar;
            _s = Dpi.ScaleAt(new Point(pill.Left + pill.Width / 2, pill.Top + pill.Height / 2));
            Metrics();
            BuildTiles();
            LayoutPanel();
            Place();
        }

        /// <summary>
        /// Fades out and closes. Every way of dismissing the panel comes
        /// through here, so it always leaves the same way.
        /// </summary>
        public void Dismiss()
        {
            if (_closing || IsDisposed) return;
            Peek.Close(Peek.From.Shelf);
            _closing = true;
            _clock.Restart();
            _anim.Start();
            if (_dock != null) _dock.RefreshPill();
        }

        void OnAnimTick()
        {
            bool more = false;

            if (_clock.IsRunning)
            {
                double t = Math.Min(1.0, _clock.ElapsedMilliseconds / (double)(_closing ? CloseMs : OpenMs));
                if (_closing)
                {
                    double e = t * t;
                    _alpha = (byte)Math.Round(255 * (1 - e));
                    _offset = (int)Math.Round(e * R(6));
                }
                else
                {
                    // Ease out: most of the travel in the first frames, so it
                    // reads as instant but still settles rather than snapping.
                    double e = 1 - Math.Pow(1 - t, 3);
                    _alpha = (byte)Math.Round(255 * e);
                    _offset = (int)Math.Round((1 - e) * R(RisePx));
                }

                if (t >= 1)
                {
                    _clock.Stop();
                    if (_closing)
                    {
                        _anim.Stop();
                        Close();
                        return;
                    }
                }
                else
                {
                    more = true;
                }
            }

            // A rubber band held past the edge of the grid scrolls it.
            if (AutoScrollBand()) more = true;

            bool redraw = false;
            float diff = _scrollTarget - _scroll;
            if (Math.Abs(diff) > 0.5f)
            {
                _scroll += diff * 0.35f;
                redraw = more = true;
            }
            else if (_scroll != _scrollTarget)
            {
                _scroll = _scrollTarget;
                redraw = true;
            }

            if (redraw)
            {
                if (!_banding) UpdateHover(PointToClient(Cursor.Position));
                Render();
            }
            else
            {
                Present();
            }

            if (!more) _anim.Stop();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);

            // Close on losing focus, but not while dragging (the panel steps
            // aside mid-drag) or while it has a message box of its own up.
            if (!_dragging && !_modal) Dismiss();
        }

        // ----------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------

        static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        // ----------------------------------------------------------
        // Design checks
        // ----------------------------------------------------------

        /// <summary>
        /// Draws one finished frame into a bitmap without showing anything:
        /// for checking the design and for README screenshots. The caller pumps
        /// messages between calls so thumbnails can arrive.
        /// </summary>
        public Bitmap RenderPreview(Rectangle pill, Rectangle bar, Bitmap backdrop, Point backdropOrigin, int hover)
        {
            Prepare(pill, bar);
            if (_frost != null) _frost.Dispose();
            _frost = backdrop != null ? Blur.Frost(backdrop) : null;
            _frostOrigin = backdropOrigin;
            BuildChrome();

            _hover = hover;
            _hoverPart = hover >= 0 ? Part.Tile : Part.None;

            Bitmap frame = new Bitmap(_window.Width, _window.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(frame))
            {
                g.Clear(Color.Transparent);
                PaintFrame(g);
            }
            return frame;
        }

        // ----------------------------------------------------------
        // Teardown
        // ----------------------------------------------------------

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);

            _downloads.Changed -= _onChanged;
            Thumbs.Loaded -= _onThumb;
            Peek.Close(Peek.From.Shelf);

            if (_anim != null)
            {
                _anim.Stop();
                _anim.Dispose();
                _anim = null;
            }
            if (_menu != null)
            {
                _menu.Dispose();
                _menu = null;
            }
            _surface.Dispose();
            if (_chrome != null) { _chrome.Dispose(); _chrome = null; }
            if (_frost != null) { _frost.Dispose(); _frost = null; }
        }
    }
}
