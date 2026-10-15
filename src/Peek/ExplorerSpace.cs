using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Shelf
{
    /// <summary>
    /// Space for Quick Look in Explorer and on the desktop, the way Finder
    /// does it.
    ///
    /// A low-level keyboard hook sees every key, so it is kept to the bone and
    /// runs on a thread of its own: Windows waits for it before delivering each
    /// keystroke anywhere, and on the UI thread any busy moment in Shelf would
    /// have been a stutter in everyone's typing. All it decides is whether
    /// this Space belongs to us - Explorer or the desktop in front, with the
    /// file list itself focused, not a rename box, the address bar or a menu -
    /// and if so it swallows the key and hands the real work to the UI thread.
    ///
    /// While a preview opened from Explorer is up, a timer follows that
    /// window's selection, so arrow keys and clicks in Explorer flip the
    /// preview along, and closes it when Explorer loses the foreground.
    /// </summary>
    static class ExplorerSpace
    {
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100;
        const int WM_KEYUP = 0x0101;
        const int VK_SPACE = 0x20;
        const int VK_ESCAPE = 0x1B;
        const int FollowMs = 250;

        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        static Control _sync;
        static Thread _thread;
        static HookProc _proc;          // held so the delegate outlives the hook
        static IntPtr _hook;
        static volatile bool _spaceDown;
        static volatile bool _previewing;
        static System.Windows.Forms.Timer _follow;
        static IntPtr _window;
        static bool _windowIsDesktop;

        /// <summary>Installs the hook on its own thread. <paramref name="sync"/> owns the UI thread.</summary>
        public static void Start(Control sync)
        {
            if (_thread != null) return;
            _sync = sync;

            _follow = new System.Windows.Forms.Timer();
            _follow.Interval = FollowMs;
            _follow.Tick += delegate { Follow(); };

            _thread = new Thread(delegate()
            {
                _proc = OnKey;
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
                Application.Run();      // a message loop is all the hook needs
            });
            _thread.IsBackground = true;
            _thread.Name = "Shelf keyboard";
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        static IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int msg = wParam.ToInt32();
                int vk = Marshal.ReadInt32(lParam);

                if (vk == VK_SPACE)
                {
                    if (msg == WM_KEYDOWN)
                    {
                        // Held down: the repeats are ours too, but toggle nothing.
                        if (_spaceDown) return new IntPtr(1);

                        IntPtr fg = GetForegroundWindow();
                        bool desktop;
                        if (!ModifierDown() && IsFileList(fg, out desktop))
                        {
                            _spaceDown = true;
                            Post(delegate { Toggle(fg, desktop); });
                            return new IntPtr(1);
                        }
                    }
                    else if (msg == WM_KEYUP && _spaceDown)
                    {
                        _spaceDown = false;
                        return new IntPtr(1);
                    }
                }
                else if (vk == VK_ESCAPE && msg == WM_KEYDOWN && _previewing && GetForegroundWindow() == _window)
                {
                    Post(delegate { Close(); });
                    return new IntPtr(1);
                }
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        static void Post(MethodInvoker action)
        {
            try
            {
                if (_sync != null && _sync.IsHandleCreated) _sync.BeginInvoke(action);
            }
            catch { }
        }

        /// <summary>
        /// Explorer or the desktop in front, and its file list focused - not
        /// the address bar, the search box, the navigation tree, a rename in
        /// progress (it has a caret) or an open menu.
        /// </summary>
        static bool IsFileList(IntPtr fg, out bool desktop)
        {
            desktop = false;
            if (fg == IntPtr.Zero) return false;

            string frame = ClassOf(fg);
            bool explorer = frame == "CabinetWClass" || frame == "ExploreWClass";
            desktop = frame == "Progman" || frame == "WorkerW";
            if (!explorer && !desktop) return false;

            uint pid;
            GUITHREADINFO gti = new GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
            if (!GetGUIThreadInfo(GetWindowThreadProcessId(fg, out pid), ref gti)) return false;
            if (gti.hwndCaret != IntPtr.Zero || (gti.flags & GUI_INMENUMODE) != 0) return false;

            string focus = ClassOf(gti.hwndFocus);
            if (focus != "DirectUIHWND" && focus != "SysListView32") return false;
            return ClassOf(GetParent(gti.hwndFocus)) == "SHELLDLL_DefView";
        }

        static bool ModifierDown()
        {
            foreach (int vk in new int[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })    // Shift, Ctrl, Alt, Win
            {
                if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
            }
            return false;
        }

        // ----------------------------------------------------------
        // UI thread
        // ----------------------------------------------------------

        static void Toggle(IntPtr window, bool desktop)
        {
            if (Peek.IsOpen && Peek.OpenedFrom == Peek.From.Explorer)
            {
                Close();
                return;
            }

            List<string> selected = ExplorerSelection.Get(window, desktop);
            if (selected.Count == 0) return;

            _window = window;
            _windowIsDesktop = desktop;
            _previewing = true;
            Peek.Show(selected[0], Peek.From.Explorer, Screen.FromHandle(window).WorkingArea, FocusedIcon);
            _follow.Start();
        }

        static void Follow()
        {
            if (!Peek.IsOpen || Peek.OpenedFrom != Peek.From.Explorer)
            {
                _previewing = false;
                _follow.Stop();
                return;
            }

            // Explorer stepping back (another app, a click elsewhere) ends it.
            if (GetForegroundWindow() != _window)
            {
                Close();
                return;
            }

            List<string> selected = ExplorerSelection.Get(_window, _windowIsDesktop);
            if (selected.Count > 0 && !string.Equals(selected[0], Peek.Path, StringComparison.OrdinalIgnoreCase))
                Peek.Show(selected[0], Peek.From.Explorer, Screen.FromHandle(_window).WorkingArea, FocusedIcon);
        }

        /// <summary>
        /// The icon of the file Explorer has the keyboard on, for Quick Look to
        /// zoom out of and back into. Asked of UI Automation once per open or
        /// close, never polled. Empty when Explorer is no longer in front.
        /// </summary>
        static Rectangle FocusedIcon(string path)
        {
            if (GetForegroundWindow() != _window) return Rectangle.Empty;
            return ExplorerSelection.FocusedItemIcon();
        }

        static void Close()
        {
            _previewing = false;
            _follow.Stop();
            Peek.Close(Peek.From.Explorer);
        }

        // ----------------------------------------------------------
        // Interop
        // ----------------------------------------------------------

        const int GUI_INMENUMODE = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct GUITHREADINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        static string ClassOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            StringBuilder sb = new StringBuilder(64);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);

        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern IntPtr GetParent(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vk);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    }
}
