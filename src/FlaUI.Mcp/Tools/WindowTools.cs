using System.Text.Json;
using FlaUI.Mcp.Core;

namespace FlaUI.Mcp.Tools;

/// <summary>
/// List all open windows
/// </summary>
public class ListWindowsTool : ToolBase
{
    private readonly SessionManager _sessionManager;

    public ListWindowsTool(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    public override string Name => "windows_list_windows";

    public override string Description =>
        "List all open top-level windows with handles, titles, and process names. " +
        "Backed by Win32 enumeration (not UI Automation), so it keeps working even while a " +
        "modal dialog is blocking an app. Modal dialogs are flagged with [modal] and show their " +
        "owner window, class name, and pid.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new { }
    };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        try
        {
            var windows = _sessionManager.ListWindows();

            if (windows.Count == 0)
            {
                return Task.FromResult(TextResult("No windows found"));
            }

            var lines = windows.Select(w =>
            {
                var tags = new List<string> { $"class={w.ClassName}", $"pid={w.ProcessId}" };
                if (!w.IsEnabled) tags.Add("disabled");
                if (w.IsModalOwned) tags.Add($"modal owner={w.OwnerHandle}");
                else if (w.OwnerHandle != null) tags.Add($"owner={w.OwnerHandle}");

                return $"- {w.Handle}: \"{w.Title}\" ({w.ProcessName ?? "unknown"}) [{string.Join(", ", tags)}]";
            });

            return Task.FromResult(TextResult(string.Join("\n", lines)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorResult($"Failed to list windows: {ex.Message}"));
        }
    }
}

/// <summary>
/// Resolve and snapshot the active modal dialog blocking a window.
/// </summary>
public class GetActiveModalTool : ToolBase
{
    private readonly SessionManager _sessionManager;
    private readonly ElementRegistry _elementRegistry;
    private readonly SnapshotBuilder _snapshotBuilder;

    public GetActiveModalTool(SessionManager sessionManager, ElementRegistry elementRegistry)
    {
        _sessionManager = sessionManager;
        _elementRegistry = elementRegistry;
        _snapshotBuilder = new SnapshotBuilder(elementRegistry);
    }

    public override string Name => "windows_get_active_modal";

    public override string Description =>
        "Resolve the modal dialog currently blocking a window (both classic #32770 message boxes " +
        "and rich WinForms/Infragistics modal forms) using Win32 - never UI Automation enumeration - " +
        "so it returns fast even over a huge grid. Returns the modal's handle, title, class, owner, " +
        "and a shallow accessibility snapshot already scoped to the modal subtree (never leaks into a " +
        "sibling grid behind the disabled parent). Use the returned refs with windows_click / " +
        "windows_fill for deterministic UIA-pattern interaction instead of windows_send_keys.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            ownerHandle = new
            {
                type = "string",
                description = "Handle of the window being blocked (from windows_list_windows/windows_launch). " +
                    "If omitted, the foreground window is used to locate the modal."
            },
            maxDepth = new
            {
                type = "integer",
                description = "Maximum snapshot depth for the modal subtree (default 12)."
            },
            maxChildren = new
            {
                type = "integer",
                description = "Maximum children per node before truncating (default 40)."
            },
            maxElements = new
            {
                type = "integer",
                description = "Maximum total elements before stopping (default 600)."
            },
            timeoutMs = new
            {
                type = "integer",
                description = "Wall-clock budget for building the scoped snapshot (default 5000)."
            }
        }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var ownerHandle = GetStringArgument(arguments, "ownerHandle");
        var options = new SnapshotOptions
        {
            MaxDepth = GetIntArgument(arguments, "maxDepth", 12),
            MaxChildrenPerNode = GetIntArgument(arguments, "maxChildren", 40),
            MaxElements = GetIntArgument(arguments, "maxElements", 600),
            TimeBudget = TimeSpan.FromMilliseconds(GetIntArgument(arguments, "timeoutMs", 5000))
        };

        return await RunBoundedAsync(GetIntArgument(arguments, "timeoutMs", 5000), Name, () =>
        {
            SessionManager.ModalInfo? modal;
            try
            {
                modal = _sessionManager.GetActiveModal(ownerHandle);
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to resolve active modal: {ex.Message}");
            }

            if (modal == null)
            {
                return TextResult(
                    "No active modal dialog found" +
                    (string.IsNullOrEmpty(ownerHandle) ? " for the foreground window." : $" for owner {ownerHandle}.") +
                    " If a modal is open, ensure the correct ownerHandle is passed or that the app is in the foreground.");
            }

            string snapshot;
            try
            {
                snapshot = _snapshotBuilder.BuildSnapshot(modal.Handle, modal.Window, options);
            }
            catch (Exception ex)
            {
                snapshot = $"(failed to snapshot modal subtree: {ex.Message})";
            }

            var header =
                $"Modal dialog {modal.Handle}: \"{modal.Title}\" [class={modal.ClassName}]" +
                (modal.OwnerHandle != null ? $" [owner={modal.OwnerHandle}]" : "") +
                "\n\n";

            return TextResult(header + snapshot);
        });
    }
}


