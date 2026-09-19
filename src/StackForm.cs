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
    /// plus a trash tile. Supports drag-out to other applications.
    /// </summary>
    public class StackForm : Form
    {
        const int WS_EX_TOOLWINDOW = 0x80;
        const int Columns = 4;
        const int Radius = 12;
        const int TilePad = 8;
        const int ThumbSize = 64;     // thumbnail size within the tile
        const int LabelHeight = 18;
        const float LabelPt = 8f;

        AppConfig _config;
        Downloads _downloads;
        DockForm _dock;
        float _scale = 1f;
        int _tileSize;

        List<TileInfo> _tiles;
        int _hoverIndex = -1;
        int _trashIndex = -1;

        // Drag state
        int _pressIndex = -1;
        Point _pressPoint;
        bool _dragging;

        // Thumbnail cache: (path, lastWrite) -> bitmap
        Dictionary<string, Bitmap> _thumbCache;

        ContextMenuStrip _menu;

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
            BackColor = Theme.Back;
            AllowDrop = true;
            KeyPreview = true;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            BuildMenu();
            ApplyScale();
            BuildTiles();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

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

            foreach (DownloadItem item in _downloads.Items)
            {
                TileInfo t = new TileInfo();
                t.Path = item.Path;
                t.Name = item.Name;
                t.Modified = item.Modified;
                t.IsTrash = false;
                _tiles.Add(t);
            }

            // Add the trash tile at the end
            TileInfo trash = new TileInfo();
            trash.IsTrash = true;
            trash.Name = "Recycle Bin";
            _tiles.Add(trash);
            _trashIndex = _tiles.Count - 1;

            // Calculate size
            int count = _tiles.Count;
            int cols = Columns;
            int rows = (count + cols - 1) / cols;

            int pad = (int)Math.Round(TilePad * _scale);
            int w = cols * _tileSize + (cols + 1) * pad;
            int h = rows * _tileSize + (rows + 1) * pad;

            Size = new Size(w, h);

            // Preload thumbnails
            foreach (TileInfo t in _tiles)
            {
                if (!t.IsTrash && t.Path != null)
                {
                    GetThumb(t);
                }
            }
        }

        Bitmap GetThumb(TileInfo t)
        {
            if (t.IsTrash) return null;
            string key = t.Path + "|" + t.Modified.Ticks.ToString();
            Bitmap bmp;
            if (_thumbCache.TryGetValue(key, out bmp)) return bmp;

            int sz = (int)Math.Round(ThumbSize * _scale);
            bmp = Shell.GetThumbnail(t.Path, sz);
            if (bmp != null) _thumbCache[key] = bmp;
            return bmp;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            IntPtr ctx = Dpi.EnterDpi();
            try { PaintCore(e.Graphics); }
            finally { Dpi.LeaveDpi(ctx); }
        }

        void PaintCore(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            float s = _scale;
            int w = Width;
            int h = Height;

            // Rounded background
            RectangleF r = new RectangleF(0.5f, 0.5f, w - 1.5f, h - 1.5f);
            using (GraphicsPath path = Theme.Rounded(r, Radius * s))
            {
                using (SolidBrush b = new SolidBrush(Theme.Card))
                    g.FillPath(b, path);
                using (Pen p = new Pen(Theme.Border, 1f))
                    g.DrawPath(p, path);
            }

            // Draw tiles
            int pad = (int)Math.Round(TilePad * s);
            int cols = Columns;

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

        void DrawTile(Graphics g, TileInfo t, int x, int y, bool hover)
        {
            float s = _scale;
            int ts = _tileSize;

            // Hover highlight
            if (hover)
            {
                RectangleF r = new RectangleF(x, y, ts, ts);
                using (GraphicsPath path = Theme.Rounded(r, 8 * s))
                using (SolidBrush b = new SolidBrush(Theme.CardHi))
                    g.FillPath(b, path);
            }

            int thumbSz = (int)Math.Round(ThumbSize * s);
            int thumbX = x + (ts - thumbSz) / 2;
            int thumbY = y + (int)Math.Round(6 * s);

            if (t.IsTrash)
            {
                // Draw trash icon (Segoe MDL2 Assets U+E74D = delete)
                Font icon = Theme.IconFont(thumbSz * 0.6f);
                if (icon != null)
                {
                    using (icon)
                    using (SolidBrush b = new SolidBrush(Theme.Danger))
                    using (StringFormat fmt = new StringFormat())
                    {
                        fmt.Alignment = StringAlignment.Center;
                        fmt.LineAlignment = StringAlignment.Center;
                        g.DrawString("", icon, b, x + ts / 2f, thumbY + thumbSz / 2f, fmt);
                    }
                }
            }
            else
            {
                // Draw thumbnail
                Bitmap thumb = GetThumb(t);
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

            // Label
            int labelY = y + ts - (int)Math.Round(LabelHeight * s) - (int)Math.Round(4 * s);
            Rectangle labelRect = new Rectangle(x + 2, labelY, ts - 4, (int)Math.Round(LabelHeight * s));

            using (Font f = Theme.Font(LabelPt * s, FontStyle.Regular))
            {
                TextRenderer.DrawText(g, t.Name, f, labelRect,
                    t.IsTrash ? Theme.Danger : Theme.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
        }

        int HitTest(Point pt)
        {
            int pad = (int)Math.Round(TilePad * _scale);
            int cols = Columns;

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
                Invalidate();
            }

            // Drag detection
            if (_pressIndex >= 0 && e.Button == MouseButtons.Left && !_dragging)
            {
                if (Math.Abs(e.X - _pressPoint.X) >= SystemInformation.DragSize.Width ||
                    Math.Abs(e.Y - _pressPoint.Y) >= SystemInformation.DragSize.Height)
                {
                    TileInfo t = _tiles[_pressIndex];
                    if (!t.IsTrash && t.Path != null && File.Exists(t.Path))
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
                if (!t.IsTrash && t.Path != null)
                {
                    _menu.Tag = t.Path;
                    _menu.Show(this, e.Location);
                }
            }
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
                else if (t.Path != null && File.Exists(t.Path))
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
                Invalidate();
            }
        }

        void StartFileDrag(string path)
        {
            _dragging = true;

            // Build the data object with file drop list
            DataObject data = new DataObject();
            StringCollection sc = new StringCollection();
            sc.Add(path);
            data.SetFileDropList(sc);

            // DoDragDrop is modal, so the form stays responsive during the drag.
            // We suppress close-on-deactivate by checking _dragging in OnDeactivate.
            DragDropEffects result = DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);

            _dragging = false;

            // If the file was moved (e.g., to Recycle Bin), refresh the list
            _downloads.Refresh();
            BuildTiles();
            Invalidate();
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
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Move;
            }
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);

            Point client = PointToClient(new Point(e.X, e.Y));
            int idx = HitTest(client);

            if (idx == _trashIndex && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Move;
                if (_hoverIndex != idx)
                {
                    _hoverIndex = idx;
                    Invalidate();
                }
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);

            Point client = PointToClient(new Point(e.X, e.Y));
            int idx = HitTest(client);

            if (idx == _trashIndex && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (string f in files)
                {
                    Shell.RecycleFile(f);
                }

                _downloads.Refresh();
                BuildTiles();
                Invalidate();
            }
        }

        void BuildMenu()
        {
            _menu = new ContextMenuStrip();
            _menu.Renderer = new DarkMenuRenderer();
            _menu.BackColor = Theme.Card;
            _menu.ForeColor = Theme.Text;

            ToolStripMenuItem open = new ToolStripMenuItem("Open");
            open.Click += delegate(object s, EventArgs e)
            {
                string path = _menu.Tag as string;
                if (path != null && File.Exists(path))
                {
                    try { Process.Start(path); }
                    catch { }
                    Close();
                }
            };

            ToolStripMenuItem showInFolder = new ToolStripMenuItem("Show in folder");
            showInFolder.Click += delegate(object s, EventArgs e)
            {
                string path = _menu.Tag as string;
                if (path != null && File.Exists(path))
                {
                    try { Process.Start("explorer.exe", "/select,\"" + path + "\""); }
                    catch { }
                }
            };

            ToolStripMenuItem copy = new ToolStripMenuItem("Copy");
            copy.Click += delegate(object s, EventArgs e)
            {
                string path = _menu.Tag as string;
                if (path != null && File.Exists(path))
                {
                    StringCollection sc = new StringCollection();
                    sc.Add(path);
                    Clipboard.SetFileDropList(sc);
                }
            };

            ToolStripMenuItem delete = new ToolStripMenuItem("Delete");
            delete.Click += delegate(object s, EventArgs e)
            {
                string path = _menu.Tag as string;
                if (path != null && File.Exists(path))
                {
                    Shell.RecycleFile(path);
                    _downloads.Refresh();
                    BuildTiles();
                    Invalidate();
                }
            };

            _menu.Items.Add(open);
            _menu.Items.Add(showInFolder);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(copy);
            _menu.Items.Add(delete);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

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
        }
    }
}
