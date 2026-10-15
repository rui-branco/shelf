using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace Shelf
{
    static class Program
    {
        static Mutex _mutex;
        static Downloads _downloads;
        static AppConfig _config;
        static Form _owner;          // hidden window that owns cross-thread invokes

        [STAThread]
        static void Main()
        {
            try
            {
                Run();
            }
            catch (Exception ex)
            {
                LogError(ex);
                throw;
            }
        }

        /// <summary>
        /// The one error log, at %APPDATA%\Shelf\error.log. Reachable from the rest of the
        /// app so that a click which quietly did nothing leaves a trace somewhere.
        /// </summary>
        public static void LogError(Exception ex)
        {
            try
            {
                if (!System.IO.Directory.Exists(AppConfig.Dir))
                    System.IO.Directory.CreateDirectory(AppConfig.Dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppConfig.Dir, "error.log"),
                    DateTime.Now.ToString("s") + "  " + ex.ToString() + Environment.NewLine);
            }
            catch { }
        }

        static void Run()
        {
            // An update relaunches us while the build it replaced is still shutting
            // down, so that one start is allowed to wait for the mutex instead of
            // quitting on the spot. Every other second launch still exits silently.
            bool afterUpdate = HasArg("--after-update");
            if (!ClaimSingleInstance(afterUpdate ? 10000 : 0)) return;

            // Before the first window exists, or it does not take.
            Dpi.MakeProcessAware();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // A stray exception in a click handler should cost that click, not
            // put up .NET's crash dialog over the taskbar or end the app.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { LogError(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                if (ex != null) LogError(ex);
            };

            // An update leaves the previous build renamed beside this one.
            Updater.CleanupOldBuild();

            _config = AppConfig.Load();

            string folder = _config.FolderPath;
            if (string.IsNullOrEmpty(folder)) folder = Shell.GetDownloadsFolder();

            // A hidden window owning the UI thread, for the work that finishes
            // elsewhere: watcher events, thumbnails, update checks. It never
            // needs to be shown, only to have a handle.
            _owner = new Form();
            IntPtr ownerHandle = _owner.Handle;

            Thumbs.Start(_owner);
            _downloads = new Downloads(folder, _config.MaxItems, _owner);
            PillMenu.Init(_config, _owner, folder);

            // Wait briefly for the first taskbar measurement, so the pills
            // appear in their slots straight away.
            Taskbars.Start();
            Taskbars.WaitForFirstMeasurement(1500);

            // One pill per taskbar: a second monitor with its own taskbar gets
            // its own.
            Docks.Start(_config, _downloads);

            // Space for Quick Look in Explorer and on the desktop, not only in
            // the panel.
            ExplorerSpace.Start(_owner);

            // Fetch the newest thumbnails now and whenever something lands, so
            // the panel opens with its icons already there.
            Prefetch();
            _downloads.Changed += delegate { Prefetch(); };

            // Quietly look for a newer release. Nothing pops up: the answer only
            // shows up as the wording of the menu item the next time it is opened.
            PillMenu.CheckForUpdates(false);

            Application.Run();

            // Cleanup
            _downloads.Dispose();
            ReleaseSingleInstance();
        }

        static void Prefetch()
        {
            try
            {
                // At each pill's own scale: a stack opened on a 175% monitor
                // asks for bigger thumbnails than one on a 100% monitor.
                List<int> sizes = new List<int>();
                foreach (DockForm pill in Docks.Pills)
                {
                    int size = StackForm.ThumbSizeFor(pill.MonitorScale);
                    if (sizes.Contains(size)) continue;
                    sizes.Add(size);
                    Thumbs.Prefetch(_downloads.Items, StackForm.VisibleCapacity, size);
                }
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        }

        static bool HasArg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static bool ClaimSingleInstance(int waitMs)
        {
            _mutex = new Mutex(false, "Shelf_SingleInstance_Mutex");
            try
            {
                if (_mutex.WaitOne(waitMs, false)) return true;
            }
            catch (AbandonedMutexException)
            {
                // The previous build died without releasing it. It is ours now.
                return true;
            }

            _mutex.Close();
            _mutex = null;
            return false;
        }

        /// <summary>
        /// Lets go of the single-instance mutex. An update calls this before it
        /// launches the new build, so the two never fight over the name.
        /// </summary>
        public static void ReleaseSingleInstance()
        {
            if (_mutex == null) return;
            try { _mutex.ReleaseMutex(); }
            catch { }
            try { _mutex.Close(); }
            catch { }
            _mutex = null;
        }
    }
}
