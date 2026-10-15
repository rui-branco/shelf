using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Shelf
{
    public partial class StackForm
    {
        // ----------------------------------------------------------
        // Layout
        // ----------------------------------------------------------

        void Metrics()
        {
            _m = R(ShadowPx);
            _header = R(HeaderPx);
            _footer = R(FooterPx);
            _pad = R(PadPx);
            _tileW = R(TileWPx);
            _tileH = R(TileHPx);
            _gap = R(GapPx);
            _icon = R(IconPx);
            _iconTop = R(IconTopPx);
            _gridBottom = R(GridBottomPx);
        }

        void BuildTiles()
        {
            string focusPath = _focus >= 0 && _focus < _tiles.Count ? _tiles[_focus].Item.Path : null;

            List<Tile> tiles = new List<Tile>();
            DateTime seen = _downloads.LastSeen;
            foreach (DownloadItem item in _downloads.Items)
            {
                Tile t = new Tile();
                t.Item = item;
                t.IsNew = item.Added > seen;
                tiles.Add(t);
            }
            _tiles = tiles;

            // Selection follows the file, not the slot it happened to be in.
            RemapSelection(focusPath);

            _hover = -1;
            _hoverPart = Part.None;
            _press = -1;
            _pressPart = Part.None;
        }

        Rectangle WorkArea
        {
            get { return Screen.FromRectangle(_pill).WorkingArea; }
        }

        /// <summary>Where the bottom of the panel's space is: just above the taskbar.</summary>
        int SpaceBottom
        {
            get
            {
                Rectangle wa = WorkArea;
                int bottom = wa.Bottom;
                if (!_bar.IsEmpty && _bar.Width > _bar.Height && _bar.Top > wa.Top + wa.Height / 2)
                    bottom = Math.Min(bottom, _bar.Top);
                return bottom - R(BarGapPx);
            }
        }

        int PanelWidth(int cols)
        {
            return _pad * 2 + cols * _tileW + (cols - 1) * _gap;
        }

        /// <summary>
        /// Sizes the panel to its contents. Never wider than six columns or the
        /// screen, never taller than four rows or the space above the taskbar;
        /// anything beyond that scrolls rather than being left out, which is
        /// what the first version did, quietly.
        /// </summary>
        void LayoutPanel()
        {
            Rectangle wa = WorkArea;
            int n = _tiles.Count;

            int cols = Math.Max(MinCols, Math.Min(MaxCols, n));
            while (cols > 1 && PanelWidth(cols) > wa.Width - R(EdgePx) * 2) cols--;
            _cols = cols;

            int rows = n == 0 ? 0 : (n + cols - 1) / cols;
            int room = SpaceBottom - (wa.Top + R(EdgePx)) - _header - _footer - _gridBottom;
            int fit = Math.Max(1, (room + _gap) / (_tileH + _gap));
            int visible = Math.Max(1, Math.Min(Math.Min(rows, MaxRows), fit));

            int gridH = n == 0 ? R(EmptyPx) : visible * _tileH + (visible - 1) * _gap + _gridBottom;
            _contentH = n == 0 ? gridH : rows * _tileH + (rows - 1) * _gap + _gridBottom;

            int panelW = PanelWidth(cols);
            int panelH = _header + gridH + _footer;

            _panel = new Rectangle(_m, _m, panelW, panelH);
            _grid = new Rectangle(_m, _m + _header, panelW, gridH);
            _window = new Size(panelW + _m * 2, panelH + _m * 2);

            _maxScroll = Math.Max(0, _contentH - gridH);
            _scrollTarget = Clamp(_scrollTarget, 0, _maxScroll);
            _scroll = Clamp(_scroll, 0, _maxScroll);

            // Footer buttons, sized to their text.
            int bh = R(34);
            int by = _panel.Bottom - _footer + (_footer - bh) / 2;
            Font f = Theme.Font(LabelFontPx * _s + 1);
            int openW = ButtonWidth(f, OpenLabel);
            int binW = ButtonWidth(f, "Recycle Bin");
            _openBtn = new Rectangle(_panel.Left + R(8), by, openW, bh);
            _binBtn = new Rectangle(_panel.Right - R(8) - binW, by, binW, bh);
        }

        string OpenLabel
        {
            get { return "Open " + Format.FolderName(_downloads.Folder); }
        }

        int ButtonWidth(Font f, string text)
        {
            using (Bitmap b = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(b))
            {
                int textW = (int)Math.Ceiling(g.MeasureString(text, f, PointF.Empty, StringFormat.GenericTypographic).Width);
                return R(12) + R(16) + R(8) + textW + R(14);
            }
        }

        /// <summary>
        /// Centres the panel over the pill, just above the taskbar, kept on
        /// screen. The window is larger than the panel by the shadow margin.
        /// </summary>
        void Place()
        {
            Rectangle wa = WorkArea;
            int edge = R(EdgePx);

            int px = _pill.Left + _pill.Width / 2 - _panel.Width / 2;
            px = Math.Max(wa.Left + edge, Math.Min(px, wa.Right - edge - _panel.Width));

            int py = SpaceBottom - _panel.Height;
            py = Math.Max(wa.Top + edge, py);

            _anchor = new Point(px - _m, py - _m);
        }

        /// <summary>
        /// Rebuilds after the folder changed, keeping the panel's bottom edge
        /// where it is so it grows and shrinks upwards from the taskbar instead
        /// of drifting away from the pill.
        /// </summary>
        void Rebuild()
        {
            _stale = false;
            Size before = _window;
            BuildTiles();
            LayoutPanel();
            Place();
            BuildChrome();
            if (_window != before) Bounds = new Rectangle(_anchor, _window);
            Render();
        }

        void OnDownloadsChanged()
        {
            if (!IsHandleCreated || _closing) return;
            if (_dragging)
            {
                _stale = true;         // the drag finishes first
                return;
            }
            Rebuild();
        }

        Rectangle TileRect(int i)
        {
            int col = i % _cols;
            int row = i / _cols;
            int x = _grid.X + _pad + col * (_tileW + _gap);
            int y = _grid.Y + row * (_tileH + _gap) - (int)Math.Round(_scroll);
            return new Rectangle(x, y, _tileW, _tileH);
        }

        // ----------------------------------------------------------
        // Backdrop and chrome
        // ----------------------------------------------------------

        /// <summary>
        /// Captures what is behind the panel, generously. Changes to the folder
        /// resize the panel while it is open and we cannot re-capture without
        /// photographing ourselves, so take the largest panel this screen could
        /// show, placed the same way, and crop from that.
        /// </summary>
        void CaptureBackdrop()
        {
            Rectangle wa = WorkArea;
            int edge = R(EdgePx);

            int cols = MaxCols;
            while (cols > 1 && PanelWidth(cols) > wa.Width - edge * 2) cols--;
            int maxW = PanelWidth(cols);

            int room = SpaceBottom - (wa.Top + edge) - _header - _footer - _gridBottom;
            int rows = Math.Max(1, Math.Min(MaxRows, (room + _gap) / (_tileH + _gap)));
            int maxH = _header + _footer + Math.Max(R(EmptyPx), rows * _tileH + (rows - 1) * _gap + _gridBottom);

            int x = _pill.Left + _pill.Width / 2 - maxW / 2;
            x = Math.Max(wa.Left + edge, Math.Min(x, wa.Right - edge - maxW));
            int y = Math.Max(wa.Top + edge, SpaceBottom - maxH);

            Rectangle region = Rectangle.Intersect(new Rectangle(x, y, maxW, maxH), wa);
            _frostOrigin = region.Location;

            try
            {
                using (Bitmap shot = Blur.CaptureBehind(region))
                    _frost = Blur.Frost(shot);
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
                _frost = null;      // the panel falls back to a solid fill
            }
        }

        /// <summary>
        /// Draws everything that does not change between frames: the shadow,
        /// the frosted material clipped to the rounded panel with antialiased
        /// corners, the rim, the footer band and the title.
        /// </summary>
        void BuildChrome()
        {
            if (_chrome != null) _chrome.Dispose();
            _chrome = new Bitmap(_window.Width, _window.Height, PixelFormat.Format32bppPArgb);

            float radius = RadiusPx * _s;
            RectangleF panel = _panel;

            using (Graphics g = Graphics.FromImage(_chrome))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                using (Bitmap shadow = Blur.Shadow(_window, panel, radius, 26 * _s, 8 * _s, Theme.ShadowAlpha))
                    g.DrawImage(shadow, new Rectangle(0, 0, shadow.Width, shadow.Height));

                // The material is composed square, then poured into the rounded
                // shape through a texture brush. Clipping to the path instead
                // would give hard, stair-stepped corners: GDI+ clip regions are
                // never antialiased.
                using (Bitmap body = new Bitmap(_panel.Width, _panel.Height, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics b = Graphics.FromImage(body))
                    {
                        b.Clear(Theme.PanelSolid);
                        if (_frost != null)
                        {
                            Point at = new Point(_frostOrigin.X - (_anchor.X + _m), _frostOrigin.Y - (_anchor.Y + _m));
                            b.DrawImage(_frost, new Rectangle(at.X, at.Y, _frost.Width, _frost.Height));
                        }
                        using (SolidBrush tint = new SolidBrush(Theme.PanelTint))
                            b.FillRectangle(tint, 0, 0, body.Width, body.Height);
                        using (TextureBrush grain = new TextureBrush(Blur.Noise(), WrapMode.Tile))
                            b.FillRectangle(grain, 0, 0, body.Width, body.Height);

                        // The footer sits on a slightly deeper layer, the way
                        // Windows 11 flyouts set their command bar apart.
                        using (SolidBrush band = new SolidBrush(Theme.Light ? Color.FromArgb(10, 0, 0, 0) : Color.FromArgb(46, 0, 0, 0)))
                            b.FillRectangle(band, 0, body.Height - _footer, body.Width, _footer);
                        using (Pen line = new Pen(Theme.Divider))
                            b.DrawLine(line, 0, body.Height - _footer, body.Width, body.Height - _footer);
                    }

                    using (GraphicsPath shape = Theme.Rounded(panel, radius))
                    using (TextureBrush fill = new TextureBrush(body, WrapMode.Clamp))
                    {
                        fill.TranslateTransform(_panel.X, _panel.Y);
                        g.FillPath(fill, shape);
                    }
                }

                using (GraphicsPath outer = Theme.Rounded(new RectangleF(panel.X - 0.5f, panel.Y - 0.5f, panel.Width + 1, panel.Height + 1), radius + 0.5f))
                using (Pen p = new Pen(Theme.OuterStroke, 1f))
                    g.DrawPath(p, outer);
                using (GraphicsPath inner = Theme.Rounded(new RectangleF(panel.X + 0.5f, panel.Y + 0.5f, panel.Width - 1, panel.Height - 1), radius - 0.5f))
                using (Pen p = new Pen(Theme.InnerStroke, 1f))
                    g.DrawPath(p, inner);

                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                Font title = Theme.Strong(TitleFontPx * _s);
                using (SolidBrush text = new SolidBrush(Theme.Text))
                using (StringFormat f = new StringFormat())
                {
                    f.LineAlignment = StringAlignment.Center;
                    f.Trimming = StringTrimming.EllipsisCharacter;
                    f.FormatFlags = StringFormatFlags.NoWrap;
                    RectangleF r = new RectangleF(_panel.X + R(20), _panel.Y, _panel.Width / 2f, _header);
                    g.DrawString(Format.FolderName(_downloads.Folder), title, text, r, f);
                }
            }
        }
    }
}
