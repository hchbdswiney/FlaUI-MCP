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

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

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
}
