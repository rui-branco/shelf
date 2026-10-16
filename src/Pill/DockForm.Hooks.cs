using System;
using System.Runtime.InteropServices;

namespace Shelf
{
    public partial class DockForm
    {
        void AssertTopmost()
        {
            if (!IsHandleCreated) return;
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        void StartTopmostBurst()
        {
            if (!Visible) return;
            _burstEnd = DateTime.UtcNow.AddMilliseconds(BurstDurationMs);
            if (!_topmostBurst.Enabled) _topmostBurst.Start();
            AssertTopmost();
        }

        void OnTopmostBurstTick()
        {
            if (!Visible || DateTime.UtcNow >= _burstEnd)
            {
                _topmostBurst.Stop();
                return;
            }
            AssertTopmost();
        }

        // ----------------------------------------------------------
        // WinEvent hooks
        // ----------------------------------------------------------

        void InstallHooks()
        {
            RemoveHooks();

            _foregroundProc = new WinEventDelegate(OnForegroundEvent);
            _foregroundHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);

            // Location changes from the shell's process, for this pill's
            // taskbar relaying itself out. The process covers far more than the
            // taskbar - every Explorer window, the desktop, and the mouse
            // cursor itself whenever it is over any of them - so the callback
            // throws away everything that is not the taskbar.
            uint shellPid = Taskbars.ShellProcessId();
            if (shellPid != 0)
            {
                _locationChangeProc = new WinEventDelegate(OnLocationChangeEvent);
                _locationChangeHook = SetWinEventHook(
                    EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
                    IntPtr.Zero, _locationChangeProc, shellPid, 0, WINEVENT_OUTOFCONTEXT);
            }
        }

        void RemoveHooks()
        {
            if (_foregroundHook != IntPtr.Zero)
            {
                UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
            }
            if (_locationChangeHook != IntPtr.Zero)
            {
                UnhookWinEvent(_locationChangeHook);
                _locationChangeHook = IntPtr.Zero;
            }
        }

        void OnForegroundEvent(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            // Runs on our own message loop, so no marshalling - but it must
            // never throw back into the system.
            try
            {
                if (!IsHandleCreated) return;

                // Straight away rather than on the next heartbeat: a game going
                // fullscreen should never have the pill drawn over it, even
                // for a quarter of a second.
                UpdateVisibility();
                if (!Visible) return;

                // No measurement here. A window coming to the front does not
                // move the taskbar's buttons; windows appearing and going do,
                // and the shell hook reports exactly those. Measuring on every
                // switch cost both taskbars a walk each time you clicked
                // between windows.
                StartTopmostBurst();
            }
            catch { }
        }

        void OnLocationChangeEvent(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            try
            {
                if (!IsHandleCreated || !Visible) return;
                if (idObject == OBJID_CURSOR || idObject == OBJID_CARET) return;

                IntPtr bar = _bar.Handle;
                if (bar == IntPtr.Zero || (hwnd != bar && !IsChild(bar, hwnd))) return;

                Taskbars.Request();
                StartTopmostBurst();
            }
            catch { }
        }

        // ----------------------------------------------------------
        // Interop
        // ----------------------------------------------------------

        [DllImport("user32.dll")]
        static extern bool IsChild(IntPtr parent, IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                        int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
            IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
            uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    }
}
