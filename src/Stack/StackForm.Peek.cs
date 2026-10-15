using System.Collections.Generic;
using System.Drawing;

namespace Shelf
{
    /// <summary>
    /// Quick Look from the panel: Space previews the selection beside the
    /// panel, the arrow keys move through the tiles with the preview following,
    /// and Space or Esc puts it away. The preview never takes focus, so the
    /// panel keeps the keyboard and stays open under it.
    /// </summary>
    public partial class StackForm
    {
        /// <summary>Space: the selection, or the tile under the cursor; Space again closes.</summary>
        void PreviewKeyTargets()
        {
            if (Peek.IsOpen && Peek.OpenedFrom == Peek.From.Shelf)
            {
                Peek.Close();
                return;
            }

            List<int> targets = KeyTargets();
            if (targets.Count == 0 && _hoverPart == Part.Tile && _hover >= 0) targets.Add(_hover);
            Preview(targets);
        }

        void Preview(List<int> targets)
        {
            if (targets.Count == 0) return;

            // Start from the focused tile when it is one of them, so Space after
            // arrowing around shows what the keyboard is on.
            int i = targets.Contains(_focus) ? _focus : targets[0];
            if (!IsSelected(i)) SelectOnly(i);
            _focus = i;
            ShowPeek(i);
            Render();
        }

        /// <summary>Keeps the preview on the focused tile while it is up.</summary>
        void FollowPeek()
        {
            if (Peek.IsOpen && Peek.OpenedFrom == Peek.From.Shelf && _focus >= 0 && _focus < _tiles.Count)
                ShowPeek(_focus);
        }

        /// <summary>
        /// Just above the panel, over the tile it came from, so the tiles you
        /// are arrowing through stay in sight below it.
        /// </summary>
        void ShowPeek(int i)
        {
            Rectangle wa = WorkArea;
            int panelTop = _anchor.Y + _panel.Top;
            int bottom = System.Math.Max(wa.Top + wa.Height / 2, panelTop - R(12));
            Rectangle area = Rectangle.FromLTRB(wa.Left, wa.Top, wa.Right, bottom);
            Peek.Show(_tiles[i].Item.Path, Peek.From.Shelf, area, IconOnScreen);
        }

        /// <summary>
        /// The screen rectangle of a file's icon in the grid, for Quick Look
        /// to zoom out of and back into. Empty once the panel is going or the
        /// tile has scrolled out of view.
        /// </summary>
        Rectangle IconOnScreen(string path)
        {
            if (IsDisposed || !Visible || _closing) return Rectangle.Empty;
            for (int i = 0; i < _tiles.Count; i++)
            {
                if (!string.Equals(_tiles[i].Item.Path, path, System.StringComparison.OrdinalIgnoreCase)) continue;
                Rectangle r = TileRect(i);
                if (r.Top < _grid.Top || r.Bottom > _grid.Bottom) return Rectangle.Empty;
                Rectangle icon = new Rectangle(r.X + (r.Width - _icon) / 2, r.Y + _iconTop, _icon, _icon);
                icon.Offset(_anchor.X, _anchor.Y + _offset);
                return icon;
            }
            return Rectangle.Empty;
        }
    }
}
