using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using FlaUIApplication = FlaUI.Core.Application;

namespace FlaUI.Mcp.Core;

/// <summary>
/// Manages UI Automation sessions and launched applications
/// </summary>
public class SessionManager : IDisposable
{
    private readonly UIA3Automation _automation;
    private readonly Dictionary<string, FlaUIApplication> _applications = new();
    private readonly Dictionary<string, Window> _windows = new();
    private readonly Dictionary<string, IntPtr> _windowHandles = new();
    private int _windowCounter = 0;

    public SessionManager()
    {
        _automation = new UIA3Automation();
    }

    public UIA3Automation Automation => _automation;

    public (string handle, Window window) LaunchApp(string appPath, string[]? args = null)
    {
        // Use Process.Start for more reliable launching
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = appPath,
            Arguments = args != null ? string.Join(" ", args) : "",
            UseShellExecute = true
        };
        
        var process = System.Diagnostics.Process.Start(psi);
        if (process == null)
        {
            throw new Exception($"Failed to start process: {appPath}");
        }
        
        // Wait for the process to be ready
        try
        {
            process.WaitForInputIdle(5000);
        }
        catch { /* Some processes don't support this */ }
        
        Thread.Sleep(1000); // Extra wait for window to appear
        
        // Find window by process ID from desktop
        var desktop = _automation.GetDesktop();
        Window? window = null;
        
        // Try to find by process ID first
        var element = desktop.FindFirstDescendant(cf => cf.ByProcessId(process.Id));
        if (element != null)
        {
            window = element.AsWindow();
        }
        