/// <summary>
/// Focus a window
/// </summary>
public class FocusWindowTool : ToolBase
{
    private readonly SessionManager _sessionManager;

    public FocusWindowTool(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    public override string Name => "windows_focus";

    public override string Description => 
        "Bring a window to the foreground and give it focus. Tries the UI Automation focus path first, then " +
        "automatically falls back to a pure Win32 foreground sequence (AttachThreadInput + SetForegroundWindow) " +
        "when UIA times out or fails - so it works on UIA-dead modals. Reports which path won (path=uia or path=win32).";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            handle = new
            {
                type = "string",
                description = "Window handle from windows_list_windows or windows_launch"
            },
            title = new
            {
                type = "string",
                description = "Window title (alternative to handle). Finds first window containing this text."
            },
            timeoutMs = new
            {
                type = "integer",
                description = "Wall-clock budget in milliseconds before failing fast with a retryable error (default 10000)."
            }
        }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var handle = GetStringArgument(arguments, "handle");
        var title = GetStringArgument(arguments, "title");
        var timeoutMs = GetIntArgument(arguments, "timeoutMs", 10000);

        if (string.IsNullOrEmpty(handle) && string.IsNullOrEmpty(title))
        {
            return ErrorResult("Either 'handle' or 'title' is required");
        }

        // Resolve the native HWND up front (pure Win32) so the fallback works even
        // when the UIA path is stalled.
        var hwnd = ResolveHwnd(handle, title, out var label);

        // Attempt the UIA focus path first, bounded to part of the budget so a
        // stalled provider cannot consume the whole time before the Win32 fallback.
        var uiaBudget = Math.Max(1000, timeoutMs / 2);
        var uiaTask = Task.Run<(bool ok, string? error, string? label)>(() =>
        {
            try
            {
                if (!string.IsNullOrEmpty(handle))
                {
                    _sessionManager.FocusWindow(handle!);
                    return (true, null, handle);
                }

                var (windowHandle, window) = _sessionManager.AttachToWindow(title!);
                window.Focus();
                return (true, null, $"\"{window.Title}\" (handle: {windowHandle})");
            }
            catch (Exception ex)
            {
                return (false, ex.Message, null);
            }
        });

        string? uiaError;
        if (await Task.WhenAny(uiaTask, Task.Delay(uiaBudget)).ConfigureAwait(false) == uiaTask)
        {
            var (ok, error, resolvedLabel) = await uiaTask.ConfigureAwait(false);
            if (ok)
            {
                return TextResult($"Focused window {resolvedLabel}. path=uia");
            }
            uiaError = error;
        }
        else
        {
            uiaError = $"UIA focus timed out after {uiaBudget}ms";
            _ = uiaTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        // Win32 fallback: force the OS foreground without any UIA.
        if (hwnd != IntPtr.Zero)
        {
            var foreground = Win32Interaction.BringToForeground(hwnd);
            if (foreground)
            {
                return TextResult(
                    $"Focused window {label} via the Win32 foreground sequence. path=win32 " +
                    $"(UIA path failed: {uiaError})");
            }

            return ErrorResult(
                $"Failed to focus {label}. UIA path failed ({uiaError}) and the Win32 foreground sequence " +
                "could not take foreground - another process may hold a foreground lock. Retry after " +
                "interacting with the target, or use windows_click_native/windows_click_point which force foreground per-action.");
        }

        return ErrorResult(
            $"Failed to focus window: UIA path failed ({uiaError}) and no native handle was available for the Win32 fallback. " +
            "Use windows_list_windows to get a fresh handle.");
    }

    /// <summary>Resolve a target HWND via Win32 only (no UIA), from handle or title.</summary>
    private IntPtr ResolveHwnd(string? handle, string? title, out string label)
    {
        if (!string.IsNullOrEmpty(handle))
        {
            label = handle!;
            return _sessionManager.GetNativeHandle(handle!);
        }

        if (!string.IsNullOrEmpty(title))
        {
            label = $"\"{title}\"";
            var match = Native.EnumerateTopLevelWindows(visibleOnly: true)
                .FirstOrDefault(w => !string.IsNullOrEmpty(w.Title) &&
                    w.Title.IndexOf(title!, StringComparison.OrdinalIgnoreCase) >= 0);
            if (match != null)
            {
                label = $"\"{match.Title}\"";
                return match.Handle;
            }
        }

        label = handle ?? title ?? "(unknown)";
        return IntPtr.Zero;
    }
}

/// <summary>
/// Close a window
/// </summary>
public class CloseWindowTool : ToolBase
{
    private readonly SessionManager _sessionManager;

    public CloseWindowTool(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    public override string Name => "windows_close";

    public override string Description => 
        "Close a window.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            handle = new
            {
                type = "string",
                description = "Window handle to close"
            }
        },
        required = new[] { "handle" }
    };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var handle = GetStringArgument(arguments, "handle");
        if (string.IsNullOrEmpty(handle))
        {
            return Task.FromResult(ErrorResult("Missing required argument: handle"));
        }

        try
        {
            _sessionManager.CloseWindow(handle);
            return Task.FromResult(TextResult($"Closed window {handle}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorResult($"Failed to close window: {ex.Message}"));
        }
    }
}
