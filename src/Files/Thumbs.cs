using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Windows.Forms;

namespace Shelf
{
    /// <summary>
    /// Shell thumbnails, extracted off the UI thread and kept for the life of
    /// the app.
    ///
    /// The panel used to extract every thumbnail synchronously in its
    /// constructor and throw them all away when it closed, so every single open
    /// waited on the shell for each file again - images and PDFs being the slow
    /// ones. Now extraction happens on one background thread, results outlive
    /// the panel, and the newest downloads are fetched as they arrive, so by the
    /// time the panel opens its icons are usually already here.
    ///
    /// The cache itself is touched only on the UI thread; the worker hands each
    /// finished bitmap over with BeginInvoke. That way nothing is ever disposed
    /// while a paint is using it.
    /// </summary>
    public static class Thumbs
    {
        public sealed class Thumb
        {
            /// <summary>Null when the shell had nothing to give; drawn as a generic file.</summary>
            public Bitmap Image;

            /// <summary>
            /// An actual preview (photo, video frame, page) rather than an icon.
            /// Previews are opaque rectangles, so the panel rounds their corners
            /// and frames them; icons have their own shape and are left alone.
            /// </summary>
            public bool IsPreview;

            internal long Used;
        }

        sealed class Job
        {
            public string Key;
            public string Path;
            public int Size;
        }

        const int Capacity = 400;

        static Control _sync;
        static Thread _worker;
        static readonly object _lock = new object();
        static readonly Queue<Job> _queue = new Queue<Job>();
        static readonly HashSet<string> _pending = new HashSet<string>();
        static readonly AutoResetEvent _wake = new AutoResetEvent(false);

        // UI thread only.
        static readonly Dictionary<string, Thumb> _cache = new Dictionary<string, Thumb>();
        static long _clock;

        /// <summary>Raised on the UI thread whenever a requested thumbnail arrives.</summary>
        public static event EventHandler Loaded;

        /// <summary>Starts the extraction thread. <paramref name="sync"/> owns the UI thread.</summary>
        public static void Start(Control sync)
        {
            if (_worker != null) return;
            _sync = sync;

            _worker = new Thread(Work);
            _worker.IsBackground = true;
            _worker.Name = "Shelf thumbnails";
            // Thumbnail handlers are COM objects, plenty of them apartment-threaded.
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }

        static string Key(string path, DateTime stamp, int size)
        {
            return path + "|" + stamp.Ticks + "|" + size;
        }

        /// <summary>
        /// The thumbnail for a file at a pixel size, or null if it is not ready
        /// yet - in which case it has been queued and Loaded will fire. A file
        /// that changes gets a new stamp and so a fresh thumbnail.
        /// </summary>
        public static Thumb Get(string path, DateTime stamp, int size)
        {
            string key = Key(path, stamp, size);
            Thumb t;
            if (_cache.TryGetValue(key, out t))
            {
                t.Used = ++_clock;
                return t;
            }
            Enqueue(key, path, size);
            return null;
        }

        /// <summary>
        /// The cached image if there is one, without queuing anything. The
        /// bitmap belongs to the cache: copy it, never dispose it.
        /// </summary>
        public static Bitmap Peek(string path, DateTime stamp, int size)
        {
            Thumb t;
            if (_cache.TryGetValue(Key(path, stamp, size), out t) && t.Image != null)
                return t.Image;
            return null;
        }

        /// <summary>Queues the newest items so the panel opens with its icons ready.</summary>
        public static void Prefetch(IList<DownloadItem> items, int count, int size)
        {
            int n = Math.Min(count, items.Count);
            for (int i = 0; i < n; i++)
            {
                DownloadItem it = items[i];
                string key = Key(it.Path, it.Modified, size);
                if (!_cache.ContainsKey(key)) Enqueue(key, it.Path, size);
            }
        }

        static void Enqueue(string key, string path, int size)
        {
            lock (_lock)
            {
                if (!_pending.Add(key)) return;
                Job j = new Job();
                j.Key = key;
                j.Path = path;
                j.Size = size;
                _queue.Enqueue(j);
            }
            _wake.Set();
        }

        static void Work()
        {
            while (true)
            {
                Job job = null;
                lock (_lock)
                {
                    if (_queue.Count > 0) job = _queue.Dequeue();
                }

                if (job == null)
                {
                    _wake.WaitOne();
                    continue;
                }

                Bitmap bmp = null;
                try { bmp = Shell.GetThumbnail(job.Path, job.Size); }
                catch { }

                Thumb t = new Thumb();
                t.Image = bmp;
                t.IsPreview = bmp != null && IsOpaqueRectangle(bmp);
                Deliver(job.Key, t);
            }
        }

        static void Deliver(string key, Thumb t)
        {
            Control sync = _sync;
            if (sync == null || sync.IsDisposed || !sync.IsHandleCreated)
            {
                if (t.Image != null) t.Image.Dispose();
                return;
            }

            try
            {
                sync.BeginInvoke(new MethodInvoker(delegate
                {
                    lock (_lock) { _pending.Remove(key); }

                    Thumb old;
                    if (_cache.TryGetValue(key, out old) && old.Image != null) old.Image.Dispose();
                    t.Used = ++_clock;
                    _cache[key] = t;
                    Trim();

                    EventHandler h = Loaded;
                    if (h != null) h(null, EventArgs.Empty);
                }));
            }
            catch
            {
                if (t.Image != null) t.Image.Dispose();
            }
        }

        /// <summary>Drops the least recently drawn thumbnails once the cache is full.</summary>
        static void Trim()
        {
            if (_cache.Count <= Capacity) return;

            List<KeyValuePair<string, Thumb>> all = new List<KeyValuePair<string, Thumb>>(_cache);
            all.Sort(delegate(KeyValuePair<string, Thumb> a, KeyValuePair<string, Thumb> b)
            {
                return a.Value.Used.CompareTo(b.Value.Used);
            });

            int drop = _cache.Count - Capacity * 3 / 4;
            for (int i = 0; i < drop; i++)
            {
                if (all[i].Value.Image != null) all[i].Value.Image.Dispose();
                _cache.Remove(all[i].Key);
            }
        }

        /// <summary>
        /// True when all four corners are fully opaque. Icons are shapes on a
        /// transparent canvas; previews are pictures that fill their rectangle.
        /// </summary>
        static bool IsOpaqueRectangle(Bitmap b)
        {
            try
            {
                int r = b.Width - 1, btm = b.Height - 1;
                return b.GetPixel(0, 0).A == 255 && b.GetPixel(r, 0).A == 255
                    && b.GetPixel(0, btm).A == 255 && b.GetPixel(r, btm).A == 255;
            }
            catch
            {
                return false;
            }
        }
    }
}
