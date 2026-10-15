using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Shelf
{
    public partial class StackForm
    {
        // ----------------------------------------------------------
        // Drag out
        // ----------------------------------------------------------

        void StartFileDrag(List<DownloadItem> items)
        {
            _dragging = true;
            _pressPart = Part.None;
            _press = -1;
            Render();

            // The panel in screen coordinates, without the shadow: this is what
            // "the cursor is still on the panel" means for the rest of the drag.
            Rectangle panelScreen = new Rectangle(_anchor.X + _panel.X, _anchor.Y + _panel.Y, _panel.Width, _panel.Height);

            DataObject data = new DataObject();
            StringCollection sc = new StringCollection();
            foreach (DownloadItem it in items) sc.Add(it.Path);
            data.SetFileDropList(sc);

            // Carry the file's own icon under the cursor. This is ours, not the
            // shell's: IDragSourceHelper only draws when the drop target calls
            // IDropTargetHelper, so over anything that does not participate the
            // image never appeared at all.
            DragGhost ghost = null;
            try
            {
                using (Bitmap image = GhostImage(items))
                    if (image != null) ghost = new DragGhost(image, 220);
            }
            catch { }

            // GiveFeedback is the only event that fires continuously through a
            // modal drag, so it is where the ghost gets moved and where the
            // panel decides whether to get out of the way.
            DragGhost gh = ghost;
            GiveFeedbackEventHandler feedback = delegate(object s, GiveFeedbackEventArgs fe)
            {
                fe.UseDefaultCursors = true;
                Point pt = Cursor.Position;
                if (gh != null) gh.MoveTo(pt);

                // Still on the panel: keep it up, the Recycle Bin is one of its
                // own buttons. Back over the pill means "I changed my mind" -
                // bring it back so the file has somewhere to land. Anywhere
                // else, get out of the way: the panel sits directly over the
                // area you are most likely to be dragging towards, and a drop
                // target you cannot see is not a drop target.
                bool overOwn = panelScreen.Contains(pt) || (_dock != null && _dock.GetScreenBounds().Contains(pt));
                if (overOwn && !Visible) ShowWindow(Handle, SW_SHOWNOACTIVATE);
                else if (!overOwn && Visible) Hide();
            };
            GiveFeedback += feedback;

            try
            {
                DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            }
            catch (Exception ex)
            {
                // A drag that threw is not worth the panel. Logged rather than left to the
                // top-level handler, which would close Shelf over a failed drop.
                Program.LogError(ex);
            }
            finally
            {
                // Unwound in a finally, not on the line after the call. DoDragDrop is a
                // modal COM call, and when it threw, none of this ran: the handler stayed
                // attached, the ghost stayed on screen as a topmost icon over everything,
                // and _dragging stayed true - which is the flag OnDeactivate checks before
                // closing, so click-away stopped closing the panel for good.
                GiveFeedback -= feedback;
                if (ghost != null) ghost.Dispose();
                _dragging = false;
                _binHover = false;
            }

            // Hidden at the end means the drop landed somewhere else; the panel
            // is done. Close rather than leave an invisible window holding focus.
            if (!Visible)
            {
                Close();
                return;
            }

            _downloads.Refresh();
            if (_stale) Rebuild();
            else Render();
        }

        /// <summary>
        /// What follows the cursor: the file's icon, or for several files the
        /// first few fanned out with a count on top, the way Explorer shows a
        /// multi-file drag. A fresh bitmap the caller disposes.
        /// </summary>
        Bitmap GhostImage(List<DownloadItem> items)
        {
            int size = _icon;
            int shown = Math.Min(3, items.Count);
            int step = R(7);
            int badge = items.Count > 1 ? R(22) : 0;
            int w = size + step * (shown - 1) + badge / 2;
            int h = size + step * (shown - 1) + badge / 2;

            Bitmap canvas = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                // Back to front, each one up and to the right of the last.
                for (int k = shown - 1; k >= 0; k--)
                {
                    DownloadItem it = items[k];
                    Bitmap icon = Thumbs.Peek(it.Path, it.Modified, size);
                    bool owned = false;
                    if (icon == null)
                    {
                        icon = Shell.GetThumbnail(it.Path, size);
                        owned = true;
                    }
                    if (icon == null) continue;

                    int x = step * k, y = h - size - step * k - (badge > 0 ? 0 : 0);
                    float fit = Math.Min(1f, Math.Min((float)size / icon.Width, (float)size / icon.Height));
                    int iw = (int)(icon.Width * fit), ih = (int)(icon.Height * fit);
                    g.DrawImage(icon, new Rectangle(x + (size - iw) / 2, y + (size - ih) / 2, iw, ih));
                    if (owned) icon.Dispose();
                }

                if (badge > 0)
                {
                    RectangleF b = new RectangleF(w - badge, 0, badge, badge);
                    using (SolidBrush fill = new SolidBrush(Theme.Accent))
                        g.FillEllipse(fill, b);
                    using (SolidBrush text = new SolidBrush(Theme.Light ? Color.White : Color.Black))
                    using (StringFormat f = new StringFormat())
                    {
                        f.Alignment = StringAlignment.Center;
                        f.LineAlignment = StringAlignment.Center;
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        string count = items.Count > 99 ? "99+" : items.Count.ToString();
                        g.DrawString(count, Theme.Strong(11 * _s), text, b, f);
                    }
                }
            }
            return canvas;
        }

        const int SW_SHOWNOACTIVATE = 4;

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        // ----------------------------------------------------------
        // Drop in
        // ----------------------------------------------------------

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);

            int idx;
            Part part = HitTest(PointToClient(new Point(e.X, e.Y)), out idx);
            string[] files = DroppedFiles(e.Data);

            bool bin = false, drop = false;
            bool copy = (e.KeyState & 8) != 0;     // Ctrl held = copy, else move

            if (files != null && part == Part.Bin)
            {
                e.Effect = DragDropEffects.Move;
                bin = true;
            }
            else if (files != null && !_dragging && part != Part.Outside && !AllInFolder(files, _downloads.Folder))
            {
                e.Effect = copy ? DragDropEffects.Copy : DragDropEffects.Move;
                drop = true;
            }
            else
            {
                // Our own drag wobbling back, files already here, or not files.
                e.Effect = DragDropEffects.None;
            }

            if (bin != _binHover || drop != _dropHover || (drop && copy != _dropCopy))
            {
                _binHover = bin;
                _dropHover = drop;
                _dropCopy = copy;
                Render();
            }
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            if (_dropHover || _binHover)
            {
                _dropHover = false;
                _binHover = false;
                Render();
            }
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);

            int idx;
            Part part = HitTest(PointToClient(new Point(e.X, e.Y)), out idx);
            string[] files = DroppedFiles(e.Data);

            if (files != null && part == Part.Bin)
            {
                foreach (string f in files) Shell.RecycleFile(f);
                _downloads.Refresh();
            }
            else if (files != null && !_dragging && !AllInFolder(files, _downloads.Folder))
            {
                Shell.MoveInto(files, _downloads.Folder, (e.KeyState & 8) != 0);
                _downloads.Refresh();
            }

            _dropHover = false;
            _binHover = false;
            if (!_closing) Render();
        }

        static string[] DroppedFiles(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return null;
            string[] files = data.GetData(DataFormats.FileDrop) as string[];
            return files != null && files.Length > 0 ? files : null;
        }

        /// <summary>
        /// True if every file already lives directly in <paramref name="folder"/>,
        /// so dropping them there would do nothing.
        /// </summary>
        public static bool AllInFolder(string[] files, string folder)
        {
            try
            {
                string target = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
                foreach (string f in files)
                {
                    string dir = Path.GetDirectoryName(f);
                    if (dir == null) continue;
                    if (!string.Equals(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar), target,
                                       StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
