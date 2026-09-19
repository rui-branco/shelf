using System;
using System.Collections.Generic;
using System.IO;
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
        public DateTime Modified;
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
        Control _syncTarget;
        List<DownloadItem> _items;
        HashSet<string> _knownPaths;

        // Extensions that indicate an in-progress download. These are temporary
        // files that browsers create while downloading; showing them would be
        // confusing because they vanish or change name when the download finishes.
        static readonly string[] PartialExtensions = new string[] {
            ".crdownload",  // Chrome
            ".part",        // Firefox
            ".partial",     // IE/Edge legacy
            ".tmp",         // various
            ".download"     // Safari
        };

        /// <summary>Raised when the file list changes. Always on the UI thread.</summary>
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

            // Debounce timer: FileSystemWatcher fires in bursts, so we coalesce
            // multiple events into a single refresh after a short delay.
            _debounce = new Timer();
            _debounce.Interval = 400;
            _debounce.Tick += delegate(object s, EventArgs e)
            {
                _debounce.Stop();
                Refresh();
            };

            Refresh();
            StartWatcher();
        }

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
        /// Rescans the folder and updates the item list.
        /// </summary>
        public void Refresh()
        {
            List<DownloadItem> newItems = new List<DownloadItem>();
            HashSet<string> newKnown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string arrived = null;

            try
            {
                if (!Directory.Exists(_folder)) return;

                DirectoryInfo dir = new DirectoryInfo(_folder);

                // Folders belong here as much as files do. A macOS stack lists
                // whatever is in the folder, and a Downloads folder holding only
                // extracted archives - which is exactly what this one held - would
                // otherwise show an empty grid and look broken. They drag out as
                // CF_HDROP and recycle the same way, so nothing downstream cares.
                FileSystemInfo[] files = dir.GetFileSystemInfos();

                // Sort by LastWriteTime descending
                Array.Sort(files, delegate(FileSystemInfo a, FileSystemInfo b)
                {
                    return b.LastWriteTime.CompareTo(a.LastWriteTime);
                });

                int count = 0;
                foreach (FileSystemInfo fi in files)
                {
                    if (count >= _maxItems) break;

                    // Skip hidden and system entries
                    if ((fi.Attributes & FileAttributes.Hidden) != 0) continue;
                    if ((fi.Attributes & FileAttributes.System) != 0) continue;

                    // Skip in-progress downloads
                    if (IsPartialDownload(fi.Name)) continue;

                    DownloadItem item = new DownloadItem();
                    item.Path = fi.FullName;
                    item.Name = fi.Name;
                    item.Modified = fi.LastWriteTime;
                    newItems.Add(item);
                    newKnown.Add(fi.FullName);
                    count++;

                    // Detect genuinely new file: not in our previous known set
                    if (!_knownPaths.Contains(fi.FullName) && _knownPaths.Count > 0)
                    {
                        // Only report the most recent new file
                        if (arrived == null) arrived = fi.FullName;
                    }
                }
            }
            catch
            {
                // If we cannot read the folder, just leave the list empty.
            }

            _items = newItems;
            _knownPaths = newKnown;

            if (Changed != null) Changed(this, EventArgs.Empty);
            if (arrived != null && NewFileArrived != null)
            {
                NewFileArrived(this, arrived);
            }
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
            try
            {
                if (!Directory.Exists(_folder)) return;

                _watcher = new FileSystemWatcher(_folder);
                _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                                      | NotifyFilters.CreationTime;
                _watcher.Created += OnWatcherEvent;
                _watcher.Deleted += OnWatcherEvent;
                _watcher.Renamed += OnWatcherEvent;
                _watcher.Changed += OnWatcherEvent;
                _watcher.EnableRaisingEvents = true;
            }
            catch
            {
                // Watcher can fail on network paths or permission issues.
                // The app still runs, just without live updates.
            }
        }

        void OnWatcherEvent(object sender, FileSystemEventArgs e)
        {
            // Watcher events arrive on a threadpool thread. Marshal to the UI
            // thread and restart the debounce timer.
            if (_syncTarget == null || _syncTarget.IsDisposed) return;

            try
            {
                _syncTarget.BeginInvoke(new MethodInvoker(delegate
                {
                    _debounce.Stop();
                    _debounce.Start();
                }));
            }
            catch { }
        }

        public void Dispose()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
            if (_debounce != null)
            {
                _debounce.Stop();
                _debounce.Dispose();
                _debounce = null;
            }
        }
    }
}
