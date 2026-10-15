using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Forms;

namespace Shelf
{
    /// <summary>The shape a preview wants its window to take.</summary>
    public enum PreviewShape
    {
        /// <summary>A picture: the window takes the picture's proportions.</summary>
        Picture,
        /// <summary>A document: a tall page-shaped window.</summary>
        Page,
        /// <summary>Nothing to show but the file itself: a small card.</summary>
        Card,
    }

    /// <summary>
    /// What Quick Look shows, picked per file, best first:
    /// photos at full size (with their camera rotation), then whatever preview
    /// handler Windows has for the file - PDFs, Office, text, code, media -
    /// then plain text for anything that reads as text, and finally a card with
    /// the file's big shell thumbnail and its details.
    /// </summary>
    public class PreviewPane : Control
    {
        static readonly string[] PictureTypes = {
            ".jpg", ".jpeg", ".jpe", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico",
            ".webp", ".heic", ".heif", ".avif", ".jxr", ".dng", ".cr2", ".nef", ".arw"
        };

        static readonly string[] VideoTypes = {
            ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".mpg", ".mpeg"
        };

        /// <summary>
        /// Read as text by Shelf itself, ahead of any preview handler: Windows'
        /// own text previewer draws a white page edge to edge whatever the
        /// theme, which in dark mode is a flashbang.
        /// </summary>
        static readonly string[] TextTypes = {
            ".txt", ".log", ".md", ".markdown", ".json", ".xml", ".csv", ".tsv", ".ini", ".cfg", ".conf",
            ".yaml", ".yml", ".toml", ".env", ".cs", ".js", ".ts", ".tsx", ".jsx", ".py", ".rb", ".go",
            ".rs", ".java", ".kt", ".c", ".h", ".cpp", ".hpp", ".css", ".scss", ".html", ".htm", ".sql",
            ".ps1", ".psm1", ".bat", ".cmd", ".sh", ".php", ".swift", ".lua", ".gitignore", ".editorconfig"
        };

        const long MaxTextBytes = 2 * 1024 * 1024;

        PreviewShape _shape = PreviewShape.Card;
        Bitmap _picture;
        bool _pictureIsThumbnail;

        object _handlerObject;
        PreviewInterop.IPreviewHandler _handler;
        Panel _host;

        TextBox _text;

        Bitmap _cardIcon;
        string _cardTitle;
        string _cardDetail;

        float _s = 1f;

        /// <summary>
        /// True while another process's preview handler is drawing in here.
        /// That content never shows up in our own snapshots of the window.
        /// </summary>
        public bool HostsHandler
        {
            get { return _handler != null; }
        }

        public PreviewPane()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        /// <summary>
        /// Shows a file. <paramref name="limit"/> is the most screen the
        /// window could have, so a 50-megapixel photo is kept at screen size
        /// rather than at 200 MB. <paramref name="natural"/> is a picture's own
        /// size, for the window to take its shape.
        /// </summary>
        public PreviewShape Load(string path, float scale, Size limit, out Size natural)
        {
            Unload();
            _s = scale;
            natural = Size.Empty;
            BackColor = Theme.PanelSolid;

            try
            {
                if (!Directory.Exists(path))
                {
                    string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();

                    if (Array.IndexOf(PictureTypes, ext) >= 0 && LoadPicture(path, limit, out natural))
                        return Shape(PreviewShape.Picture);

                    if (Array.IndexOf(TextTypes, ext) >= 0 && LoadText(path))
                        return Shape(PreviewShape.Page);

                    if (LoadHandler(path))
                        return Shape(PreviewShape.Page);

                    // No handler for a video on this machine: a frame of it is
                    // still better than an icon.
                    if (Array.IndexOf(VideoTypes, ext) >= 0 && LoadThumbnailPicture(path, limit, out natural))
                        return Shape(PreviewShape.Picture);

                    if (LoadText(path))
                        return Shape(PreviewShape.Page);
                }
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
                Unload();
            }

            LoadCard(path);
            return Shape(PreviewShape.Card);
        }

        PreviewShape Shape(PreviewShape s)
        {
            _shape = s;
            Invalidate();
            return s;
        }

