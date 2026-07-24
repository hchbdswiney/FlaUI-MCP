using System.Text.Json;
using FlaUI.Mcp.Core;

namespace FlaUI.Mcp.Tools;

/// <summary>
/// Shared UIA-independent native-click logic for windows_click_native and
/// windows_dismiss_modal.
/// </summary>
internal static class NativeClickCore
{
    public sealed record Result(bool Success, string Message);

    /// <summary>
    /// Foreground the target window and click a captioned child control purely via
    /// Win32. On a genuine #32770 dialog the standard WM_COMMAND fast-path is used;
    /// otherwise a real SendInput click at the control rectangle is issued (with a
    /// secondary posted click), which is required for rich WinForms/Infragistics
    /// buttons that do not honor BM_CLICK/WM_COMMAND.
    /// </summary>
    public static Result Click(
        IntPtr hwnd,
        string? text,
        string? childClass,
        int index,
        Win32Interaction.MouseButtonKind button,
        bool restoreCursor)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd))
        {
            return new Result(false, "Target window handle is not a valid window (it may have closed).");
        }

        var className = Native.GetWindowClass(hwnd);
        var foreground = Win32Interaction.BringToForeground(hwnd);
        var foregroundPath = foreground ? "foreground=achieved" : "foreground=NOT-achieved";

        // No caption: click the center of the window itself.
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(childClass))
        {
            if (!Native.GetWindowRect(hwnd, out var wr))
            {
                return new Result(false, "Could not read the window rectangle.");
            }
            var wcx = wr.Left + wr.Width / 2;
            var wcy = wr.Top + wr.Height / 2;
            Win32Interaction.ClickAtScreenPoint(wcx, wcy, button, false, restoreCursor);
            return new Result(true,
                $"Clicked window {className} center at screen ({wcx},{wcy}). {foregroundPath}. " +
                (foreground ? "" : "WARNING: another process holds the foreground lock; the click may not have landed. "));
        }

        var children = Native.EnumerateChildWindows(hwnd);
        var wantText = text?.Trim();

        var matches = children.Where(c =>
        {
            var textOk = string.IsNullOrEmpty(wantText) ||
                string.Equals(Win32Interaction.StripMnemonic(c.Text).Trim(), wantText, StringComparison.OrdinalIgnoreCase);
            var classOk = string.IsNullOrEmpty(childClass) ||
                c.ClassName.IndexOf(childClass, StringComparison.OrdinalIgnoreCase) >= 0;
            return textOk && classOk;
        }).ToList();

        if (matches.Count == 0)
        {
            var found = children.Count == 0
                ? "(no child windows have their own HWND - this is common for custom-drawn Infragistics controls; use windows_click_point on a screenshot instead)"
                : string.Join("; ", children
                    .Where(c => !string.IsNullOrEmpty(c.Text) || !string.IsNullOrEmpty(c.ClassName))
                    .Take(40)
                    .Select(c => $"\"{Win32Interaction.StripMnemonic(c.Text)}\" [class={c.ClassName}]"));

            return new Result(false,
                $"No child control matched text=\"{text}\" childClass=\"{childClass}\" on {className}. " +
                $"Children found: {found}. " +
                "If the button is custom-drawn (no HWND), escalate to windows_click_point with a screenshot coordinate.");
        }

        if (index < 0 || index >= matches.Count)
        {
            return new Result(false,
                $"index {index} is out of range; {matches.Count} control(s) matched text=\"{text}\" childClass=\"{childClass}\".");
        }

        var child = matches[index];

        // Genuine #32770 dialog: use the standard WM_COMMAND fast-path.
        if (className == Native.DialogClassName)
        {
            var ctrlId = Native.GetDlgCtrlID(child.Handle);
            if (ctrlId != 0)
            {
                Native.SendMessage(hwnd, Native.WM_COMMAND,
                    (IntPtr)((0 << 16) | (ctrlId & 0xFFFF)), child.Handle);
                return new Result(true,
                    $"Actuated #32770 button \"{Win32Interaction.StripMnemonic(child.Text)}\" " +
                    $"(ctrlId={ctrlId}) via WM_COMMAND. {foregroundPath}.");
            }
        }

        var cx = child.Rect.Left + child.Rect.Width / 2;
        var cy = child.Rect.Top + child.Rect.Height / 2;

        Win32Interaction.ClickAtScreenPoint(cx, cy, button, false, restoreCursor);
        // Secondary attempt for windowed controls that ignore synthetic movement.
        Win32Interaction.PostClickToChild(child.Handle, button);

        return new Result(true,
            $"Clicked \"{Win32Interaction.StripMnemonic(child.Text)}\" [class={child.ClassName}] " +
            $"at screen rect ({child.Rect.Left},{child.Rect.Top},{child.Rect.Right},{child.Rect.Bottom}) " +
            $"center ({cx},{cy}). {foregroundPath}." +
            (foreground ? "" : " WARNING: another process holds the foreground lock; verify the click landed with windows_list_windows."));
    }
}

