using System.Text;

namespace FlaUI.Mcp.Core;

/// <summary>
/// Higher-level, UI-Automation-independent interaction primitives built purely on
/// the Win32 P/Invokes in <see cref="Native"/>. These power the deterministic
/// fallbacks (foreground native click and screenshot-coordinate click) used when
/// UIA is stalled or a control has no UIA peer.
/// </summary>
internal static class Win32Interaction
{
    public enum MouseButtonKind
    {
        Left,
        Right,
        Middle
    }

    public static MouseButtonKind ParseButton(string? button) => button?.ToLowerInvariant() switch
    {
        "right" => MouseButtonKind.Right,
        "middle" => MouseButtonKind.Middle,
        _ => MouseButtonKind.Left
    };

    /// <summary>
    /// Strip a single mnemonic ampersand from a Win32 caption so "&amp;Yes" matches
    /// "Yes". A literal "&amp;&amp;" collapses to a single "&amp;".
    /// </summary>
    public static string StripMnemonic(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder(text!.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '&')
            {
                if (i + 1 < text.Length && text[i + 1] == '&')
                {
                    sb.Append('&');
                    i++;
                }
                // else: drop the single mnemonic ampersand
            }
            else
            {
                sb.Append(text[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Bring <paramref name="hwnd"/> to the OS foreground using the reliable Win32
    /// sequence (AllowSetForegroundWindow + AttachThreadInput + BringWindowToTop +
    /// SetForegroundWindow). Restores the window first if minimized. Returns true
    /// only when <see cref="Native.GetForegroundWindow"/> confirms success.
    /// </summary>
    public static bool BringToForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;

        if (Native.IsIconic(hwnd))
        {
            Native.ShowWindow(hwnd, Native.SW_RESTORE);
        }

        if (Native.GetForegroundWindow() == hwnd) return true;

        Native.AllowSetForegroundWindow(Native.ASFW_ANY);

        var targetThread = Native.GetWindowThreadProcessId(hwnd, out _);
        var currentThread = Native.GetCurrentThreadId();
        var attached = false;

        if (targetThread != 0 && targetThread != currentThread)
        {
            attached = Native.AttachThreadInput(currentThread, targetThread, true);
        }

        try
        {
            Native.BringWindowToTop(hwnd);
            Native.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                Native.AttachThreadInput(currentThread, targetThread, false);
            }
        }

        return Native.GetForegroundWindow() == hwnd;
    }

    /// <summary>
    /// Synthesize a real mouse click at an absolute virtual-desktop pixel using
    /// SendInput (absolute + virtual-desktop normalized). Optionally double-clicks
    /// and restores the prior cursor position afterward.
    /// </summary>
    public static void ClickAtScreenPoint(int screenX, int screenY, MouseButtonKind button, bool doubleClick, bool restoreCursor)
    {
        Native.POINT prev = default;
        var haveCursor = restoreCursor && Native.GetCursorPos(out prev);

        var vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        var vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        var vcx = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        var vcy = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

        // Normalize to the 0..65535 absolute space over the whole virtual desktop.
        var nx = vcx > 1 ? (int)Math.Round((screenX - vx) * 65535.0 / (vcx - 1)) : 0;
        var ny = vcy > 1 ? (int)Math.Round((screenY - vy) * 65535.0 / (vcy - 1)) : 0;

        var (down, up) = button switch
        {
            MouseButtonKind.Right => (Native.MOUSEEVENTF_RIGHTDOWN, Native.MOUSEEVENTF_RIGHTUP),
            MouseButtonKind.Middle => (Native.MOUSEEVENTF_MIDDLEDOWN, Native.MOUSEEVENTF_MIDDLEUP),
            _ => (Native.MOUSEEVENTF_LEFTDOWN, Native.MOUSEEVENTF_LEFTUP)
        };

        const uint absDesk = Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK;

        var inputs = new List<Native.INPUT>
        {
            MouseInput(nx, ny, Native.MOUSEEVENTF_MOVE | absDesk),
            MouseInput(nx, ny, down | absDesk),
            MouseInput(nx, ny, up | absDesk)
        };

        if (doubleClick)
        {
            inputs.Add(MouseInput(nx, ny, down | absDesk));
            inputs.Add(MouseInput(nx, ny, up | absDesk));
        }

        var array = inputs.ToArray();
        Native.SendInput((uint)array.Length, array, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.INPUT)));

        if (haveCursor)
        {
            Native.SetCursorPos(prev.X, prev.Y);
        }
    }

    private static Native.INPUT MouseInput(int nx, int ny, uint flags) => new()
    {
        type = Native.INPUT_MOUSE,
        mi = new Native.MOUSEINPUT
        {
            dx = nx,
            dy = ny,
            mouseData = 0,
            dwFlags = flags,
            time = 0,
            dwExtraInfo = IntPtr.Zero
        }
    };

    /// <summary>
    /// Secondary actuation attempt: post WM_*BUTTONDOWN/UP to a child HWND using
    /// client-relative coordinates. Works for standard windowed controls; does not
    /// reliably actuate custom-drawn (ownerless) controls.
    /// </summary>
    public static void PostClickToChild(IntPtr child, MouseButtonKind button)
    {
        if (child == IntPtr.Zero || !Native.GetWindowRect(child, out var rect)) return;

        // Client-relative center: the window is (Width x Height); its client origin
        // maps to the window's top-left for these controls, so center works well.
        var cx = rect.Width / 2;
        var cy = rect.Height / 2;
        var lParam = (IntPtr)((cy << 16) | (cx & 0xFFFF));

        var (down, up, mk) = button switch
        {
            MouseButtonKind.Right => (Native.WM_RBUTTONDOWN, Native.WM_RBUTTONUP, Native.MK_RBUTTON),
            MouseButtonKind.Middle => (Native.WM_MBUTTONDOWN, Native.WM_MBUTTONUP, Native.MK_MBUTTON),
            _ => (Native.WM_LBUTTONDOWN, Native.WM_LBUTTONUP, Native.MK_LBUTTON)
        };

        Native.PostMessage(child, down, (IntPtr)mk, lParam);
        Native.PostMessage(child, up, IntPtr.Zero, lParam);
    }
}
