using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Shelf
{
    /// <summary>
    /// The tiles' right-click menu. It acts on the selection when the clicked
    /// tile is part of it, and on that tile alone otherwise - the same rule
    /// Explorer follows.
    /// </summary>
    public partial class StackForm
    {
        ToolStripMenuItem _openWithItem;

        delegate void TilesAction(List<int> targets);

        void ShowMenu(int i, Point client)
        {
            if (_menu == null) BuildMenu();
            List<int> targets = Targets(i);
            _menu.Tag = targets;

            // Open with picks one program for one file; Windows has no picker
            // for several at once.
            _openWithItem.Enabled = targets.Count == 1;

            _menu.Show(this, client);
        }

        void BuildMenu()
        {
            _menu = new ContextMenuStrip();
            Theme.StyleMenu(_menu);

            AddItem("Open", 0xE8E5, delegate(List<int> t) { OpenItems(t); });
            _openWithItem = AddItem("Open with\u2026", 0xE7AC, delegate(List<int> t)
            {
                try { Shell.OpenWith(_tiles[t[0]].Item.Path); }
                catch (Exception ex) { Program.LogError(ex); }
                Dismiss();
            });
            AddItem("Quick Look", 0xE7B3, delegate(List<int> t) { Preview(t); });
            AddItem("Show in folder", 0xE838, delegate(List<int> t) { ShowInFolder(t); });
            _menu.Items.Add(new ToolStripSeparator());
            AddItem("Copy", 0xE8C8, delegate(List<int> t) { CopyItems(t); });
            AddItem("Copy as path", 0xE71B, delegate(List<int> t) { CopyPaths(t); });
            _menu.Items.Add(new ToolStripSeparator());
            AddItem("Delete", 0xE74D, delegate(List<int> t) { RecycleItems(t); });
            _menu.Items.Add(new ToolStripSeparator());
            AddItem("Properties", 0xE946, delegate(List<int> t) { ShowProperties(t); });
        }

        ToolStripMenuItem AddItem(string text, int glyph, TilesAction action)
        {
            ToolStripMenuItem item = Theme.MenuItem(text, glyph);
            item.Click += delegate
            {
                List<int> targets = _menu.Tag as List<int>;
                if (targets == null) return;

                // The list can change under an open menu; act only on tiles
                // that are still there.
                List<int> live = new List<int>();
                foreach (int i in targets)
                    if (i >= 0 && i < _tiles.Count) live.Add(i);
                if (live.Count > 0) action(live);
            };
            _menu.Items.Add(item);
            return item;
        }
    }
}
