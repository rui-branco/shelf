using System;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Shelf
{
    static class Program
    {
        static Mutex _mutex;
        static NotifyIcon _tray;
        static DockForm _dock;
        static Downloads _downloads;
        static AppConfig _config;

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

        static void LogError(Exception ex)
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
            // Single instance check. A second launch just exits silently.
            bool created;
            _mutex = new Mutex(true, "Shelf_SingleInstance_Mutex", out created);
            if (!created)
            {
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

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
            owner.Load += delegate { owner.Visible = false; };

            _downloads = new Downloads(folder, _config.MaxItems, owner);

            _dock = new DockForm(_config, _downloads);

            SetupTray();

            owner.Show();
            _dock.Show();

            Application.Run();

            // Cleanup
            _tray.Visible = false;
            _tray.Dispose();
            _downloads.Dispose();
            _mutex.ReleaseMutex();
        }

        static void SetupTray()
        {
            _tray = new NotifyIcon();
            _tray.Text = "Shelf";

            // Use the app icon if available, otherwise a generic one
            try
            {
                _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                _tray.Icon = System.Drawing.SystemIcons.Application;
            }

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

            ToolStripMenuItem quit = new ToolStripMenuItem("Quit");
            quit.Click += delegate
            {
                Application.Exit();
            };

            menu.Items.Add(openFolder);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(startup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(quit);

            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;

            _tray.DoubleClick += delegate
            {
                // Show/focus the dock
                if (_dock != null && !_dock.IsDisposed)
                {
                    _dock.Show();
                    _dock.BringToFront();
                }
            };
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