        // ----------------------------------------------------------
        // Pictures
        // ----------------------------------------------------------

        bool LoadPicture(string path, Size limit, out Size natural)
        {
            natural = Size.Empty;
            try
            {
                // Read into memory first: GDI+ keeps the file locked for as long
                // as an image made straight from it is alive.
                byte[] bytes = File.ReadAllBytes(path);
                using (MemoryStream ms = new MemoryStream(bytes))
                using (Image src = Image.FromStream(ms, false, false))
                {
                    RotateUpright(src);
                    natural = src.Size;
                    _picture = Fit(src, limit);
                    return true;
                }
            }
            catch
            {
                // Not a format GDI+ reads (WebP, HEIC, RAW): Windows' own
                // codecs can still render it as a large thumbnail.
                return LoadThumbnailPicture(path, limit, out natural);
            }
        }

        bool LoadThumbnailPicture(string path, Size limit, out Size natural)
        {
            natural = Size.Empty;
            int size = Math.Max(256, Math.Min(1600, Math.Max(limit.Width, limit.Height)));
            Bitmap thumb = Shell.GetThumbnail(path, size);
            if (thumb == null) return false;
            _picture = thumb;
            _pictureIsThumbnail = true;
            natural = thumb.Size;
            return true;
        }

        /// <summary>
        /// Phones store photos sideways and record which way is up in EXIF
        /// orientation (tag 0x0112). Explorer honours it; so does this.
        /// </summary>
        static void RotateUpright(Image img)
        {
            if (Array.IndexOf(img.PropertyIdList, 0x0112) < 0) return;
            int o = img.GetPropertyItem(0x0112).Value[0];
            RotateFlipType t = RotateFlipType.RotateNoneFlipNone;
            switch (o)
            {
                case 2: t = RotateFlipType.RotateNoneFlipX; break;
                case 3: t = RotateFlipType.Rotate180FlipNone; break;
                case 4: t = RotateFlipType.Rotate180FlipX; break;
                case 5: t = RotateFlipType.Rotate90FlipX; break;
                case 6: t = RotateFlipType.Rotate90FlipNone; break;
                case 7: t = RotateFlipType.Rotate270FlipX; break;
                case 8: t = RotateFlipType.Rotate270FlipNone; break;
            }
            if (t != RotateFlipType.RotateNoneFlipNone) img.RotateFlip(t);
        }

        static Bitmap Fit(Image src, Size limit)
        {
            float k = Math.Min(1f, Math.Min((float)limit.Width / src.Width, (float)limit.Height / src.Height));
            int w = Math.Max(1, (int)(src.Width * k)), h = Math.Max(1, (int)(src.Height * k));
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            return b;
        }

        // ----------------------------------------------------------
        // Preview handlers
        // ----------------------------------------------------------

        /// <summary>
        /// Creates the registered handler, hands it the file by whichever of
        /// the three ways it accepts, and lets it draw into a child window.
        /// Most handlers run out of process in prevhost.exe and parent their
        /// window into ours, exactly as they do in Explorer.
        /// </summary>
        bool LoadHandler(string path)
        {
            Guid clsid;
            if (!PreviewInterop.TryGetHandler(path, out clsid)) return false;

            object obj = null;
            try
            {
                Type t = Type.GetTypeFromCLSID(clsid, false);
                if (t == null) return false;
                obj = Activator.CreateInstance(t);

                PreviewInterop.IPreviewHandler handler = obj as PreviewInterop.IPreviewHandler;
                if (handler == null || !Initialize(obj, path)) return Release(obj);

                _host = new Panel();
                _host.BackColor = BackColor;
                _host.Bounds = ClientRectangle;
                Controls.Add(_host);

                PreviewInterop.IPreviewHandlerVisuals visuals = obj as PreviewInterop.IPreviewHandlerVisuals;
                if (visuals != null)
                {
                    try
                    {
                        visuals.SetBackgroundColor(PreviewInterop.ColorRef(BackColor));
                        visuals.SetTextColor(PreviewInterop.ColorRef(Theme.Text));
                    }
                    catch { }
                }

                PreviewInterop.RECT r = new PreviewInterop.RECT(_host.ClientRectangle);
                handler.SetWindow(_host.Handle, ref r);
                handler.DoPreview();

                _handler = handler;
                _handlerObject = obj;
                return true;
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
                if (_host != null) { _host.Dispose(); _host = null; }
                return Release(obj);
            }
        }

