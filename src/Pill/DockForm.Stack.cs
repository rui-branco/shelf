using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Shelf
{
    /// <summary>The pill's side of the stack: opening it above the pill, and closing it.</summary>
    public partial class DockForm
    {
        /// <summary>
        /// Returns the pill's bounding rectangle in screen coordinates. Used by
        /// StackForm to position itself above the pill.
        /// </summary>
        public Rectangle GetScreenBounds()
        {
            return Bounds;
        }

        void ToggleStack()
        {
            if (_stack != null && !_stack.IsDisposed)
            {
                // Already on its way out: this is the click that is closing it.
                if (!_stack.IsClosing) _stack.Dismiss();
                return;
            }

            // A click that landed just after the stack closed itself is the
            // same gesture that closed it - the user reaching for the pill to
            // dismiss what was open - so swallow it rather than reopening.
            if ((DateTime.UtcNow - _stackClosedAt).TotalMilliseconds < 250) return;

            // One stack at a time, whichever screen it was opened on.
            Docks.CloseStacks(this);

            StackForm stack = new StackForm(_config, _downloads, this);
            stack.FormClosed += delegate
            {
                if (_stack == stack) _stack = null;
                _stackClosedAt = DateTime.UtcNow;
                _downloads.MarkSeen();
                Render();
            };
            _stack = stack;

            stack.Open(GetScreenBounds(), _bar.Rect);

            // The pill cannot be activated, so showing from it does not
            // hand the popup the foreground on its own. Ask explicitly -
            // Windows grants it because we are handling the click that got
            // us here. Without this the popup paints but never has focus,
            // which costs it both Esc and close-on-click-away.
            SetForegroundWindow(stack.Handle);
            Render();
        }

        /// <summary>
        /// Closes the stack popup at once if it is open: going fullscreen, or
        /// opening the pill's menu.
        /// </summary>
        public void CloseStack()
        {
            if (_stack != null && !_stack.IsDisposed)
            {
                _stack.Close();
                _stack = null;
            }
        }

        public bool IsStackOpen
        {
            get { return _stack != null && !_stack.IsDisposed && _stack.Visible && !_stack.IsClosing; }
        }

        /// <summary>
        /// Repaints the pill. The stack calls this the moment it starts closing,
        /// so the open highlight goes with it rather than after the fade.
        /// </summary>
        public void RefreshPill()
        {
            Render();
        }

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
