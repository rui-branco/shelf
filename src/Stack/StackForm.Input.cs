using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Shelf
{
    public partial class StackForm
    {
        /// <summary>
        /// The keyboard, Explorer's way: arrows move, Shift stretches a range,
        /// Ctrl moves without selecting (Ctrl+Space picks), Ctrl+A takes
        /// everything, and Enter, Delete, Ctrl+C and Alt+Enter act on the
        /// selection.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_closing) return true;

            Keys key = keyData & Keys.KeyCode;
            bool shift = (keyData & Keys.Shift) != 0;
            bool ctrl = (keyData & Keys.Control) != 0;
            bool alt = (keyData & Keys.Alt) != 0;
            int f = _focus;
            int page = _cols * MaxRows;

            switch (key)
            {
                case Keys.Escape:
                    // One thing at a time: the band, then the preview, then the panel.
                    if (_banding) EndBand();
                    else if (Peek.IsOpen && Peek.OpenedFrom == Peek.From.Shelf) Peek.Close();
                    else Dismiss();
                    return true;
                case Keys.Left: MoveFocus(f < 0 ? 0 : f - 1, shift, ctrl); return true;
                case Keys.Right: MoveFocus(f < 0 ? 0 : f + 1, shift, ctrl); return true;
                case Keys.Up: MoveFocus(f < 0 ? 0 : f - _cols, shift, ctrl); return true;
                case Keys.Down: MoveFocus(f < 0 ? 0 : f + _cols, shift, ctrl); return true;
                case Keys.PageUp: MoveFocus(f < 0 ? 0 : f - page, shift, ctrl); return true;
                case Keys.PageDown: MoveFocus(f < 0 ? 0 : f + page, shift, ctrl); return true;
                case Keys.Home: MoveFocus(0, shift, ctrl); return true;
                case Keys.End: MoveFocus(_tiles.Count - 1, shift, ctrl); return true;
                case Keys.Space:
                    if (ctrl && f >= 0)
                    {
                        ToggleSelection(f);
                        Render();
                    }
                    else
                    {
                        PreviewKeyTargets();
                    }
                    return true;
                case Keys.A:
                    if (!ctrl) break;
                    SelectAll();
                    return true;
                case Keys.C:
                    if (!ctrl) break;
                    CopyItems(KeyTargets());
                    return true;
                case Keys.Enter:
                    if (alt) ShowProperties(KeyTargets());
                    else OpenItems(KeyTargets());
                    return true;
                case Keys.Delete:
                    RecycleItems(KeyTargets());
                    return true;
                case Keys.Apps:
                case Keys.F10:
                    if (key == Keys.F10 && !shift) break;
                    if (f >= 0)
                    {
                        Rectangle r = TileRect(f);
                        ShowMenu(f, new Point(r.Left + r.Width / 2, r.Top + r.Height / 2));
                    }
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ----------------------------------------------------------
        // Mouse
        // ----------------------------------------------------------

        Part HitTest(Point p, out int index)
        {
            index = -1;
            if (!_panel.Contains(p)) return Part.Outside;
            if (_openBtn.Contains(p)) return Part.OpenFolder;
            if (_binBtn.Contains(p)) return Part.Bin;
            if (_grid.Contains(p))
            {
                for (int i = 0; i < _tiles.Count; i++)
                {
                    if (TileRect(i).Contains(p))
                    {
                        index = i;
                        return Part.Tile;
                    }
                }
            }
            return Part.None;
        }

        void UpdateHover(Point p)
        {
            int idx;
            Part part = HitTest(p, out idx);
            if (part == _hoverPart && idx == _hover) return;

            _hoverPart = part;
            _hover = idx;
            // The buttons act on a single click and say so. Tiles are files, as
            // in Explorer: an arrow, select on click, open on double-click.
            Cursor = part == Part.OpenFolder || part == Part.Bin ? Cursors.Hand : Cursors.Default;
            Render();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_closing) return;

            if (_banding)
            {
                UpdateBand(e.Location);
                // Held past the top or bottom: the animation tick scrolls.
                if (e.Y < _grid.Top || e.Y > _grid.Bottom) _anim.Start();
                return;
            }

            UpdateHover(e.Location);

            if (_pressPart == Part.Tile && _press >= 0 && !_dragging && (e.Button & MouseButtons.Left) != 0)
            {
                Size d = SystemInformation.DragSize;
                if (Math.Abs(e.X - _pressPoint.X) >= d.Width || Math.Abs(e.Y - _pressPoint.Y) >= d.Height)
                {
                    // A selected tile carries the whole selection with it; an
                    // unselected one goes alone, and becomes the selection, as
                    // in Explorer.
                    if (!IsSelected(_press)) SelectOnly(_press);
                    List<DownloadItem> items = new List<DownloadItem>();
                    foreach (DownloadItem it in Items(SelectedIndices()))
                        if (Format.Exists(it.Path)) items.Add(it);
                    if (items.Count > 0) StartFileDrag(items);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_closing) return;

            int idx;
            Part part = HitTest(e.Location, out idx);
            _showFocus = false;

            // The shadow is part of the window, but a click on it is a click
            // beside the panel, and means the same as clicking anywhere else.
            if (part == Part.Outside)
            {
                Dismiss();
                return;
            }

            if (e.Button == MouseButtons.Left)
            {
                // Any empty part of the panel starts a band - the header, the
                // gaps, the footer between its buttons - not just the grid. The
                // band itself is clamped to the grid, so one started in the
                // header picks up from the top row.
                if (part == Part.None && _tiles.Count > 0)
                {
                    BeginBand(e.Location, (ModifierKeys & Keys.Control) != 0);
                    return;
                }

                if (part == Part.Tile)
                {
                    // Double-click opens, on the second press, as in Explorer.
                    if (e.Clicks >= 2)
                    {
                        OpenItems(Targets(idx));
                        return;
                    }

                    // A plain press on a tile that is not selected selects it
                    // straight away, so it lights up under the finger and a drag
                    // that follows carries it. One already in the selection
                    // waits for the release, so the whole selection can be
                    // dragged.
                    if (ModifierKeys == Keys.None && !IsSelected(idx))
                    {
                        SelectOnly(idx);
                        FollowPeek();
                    }
                }

                _pressPart = part;
                _press = idx;
                _pressPoint = e.Location;
                Render();
            }
            else if (e.Button == MouseButtons.Right && part == Part.Tile)
            {
                // Right-clicking outside the selection makes that tile the
                // selection; inside it, the menu acts on all of it.
                if (!IsSelected(idx)) SelectOnly(idx);
                Render();
                ShowMenu(idx, e.Location);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || _closing) return;

            if (_banding)
            {
                EndBand();
                return;
            }

            int idx;
            Part part = HitTest(e.Location, out idx);
            Part pressed = _pressPart;
            int pressedIdx = _press;
            _pressPart = Part.None;
            _press = -1;

            if (!_dragging && part == pressed)
            {
                if (part == Part.Tile && idx == pressedIdx) ClickTile(idx);
                else if (part == Part.OpenFolder) OpenFolder();
                else if (part == Part.Bin) OpenBin();
            }
            if (!_closing) Render();
        }

        /// <summary>
        /// A click selects, the way Explorer does: alone, or with Ctrl to add
        /// and remove, or with Shift for a range. Opening is the double-click
        /// (or Enter), and Space previews what is selected. With Quick Look
        /// up, the preview follows the click.
        /// </summary>
        void ClickTile(int i)
        {
            bool ctrl = (ModifierKeys & Keys.Control) != 0;
            bool shift = (ModifierKeys & Keys.Shift) != 0;

            if (shift) SelectRange(_rangeAnchor >= 0 ? _rangeAnchor : i, i, ctrl);
            else if (ctrl) ToggleSelection(i);
            else SelectOnly(i);
            FollowPeek();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1 || _hoverPart != Part.None)
            {
                _hover = -1;
                _hoverPart = Part.None;
                Render();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_maxScroll <= 0 || _closing) return;
            ScrollTo(_scrollTarget - e.Delta / 120f * (_tileH + _gap));
        }

        void ScrollTo(float target)
        {
            _scrollTarget = Clamp(target, 0, _maxScroll);
            if (_scrollTarget != _scroll) _anim.Start();
        }

        // ----------------------------------------------------------
        // Actions
        // ----------------------------------------------------------

        void OpenItems(List<int> targets)
        {
            if (targets.Count == 0) return;

            bool opened = false;
            foreach (int i in targets)
                if (OpenItem(_tiles[i].Item.Path)) opened = true;

            if (opened) Dismiss();
            else _downloads.Refresh();
        }

        /// <summary>
        /// Shell-executed, the way a double-click in Explorer opens it. A file
        /// with no program behind it used to fail silently here; now it gets
        /// Windows' own "how do you want to open this" picker.
        /// </summary>
        static bool OpenItem(string path)
        {
            if (!Format.Exists(path)) return false;
            try
            {
                Process.Start(path);
                return true;
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode != ERROR_NO_ASSOCIATION)
                {
                    Program.LogError(ex);
                    return false;
                }
                try
                {
                    Shell.OpenWith(path);
                    return true;
                }
                catch (Exception ex2) { Program.LogError(ex2); }
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
            return false;
        }

        const int ERROR_NO_ASSOCIATION = 1155;

        void OpenFolder()
        {
            try { Process.Start("explorer.exe", "\"" + _downloads.Folder + "\""); }
            catch (Exception ex) { Program.LogError(ex); }
            Dismiss();
        }

        void OpenBin()
        {
            try { Process.Start("explorer.exe", "shell:RecycleBinFolder"); }
            catch (Exception ex) { Program.LogError(ex); }
            Dismiss();
        }

        void CopyItems(List<int> targets)
        {
            StringCollection sc = new StringCollection();
            foreach (int i in targets)
                if (Format.Exists(_tiles[i].Item.Path)) sc.Add(_tiles[i].Item.Path);
            if (sc.Count > 0) Clipboard.SetFileDropList(sc);
        }

        void CopyPaths(List<int> targets)
        {
            List<string> lines = new List<string>();
            foreach (int i in targets) lines.Add("\"" + _tiles[i].Item.Path + "\"");
            if (lines.Count > 0) Clipboard.SetText(string.Join("\r\n", lines.ToArray()));
        }

        void ShowInFolder(List<int> targets)
        {
            if (targets.Count == 0) return;
            try { Process.Start("explorer.exe", "/select,\"" + _tiles[targets[0]].Item.Path + "\""); }
            catch (Exception ex) { Program.LogError(ex); }
            Dismiss();
        }

        /// <summary>Windows' own Properties sheet; one combined sheet for several files.</summary>
        void ShowProperties(List<int> targets)
        {
            List<string> paths = new List<string>();
            foreach (int i in targets)
                if (Format.Exists(_tiles[i].Item.Path)) paths.Add(_tiles[i].Item.Path);
            if (paths.Count == 0) return;

            if (!Shell.ShowProperties(paths.ToArray()))
                Program.LogError(new InvalidOperationException("Properties failed for " + paths[0]));
            Dismiss();
        }

        void RecycleItems(List<int> targets)
        {
            List<string> paths = new List<string>();
            foreach (int i in targets)
                if (Format.Exists(_tiles[i].Item.Path)) paths.Add(_tiles[i].Item.Path);
            if (paths.Count == 0) return;

            // RecycleFiles asks the shell for FOF_SILENT, so Windows shows nothing
            // either way and this bool is the only report there is. Dropped on the
            // floor, a locked or protected file meant the grid redrew with the tile
            // still sitting in it and no hint of why.
            if (!Shell.RecycleFiles(paths.ToArray()))
            {
                _modal = true;
                try
                {
                    MessageBox.Show(this,
                        (paths.Count == 1 ? "Could not delete\r\n" + paths[0] : "Could not delete all " + paths.Count + " items.")
                        + "\r\n\r\nA file may be open in another program, or protected.",
                        "Shelf", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally { _modal = false; }
            }
            _downloads.Refresh();
        }
    }
}