        static bool Initialize(object obj, string path)
        {
            PreviewInterop.IInitializeWithFile byFile = obj as PreviewInterop.IInitializeWithFile;
            if (byFile != null)
            {
                try { byFile.Initialize(path, PreviewInterop.STGM_READ); return true; }
                catch { }
            }

            PreviewInterop.IInitializeWithItem byItem = obj as PreviewInterop.IInitializeWithItem;
            if (byItem != null)
            {
                try
                {
                    object item;
                    PreviewInterop.SHCreateItemFromParsingName(path, IntPtr.Zero, PreviewInterop.IID_IShellItem, out item);
                    byItem.Initialize(item, PreviewInterop.STGM_READ);
                    return true;
                }
                catch { }
            }

            PreviewInterop.IInitializeWithStream byStream = obj as PreviewInterop.IInitializeWithStream;
            if (byStream != null)
            {
                try
                {
                    IStream stream;
                    PreviewInterop.SHCreateStreamOnFileEx(path,
                        PreviewInterop.STGM_READ | PreviewInterop.STGM_SHARE_DENY_NONE, 0, false, IntPtr.Zero, out stream);
                    byStream.Initialize(stream, PreviewInterop.STGM_READ);
                    return true;
                }
                catch { }
            }
            return false;
        }

        static bool Release(object obj)
        {
            if (obj != null && Marshal.IsComObject(obj))
            {
                try { Marshal.FinalReleaseComObject(obj); }
                catch { }
            }
            return false;
        }

        // ----------------------------------------------------------
        // Text
        // ----------------------------------------------------------

        bool LoadText(string path)
        {
            FileInfo fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > MaxTextBytes) return false;

            byte[] head = new byte[Math.Min(8192, (int)fi.Length)];
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                fs.Read(head, 0, head.Length);
            if (Array.IndexOf(head, (byte)0) >= 0) return false;     // binary

            string text;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader sr = new StreamReader(fs, Encoding.UTF8, true))
                text = sr.ReadToEnd();

