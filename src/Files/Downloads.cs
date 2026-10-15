using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Shelf
{
    /// <summary>
    /// Represents a file in the Downloads folder.
    /// </summary>
    public class DownloadItem
    {
        public string Path;
        public string Name;
        /// <summary>Last write time: what changes when the file's content does.</summary>
        public DateTime Modified;
        /// <summary>When it landed in the folder, as best NTFS can tell. The sort key.</summary>
        public DateTime Added;
        public long Size;
        public bool IsFolder;
    }

    /// <summary>
    /// Watches the Downloads folder and maintains a list of recent files.
    /// </summary>
    public class Downloads : IDisposable
    {
        string _folder;
        int _maxItems;
        FileSystemWatcher _watcher;
        Timer _debounce;
        Timer _retry;
        Control _syncTarget;
        List<DownloadItem> _items;
        HashSet<string> _knownPaths;
        string _signature = "";
        DateTime _lastSeen;

        // Extensions that indicate an in-progress download. These are temporary
        // files that browsers create while downloading; showing them would be
        // confusing because they vanish or change name when the download finishes.
        static readonly string[] PartialExtensions = new string[] {
            ".crdownload",  // Chrome, Edge
            ".part",        // Firefox
            ".partial",     // IE/Edge legacy
            ".tmp",         // various
            ".download",    // Safari
            ".opdownload"   // Opera
        };

        /// <summary>Raised when the file list actually changes. Always on the UI thread.</summary>
        public event EventHandler Changed;

        /// <summary>
        /// Raised when a genuinely new file appears (not just a rename or change).
        /// The string argument is the file path.
        /// </summary>
        public event EventHandler<string> NewFileArrived;

        public Downloads(string folder, int maxItems, Control syncTarget)
        {
            _folder = folder;
            _maxItems = maxItems;
            _syncTarget = syncTarget;
            _items = new List<DownloadItem>();
            _knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _lastSeen = DateTime.Now;

            // Debounce timer: FileSystemWatcher fires in bursts, so we coalesce
            // multiple events into a single refresh after a short delay.
            _debounce = new Timer();
            _debounce.Interval = 300;
            _debounce.Tick += delegate(object s, EventArgs e)
            {
                _debounce.Stop();
                Refresh();
            };

            // A watcher can die (the folder was missing at startup, a network
            // share dropped, the event buffer overflowed). Rather than quietly
            // going stale until the next restart, check back now and then.
            _retry = new Timer();
            _retry.Interval = 5000;
            _retry.Tick += delegate(object s, EventArgs e)
            {
                if (_watcher == null && Directory.Exists(_folder))
                {
                    StartWatcher();
                    Refresh();
                }
            };
            _retry.Start();

            Refresh();
            StartWatcher();
        }

        /// <summary>Newest first.</summary>
        public List<DownloadItem> Items
        {
            get { return _items; }
        }

        /// <summary>The folder being watched.</summary>
        public string Folder
        {
            get { return _folder; }
        }

        /// <summary>
        /// When the panel was last closed. Anything added after it is new to
        /// the user, which is what the pill's dot and the tiles' markers mean.
        /// </summary>
        public DateTime LastSeen
        {
            get { return _lastSeen; }
        }

        public bool HasUnseen
        {
            get { return _items.Count > 0 && _items[0].Added > _lastSeen; }
        }

        public void MarkSeen()
        {
            _lastSeen = DateTime.Now;
        }

        /// <summary>
        /// Rescans the folder and updates the item list.
        /// </summary>
        public void Refresh()
        {
            List<DownloadItem> newItems = new List<DownloadItem>();
            HashSet<string> newKnown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string arrived = null;

            try
            {
                if (Directory.Exists(_folder))
                {
                    DirectoryInfo dir = new DirectoryInfo(_folder);

                    // Folders belong here as much as files do. A macOS stack lists
                    // whatever is in the folder, and a Downloads folder holding only
                    // extracted archives would otherwise show an empty grid and look
                    // broken. They drag out as CF_HDROP and recycle the same way, so
                    // nothing downstream cares.
                    FileSystemInfo[] entries = dir.GetFileSystemInfos();

                    // Firefox writes a download into "name.part" and leaves an empty
                    // "name" beside it until it is done. The placeholder is not a
                    // download yet.
                    HashSet<string> inProgress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (FileSystemInfo fi in entries)
                    {
                        if (fi.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                            inProgress.Add(fi.Name.Substring(0, fi.Name.Length - 5));
                    }

                    List<DownloadItem> all = new List<DownloadItem>(entries.Length);
                    foreach (FileSystemInfo fi in entries)
                    {
                        if ((fi.Attributes & FileAttributes.Hidden) != 0) continue;
                        if ((fi.Attributes & FileAttributes.System) != 0) continue;
                        if (IsPartialDownload(fi.Name)) continue;
                        if (inProgress.Contains(fi.Name)) continue;

                        DownloadItem item = new DownloadItem();
                        item.Path = fi.FullName;
                        item.Name = fi.Name;
                        item.Modified = fi.LastWriteTime;
                        item.Added = AddedTime(fi);
                        item.IsFolder = (fi.Attributes & FileAttributes.Directory) != 0;
                        FileInfo file = fi as FileInfo;
                        item.Size = file != null ? file.Length : 0;
                        all.Add(item);
                    }

                    all.Sort(delegate(DownloadItem a, DownloadItem b)
                    {
                        return b.Added.CompareTo(a.Added);
                    });

                    for (int i = 0; i < all.Count && i < _maxItems; i++)
                    {
                        DownloadItem item = all[i];
                        newItems.Add(item);
                        newKnown.Add(item.Path);

                        // Genuinely new: not in the previous scan. The first scan
                        // has nothing to compare with, so it reports nothing.
                        if (arrived == null && _knownPaths.Count > 0 && !_knownPaths.Contains(item.Path))
                            arrived = item.Path;
                    }
                }
            }
            catch
            {
                // If we cannot read the folder, just leave the list empty.
            }

            // A download in progress rewrites its temp file constantly, and every
            // write wakes the watcher. Only a list that actually differs is news;
            // anything else would have the open panel rebuilding itself several
            // times a second for nothing.
            string signature = Signature(newItems);
            _items = newItems;
            _knownPaths = newKnown;
            if (signature == _signature) return;
            _signature = signature;

            if (Changed != null) Changed(this, EventArgs.Empty);
            if (arrived != null && NewFileArrived != null)
            {
                NewFileArrived(this, arrived);
            }
        }

        /// <summary>
        /// The newer of creation and write time. Sorting on write time alone put
        /// a fresh download in among the old ones whenever the file arrived
        /// carrying an old timestamp - extracted archives, photos copied off a
        /// phone, anything a browser saved with the server's date - and the stack
        /// only shows so many, so those simply never appeared. Creation time is
        /// when the file was made in this folder, which for a download is the
        /// moment it arrived.
        /// </summary>
        static DateTime AddedTime(FileSystemInfo fi)
        {
            DateTime created = fi.CreationTime;
            DateTime written = fi.LastWriteTime;
            return created > written ? created : written;
        }

        static string Signature(List<DownloadItem> items)
        {
            StringBuilder sb = new StringBuilder(items.Count * 48);
            foreach (DownloadItem it in items)
            {
                sb.Append(it.Path).Append('|').Append(it.Modified.Ticks).Append('|').Append(it.Size).Append('\n');
            }
            return sb.ToString();
        }

        static bool IsPartialDownload(string name)
        {
            foreach (string ext in PartialExtensions)
            {
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        void StartWatcher()
        {
            StopWatcher();
            try
            {
                if (!Directory.Exists(_folder)) return;

                _watcher = new FileSystemWatcher(_folder);
                // DirectoryName as well: without it a folder appearing - an
                // archive extracted in place, a folder dragged in - raised
                // nothing at all and only showed up on some later, unrelated
                // change.
                _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                      | NotifyFilters.LastWrite | NotifyFilters.CreationTime;
                _watcher.InternalBufferSize = 64 * 1024;
                _watcher.Created += OnWatcherEvent;
                _watcher.Deleted += OnWatcherEvent;
                _watcher.Renamed += OnWatcherEvent;
                _watcher.Changed += OnWatcherEvent;
                _watcher.Error += OnWatcherError;
                _watcher.EnableRaisingEvents = true;
            }
            catch
            {
                // Watcher can fail on network paths or permission issues. The
                // retry timer will try again; the list still refreshes whenever
                // the panel acts on it.
                StopWatcher();
            }
        }

        void StopWatcher()
        {
            if (_watcher == null) return;
            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
            }
            catch { }
            _watcher = null;
        }

        void OnWatcherEvent(object sender, FileSystemEventArgs e)
        {
            // Watcher events arrive on a threadpool thread. Marshal to the UI
            // thread and restart the debounce timer - but only for so long. A
            // download in progress writes continuously, and a debounce that
            // restarts on every write never fires, so a second file finishing
            // meanwhile would not show until the first one was done.
            Post(delegate
            {
                DateTime now = DateTime.UtcNow;
                if (!_debounce.Enabled) _pendingSince = now;
                else if ((now - _pendingSince).TotalMilliseconds > MaxDebounceMs) return;
                _debounce.Stop();
                _debounce.Start();
            });
        }

        DateTime _pendingSince;
        const int MaxDebounceMs = 1500;

        /// <summary>
        /// A buffer overflow means events were lost, and other errors stop the
        /// watcher outright. Either way the list can no longer be trusted, so
        /// start over: a fresh watcher and a full rescan.
        /// </summary>
        void OnWatcherError(object sender, ErrorEventArgs e)
        {
            Post(delegate
            {
                StartWatcher();
                Refresh();
            });
        }

        void Post(MethodInvoker action)
        {
            if (_syncTarget == null || _syncTarget.IsDisposed) return;
            try { _syncTarget.BeginInvoke(action); }
            catch { }
        }

        public void Dispose()
        {
            StopWatcher();
            if (_debounce != null)
            {
                _debounce.Stop();
                _debounce.Dispose();
                _debounce = null;
            }
            if (_retry != null)
            {
                _retry.Stop();
                _retry.Dispose();
                _retry = null;
            }
        }
    }
}