        // If not found, the app might have spawned a different process (common for UWP)
        // Search by waiting for a new window
        if (window == null)
        {
            // Get window count before
            var existingTitles = new HashSet<string>(
                _windows.Values.Select(w => w.Title).Where(t => !string.IsNullOrEmpty(t))
            );
            
            // Wait and look for new windows
            for (int i = 0; i < 10 && window == null; i++)
            {
                Thread.Sleep(500);
                var windows = desktop.FindAllChildren(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window));
                foreach (var w in windows)
                {
                    var win = w.AsWindow();
                    if (win != null && !string.IsNullOrEmpty(win.Title))
                    {
                        // Check if this looks like our app
                        var title = win.Title.ToLowerInvariant();
                        var appName = Path.GetFileNameWithoutExtension(appPath).ToLowerInvariant();
                        if (title.Contains(appName) || !existingTitles.Contains(win.Title))
                        {
                            window = win;
                            break;
                        }
                    }
                }
            }
        }
        
        if (window == null)
        {
            throw new Exception($"Could not find window for {appPath}. Try using windows_list_windows and windows_focus instead.");
        }

        var windowHandle = RegisterWindow(window);
        return (windowHandle, window);
    }

    public (string handle, Window window) AttachToWindow(string title)
    {
        var desktop = _automation.GetDesktop();
        var window = desktop.FindFirstDescendant(cf => cf.ByName(title))?.AsWindow();
        
        if (window == null)
        {
            throw new Exception($"Window not found: {title}");
        }

        var handle = RegisterWindow(window);
        return (handle, window);
    }

    public string RegisterWindow(Window window)
    {
        var handle = $"w{++_windowCounter}";
        _windows[handle] = window;
        try
        {
            if (window.Properties.NativeWindowHandle.TryGetValue(out var nativeHandle) && nativeHandle != IntPtr.Zero)
            {
                _windowHandles[handle] = nativeHandle;
            }
        }
        catch { /* provider may be busy; hwnd is optional */ }
        return handle;
    }

    /// <summary>
    /// Register a native window handle (HWND) and return a session handle. If the
    /// same HWND was already registered, its existing handle is reused so refs stay
    /// stable. The UIA element is resolved lazily via <see cref="GetWindow"/>.
    /// </summary>
    public string RegisterWindowHandle(IntPtr hwnd)
    {
        foreach (var kvp in _windowHandles)
        {
            if (kvp.Value == hwnd) return kvp.Key;
        }

        var handle = $"w{++_windowCounter}";
        _windowHandles[handle] = hwnd;
        return handle;
    }

    public Window? GetWindow(string handle)
    {
        if (_windows.TryGetValue(handle, out var window)) return window;

        // Lazily resolve an hwnd-backed handle to a UIA element. AutomationElement
        // .FromHandle roots the tree at exactly that HWND, so snapshots scoped to a
        // modal handle can never leak into a sibling grid behind the disabled parent.
        if (_windowHandles.TryGetValue(handle, out var hwnd) && hwnd != IntPtr.Zero && Native.IsWindow(hwnd))
        {
            var resolved = _automation.FromHandle(hwnd)?.AsWindow();
            if (resolved != null)
            {
                _windows[handle] = resolved;
                return resolved;
            }
        }

        return null;
    }

    /// <summary>
    /// Re-resolve a handle's UIA element directly from its native HWND, bypassing
    /// any cached element. Guarantees a fresh tree rooted at the exact window.
    /// </summary>
    public Window? ResolveFromHandle(string handle)
    {
        var hwnd = GetNativeHandle(handle);
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return null;

        var resolved = _automation.FromHandle(hwnd)?.AsWindow();
        if (resolved != null)
        {
            _windows[handle] = resolved;
        }
        return resolved;
    }

    /// <summary>
    /// Get the native HWND for a session handle, reading it from a cached UIA
    /// element only if it was not registered directly from an HWND.
    /// </summary>
    public IntPtr GetNativeHandle(string handle)
    {
        if (_windowHandles.TryGetValue(handle, out var hwnd) && hwnd != IntPtr.Zero)
        {
            return hwnd;
        }

        if (_windows.TryGetValue(handle, out var window))
        {
            try
            {
                if (window.Properties.NativeWindowHandle.TryGetValue(out var nativeHandle) && nativeHandle != IntPtr.Zero)
                {
                    _windowHandles[handle] = nativeHandle;
                    return nativeHandle;
                }
            }
            catch { /* provider may be busy */ }
        }

        return IntPtr.Zero;
    }

    /// <summary>Rich, modal-proof description of a top-level window.</summary>
    public sealed record WindowInfo(
        string Handle,
        string Title,
        string? ProcessName,
        string ClassName,
        int ProcessId,
        bool IsEnabled,
        string? OwnerHandle,
        bool IsModalOwned);

    /// <summary>
    /// Enumerate top-level windows using Win32 EnumWindows (not UI Automation), so
    /// this succeeds even while a modal dialog blocks the target app's UIA provider.
    /// </summary>
    public List<WindowInfo> ListWindows()
    {
        var infos = Native.EnumerateTopLevelWindows(visibleOnly: true);
        var result = new List<WindowInfo>();

        foreach (var info in infos)
        {
            // Keep windows that have a title, or are classic dialogs (#32770 message
            // boxes can have an empty caption but are still interesting).
            if (string.IsNullOrEmpty(info.Title) && info.ClassName != Native.DialogClassName)
            {
                continue;
            }

            var handle = RegisterWindowHandle(info.Handle);

            string? processName = null;
            try { processName = System.Diagnostics.Process.GetProcessById(info.ProcessId).ProcessName; }
            catch { }

            string? ownerHandle = null;
            var isModalOwned = false;
            if (info.Owner != IntPtr.Zero)
            {
                ownerHandle = RegisterWindowHandle(info.Owner);
                // An enabled+visible window whose owner is disabled is a modal dialog.
                isModalOwned = info.IsEnabled && !Native.IsWindowEnabled(info.Owner);
            }

            result.Add(new WindowInfo(
                handle,
                info.Title,
                processName,
                info.ClassName,
                info.ProcessId,
                info.IsEnabled,
                ownerHandle,
                isModalOwned));
        }

        return result;
    }

    /// <summary>Result of resolving the active modal blocking a window.</summary>
    public sealed record ModalInfo(
        string Handle,
        Window Window,
        string Title,
        string ClassName,
        string? OwnerHandle);

    /// <summary>
    /// Resolve the modal dialog currently blocking <paramref name="ownerHandle"/>
    /// (or the foreground window if not supplied) using Win32 only, then attach a
    /// UIA element to it via FromHandle. Returns null when no modal is active.
    /// </summary>
    public ModalInfo? GetActiveModal(string? ownerHandle)
    {
        var ownerHwnd = IntPtr.Zero;
        if (!string.IsNullOrEmpty(ownerHandle))
        {
            ownerHwnd = GetNativeHandle(ownerHandle!);
        }
        if (ownerHwnd == IntPtr.Zero)
        {
            ownerHwnd = Native.GetForegroundWindow();
        }

        var modalHwnd = Native.GetActiveModal(ownerHwnd);

        // Fallback: if the owner exposes no enabled popup, the foreground window may
        // itself be a modal dialog (owned popup or classic #32770 message box).
        if (modalHwnd == IntPtr.Zero)
        {
            var foreground = Native.GetForegroundWindow();
            if (foreground != IntPtr.Zero && foreground != ownerHwnd
                && Native.IsWindowVisible(foreground) && Native.IsWindowEnabled(foreground))
            {
                var owner = Native.GetWindow(foreground, Native.GW_OWNER);
                var className = Native.GetWindowClass(foreground);
                if (owner != IntPtr.Zero || className == Native.DialogClassName)
                {
                    modalHwnd = foreground;
                }
            }
        }

        if (modalHwnd == IntPtr.Zero) return null;

        var window = _automation.FromHandle(modalHwnd)?.AsWindow();
        if (window == null) return null;

        var handle = RegisterWindowHandle(modalHwnd);
        _windows[handle] = window;

        var realOwner = Native.GetWindow(modalHwnd, Native.GW_OWNER);
        var resolvedOwner = realOwner != IntPtr.Zero ? RegisterWindowHandle(realOwner) : ownerHandle;

        return new ModalInfo(
            handle,
            window,
            Native.GetWindowTitle(modalHwnd),
            Native.GetWindowClass(modalHwnd),
            resolvedOwner);
    }

    /// <summary>
    /// Resolve the modal dialog currently blocking <paramref name="ownerHandle"/>
    /// (or the foreground window if not supplied) using Win32 ONLY, returning just
    /// its HWND. Unlike <see cref="GetActiveModal"/> this never attaches a UIA
    /// element, so it stays responsive on UIA-dead dialogs. Returns
    /// <see cref="IntPtr.Zero"/> when no modal is active.
    /// </summary>
    public IntPtr ResolveActiveModalHandle(string? ownerHandle)
    {
        var ownerHwnd = IntPtr.Zero;
        if (!string.IsNullOrEmpty(ownerHandle))
        {
            ownerHwnd = GetNativeHandle(ownerHandle!);
        }
        if (ownerHwnd == IntPtr.Zero)
        {
            ownerHwnd = Native.GetForegroundWindow();
        }

        var modalHwnd = Native.GetActiveModal(ownerHwnd);

        if (modalHwnd == IntPtr.Zero)
        {
            var foreground = Native.GetForegroundWindow();
            if (foreground != IntPtr.Zero && foreground != ownerHwnd
                && Native.IsWindowVisible(foreground) && Native.IsWindowEnabled(foreground))
            {
                var owner = Native.GetWindow(foreground, Native.GW_OWNER);
                var className = Native.GetWindowClass(foreground);
                if (owner != IntPtr.Zero || className == Native.DialogClassName)
                {
                    modalHwnd = foreground;
                }
            }
        }

        return modalHwnd;
    }

    public void FocusWindow(string handle)
    {
        var window = GetWindow(handle);
        if (window == null)
        {
            throw new Exception($"Window not found: {handle}");
        }
        window.Focus();
    }

    public void CloseWindow(string handle)
    {
        var window = GetWindow(handle);
        if (window == null)
        {
            throw new Exception($"Window not found: {handle}");
        }
        window.Close();
        _windows.Remove(handle);
        _windowHandles.Remove(handle);
    }

    public void Dispose()
    {
        foreach (var app in _applications.Values)
        {
            try { app.Close(); } catch { }
        }
        _applications.Clear();
        _windows.Clear();
        _windowHandles.Clear();
        _automation.Dispose();
    }
}
