using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Shelf
{
    /// <summary>
    /// The stack popup: a grid of the most recent Downloads files with thumbnails,
    /// plus a trash tile. Supports drag-out to other applications and drag-in from
    /// Explorer to import files into the watched folder.
    ///
    /// This is a layered window (WS_EX_LAYERED + UpdateLayeredWindow) that captures
    /// and blurs the screen behind it before showing. Per-pixel alpha gives clean
    /// rounded corners and, because we push a whole bitmap every frame, ghosting
    /// is impossible.
    ///
    /// The backdrop is a snapshot: it will not track content moving behind the
    /// popup. This is fine and expected for a transient popup.
    /// </summary>
    public class StackForm : Form
    {
        const int WS_EX_TOOLWINDOW = 0x80;
        const int WS_EX_LAYERED = 0x80000;
        const int MaxColumns = 6;
        const int TilePad = 8;
        const int ThumbSize = 40;     // thumbnail size within the tile
        const int LabelHeight = 18;
        const float LabelPt = 8f;
        const int CornerRadius = 8;

        AppConfig _config;
        Downloads _downloads;
        DockForm _dock;
        float _scale = 1f;
        int _tileSize;

        List<TileInfo> _tiles;
        int _hoverIndex = -1;
        int _trashIndex = -1;

        /// <summary>
        /// Columns the current grid actually uses. Not a constant: it shrinks
        /// for a nearly empty stack and again if a full row would not fit the
        /// monitor, and painting and hit testing both have to agree with the
        /// number the layout settled on.
        /// </summary>
        int _cols = 1;

        // Drag state
        int _pressIndex = -1;
        Point _pressPoint;
        bool _dragging;

        // Drop hover state: true while a valid non-trash drop is hovering, so we
        // can draw an affordance outline indicating where the files will land.
        bool _dropHover;

        // The composed frame, kept so the open animation can re-blit it at
        // different offsets without repainting every tile.
        Bitmap _frame;

        // Open animation: the panel rises the last few pixels as it fades in.
        Timer _openAnim;
        // Starts at zero, not 255. The first Render happens before the timer
        // does, and blitting a finished panel for one frame before fading it in
        // from nothing is exactly the flash that read as a glitch.
        byte _openAlpha;
        int _openOffsetY;

        /// <summary>
        /// Where the panel is meant to sit, independent of the animation.
        ///
        /// UpdateLayeredWindow does not just draw - it MOVES the window to the
        /// destination point it is given. Blitting at Top + offset therefore
        /// moved the window down by the offset, and the next frame read that
        /// new Top and added the offset again. The panel walked itself roughly
        /// 75px down the screen over the course of the open animation and ended
        /// up sitting on the taskbar, no matter what gap was configured.
        /// </summary>
        Point _anchor = Point.Empty;
        bool _anchorSet;
        const int OpenRisePx = 26;
        // Clearance above the taskbar. Windows' own thumbnail previews stand
        // well clear of the bar rather than resting on it.
        const int PanelGapPx = 16;

        // Blurred backdrop captured before the window is shown
        Bitmap _backdrop;
        Bitmap _blurredBackdrop;
        bool _backdropCaptured;
        Point _backdropOrigin;

        // Thumbnail cache: (path, lastWrite) -> bitmap
        Dictionary<string, Bitmap> _thumbCache;

        ContextMenuStrip _menu;
        bool _menuBuilt;

        public StackForm(AppConfig config, Downloads downloads, DockForm dock)
        {
            _config = config;
            _downloads = downloads;
            _dock = dock;
            _thumbCache = new Dictionary<string, Bitmap>();

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;
            AllowDrop = true;
            KeyPreview = true;

            ApplyScale();
            BuildTiles();

            // Set the animation's starting offset now, so the very first blit
            // already shows the panel low and invisible rather than finished.
            _openOffsetY = (int)Math.Round(OpenRisePx * _scale);
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
        // focus: it is how Esc reaches it, and it is the only way OnDeactivate
        // ever fires - a window that was never activated is never deactivated,
        // so opening it unfocused would leave it on screen with no way to
        // dismiss it by clicking away. The pill is the half of the pair that
        // stays unfocusable.

        void ApplyScale()
        {
            IntPtr ctx = Dpi.EnterDpi();
            try
            {
                _scale = Dpi.ScaleForWindow(_dock.Handle);
                _tileSize = (int)Math.Round(_config.TileSize * _scale);
            }
            finally { Dpi.LeaveDpi(ctx); }
        }

        void BuildTiles()
        {
            _tiles = new List<TileInfo>();

            // The folder itself leads, the way a macOS stack does - it is the
            // one entry that is always there and always means the same thing,
            // so it gets the position the eye lands on first.
            TileInfo openFolder = new TileInfo();
            openFolder.IsOpenFolder = true;
            openFolder.Path = _downloads.Folder;
            // Named after the folder it opens, not after the verb. Taken from
            // the path so pointing the app at something other than Downloads
            // still labels itself honestly.
            openFolder.Name = FolderDisplayName(_downloads.Folder);
            _tiles.Add(openFolder);

            // Oldest first, so the newest download lands last - immediately
            // before the Recycle Bin. Downloads.Items is newest-first because
            // that is what the pill wants; the grid wants the opposite end.
            List<DownloadItem> items = _downloads.Items;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                DownloadItem item = items[i];
                TileInfo t = new TileInfo();
                t.Path = item.Path;
                t.Name = item.Name;
                t.Modified = item.Modified;
                t.IsTrash = false;
                _tiles.Add(t);
            }

            // Trash closes the grid out, so the newest downloads end up right
            // beside it - the two things you reach for together when clearing
            // out what just landed.
            TileInfo trash = new TileInfo();
            trash.IsTrash = true;
            trash.Name = "Recycle Bin";
            _tiles.Add(trash);
            _trashIndex = _tiles.Count - 1;

            // Grid size. Two rules: never wider than MaxColumns, and never
            // wider than what is actually on screen. The old code always laid
            // out a full row even for two files, so a nearly empty stack still
            // opened as a wide slab of empty panel.
            int count = _tiles.Count;
            int pad = (int)Math.Round(TilePad * _scale);

            int cols = Math.Min(MaxColumns, count);
            if (cols < 1) cols = 1;

            // Shrink to fit the monitor if a full row would overflow it. The
            // popup is positioned against a screen edge, so a panel wider than
            // the work area cannot simply be nudged back into view.
            Rectangle wa = Screen.FromHandle(_dock.Handle).WorkingArea;
            int margin = (int)Math.Round(24 * _scale);
            while (cols > 1 && cols * _tileSize + (cols + 1) * pad > wa.Width - margin)
                cols--;

            // Never leave a single tile stranded on its own row. Recycle Bin
            // and Recent are the last two and belong side by side; at 13 tiles
            // across 6 columns the remainder is 1, which exiles Recent to a row
            // of its own. Narrowing the grid by a column fixes the orphan and
            // costs nothing, since the panel sizes itself to the columns.
            while (cols > 2 && count % cols == 1)
                cols--;

            _cols = cols;
            int rows = (count + cols - 1) / cols;

            int w = cols * _tileSize + (cols + 1) * pad;
            int h = rows * _tileSize + (rows + 1) * pad;

            Size = new Size(w, h);

            // Preload thumbnails
            foreach (TileInfo t in _tiles)
            {
                if (!t.IsTrash && !t.IsOpenFolder && t.Path != null)
                {
                    GetThumb(t);
                }
            }
        }

        /// <summary>
        /// Rebuilds the grid and re-anchors the panel above the pill.
        ///
        /// BuildTiles alone only changes Size, and the window grows and shrinks
        /// from its top-left corner. Delete a file and the last row disappears,
        /// but the panel stays pinned where it was, so it drifts away from the
        /// pill and leaves a dead band where the row used to be. Anything that
        /// changes the tile list has to go through here, not BuildTiles.
        /// </summary>
        public void Relayout()
        {
            BuildTiles();

            IntPtr ctx = Dpi.EnterDpi();
            try
            {
                Rectangle pill = _dock.GetScreenBounds();
                int x = pill.Left + (pill.Width - Width) / 2;
                int y = pill.Top - Height - (int)Math.Round(PanelGapPx * _scale);

                Rectangle wa = Screen.FromRectangle(pill).WorkingArea;
                if (x < wa.Left) x = wa.Left;
                if (x + Width > wa.Right) x = wa.Right - Width;
                if (y < wa.Top) y = wa.Top;

                Location = new Point(x, y);
                SetAnchor();
            }
            finally { Dpi.LeaveDpi(ctx); }

            Render();
        }

        /// <summary>
        /// A tile's target may be a folder as well as a file - a Downloads
        /// folder full of extracted archives is still a Downloads folder, and
        /// File.Exists answers false for every one of them. Guarding on
        /// File.Exists alone left folders drawn in the grid but dead to every
        /// click, drag and menu item.
        /// </summary>
        static bool TargetExists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return File.Exists(path) || Directory.Exists(path);
        }

        /// <summary>
        /// The real Recycle Bin icon, straight from the shell, so it matches
        /// what Explorer shows - including switching between empty and full.
        /// Cached per size because this repaints often.
        /// </summary>
        /// <summary>
        /// The folder's own name for its tile label. GetFileName comes back
        /// empty when a path carries a trailing separator, so trim first rather
        /// than shipping a nameless tile.
        /// </summary>
        static string FolderDisplayName(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return "Folder";
            try
            {
                string trimmed = folder.TrimEnd(Path.DirectorySeparatorChar,
                                                Path.AltDirectorySeparatorChar);
                string name = Path.GetFileName(trimmed);
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { }
            return "Folder";
        }


        /// <summary>The watched folder's own shell icon, for the folder tile.</summary>
        Bitmap GetFolderIcon(int size)
        {
            string key = "::folder|" + size.ToString();
            Bitmap bmp;
            if (_thumbCache.TryGetValue(key, out bmp)) return bmp;

            bmp = Shell.GetThumbnail(_downloads.Folder, size);
            if (bmp != null) _thumbCache[key] = bmp;
            return bmp;
        }

        Bitmap GetTrashIcon(int size)
        {
            string key = "::trash|" + size.ToString();
            Bitmap bmp;
            if (_thumbCache.TryGetValue(key, out bmp)) return bmp;

            bmp = Shell.GetThumbnail("shell:RecycleBinFolder", size);
            if (bmp != null) _thumbCache[key] = bmp;
            return bmp;
        }

        Bitmap GetThumb(TileInfo t)
        {
            if (t.IsTrash || t.IsOpenFolder) return null;
            string key = t.Path + "|" + t.Modified.Ticks.ToString();
            Bitmap bmp;
            if (_thumbCache.TryGetValue(key, out bmp)) return bmp;

            int sz = (int)Math.Round(ThumbSize * _scale);
            bmp = Shell.GetThumbnail(t.Path, sz);
            if (bmp != null) _thumbCache[key] = bmp;
            return bmp;
        }

        // ----------------------------------------------------------
        // Layered window rendering via UpdateLayeredWindow
        // ----------------------------------------------------------

        /// <summary>
        /// Captures the backdrop before the window is shown. Called from
        /// DockForm.ToggleStack after Location is set but before Show().
        /// </summary>
        public void CaptureBackdrop()
        {
            if (_backdropCaptured) return;
            _backdropCaptured = true;

            // Capture more than we currently need. Deleting a file reflows the
            // grid and the panel changes size and position, and we cannot
            // re-capture afterwards without photographing ourselves. Grabbing a
            // generous region once and cropping out of it means any later
            // layout still has real backdrop underneath it.
            int pad = (int)Math.Round(TilePad * _scale);
            int maxW = MaxColumns * _tileSize + (MaxColumns + 1) * pad;
            int maxH = 3 * _tileSize + 4 * pad;

            Rectangle wa = Screen.FromHandle(_dock.Handle).WorkingArea;
            int cx = Left + Width / 2;
            int x = cx - maxW / 2;
            int y = Bottom - maxH;

            if (x < wa.Left) x = wa.Left;
            if (x + maxW > wa.Right) x = wa.Right - maxW;
            if (y < wa.Top) y = wa.Top;
            if (maxW > wa.Width) { x = wa.Left; maxW = wa.Width; }
            if (maxH > wa.Height) { y = wa.Top; maxH = wa.Height; }

            _backdropOrigin = new Point(x, y);
            _backdrop = Blur.CaptureBehind(new Rectangle(x, y, maxW, maxH));
            if (_backdrop != null)
            {
                _blurredBackdrop = Blur.BoxBlur(_backdrop, 3);
            }
        }

        /// <summary>
        /// Renders the popup to a 32-bit ARGB bitmap and pushes it to the layered
        /// window via UpdateLayeredWindow. Call this instead of Invalidate()
        /// whenever the visual state changes.
        /// </summary>
        public void Render()
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

                // Keep the composed frame. The open animation re-blits it a
                // few dozen times at different offsets and opacities, and
                // repainting every tile and thumbnail per frame for that would
                // be pure waste.
                if (_frame != null) _frame.Dispose();
                _frame = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                _frame.SetResolution(96f, 96f);
                using (Graphics g = Graphics.FromImage(_frame))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.Clear(Color.Transparent);
                    PaintContent(g, w, h);
                }
                Blit(_frame, _openAlpha, _openOffsetY);
            }
            finally { Dpi.LeaveDpi(ctx); }
        }

        /// <summary>
        /// Paints the popup content into the given Graphics.
        /// </summary>
        void PaintContent(Graphics g, int w, int h)
        {
            float radius = CornerRadius * _scale;
            RectangleF panelRect = new RectangleF(0, 0, w, h);

            using (GraphicsPath clipPath = Theme.Rounded(panelRect, radius))
            {
                // Clip everything to the rounded rect
                g.SetClip(clipPath);

                // 1. Draw blurred backdrop
                if (_blurredBackdrop != null)
                {
                    // The capture covers more than this panel, so index into it
                    // by where we currently sit. A relayout moves and resizes
                    // the window; the snapshot underneath does not move with it.
                    g.DrawImageUnscaled(_blurredBackdrop,
                                        _backdropOrigin.X - Left,
                                        _backdropOrigin.Y - Top);
                }
                else
                {
                    // Fallback: solid card color if capture failed
                    using (SolidBrush fill = new SolidBrush(Theme.Card))
                    {
                        g.FillRectangle(fill, 0, 0, w, h);
                    }
                }

                // 2. Dark tint over the blur so text stays legible
                // Tint over the blurred snapshot. Heavier than it first was:
                // at 105 a bright window behind the panel still showed through
                // enough to fight the labels for attention.
                using (SolidBrush tint = new SolidBrush(Color.FromArgb(215, Theme.Card)))
                {
                    g.FillRectangle(tint, 0, 0, w, h);
                }

                g.ResetClip();

                // 3. Border (1px just inside the rounded edge)
                using (GraphicsPath borderPath = Theme.Rounded(new RectangleF(0.5f, 0.5f, w - 1f, h - 1f), radius))
                using (Pen border = new Pen(Theme.Border, 1f))
                {
                    g.DrawPath(border, borderPath);
                }

                // Drop hover affordance: accent outline and faint wash when a valid
                // non-trash drop is hovering, so the user knows "drop here".
                if (_dropHover)
                {
                    using (SolidBrush wash = new SolidBrush(Color.FromArgb(18, Theme.Accent)))
                    {
                        g.FillRectangle(wash, 2, 2, w - 4, h - 4);
                    }

                    using (Pen accent = new Pen(Theme.Accent, 2f))
                    {
                        g.DrawRectangle(accent, 1f, 1f, w - 2f, h - 2f);
                    }
                }

                // 4. Draw tiles
                int pad = (int)Math.Round(TilePad * _scale);
                int cols = _cols;

                for (int i = 0; i < _tiles.Count; i++)
                {
                    TileInfo t = _tiles[i];
                    int col = i % cols;
                    int row = i / cols;
                    int x = pad + col * (_tileSize + pad);
                    int y = pad + row * (_tileSize + pad);

                    DrawTile(g, t, x, y, i == _hoverIndex);
                }
            }
        }

        void DrawTile(Graphics g, TileInfo t, int x, int y, bool hover)
        {
            float s = _scale;
            int ts = _tileSize;

            // No plate behind each icon. They sit straight on the frosted
            // panel the way a macOS stack shows them - a translucent card under
            // every tile was another opaque layer fighting the blur. Hover
            // still draws one, because that one means something.

            // Hover highlight
            if (hover)
            {
                RectangleF r = new RectangleF(x, y, ts, ts);
                using (GraphicsPath path = Theme.Rounded(r, 8 * s))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(200, Theme.CardHi)))
                {
                    g.FillPath(b, path);
                }
            }

            int thumbSz = (int)Math.Round(ThumbSize * s);
            int thumbX = x + (ts - thumbSz) / 2;

            // Share the leftover height out evenly: one gap above the icon, one
            // between icon and label, one below the label.
            //
            // labelH is the height of the *text*, measured, not the nominal
            // LabelHeight box. The box is taller than the glyphs and the text
            // draws at its top, so budgeting for the box left that difference
            // as dead space below the caption - which is exactly why the bottom
            // gap looked bigger than the top one.
            int labelH;
            using (Font probe = Theme.Font(LabelPt * s, FontStyle.Regular))
                labelH = (int)Math.Ceiling(probe.GetHeight(g));

            int slack = ts - thumbSz - labelH;
            int gap = slack / 3;
            if (gap < 0) gap = 0;
            int thumbY = y + gap;

            {
                // Files and the bin draw the same way: whatever icon the
                // shell itself would show. SHCreateItemFromParsingName takes
                // shell: paths as happily as filesystem ones, so the bin needs
                // no special case beyond its path - and it comes back already
                // correct for empty versus full, which a drawn glyph never was.
                Bitmap thumb = t.IsTrash ? GetTrashIcon(thumbSz) : (t.IsOpenFolder ? GetFolderIcon(thumbSz) : GetThumb(t));
                if (thumb != null)
                {
                    // Center the thumbnail, maintaining aspect ratio
                    int tw = thumb.Width;
                    int th = thumb.Height;
                    float scale = Math.Min((float)thumbSz / tw, (float)thumbSz / th);
                    int dw = (int)(tw * scale);
                    int dh = (int)(th * scale);
                    int dx = thumbX + (thumbSz - dw) / 2;
                    int dy = thumbY + (thumbSz - dh) / 2;

                    g.DrawImage(thumb, dx, dy, dw, dh);
                }
            }

            // Label sits directly under the icon, one gap below it, with the
            // same gap again beneath the text and the tile edge.
            int labelY = thumbY + thumbSz + gap;
            int labelPad = (int)Math.Round(3 * s);
            RectangleF labelRect = new RectangleF(x + labelPad, labelY,
                                                  ts - labelPad * 2, labelH);

            using (Font f = Theme.Font(LabelPt * s, FontStyle.Regular))
            using (SolidBrush labelBrush = new SolidBrush(Theme.Text))
            using (StringFormat fmt = new StringFormat())
            {
                fmt.Alignment = StringAlignment.Center;
                fmt.LineAlignment = StringAlignment.Near;
                fmt.Trimming = StringTrimming.EllipsisCharacter;
                fmt.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(t.Name, f, labelBrush, labelRect, fmt);
            }
        }

        /// <summary>
        /// Pushes an ARGB bitmap to the layered window, preserving per-pixel alpha.
        /// </summary>
        void Blit(Bitmap bmp) { Blit(bmp, 255, 0); }

        /// <summary>
        /// Pushes the frame, optionally faded and nudged down the screen. The
        /// open animation drives both: the panel rises the last few pixels into
        /// place as it fades in, which is what the taskbar's own thumbnail
        /// previews do.
        /// </summary>
        void Blit(Bitmap bmp, byte alpha, int offsetY)
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
                // Always from the anchor, never from the current Top - see the
                // note on _anchor. Reading Top here is what made the window
                // drift down a frame at a time.
                BLITPOINT dst;
                dst.X = _anchorSet ? _anchor.X : Left;
                dst.Y = (_anchorSet ? _anchor.Y : Top) + offsetY;

                BLENDFUNCTION blend;
                blend.BlendOp = 0;               // AC_SRC_OVER
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = alpha;
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

        // ----------------------------------------------------------
        // Mouse and input handling
        // ----------------------------------------------------------

        int HitTest(Point pt)
        {
            int pad = (int)Math.Round(TilePad * _scale);
            int cols = _cols;

            for (int i = 0; i < _tiles.Count; i++)
            {
                int col = i % cols;
                int row = i / cols;
                int x = pad + col * (_tileSize + pad);
                int y = pad + row * (_tileSize + pad);

                Rectangle r = new Rectangle(x, y, _tileSize, _tileSize);
                if (r.Contains(pt)) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            int idx = HitTest(e.Location);
            if (idx != _hoverIndex)
            {
                _hoverIndex = idx;
                // Every tile does something on click, so the pointer changes to
                // say so; the gaps between them do not.
                Cursor = idx >= 0 ? Cursors.Hand : Cursors.Default;
                Render();
            }

            // Drag detection
            if (_pressIndex >= 0 && e.Button == MouseButtons.Left && !_dragging)
            {
                if (Math.Abs(e.X - _pressPoint.X) >= SystemInformation.DragSize.Width ||
                    Math.Abs(e.Y - _pressPoint.Y) >= SystemInformation.DragSize.Height)
                {
                    TileInfo t = _tiles[_pressIndex];
                    if (!t.IsTrash && !t.IsOpenFolder && t.Path != null && TargetExists(t.Path))
                    {
                        StartFileDrag(t.Path);
                    }
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            int idx = HitTest(e.Location);

            if (e.Button == MouseButtons.Left)
            {
                _pressIndex = idx;
                _pressPoint = e.Location;
                _dragging = false;
            }
            else if (e.Button == MouseButtons.Right && idx >= 0)
            {
                TileInfo t = _tiles[idx];
                if (!t.IsTrash && !t.IsOpenFolder && t.Path != null)
                {
                    EnsureMenu();
                    _menu.Tag = t.Path;
                    _menu.Show(this, e.Location);
                }
            }
        }

        void EnsureMenu()
        {
            if (_menuBuilt)
                return;
            _menuBuilt = true;
            BuildMenu();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (e.Button != MouseButtons.Left) return;

            int idx = HitTest(e.Location);

            if (!_dragging && idx >= 0 && idx == _pressIndex)
            {
                TileInfo t = _tiles[idx];
                if (t.IsTrash)
                {
                    // Open Recycle Bin
                    try { Process.Start("explorer.exe", "shell:RecycleBinFolder"); }
                    catch { }
                }
                else if (t.IsOpenFolder)
                {
                    try { Process.Start("explorer.exe", "\"" + _downloads.Folder + "\""); }
                    catch { }
                    Close();
                }
                else if (t.Path != null && TargetExists(t.Path))
                {
                    // Open the file
                    try { Process.Start(t.Path); }
                    catch { }
                    Close();
                }
            }

            _pressIndex = -1;
            _dragging = false;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverIndex != -1)
            {
                _hoverIndex = -1;
                Render();
            }
        }

        /// <summary>
        /// Re-shows the panel mid-drag without stealing the drag. Activating it
        /// here would end the drag loop, so this goes through ShowWindow with
        /// SW_SHOWNOACTIVATE rather than Show().
        /// </summary>
        /// <summary>
        /// Slides the panel up into place as it fades in. Started by DockForm
        /// right after the first Render, so the opening frame is already
        /// composed and each step is only a re-blit.
        /// </summary>
        public void StartOpenAnimation()
        {
            // DockForm has just placed us; that position is the anchor every
            // animation frame blits against.
            SetAnchor();

            if (_openAnim == null)
            {
                _openAnim = new Timer();
                _openAnim.Interval = 8;            // ~120fps, no visible stepping
                _openAnim.Tick += delegate(object s, EventArgs e) { StepOpenAnimation(); };
            }
            _openAnim.Start();
            BlitCurrent();
        }

        void StepOpenAnimation()
        {
            // Ease out: most of the distance goes in the first frames, so it
            // reads as immediate but still settles instead of snapping.
            int remaining = 255 - _openAlpha;
            int step = (int)Math.Round(remaining * 0.34);
            if (step < 1) step = 1;
            int next = _openAlpha + step;
            _openAlpha = next > 255 ? (byte)255 : (byte)next;

            _openOffsetY = (int)Math.Round(OpenRisePx * _scale * (255 - _openAlpha) / 255.0);

            BlitCurrent();

            if (_openAlpha >= 255)
            {
                _openOffsetY = 0;
                _openAnim.Stop();
                BlitCurrent();
            }
        }

        /// <summary>Records the current Location as the blit origin.</summary>
        void SetAnchor()
        {
            _anchor = Location;
            _anchorSet = true;
        }

        void BlitCurrent()
        {
            if (_frame == null || !IsHandleCreated) return;
            try { Blit(_frame, _openAlpha, _openOffsetY); }
            catch { }
        }

        void ShowDuringDrag()
        {
            try
            {
                // No Render here. A layered window keeps its surface while
                // hidden, so the panel is still drawn and re-rendering it on
                // every return - backdrop, thumbnails and all - was the stall.
                ShowWindow(Handle, SW_SHOWNOACTIVATE);
            }
            catch { }
        }

        const int SW_SHOWNOACTIVATE = 4;

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        void StartFileDrag(string path)
        {
            _dragging = true;

            // Get the panel out of the way: it sits directly over the area you
            // are most likely to be dragging towards, and a drop target you
            // cannot see is not a drop target.
            Hide();

            // Build the data object with file drop list
            DataObject data = new DataObject();
            StringCollection sc = new StringCollection();
            sc.Add(path);
            data.SetFileDropList(sc);

            // Carry the file's own icon under the cursor. This is ours, not the
            // shell's: IDragSourceHelper only draws when the drop target calls
            // IDropTargetHelper, so over anything that does not participate the
            // image never appeared at all.
            DragGhost ghost = null;
            GiveFeedbackEventHandler feedback = null;
            try
            {
                int dragSz = (int)Math.Round(48 * _scale);
                Bitmap icon = Shell.GetThumbnail(path, dragSz);
                if (icon != null)
                {
                    using (icon)
                    {
                        ghost = new DragGhost(icon, 200);
                    }

                    // GiveFeedback is the only event that fires continuously
                    // through a modal drag, so it is where the ghost gets moved
                    // and where the panel decides whether to get out of the way.
                    DragGhost g = ghost;
                    feedback = delegate(object s, GiveFeedbackEventArgs fe)
                    {
                        fe.UseDefaultCursors = true;
                        Point pt = Cursor.Position;
                        g.MoveTo(pt);

                        // Back over the pill means "I changed my mind" - bring
                        // the panel back so the file has somewhere to land.
                        // Anywhere else, stay out of the way of the real target.
                        bool overPill = _dock.GetScreenBounds().Contains(pt);
                        if (overPill && !Visible) ShowDuringDrag();
                        else if (!overPill && Visible) Hide();
                    };
                    GiveFeedback += feedback;
                }
            }
            catch { }

            // DoDragDrop is modal, so the form stays responsive during the drag.
            // We suppress close-on-deactivate by checking _dragging in OnDeactivate.
            DragDropEffects result = DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);

            if (feedback != null) GiveFeedback -= feedback;
            if (ghost != null) ghost.Dispose();

            _dragging = false;

            // Escape cancels the drag - Windows does that itself and returns
            // DragDropEffects.None. Either way the panel is done: if it is
            // still hidden the drop landed somewhere else, so close rather
            // than leaving an invisible window holding focus.
            if (!Visible)
            {
                Close();
                return;
            }
            // The mouse-up that ended the drag was swallowed by the modal drag
            // loop, so OnMouseUp never ran to clear this. Left set, the next
            // move over a tile would look like a fresh press and start a drag
            // nobody asked for.
            _pressIndex = -1;

            // If the file was moved (e.g., to Recycle Bin), refresh the list
            _downloads.Refresh();
            Relayout();
            Render();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);

            // Close on losing focus, but not while dragging
            if (!_dragging)
            {
                Close();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                Close();
            }
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            // Let OnDragOver handle the effect
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);

            Point client = PointToClient(new Point(e.X, e.Y));
            int idx = HitTest(client);

            bool isFileDrop = e.Data.GetDataPresent(DataFormats.FileDrop);
            bool overTrash = (idx == _trashIndex);

            if (overTrash && isFileDrop)
            {
                // Over trash tile: move to recycle bin (existing behaviour)
                e.Effect = DragDropEffects.Move;
                bool wasHover = _dropHover;
                _dropHover = false;
                if (wasHover) Render();
            }
            else if (isFileDrop && !_dragging)
            {
                // Not our own drag-out, and not over trash: import into folder.
                // But first check if all files already live in the watched folder.
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0 && !AllAlreadyInFolder(files))
                {
                    // Ctrl held = copy, else move
                    bool ctrlHeld = ((e.KeyState & 8) != 0);
                    e.Effect = ctrlHeld ? DragDropEffects.Copy : DragDropEffects.Move;
                    if (!_dropHover)
                    {
                        _dropHover = true;
                        Render();
                    }
                }
                else
                {
                    // Files already in folder, nothing to do
                    e.Effect = DragDropEffects.None;
                    if (_dropHover)
                    {
                        _dropHover = false;
                        Render();
                    }
                }
            }
            else
            {
                // Either our own drag wobbling back, or not a file drop
                e.Effect = DragDropEffects.None;
                if (_dropHover)
                {
                    _dropHover = false;
                    Render();
                }
            }

            // Update hover index for visual feedback
            if (_hoverIndex != idx)
            {
                _hoverIndex = idx;
                Render();
            }
        }

        /// <summary>
        /// Returns true if every file in the list already resides in the watched
        /// Downloads folder. Uses case-insensitive path comparison.
        /// </summary>
        bool AllAlreadyInFolder(string[] files)
        {
            string watchedFolder = Path.GetFullPath(_downloads.Folder);
            foreach (string f in files)
            {
                string dir = Path.GetDirectoryName(f);
                if (dir == null) continue;
                dir = Path.GetFullPath(dir);
                if (!string.Equals(dir, watchedFolder, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            if (_dropHover)
            {
                _dropHover = false;
                Render();
            }
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);

            Point client = PointToClient(new Point(e.X, e.Y));
            int idx = HitTest(client);

            bool isFileDrop = e.Data.GetDataPresent(DataFormats.FileDrop);

            if (idx == _trashIndex && isFileDrop)
            {
                // Drop on trash: recycle files
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (string f in files)
                {
                    Shell.RecycleFile(f);
                }

                _downloads.Refresh();
                Relayout();
                Render();
            }
            else if (isFileDrop && !_dragging)
            {
                // Drop elsewhere: import into the watched folder
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0 && !AllAlreadyInFolder(files))
                {
                    bool ctrlHeld = ((e.KeyState & 8) != 0);
                    Shell.MoveInto(files, _downloads.Folder, ctrlHeld);

                    _downloads.Refresh();
                    Relayout();
                    Render();
                }
            }

            _dropHover = false;
            Render();
        }

        void BuildMenu()
        {
            _menu = new ContextMenuStrip();
            _menu.Renderer = new DarkMenuRenderer();
            _menu.BackColor = Theme.Card;
            _menu.ForeColor = Theme.Text;

            ToolStripMenuItem open = new ToolStripMenuItem("Open");
            open.Click += delegate(object s, EventArgs ev)
            {
                string path = _menu.Tag as string;
                if (TargetExists(path))
                {
                    try { Process.Start(path); }
                    catch { }
                    Close();
                }
            };

            ToolStripMenuItem showInFolder = new ToolStripMenuItem("Show in folder");
            showInFolder.Click += delegate(object s, EventArgs ev)
            {
                string path = _menu.Tag as string;
                if (TargetExists(path))
                {
                    try { Process.Start("explorer.exe", "/select,\"" + path + "\""); }
                    catch { }
                }
            };

            ToolStripMenuItem copy = new ToolStripMenuItem("Copy");
            copy.Click += delegate(object s, EventArgs ev)
            {
                string path = _menu.Tag as string;
                if (TargetExists(path))
                {
                    StringCollection sc = new StringCollection();
                    sc.Add(path);
                    Clipboard.SetFileDropList(sc);
                }
            };

            ToolStripMenuItem delete = new ToolStripMenuItem("Delete");
            delete.Click += delegate(object s, EventArgs ev)
            {
                string path = _menu.Tag as string;
                if (TargetExists(path))
                {
                    Shell.RecycleFile(path);
                    _downloads.Refresh();
                    Relayout();
                    Render();
                }
            };

            _menu.Items.Add(open);
            _menu.Items.Add(showInFolder);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(copy);
            _menu.Items.Add(delete);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);

            if (_openAnim != null)
            {
                _openAnim.Stop();
                _openAnim.Dispose();
                _openAnim = null;
            }
            if (_frame != null)
            {
                _frame.Dispose();
                _frame = null;
            }

            // Dispose backdrop bitmaps
            if (_backdrop != null)
            {
                _backdrop.Dispose();
                _backdrop = null;
            }
            if (_blurredBackdrop != null)
            {
                _blurredBackdrop.Dispose();
                _blurredBackdrop = null;
            }

            // Dispose cached thumbnails
            foreach (Bitmap bmp in _thumbCache.Values)
            {
                if (bmp != null) bmp.Dispose();
            }
            _thumbCache.Clear();
        }

        class TileInfo
        {
            public string Path;
            public string Name;
            public DateTime Modified;
            public bool IsTrash;
        /// <summary>The "open the folder itself" tile, the way a macOS stack ends with Open in Finder.</summary>
        public bool IsOpenFolder;
        }
    }
}
