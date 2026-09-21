using System;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Shelf
{
    static class Program
    {
        static Mutex _mutex;
        static DockForm _dock;
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

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // An update leaves the previous build renamed beside this one.
            Updater.CleanupOldBuild();

            _config = AppConfig.Load();

            // Determine the folder to watch
            string folder = _config.FolderPath;
            if (string.IsNullOrEmpty(folder))
            {
                folder = Shell.GetDownloadsFolder();
            }

            // Create a hidden form to own the downloads watcher (for BeginInvoke)
            Form owner = new Form();
            owner.ShowInTaskbar = false;
            owner.FormBorderStyle = FormBorderStyle.None;
            owner.Size = new System.Drawing.Size(0, 0);
            owner.StartPosition = FormStartPosition.Manual;
            owner.Location = new System.Drawing.Point(-32000, -32000);
            owner.Load += delegate { owner.Visible = false; };
            _owner = owner;

            _downloads = new Downloads(folder, _config.MaxItems, owner);

            _dock = new DockForm(_config, _downloads);

            owner.Show();
            _dock.Show();

            // Quietly look for a newer release. Nothing pops up: the answer only
            // shows up as the wording of the menu item the next time it is opened.
            CheckForUpdates(false);

            Application.Run();

            // Cleanup
            _downloads.Dispose();
            ReleaseSingleInstance();
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

        static ContextMenuStrip _trayMenu;   // the pill's right-click menu
        static ToolStripMenuItem _updateItem;
        static ReleaseInfo _update;          // the newer release, once one is known
        static bool _checking;
        static bool _updating;

        /// <summary>
        /// Builds the pill's right-click menu. There is no tray icon: the pill
        /// is already a permanent, visible presence on the taskbar, and a
        /// second icon in the notification area to quit the first one was one
        /// icon too many. Everything the tray offered lives here instead.
        /// </summary>
        public static ContextMenuStrip BuildPillMenu()
        {
            if (_trayMenu != null) return _trayMenu;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new DarkMenuRenderer();
            menu.BackColor = Theme.Card;
            menu.ForeColor = Theme.Text;

            ToolStripMenuItem openFolder = new ToolStripMenuItem("Open Downloads folder");
            openFolder.Click += delegate
            {
                string folder = _config.FolderPath;
                if (string.IsNullOrEmpty(folder)) folder = Shell.GetDownloadsFolder();
                try { System.Diagnostics.Process.Start("explorer.exe", folder); }
                catch { }
            };

            ToolStripMenuItem startup = new ToolStripMenuItem("Start with Windows");
            startup.Checked = _config.StartWithWindows;
            startup.Click += delegate
            {
                _config.StartWithWindows = !_config.StartWithWindows;
                startup.Checked = _config.StartWithWindows;
                SetStartup(_config.StartWithWindows);
                _config.Save();
            };

            _updateItem = new ToolStripMenuItem("Check for updates");
            _updateItem.Click += delegate
            {
                if (_update != null) StartUpdate();
                else CheckForUpdates(true);
            };

            ToolStripMenuItem version = new ToolStripMenuItem("Shelf " + Updater.CurrentText);
            version.Enabled = false;

            ToolStripMenuItem quit = new ToolStripMenuItem("Quit");
            quit.Click += delegate
            {
                Application.Exit();
            };

            menu.Items.Add(openFolder);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(startup);
            menu.Items.Add(_updateItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(version);
            menu.Items.Add(quit);

            _trayMenu = menu;
            RefreshUpdateItem();
            return menu;
        }

        // ---------- updates ----------

        /// <summary>
        /// Looks for a newer release. Silent at startup; from the menu it reports
        /// what it found either way, because a check that answers nothing reads
        /// as a broken one.
        /// </summary>
        static void CheckForUpdates(bool announce)
        {
            if (_checking || _updating || _update != null)
            {
                if (announce && _update != null) StartUpdate();
                return;
            }

            _checking = true;
            RefreshUpdateItem();

            Updater.CheckAsync(delegate(ReleaseInfo r, string err)
            {
                OnUi(delegate
                {
                    _checking = false;
                    _update = r;
                    RefreshUpdateItem();

                    if (!announce) return;

                    if (r != null)
                    {
                        if (Ask("Shelf " + Trim(r.Tag) + " is available.\n\nDownload and install it now?"))
                            StartUpdate();
                    }
                    else if (err != null)
                    {
                        Say("Could not check for updates:\n\n" + err, MessageBoxIcon.Warning);
                    }
                    else
                    {
                        Say("Shelf " + Updater.CurrentText + " is the latest version.",
                            MessageBoxIcon.Information);
                    }
                });
            });
        }

        static void StartUpdate()
        {
            if (_update == null || _updating) return;

            _updating = true;
            RefreshUpdateItem();

            ReleaseInfo r = _update;
            Updater.DownloadAsync(r, delegate(string path, string err)
            {
                OnUi(delegate { FinishUpdate(path, err); });
            });
        }

        static void FinishUpdate(string path, string err)
        {
            if (err == null && path != null)
            {
                // Apply restarts us, so nothing after this runs on success.
                try { Updater.Apply(path); return; }
                catch (Exception ex) { err = ex.Message; }
            }

            _updating = false;
            RefreshUpdateItem();
            Say("The update could not be installed:\n\n" + (err == null ? "no download" : err),
                MessageBoxIcon.Warning);
        }

        static void RefreshUpdateItem()
        {
            if (_updateItem == null) return;

            if (_updating) _updateItem.Text = "Updating...";
            else if (_checking) _updateItem.Text = "Checking for updates...";
            else if (_update != null) _updateItem.Text = "Update to Shelf " + Trim(_update.Tag);
            else _updateItem.Text = "Check for updates";

            _updateItem.Enabled = !_checking && !_updating;
        }

        static string Trim(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "";
            return tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag.Substring(1) : tag;
        }

        /// <summary>Runs an action on the UI thread, wherever the caller came from.</summary>
        static void OnUi(MethodInvoker action)
        {
            try
            {
                if (_owner != null && _owner.IsHandleCreated && _owner.InvokeRequired)
                    _owner.BeginInvoke(action);
                else
                    action();
            }
            catch { }
        }

        /// <summary>
        /// The pill is WS_EX_NOACTIVATE and owns no real window, so a dialog it
        /// puts up can open behind everything. A throwaway topmost owner keeps
        /// it in front and off the taskbar.
        /// </summary>
        static DialogResult Dialog(string text, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            using (Form owner = new Form())
            {
                owner.ShowInTaskbar = false;
                owner.FormBorderStyle = FormBorderStyle.None;
                owner.StartPosition = FormStartPosition.Manual;
                owner.Size = new System.Drawing.Size(1, 1);
                owner.Location = new System.Drawing.Point(-32000, -32000);
                owner.TopMost = true;
                owner.Show();
                try
                {
                    return MessageBox.Show(owner, text, "Shelf", buttons, icon);
                }
                finally
                {
                    owner.Hide();
                }
            }
        }

        static bool Ask(string text)
        {
            return Dialog(text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        static void Say(string text, MessageBoxIcon icon)
        {
            Dialog(text, MessageBoxButtons.OK, icon);
        }

        static void SetStartup(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;

                    if (enable)
                    {
                        key.SetValue("Shelf", "\"" + Application.ExecutablePath + "\"");
                    }
                    else
                    {
                        key.DeleteValue("Shelf", false);
                    }
                }
            }
            catch { }
        }
    }
}
