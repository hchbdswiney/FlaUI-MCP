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
        "Bring a window to the foreground and give it focus.";

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

        return await RunBoundedAsync(timeoutMs, Name, () =>
        {
            try
            {
                if (!string.IsNullOrEmpty(handle))
                {
                    _sessionManager.FocusWindow(handle);
                    return TextResult($"Focused window {handle}");
                }
                else if (!string.IsNullOrEmpty(title))
                {
                    var (windowHandle, window) = _sessionManager.AttachToWindow(title);
                    window.Focus();
                    return TextResult($"Focused window \"{window.Title}\" (handle: {windowHandle})");
                }
                else
                {
                    return ErrorResult("Either 'handle' or 'title' is required");
                }
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to focus window: {ex.Message}");
            }
        });
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
