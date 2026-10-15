using System;
using System.Windows.Forms;

namespace Shelf
{
    public partial class DockForm
    {
        // ----------------------------------------------------------
        // Input
        // ----------------------------------------------------------

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (!_hover)
            {
                _hover = true;
                Render();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover)
            {
                _hover = false;
                Render();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _pressed = true;
                Render();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                bool wasPressed = _pressed;
                _pressed = false;
                // Released somewhere else is a change of mind, like any button.
                if (wasPressed && ClientRectangle.Contains(e.Location)) ToggleStack();
                Render();
            }
            else if (e.Button == MouseButtons.Right)
            {
                // Everything the tray icon used to offer. The pill is already
                // on the taskbar; a second icon in the notification area just
                // to quit this one was one icon too many.
                CloseStack();
                ContextMenuStrip menu = PillMenu.Get();
                if (menu != null)
                {
                    menu.Show(PointToScreen(e.Location), ToolStripDropDownDirection.AboveRight);

                    // The pill is WS_EX_NOACTIVATE, so a menu it owns never
                    // takes the foreground - and a popup menu that was never
                    // activated never receives the click-elsewhere that would
                    // dismiss it, so it sat there until something was picked.
                    // This is the same trick tray menus have always needed.
                    SetForegroundWindow(menu.Handle);
                }
            }
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            string[] files = DroppedFiles(e.Data);
            bool ok = files != null && !StackForm.AllInFolder(files, _downloads.Folder);
            e.Effect = !ok ? DragDropEffects.None
                     : ((e.KeyState & 8) != 0 ? DragDropEffects.Copy : DragDropEffects.Move);
            if (ok != _dropHover)
            {
                _dropHover = ok;
                Render();
            }
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
            _dropHover = false;
            string[] files = DroppedFiles(e.Data);
            if (files != null && !StackForm.AllInFolder(files, _downloads.Folder))
            {
                Shell.MoveInto(files, _downloads.Folder, (e.KeyState & 8) != 0);
                _downloads.Refresh();
            }
            Render();
        }

        static string[] DroppedFiles(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return null;
            string[] files = data.GetData(DataFormats.FileDrop) as string[];
            return files != null && files.Length > 0 ? files : null;
        }
    }
}
