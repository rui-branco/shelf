using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Shelf
{
    /// <summary>
    /// Selection the way Explorer does it: Ctrl+click toggles a tile,
    /// Shift+click takes a range, a rubber band dragged across empty space takes
    /// everything it touches, and the keyboard does the same with the arrows.
    ///
    /// Tracked by path rather than by position, so a download landing while
    /// the panel is open - which shifts every tile along - leaves the same
    /// files selected.
    /// </summary>
    public partial class StackForm
    {
        readonly HashSet<string> _selection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int _focus = -1;           // the tile the keyboard is on
        int _rangeAnchor = -1;     // where a Shift range starts from
        bool _showFocus;           // the focus ring shows once the keyboard is in use

        // The rubber band, in content coordinates (scroll included) so it stays
        // anchored to the tiles while the grid scrolls under it.
        bool _banding;
        Point _bandStart;
        Point _bandEnd;
        HashSet<string> _bandBase;

        bool IsSelected(int i)
        {
            return i >= 0 && i < _tiles.Count && _selection.Contains(_tiles[i].Item.Path);
        }

        /// <summary>The selected tiles, in grid order.</summary>
        List<int> SelectedIndices()
        {
            List<int> list = new List<int>();
            for (int i = 0; i < _tiles.Count; i++)
                if (_selection.Contains(_tiles[i].Item.Path)) list.Add(i);
            return list;
        }

        /// <summary>
        /// What an action on tile <paramref name="i"/> applies to: the whole
        /// selection when the tile is part of it, otherwise that tile alone.
        /// </summary>
        List<int> Targets(int i)
        {
            if (IsSelected(i)) return SelectedIndices();
            List<int> one = new List<int>();
            if (i >= 0 && i < _tiles.Count) one.Add(i);
            return one;
        }

        /// <summary>What the keyboard acts on: the selection, or failing that the focused tile.</summary>
        List<int> KeyTargets()
        {
            List<int> list = SelectedIndices();
            if (list.Count == 0 && _focus >= 0 && _focus < _tiles.Count) list.Add(_focus);
            return list;
        }

        List<DownloadItem> Items(List<int> indices)
        {
            List<DownloadItem> items = new List<DownloadItem>();
            foreach (int i in indices) items.Add(_tiles[i].Item);
            return items;
        }

        void SelectOnly(int i)
        {
            _selection.Clear();
            if (i >= 0 && i < _tiles.Count) _selection.Add(_tiles[i].Item.Path);
            _focus = i;
            _rangeAnchor = i;
        }

        void ToggleSelection(int i)
        {
            string path = _tiles[i].Item.Path;
            if (!_selection.Remove(path)) _selection.Add(path);
            _focus = i;
            _rangeAnchor = i;
        }

        /// <summary>Selects every tile from <paramref name="from"/> to <paramref name="to"/>.</summary>
        void SelectRange(int from, int to, bool add)
        {
            if (!add) _selection.Clear();
            int lo = Math.Min(from, to), hi = Math.Max(from, to);
            for (int k = Math.Max(0, lo); k <= hi && k < _tiles.Count; k++)
                _selection.Add(_tiles[k].Item.Path);
            _focus = to;
        }

        void SelectAll()
        {
            foreach (Tile t in _tiles) _selection.Add(t.Item.Path);
            if (_focus < 0 && _tiles.Count > 0) _focus = 0;
            Render();
        }

        /// <summary>
        /// The keyboard's move: plain arrows move the selection, Shift stretches
        /// it from where the range started, Ctrl moves only the focus so that
        /// Ctrl+Space can pick tiles out one at a time.
        /// </summary>
        void MoveFocus(int target, bool shift, bool ctrl)
        {
            int n = _tiles.Count;
            if (n == 0) return;
            target = Math.Max(0, Math.Min(n - 1, target));

            if (shift)
            {
                if (_rangeAnchor < 0) _rangeAnchor = _focus >= 0 ? _focus : target;
                SelectRange(_rangeAnchor, target, ctrl);
            }
            else if (!ctrl)
            {
                SelectOnly(target);
            }

            _focus = target;
            _showFocus = true;
            EnsureVisible(target);
            FollowPeek();
            Render();
        }

        void EnsureVisible(int i)
        {
            int top = (i / _cols) * (_tileH + _gap);
            int view = _grid.Height - _gridBottom;
            if (top < _scrollTarget) ScrollTo(top);
            else if (top + _tileH > _scrollTarget + view) ScrollTo(top + _tileH - view);
        }

        /// <summary>
        /// Keeps the selection honest after the folder changed: files that are
        /// gone drop out, and the focus follows its file to wherever it moved.
        /// </summary>
        void RemapSelection(string focusPath)
        {
            HashSet<string> present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _focus = -1;
            for (int i = 0; i < _tiles.Count; i++)
            {
                string p = _tiles[i].Item.Path;
                present.Add(p);
                if (focusPath != null && string.Equals(p, focusPath, StringComparison.OrdinalIgnoreCase)) _focus = i;
            }
            _selection.RemoveWhere(delegate(string p) { return !present.Contains(p); });
            _rangeAnchor = _focus;
        }

        // ----------------------------------------------------------
        // Rubber band
        // ----------------------------------------------------------

        /// <summary>
        /// Starts a band on empty space. Without Ctrl it replaces the
        /// selection, so a plain click on empty space also clears it, as in
        /// Explorer; with Ctrl it adds to what is already selected.
        /// </summary>
        void BeginBand(Point client, bool add)
        {
            _banding = true;
            _bandStart = _bandEnd = ToContent(client);
            if (!add) _selection.Clear();
            _bandBase = new HashSet<string>(_selection, StringComparer.OrdinalIgnoreCase);
            Render();
        }

        void UpdateBand(Point client)
        {
            if (!_banding) return;
            _bandEnd = ToContent(client);

            Rectangle band = BandRect();
            _selection.Clear();
            _selection.UnionWith(_bandBase);
            int scroll = (int)Math.Round(_scroll);
            for (int i = 0; i < _tiles.Count; i++)
            {
                Rectangle r = TileRect(i);
                r.Offset(0, scroll);
                if (band.IntersectsWith(r))
                {
                    _selection.Add(_tiles[i].Item.Path);
                    _focus = i;
                }
            }
            Render();
        }

        void EndBand()
        {
            _banding = false;
            _bandBase = null;
            _rangeAnchor = _focus;
            Render();
        }

        /// <summary>The band in content coordinates.</summary>
        Rectangle BandRect()
        {
            return Rectangle.FromLTRB(
                Math.Min(_bandStart.X, _bandEnd.X), Math.Min(_bandStart.Y, _bandEnd.Y),
                Math.Max(_bandStart.X, _bandEnd.X), Math.Max(_bandStart.Y, _bandEnd.Y));
        }

        Point ToContent(Point client)
        {
            // Clamped to the grid, so dragging past an edge selects up to the
            // edge rather than off into the header.
            int x = Math.Max(_grid.Left, Math.Min(_grid.Right, client.X));
            int y = Math.Max(_grid.Top, Math.Min(_grid.Bottom, client.Y));
            return new Point(x, y + (int)Math.Round(_scroll));
        }

        /// <summary>
        /// Scrolls while the band is held past the top or bottom of the grid,
        /// faster the further out the cursor is. Called from the animation tick;
        /// true while there is more to do.
        /// </summary>
        bool AutoScrollBand()
        {
            if (!_banding || _maxScroll <= 0) return false;

            Point c = PointToClient(Cursor.Position);
            float step = 0;
            if (c.Y < _grid.Top) step = -Math.Max(4f, (_grid.Top - c.Y) / 2f);
            else if (c.Y > _grid.Bottom) step = Math.Max(4f, (c.Y - _grid.Bottom) / 2f);
            if (step == 0) return false;

            _scrollTarget = Clamp(_scrollTarget + step, 0, _maxScroll);
            _scroll = _scrollTarget;
            UpdateBand(c);
            return true;
        }

        void DrawBand(Graphics g)
        {
            if (!_banding) return;
            Rectangle r = BandRect();
            r.Offset(0, -(int)Math.Round(_scroll));
            if (r.Width < 2 && r.Height < 2) return;

            g.SetClip(_grid);
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(Theme.Light ? 40 : 46, Theme.Accent)))
                g.FillRectangle(fill, r);
            using (Pen pen = new Pen(Color.FromArgb(200, Theme.Accent), 1f))
                g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
            g.ResetClip();
        }
    }
}
