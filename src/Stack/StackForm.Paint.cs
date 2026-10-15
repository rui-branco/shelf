using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Shelf
{
    public partial class StackForm
    {
        // ----------------------------------------------------------
        // Rendering
        // ----------------------------------------------------------

        /// <summary>
        /// Draws a frame and puts it on screen. Call this instead of
        /// Invalidate() whenever the visual state changes.
        /// </summary>
        public void Render()
        {
            if (!IsHandleCreated || IsDisposed || _chrome == null) return;

            try
            {
                using (Graphics g = _surface.Begin(_window.Width, _window.Height))
                    PaintFrame(g);
                Present();
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
        }

        /// <summary>
        /// Re-blits the current frame at the animation's offset and opacity.
        /// Always from the anchor: UpdateLayeredWindow moves the window as well
        /// as painting it, so working from the current Top would walk the panel
        /// down the screen a frame at a time.
        /// </summary>
        void Present()
        {
            if (!IsHandleCreated || IsDisposed) return;
            _surface.Present(Handle, new Point(_anchor.X, _anchor.Y + _offset), _alpha);
        }

        void PaintFrame(Graphics g)
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(_chrome, new Rectangle(0, 0, _chrome.Width, _chrome.Height));
            g.CompositingMode = CompositingMode.SourceOver;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            DrawHeaderInfo(g);

            if (_tiles.Count == 0)
            {
                DrawEmpty(g);
            }
            else
            {
                g.SetClip(_grid);
                for (int i = 0; i < _tiles.Count; i++)
                {
                    Rectangle r = TileRect(i);
                    if (r.Bottom < _grid.Top || r.Top > _grid.Bottom) continue;
                    DrawTile(g, i, r);
                }
                g.ResetClip();
                DrawBand(g);
                DrawScrollbar(g);
            }

            DrawButton(g, _openBtn, "\uE838", OpenLabel, Part.OpenFolder);
            DrawButton(g, _binBtn, "\uE74D", "Recycle Bin", Part.Bin);

            if (_dropHover) DrawDropOverlay(g);
        }

        static readonly string Dot = "  " + (char)0x00B7 + "  ";

        void DrawHeaderInfo(Graphics g)
        {
            string info;
            List<int> selected = SelectedIndices();
            int idx = _hoverPart == Part.Tile && _hover >= 0 ? _hover : (selected.Count == 1 ? selected[0] : -1);
            if (idx >= 0 && idx < _tiles.Count)
            {
                DownloadItem it = _tiles[idx].Item;
                info = (it.IsFolder ? "Folder" : Format.Size(it.Size)) + Dot + Format.When(it.Added);
            }
            else if (selected.Count > 1)
            {
                // Like Explorer's status bar: how many, and how big together.
                long total = 0;
                bool folders = false;
                foreach (int i in selected)
                {
                    total += _tiles[i].Item.Size;
                    folders |= _tiles[i].Item.IsFolder;
                }
                info = selected.Count + " selected" + (total > 0 || !folders ? Dot + Format.Size(total) : "");
            }
            else
            {
                int n = _tiles.Count;
                info = n == 0 ? "" : (n == 1 ? "1 item" : n + " items");
            }
            if (info.Length == 0) return;

            using (SolidBrush b = new SolidBrush(Theme.TextDim))
            using (StringFormat f = new StringFormat())
            {
                f.Alignment = StringAlignment.Far;
                f.LineAlignment = StringAlignment.Center;
                f.FormatFlags = StringFormatFlags.NoWrap;
                RectangleF r = new RectangleF(_panel.X + _panel.Width / 3f, _panel.Y, _panel.Width * 2 / 3f - R(20), _header);
                g.DrawString(info, Theme.Font(LabelFontPx * _s), b, r, f);
            }
        }

        void DrawTile(Graphics g, int i, Rectangle r)
        {
            Tile t = _tiles[i];
            bool hot = _hoverPart == Part.Tile && _hover == i;
            bool pressed = hot && _pressPart == Part.Tile && _press == i;
            float radius = 6 * _s;

            if (IsSelected(i))
            {
                Theme.FillRounded(g, Color.FromArgb(Theme.Light ? 36 : 44, Theme.Accent), r, radius);
                using (GraphicsPath p = Theme.Rounded(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), radius))
                using (Pen pen = new Pen(Color.FromArgb(150, Theme.Accent), 1f))
                    g.DrawPath(pen, p);
            }
            else if (pressed)
            {
                Theme.FillRounded(g, Theme.Pressed, r, radius);
            }
            else if (hot)
            {
                Theme.FillRounded(g, Theme.Hover, r, radius);
            }

            // Where the keyboard is, once it is being used, even when that
            // tile is not selected (Ctrl+arrows move without selecting).
            if (_showFocus && i == _focus)
            {
                using (GraphicsPath p = Theme.Rounded(new RectangleF(r.X + 1.5f, r.Y + 1.5f, r.Width - 3, r.Height - 3), radius - 1))
                using (Pen pen = new Pen(Theme.Text, 1.5f * _s))
                    g.DrawPath(pen, p);
            }

            int ix = r.X + (r.Width - _icon) / 2;
            int iy = r.Y + _iconTop;
            DrawIcon(g, t.Item, new Rectangle(ix, iy, _icon, _icon));

            if (t.IsNew)
            {
                float d = 7 * _s;
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    g.FillEllipse(b, ix + _icon - d * 0.8f, iy - d * 0.1f, d, d);
            }

            Font font = Theme.Font(LabelFontPx * _s);
            float lineH = font.GetHeight(g);
            RectangleF lr = new RectangleF(r.X + R(4), iy + _icon + R(6), r.Width - R(8), lineH * 2 + 1);
            if (t.Label == null) t.Label = Labels.Fit(g, t.Item.Name, font, lr.Size);

            using (SolidBrush b = new SolidBrush(Theme.Text))
                g.DrawString(t.Label, font, b, lr, Labels.Format);
        }

        /// <summary>
        /// The item's shell thumbnail, centred in its box at its own size so it
        /// stays pixel-sharp. Previews (photos, pages) get rounded corners and a
        /// hairline frame; icons already have a shape of their own.
        /// </summary>
        void DrawIcon(Graphics g, DownloadItem item, Rectangle box)
        {
            Thumbs.Thumb th = Thumbs.Get(item.Path, item.Modified, _icon);
            if (th == null) return;     // on its way; Loaded repaints

            if (th.Image == null)
            {
                Theme.DrawGlyph(g, item.IsFolder ? "\uE8B7" : "\uE7C3", box.Height * 0.7f, Theme.TextDim, box);
                return;
            }

            Bitmap img = th.Image;
            float k = Math.Min(1f, Math.Min((float)box.Width / img.Width, (float)box.Height / img.Height));
            int w = (int)Math.Round(img.Width * k), h = (int)Math.Round(img.Height * k);
            Rectangle dst = new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);

            if (!th.IsPreview)
            {
                g.DrawImage(img, dst);
                return;
            }

            float radius = 4 * _s;
            using (TextureBrush tb = new TextureBrush(img, WrapMode.Clamp))
            using (GraphicsPath p = Theme.Rounded(dst, radius))
            {
                tb.TranslateTransform(dst.X, dst.Y);
                if (k < 1f) tb.ScaleTransform(k, k);
                g.FillPath(tb, p);
            }
            using (GraphicsPath p = Theme.Rounded(new RectangleF(dst.X + 0.5f, dst.Y + 0.5f, dst.Width - 1, dst.Height - 1), radius))
            using (Pen pen = new Pen(Theme.Light ? Color.FromArgb(36, 0, 0, 0) : Color.FromArgb(40, 255, 255, 255), 1f))
                g.DrawPath(pen, p);
        }

        void DrawScrollbar(Graphics g)
        {
            if (_maxScroll <= 0) return;

            int trackTop = _grid.Top + R(4), trackH = _grid.Height - _gridBottom - R(8);
            float thumbH = Math.Max(R(24), trackH * (float)_grid.Height / _contentH);
            float y = trackTop + (trackH - thumbH) * (_scroll / _maxScroll);
            float w = 3 * _s;
            RectangleF thumb = new RectangleF(_panel.Right - R(5) - w, y, w, thumbH);
            Theme.FillRounded(g, Theme.TextFaint, thumb, w / 2);
        }

        void DrawButton(Graphics g, Rectangle r, string glyph, string text, Part part)
        {
            bool hot = _hoverPart == part;
            bool pressed = hot && _pressPart == part;
            bool danger = part == Part.Bin && _binHover;
            Color fg = Theme.Text;
            float radius = 4 * _s;

            if (danger)
            {
                Theme.FillRounded(g, Color.FromArgb(Theme.Light ? 28 : 40, Theme.Danger), r, radius);
                fg = Theme.Danger;
            }
            else if (pressed)
            {
                Theme.FillRounded(g, Theme.Pressed, r, radius);
            }
            else if (hot)
            {
                Theme.FillRounded(g, Theme.Hover, r, radius);
            }

            // While a tile is being dragged, the bin says it is a place to put it.
            if (part == Part.Bin && _dragging && !danger)
            {
                using (GraphicsPath p = Theme.Rounded(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), radius))
                using (Pen pen = new Pen(Theme.TextFaint, 1f))
                {
                    pen.DashStyle = DashStyle.Dash;
                    g.DrawPath(pen, p);
                }
            }

            RectangleF gl = new RectangleF(r.X + R(12), r.Y, R(16), r.Height);
            Theme.DrawGlyph(g, glyph, 16 * _s, fg, gl);

            using (SolidBrush b = new SolidBrush(fg))
            using (StringFormat f = new StringFormat(StringFormat.GenericTypographic))
            {
                f.LineAlignment = StringAlignment.Center;
                f.FormatFlags = StringFormatFlags.NoWrap;
                RectangleF tr = new RectangleF(gl.Right + R(8), r.Y, r.Right - gl.Right - R(8), r.Height);
                g.DrawString(text, Theme.Font(LabelFontPx * _s + 1), b, tr, f);
            }
        }

        void DrawEmpty(Graphics g)
        {
            float cx = _grid.X + _grid.Width / 2f;
            float cy = _grid.Y + _grid.Height / 2f;

            Theme.DrawGlyph(g, "\uE896", 34 * _s, Theme.TextFaint,
                new RectangleF(cx - R(30), cy - R(58), R(60), R(44)));

            using (StringFormat f = new StringFormat())
            {
                f.Alignment = StringAlignment.Center;
                f.LineAlignment = StringAlignment.Center;
                using (SolidBrush b = new SolidBrush(Theme.Text))
                    g.DrawString("No downloads yet", Theme.Strong(TitleFontPx * _s), b,
                        new RectangleF(_grid.X, cy - R(4), _grid.Width, R(24)), f);
                using (SolidBrush b = new SolidBrush(Theme.TextDim))
                    g.DrawString("Files you download will show up here", Theme.Font(LabelFontPx * _s), b,
                        new RectangleF(_grid.X, cy + R(20), _grid.Width, R(20)), f);
            }
        }

        void DrawDropOverlay(Graphics g)
        {
            RectangleF r = new RectangleF(_grid.X + R(8), _grid.Y + R(2), _grid.Width - R(16), _grid.Height - R(10));
            float radius = 6 * _s;

            Theme.FillRounded(g, Color.FromArgb(225, Theme.PanelSolid), r, radius);
            Theme.FillRounded(g, Color.FromArgb(Theme.Light ? 22 : 28, Theme.Accent), r, radius);
            using (GraphicsPath p = Theme.Rounded(r, radius))
            using (Pen pen = new Pen(Theme.Accent, 1.5f * _s))
            {
                pen.DashStyle = DashStyle.Dash;
                g.DrawPath(pen, p);
            }

            float cy = r.Y + r.Height / 2f;
            Theme.DrawGlyph(g, "\uE896", 26 * _s, Theme.Accent, new RectangleF(r.X, cy - R(36), r.Width, R(32)));
            using (StringFormat f = new StringFormat())
            using (SolidBrush b = new SolidBrush(Theme.Text))
            {
                f.Alignment = StringAlignment.Center;
                f.LineAlignment = StringAlignment.Center;
                string verb = _dropCopy ? "copy" : "move";
                g.DrawString("Drop to " + verb + " into " + Format.FolderName(_downloads.Folder),
                    Theme.Strong((LabelFontPx + 1) * _s), b, new RectangleF(r.X, cy + R(2), r.Width, R(24)), f);
            }
        }
    }
}