            _text = new TextBox();
            _text.Multiline = true;
            _text.ReadOnly = true;
            _text.WordWrap = true;
            _text.ScrollBars = ScrollBars.Vertical;
            _text.BorderStyle = BorderStyle.None;
            _text.BackColor = BackColor;
            _text.ForeColor = Theme.Text;
            _text.Font = new Font("Consolas", 10f * _s, GraphicsUnit.Point);
            _text.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            _text.Bounds = TextBounds();
            // Windows' dark scrollbars, in dark mode: the classic light ones
            // glare down the side of an otherwise dark window.
            if (!Theme.Light) _text.HandleCreated += delegate { SetWindowTheme(_text.Handle, "DarkMode_Explorer", null); };
            Controls.Add(_text);
            _text.Select(0, 0);
            return true;
        }

        Rectangle TextBounds()
        {
            int pad = (int)Math.Round(16 * _s);
            return new Rectangle(pad, pad / 2, Math.Max(1, Width - pad * 2), Math.Max(1, Height - pad));
        }

        // ----------------------------------------------------------
        // Card
        // ----------------------------------------------------------

        void LoadCard(string path)
        {
            _cardIcon = Shell.GetThumbnail(path, (int)Math.Round(160 * _s));
            _cardTitle = Format.FolderName(path);
            try
            {
                if (Directory.Exists(path))
                {
                    _cardDetail = "Folder" + Separator + Format.When(Directory.GetLastWriteTime(path));
                }
                else
                {
                    FileInfo fi = new FileInfo(path);
                    string type = fi.Extension.Length > 1 ? fi.Extension.Substring(1).ToUpperInvariant() + " file" : "File";
                    _cardDetail = type + Separator + Format.Size(fi.Length) + Separator + Format.When(fi.LastWriteTime);
                }
            }
            catch
            {
                _cardDetail = "";
            }
        }

        static readonly string Separator = "  " + (char)0x00B7 + "  ";

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        // ----------------------------------------------------------
        // Layout, paint, teardown
        // ----------------------------------------------------------

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_host != null && _handler != null)
            {
                _host.Bounds = ClientRectangle;
                PreviewInterop.RECT r = new PreviewInterop.RECT(_host.ClientRectangle);
                try { _handler.SetRect(ref r); }
                catch { }
            }
            if (_text != null) _text.Bounds = TextBounds();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(BackColor))
                g.FillRectangle(b, ClientRectangle);

            PaintContent(g);
        }

        /// <summary>
        /// Draws what the pane shows into any Graphics, in its own coordinates.
        /// The zoom flies a picture of the window before the window is ever on
        /// screen, and the generic way of taking one (DrawToBitmap on a hidden
        /// window) came back with the pane blank - so the zoom flew an empty
        /// frame and the photo popped in afterwards. A preview handler's own
        /// drawing cannot be had this way at all; its pane stays plain rather
        /// than borrowing the file's icon, which only showed a giant PDF logo
        /// before the page.
        /// </summary>
        public void PaintSnapshot(Graphics g)
        {
            using (SolidBrush b = new SolidBrush(BackColor))
                g.FillRectangle(b, 0, 0, Width, Height);
            PaintContent(g);

            if (_text != null)
            {
                TextRenderer.DrawText(g, _text.Text, _text.Font, _text.Bounds, _text.ForeColor,
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix
                    | TextFormatFlags.PreserveGraphicsTranslateTransform);
            }
        }

        void PaintContent(Graphics g)
        {
            if (_shape == PreviewShape.Picture && _picture != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float k = Math.Min((float)Width / _picture.Width, (float)Height / _picture.Height);
                if (_pictureIsThumbnail) k = Math.Min(k, 1f);     // never blow a thumbnail up
                int w = (int)(_picture.Width * k), h = (int)(_picture.Height * k);
                g.DrawImage(_picture, new Rectangle((Width - w) / 2, (Height - h) / 2, w, h));
            }
            else if (_shape == PreviewShape.Card)
            {
                PaintCard(g);
            }
        }

        void PaintCard(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float cy = Height / 2f;

            if (_cardIcon != null)
            {
                int w = _cardIcon.Width, h = _cardIcon.Height;
                g.DrawImage(_cardIcon, new Rectangle((Width - w) / 2, (int)(cy - h - 8 * _s), w, h));
            }

            using (StringFormat f = new StringFormat())
            {
                f.Alignment = StringAlignment.Center;
                f.Trimming = StringTrimming.EllipsisCharacter;
                f.FormatFlags = StringFormatFlags.NoWrap;
                float pad = 24 * _s;
                using (SolidBrush b = new SolidBrush(Theme.Text))
                    g.DrawString(_cardTitle ?? "", Theme.Strong(16 * _s), b, new RectangleF(pad, cy + 6 * _s, Width - pad * 2, 26 * _s), f);
                using (SolidBrush b = new SolidBrush(Theme.TextDim))
                    g.DrawString(_cardDetail ?? "", Theme.Font(13 * _s), b, new RectangleF(pad, cy + 36 * _s, Width - pad * 2, 22 * _s), f);
            }
        }

        /// <summary>Lets go of whatever is showing, the handler first.</summary>
        public void Unload()
        {
            if (_handler != null)
            {
                try { _handler.Unload(); }
                catch { }
                _handler = null;
            }
            Release(_handlerObject);
            _handlerObject = null;

            if (_host != null) { _host.Dispose(); _host = null; }
            if (_text != null) { _text.Dispose(); _text = null; }
            if (_picture != null) { _picture.Dispose(); _picture = null; }
            if (_cardIcon != null) { _cardIcon.Dispose(); _cardIcon = null; }
            _pictureIsThumbnail = false;
            _shape = PreviewShape.Card;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Unload();
            base.Dispose(disposing);
        }
    }
}
