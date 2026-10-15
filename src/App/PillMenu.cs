using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Shelf
{
    /// <summary>
    /// The pill's right-click menu, and the update flow that lives in it.
    ///
    /// There is no tray icon: the pill is already a permanent, visible presence
    /// on the taskbar, and a second icon in the notification area to quit the
    /// first one was one icon too many. Everything the tray offered lives here.
    /// </summary>
    public static class PillMenu
    {
        static AppConfig _config;
        static Control _owner;          // owns the UI thread for cross-thread callbacks
        static string _folder;

        static ContextMenuStrip _menu;
        static ToolStripMenuItem _updateItem;
        static ReleaseInfo _update;     // the newer release, once one is known
        static bool _checking;
        static bool _updating;

        public static void Init(AppConfig config, Control owner, string folder)
        {
            _config = config;
            _owner = owner;
            _folder = folder;
        }

        /// <summary>Builds the menu on first use; the same instance after that.</summary>
        public static ContextMenuStrip Get()
        {
            if (_menu != null) return _menu;

            ContextMenuStrip menu = new ContextMenuStrip();
            Theme.StyleMenu(menu);

            ToolStripMenuItem openFolder = Theme.MenuItem("Open Downloads folder", 0xE838);
            openFolder.Click += delegate
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "\"" + _folder + "\""); }
                catch (Exception ex) { Program.LogError(ex); }
            };

            ToolStripMenuItem startup = Theme.MenuItem("Start with Windows", 0);
            startup.Checked = _config.StartWithWindows;
            startup.Click += delegate
            {
                _config.StartWithWindows = !_config.StartWithWindows;
                startup.Checked = _config.StartWithWindows;
                SetStartup(_config.StartWithWindows);
                _config.Save();
            };

            _updateItem = Theme.MenuItem("Check for updates", 0xE895);
            _updateItem.Click += delegate
            {
                if (_update != null) StartUpdate();
                else CheckForUpdates(true);
            };

            ToolStripMenuItem version = Theme.MenuItem("Shelf " + Updater.CurrentText, 0xE946);
            version.Enabled = false;

            ToolStripMenuItem quit = Theme.MenuItem("Quit", 0xE7E8);
            quit.Click += delegate { Application.Exit(); };

            menu.Items.Add(openFolder);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(startup);
            menu.Items.Add(_updateItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(version);
            menu.Items.Add(quit);

            _menu = menu;
            RefreshUpdateItem();
            return menu;
        }

        // ---------- updates ----------

        /// <summary>
        /// Looks for a newer release. Silent at startup, where the answer only
        /// shows up as the wording of the menu item the next time it is opened;
        /// from the menu it reports what it found either way, because a check
        /// that answers nothing reads as a broken one.
        /// </summary>
        public static void CheckForUpdates(bool announce)
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

                    if (enable) key.SetValue("Shelf", "\"" + Application.ExecutablePath + "\"");
                    else key.DeleteValue("Shelf", false);
                }
            }
            catch { }
        }
    }
}