/// <summary>
/// Foreground native click - a UI-Automation-independent fallback that resolves a
/// window/dialog and optional child control via Win32 only and issues a real
/// mouse click.
/// </summary>
public class NativeClickTool : ToolBase
{
    private readonly SessionManager _sessionManager;

    public NativeClickTool(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    public override string Name => "windows_click_native";

    public override string Description =>
        "UIA-independent fallback click. Forces a window/dialog to the OS foreground via Win32 and clicks a " +
        "captioned child control with a real mouse click (SendInput) - no UI Automation, no Invoke pattern. " +
        "Works on a modal even while it blocks its owner and UIA is timing out. For rich WinForms/Infragistics " +
        "dialogs (buttons are UltraButton, not native BUTTON), this real-click path is required; BM_CLICK/WM_COMMAND " +
        "do not actuate them (a #32770 WM_COMMAND fast-path is used only when the class is genuinely #32770). " +
        "Escalation order: UIA (windows_click) -> this -> windows_click_point (coordinate) when a control has no HWND.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            handle = new
            {
                type = "string",
                description = "Window or dialog handle from windows_list_windows / windows_get_active_modal. Works on a modal blocking its owner."
            },
            text = new
            {
                type = "string",
                description = "Caption of the child control to click. Matched against child window text with the mnemonic '&' stripped (\"&Yes\" matches \"Yes\"), case-insensitive and trimmed. Omit to click the window center."
            },
            childClass = new
            {
                type = "string",
                description = "Restrict the child match to a window class substring (e.g. the WinForms button class), for disambiguation."
            },
            index = new
            {
                type = "integer",
                description = "Which match to click when several children share the caption/class (default 0)."
            },
            button = new
            {
                type = "string",
                @enum = new[] { "left", "right", "middle" },
                description = "Mouse button (default left)."
            },
            restoreCursor = new
            {
                type = "boolean",
                description = "Move the cursor back to its prior position after clicking (default true)."
            },
            timeoutMs = new
            {
                type = "integer",
                description = "Fast-fail budget in milliseconds (default 8000)."
            }
        },
        required = new[] { "handle" }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var handle = GetStringArgument(arguments, "handle");
        if (string.IsNullOrEmpty(handle))
        {
            return ErrorResult("Missing required argument: handle");
        }

        var text = GetStringArgument(arguments, "text");
        var childClass = GetStringArgument(arguments, "childClass");
        var index = GetIntArgument(arguments, "index", 0);
        var button = Win32Interaction.ParseButton(GetStringArgument(arguments, "button"));
        var restoreCursor = GetBoolArgument(arguments, "restoreCursor", true);
        var timeoutMs = GetIntArgument(arguments, "timeoutMs", 8000);

        var hwnd = _sessionManager.GetNativeHandle(handle!);
        if (hwnd == IntPtr.Zero)
        {
            return ErrorResult($"No native window handle for {handle}. Run windows_list_windows to refresh handles.");
        }

        return await RunBoundedAsync(timeoutMs, Name, () =>
        {
            try
            {
                var result = NativeClickCore.Click(hwnd, text, childClass, index, button, restoreCursor);
                return result.Success ? TextResult(result.Message) : ErrorResult(result.Message);
            }
            catch (Exception ex)
            {
                return ErrorResult($"Native click failed: {ex.Message}");
            }
        });
    }
}

/// <summary>
/// Convenience specialization of windows_click_native for dismissing the modal
/// dialog blocking an app - the most common UIA-dead recovery.
/// </summary>
public class DismissModalTool : ToolBase
{
    private readonly SessionManager _sessionManager;

