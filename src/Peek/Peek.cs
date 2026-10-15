using System;
using System.Drawing;

namespace Shelf
{
    /// <summary>
    /// Quick Look: Space shows the selected file in a floating preview, from
    /// the panel or from Explorer and the desktop, and Space or Esc puts it
    /// away again. One preview at a time, reused as the selection moves.
    /// </summary>
    public static class Peek
    {
        public enum From { Shelf, Explorer }

        /// <summary>Where a file's icon is on screen right now, or Rectangle.Empty.</summary>
        public delegate Rectangle OriginFinder(string path);

        static PeekForm _form;
        static From _from;
        static OriginFinder _origin;

        /// <summary>
        /// From the moment it is asked for until it is closed - including the
        /// zoom, while the real window is still hidden. Counting only a visible
        /// window let the Explorer watcher look during the zoom, decide the
        /// preview was gone, and stop watching, so it never closed again.
        /// </summary>
        public static bool IsOpen
        {
            get { return _form != null && !_form.IsDisposed; }
        }

        /// <summary>Who opened the preview, so each side only closes its own.</summary>
        public static From OpenedFrom
        {
            get { return _from; }
        }

        /// <summary>The file on show, or null.</summary>
        public static string Path
        {
            get { return IsOpen ? _form.FilePath : null; }
        }

        /// <summary>
        /// Shows <paramref name="path"/>, centred in <paramref name="area"/>
        /// (screen coordinates). <paramref name="origin"/> says where the file's
        /// icon is, for the preview to zoom out of it and back into it.
        /// </summary>
        public static void Show(string path, From from, Rectangle area, OriginFinder origin)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (IsOpen && _from == from && string.Equals(_form.FilePath, path, StringComparison.OrdinalIgnoreCase)) return;

            _from = from;
            _origin = origin;
            try
            {
                bool first = _form == null || _form.IsDisposed;
                if (first) _form = new PeekForm();
                _form.ShowFile(path, area, first ? Find(path) : Rectangle.Empty, from == From.Shelf);
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
                Close();
            }
        }

        /// <summary>Closes the preview from whichever side opened it, zooming back into the file.</summary>
        public static void Close()
        {
            PeekForm f = _form;
            _form = null;
            if (f != null && !f.IsDisposed)
            {
                try { f.CloseAnimated(Find(f.FilePath)); }
                catch (Exception ex)
                {
                    Program.LogError(ex);
                    if (!f.IsDisposed) f.Close();
                }
            }
            _origin = null;
        }

        static Rectangle Find(string path)
        {
            if (_origin == null || path == null) return Rectangle.Empty;
            try { return _origin(path); }
            catch { return Rectangle.Empty; }
        }

        /// <summary>Closes the preview only if <paramref name="from"/> opened it.</summary>
        public static void Close(From from)
        {
            if (IsOpen && _from == from) Close();
        }
    }
}
