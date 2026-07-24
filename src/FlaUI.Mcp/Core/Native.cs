using System.Runtime.InteropServices;
using System.Text;

namespace FlaUI.Mcp.Core;

/// <summary>
/// Thin Win32 interop layer used to resolve and enumerate top-level windows and
/// modal dialogs WITHOUT going through UI Automation. UIA enumeration/property
/// reads stall (HRESULT 0x80131505 / 30s timeouts) while a modal dialog or a
/// busy provider (e.g. a lazily-loading grid) is present. These calls are pure
/// Win32 and return immediately, so they stay responsive even when the target
/// app's UIA provider is blocked.
/// </summary>
internal static class Native
{
    /// <summary>GetWindow: retrieve the owner window.</summary>
    public const uint GW_OWNER = 4;

    /// <summary>
    /// GetWindow: retrieve the enabled popup owned by the specified window.
    /// When a window owns a modal dialog, this deterministically returns that
    /// modal's HWND - "the thing blocking this form".
    /// </summary>
    public const uint GW_ENABLEDPOPUP = 6;

    /// <summary>Well-known Win32 dialog window class (MessageBox, common dialogs).</summary>
    public const string DialogClassName = "#32770";

    // ShowWindow commands.
    public const int SW_RESTORE = 9;

    // AllowSetForegroundWindow: allow any process to set foreground.
    public const uint ASFW_ANY = 0xFFFFFFFF;

    // SendInput event types / mouse flags.
    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    // GetSystemMetrics indices for the virtual desktop (spans all monitors).
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    // Windows message ids for posting synthetic clicks to a child HWND.
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_MBUTTONUP = 0x0208;
    public const uint WM_COMMAND = 0x0111;
    public const int MK_LBUTTON = 0x0001;
    public const int MK_RBUTTON = 0x0002;
    public const int MK_MBUTTON = 0x0010;

    // Standard #32770 dialog command ids (used only on genuine #32770 dialogs).
    public const int IDOK = 1;
    public const int IDCANCEL = 2;
    public const int IDYES = 6;
    public const int IDNO = 7;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetDlgCtrlID(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>Get the window title text via Win32 (never blocks on UIA).</summary>
    public static string GetWindowTitle(IntPtr hWnd)
    {
        var length = GetWindowTextLength(hWnd);
        if (length <= 0) return string.Empty;

        var sb = new StringBuilder(length + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Get the window class name via Win32.</summary>
    public static string GetWindowClass(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Get the owning process id for a window via Win32.</summary>
    public static int GetProcessId(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out var pid);
        return (int)pid;
    }

    /// <summary>Lightweight snapshot of a top-level window's Win32 state.</summary>
    public sealed record NativeWindowInfo(
        IntPtr Handle,
        string Title,
        string ClassName,
        int ProcessId,
        bool IsEnabled,
        bool IsVisible,
        IntPtr Owner);

    /// <summary>
    /// Enumerate top-level windows using Win32 EnumWindows. This never touches
    /// UI Automation, so it succeeds even while a modal dialog is blocking the
    /// target app's provider.
    /// </summary>
    public static List<NativeWindowInfo> EnumerateTopLevelWindows(bool visibleOnly = true, int? processId = null)
    {
        var result = new List<NativeWindowInfo>();

        EnumWindows((hWnd, _) =>
        {
            if (visibleOnly && !IsWindowVisible(hWnd)) return true;

            var pid = GetProcessId(hWnd);
            if (processId.HasValue && pid != processId.Value) return true;

            var owner = GetWindow(hWnd, GW_OWNER);
            result.Add(new NativeWindowInfo(
                hWnd,
                GetWindowTitle(hWnd),
                GetWindowClass(hWnd),
                pid,
                IsWindowEnabled(hWnd),
                IsWindowVisible(hWnd),
                owner));

            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// Resolve the modal dialog that is blocking <paramref name="ownerHwnd"/>,
    /// if any. Uses GW_ENABLEDPOPUP which returns the enabled popup owned by the
    /// (typically disabled) owner window - deterministically "the modal on top".
    /// Returns <see cref="IntPtr.Zero"/> when there is no active modal.
    /// </summary>
    public static IntPtr GetActiveModal(IntPtr ownerHwnd)
    {
        if (ownerHwnd == IntPtr.Zero) return IntPtr.Zero;

        var popup = GetWindow(ownerHwnd, GW_ENABLEDPOPUP);
        if (popup != IntPtr.Zero && popup != ownerHwnd && IsWindow(popup) && IsWindowVisible(popup))
        {
            return popup;
        }

        return IntPtr.Zero;
    }

    /// <summary>Lightweight Win32 info for a child control (no UI Automation).</summary>
    public sealed record NativeChildInfo(
        IntPtr Handle,
        string Text,
        string ClassName,
        RECT Rect);

    /// <summary>
    /// Enumerate all descendant child windows of <paramref name="parent"/> using
    /// Win32 EnumChildWindows. Returns each child's caption, class, and screen rect
    /// without touching UI Automation, so it stays responsive on UIA-dead dialogs.
    /// Note: custom-drawn controls (e.g. some Infragistics buttons) may have no
    /// dedicated HWND and therefore will not appear here - those require a
    /// coordinate click via windows_click_point.
    /// </summary>
    public static List<NativeChildInfo> EnumerateChildWindows(IntPtr parent)
    {
        var result = new List<NativeChildInfo>();
        if (parent == IntPtr.Zero) return result;

        EnumChildWindows(parent, (hWnd, _) =>
        {
            GetWindowRect(hWnd, out var rect);
            result.Add(new NativeChildInfo(
                hWnd,
                GetWindowTitle(hWnd),
                GetWindowClass(hWnd),
                rect));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// Effective DPI of the monitor hosting <paramref name="hWnd"/>. Falls back to
    /// 96 (100%) when the API is unavailable (pre-Win10) or the call fails.
    /// </summary>
    public static int GetWindowDpi(IntPtr hWnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hWnd);
            return dpi > 0 ? (int)dpi : 96;
        }
        catch
        {
            return 96;
        }
    }
}