    public DismissModalTool(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    public override string Name => "windows_dismiss_modal";

    public override string Description =>
        "Dismiss the modal dialog blocking an app by clicking a captioned button (Yes/No/OK/Cancel/...), " +
        "purely via Win32 - no UI Automation - so it works when windows_get_active_modal / windows_focus time out. " +
        "Locates the modal with the same Win32 finder as windows_get_active_modal (no UIA snapshot required), then " +
        "uses the native-click logic. Re-checks that the modal is gone and the owner re-enabled. " +
        "Pass modalHandle if you already resolved it, or ownerHandle to find the modal blocking that window.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            ownerHandle = new
            {
                type = "string",
                description = "Handle of the window being blocked; the modal it owns is resolved via Win32. If omitted, the foreground window is used."
            },
            modalHandle = new
            {
                type = "string",
                description = "Handle of the modal dialog itself, if already known (from windows_list_windows / windows_get_active_modal)."
            },
            button = new
            {
                type = "string",
                description = "Caption of the button to click (default \"Yes\"). Mnemonic '&' is stripped for matching."
            },
            childClass = new
            {
                type = "string",
                description = "Optional child window class substring to disambiguate the button."
            },
            restoreCursor = new
            {
                type = "boolean",
                description = "Move the cursor back to its prior position after clicking (default true)."
            },
            timeoutMs = new
            {
                type = "integer",
                description = "Fast-fail budget in milliseconds (default 8000)."
            }
        }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var ownerHandle = GetStringArgument(arguments, "ownerHandle");
        var modalHandle = GetStringArgument(arguments, "modalHandle");
        var button = GetStringArgument(arguments, "button") ?? "Yes";
        var childClass = GetStringArgument(arguments, "childClass");
        var restoreCursor = GetBoolArgument(arguments, "restoreCursor", true);
        var timeoutMs = GetIntArgument(arguments, "timeoutMs", 8000);

        return await RunBoundedAsync(timeoutMs, Name, () =>
        {
            try
            {
                IntPtr modalHwnd;
                IntPtr ownerHwnd = IntPtr.Zero;

                if (!string.IsNullOrEmpty(modalHandle))
                {
                    modalHwnd = _sessionManager.GetNativeHandle(modalHandle!);
                    if (modalHwnd == IntPtr.Zero)
                    {
                        return ErrorResult($"No native window handle for modalHandle {modalHandle}.");
                    }
                    ownerHwnd = Native.GetWindow(modalHwnd, Native.GW_OWNER);
                }
                else
                {
                    var modal = _sessionManager.ResolveActiveModalHandle(ownerHandle);
                    if (modal == IntPtr.Zero)
                    {
                        return ErrorResult(
                            "No active modal dialog found" +
                            (string.IsNullOrEmpty(ownerHandle) ? " for the foreground window." : $" for owner {ownerHandle}.") +
                            " Pass modalHandle explicitly or ensure the correct ownerHandle.");
                    }
                    modalHwnd = modal;
                    ownerHwnd = Native.GetWindow(modalHwnd, Native.GW_OWNER);
                }

                var result = NativeClickCore.Click(modalHwnd, button, childClass, 0, Win32Interaction.MouseButtonKind.Left, restoreCursor);
                if (!result.Success)
                {
                    return ErrorResult(result.Message);
                }

                // Verify post-state: give the app a moment to tear the dialog down.
                var gone = false;
                var ownerEnabled = false;
                for (var i = 0; i < 20; i++)
                {
                    gone = !Native.IsWindow(modalHwnd) || !Native.IsWindowVisible(modalHwnd);
                    ownerEnabled = ownerHwnd == IntPtr.Zero || Native.IsWindowEnabled(ownerHwnd);
                    if (gone && ownerEnabled) break;
                    Thread.Sleep(50);
                }

                var state = gone
                    ? "Modal dismissed" + (ownerEnabled ? " and owner re-enabled." : " (owner still disabled - it may raise another dialog).")
                    : "Button clicked but the modal is still present; it may require a different caption or a coordinate click (windows_click_point).";

                return gone
                    ? TextResult($"{result.Message}\n{state}")
                    : ErrorResult($"{result.Message}\n{state}");
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to dismiss modal: {ex.Message}");
            }
        });
    }
}
