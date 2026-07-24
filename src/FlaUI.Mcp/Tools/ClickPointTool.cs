using System.Text.Json;
using FlaUI.Mcp.Core;

namespace FlaUI.Mcp.Tools;

/// <summary>
/// Screenshot-coordinate click - the last-resort, UI-Automation-independent path.
/// The agent reads a screenshot, picks a pixel, and this tool translates it into a
/// physical screen coordinate and issues a real click there.
/// </summary>
public class ClickPointTool : ToolBase
{
    private readonly SessionManager _sessionManager;
    private readonly CaptureRegistry _captureRegistry;

    public ClickPointTool(SessionManager sessionManager, CaptureRegistry captureRegistry)
    {
        _sessionManager = sessionManager;
        _captureRegistry = captureRegistry;
    }

    public override string Name => "windows_click_point";

    public override string Description =>
        "Click a point with no UI Automation at all - the guaranteed last resort when a control has no UIA peer " +
        "and no child HWND (e.g. custom-drawn Infragistics buttons). Translates the point to a physical screen " +
        "coordinate and issues a real SendInput click. Prefer space=\"image\" right after windows_screenshot " +
        "(pixels in that capture) or space=\"normalized\" (0..1 fractions) so you never need to know DPI or monitor " +
        "offsets. Escalation order: UIA (windows_click) -> windows_click_native -> this. " +
        "Out-of-range points are rejected instead of clicking the wrong place.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            x = new
            {
                type = "number",
                description = "X coordinate to click, interpreted in 'space'."
            },
            y = new
            {
                type = "number",
                description = "Y coordinate to click, interpreted in 'space'."
            },
            space = new
            {
                type = "string",
                @enum = new[] { "screen", "window", "image", "normalized" },
                description = "Coordinate space: 'screen' = physical virtual-desktop pixels (may be negative on multi-monitor); " +
                    "'window' = pixels from handle's window top-left; 'image' = pixels within the most recent screenshot for " +
                    "the given handle/full-screen; 'normalized' = 0..1 fractions within the window (or virtual desktop) region."
            },
            handle = new
            {
                type = "string",
                description = "Window handle, required for 'window'/'image'/'normalized' (anchors the region and is brought to foreground). For 'image', omit to use the most recent full-screen capture."
            },
            button = new
            {
                type = "string",
                @enum = new[] { "left", "right", "middle" },
                description = "Mouse button (default left)."
            },
            doubleClick = new
            {
                type = "boolean",
                description = "Double-click instead of single (default false)."
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
        required = new[] { "x", "y", "space" }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var x = GetDoubleArgument(arguments, "x");
        var y = GetDoubleArgument(arguments, "y");
        var space = GetStringArgument(arguments, "space");

        if (x == null || y == null)
        {
            return ErrorResult("Both 'x' and 'y' are required.");
        }
        if (string.IsNullOrEmpty(space))
        {
            return ErrorResult("Missing required argument: space (screen|window|image|normalized)");
        }

        var handle = GetStringArgument(arguments, "handle");
        var button = Win32Interaction.ParseButton(GetStringArgument(arguments, "button"));
        var doubleClick = GetBoolArgument(arguments, "doubleClick", false);
        var restoreCursor = GetBoolArgument(arguments, "restoreCursor", true);
        var timeoutMs = GetIntArgument(arguments, "timeoutMs", 8000);

        return await RunBoundedAsync(timeoutMs, Name, () =>
        {
            try
            {
                if (!TryResolveScreenPoint(space!, x.Value, y.Value, handle, out var screenX, out var screenY, out var anchorHwnd, out var error))
                {
                    return ErrorResult(error);
                }

                // Foreground the anchor window (if any) so the click lands on the intended app.
                var foreground = "foreground=n/a";
                if (anchorHwnd != IntPtr.Zero)
                {
                    foreground = Win32Interaction.BringToForeground(anchorHwnd) ? "foreground=achieved" : "foreground=NOT-achieved";
                }

                Win32Interaction.ClickAtScreenPoint(screenX, screenY, button, doubleClick, restoreCursor);

                return TextResult(
                    $"{(doubleClick ? "Double-clicked" : "Clicked")} at screen ({screenX},{screenY}) " +
                    $"[space={space}, input=({x.Value},{y.Value})]. {foreground}.");
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to click point: {ex.Message}");
            }
        });
    }

    private bool TryResolveScreenPoint(string space, double x, double y, string? handle,
        out int screenX, out int screenY, out IntPtr anchorHwnd, out string error)
    {
        screenX = 0;
        screenY = 0;
        anchorHwnd = IntPtr.Zero;
        error = "";

        switch (space.ToLowerInvariant())
        {
            case "screen":
            {
                var vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
                var vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
                var vcx = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
                var vcy = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
                var sx = (int)Math.Round(x);
                var sy = (int)Math.Round(y);
                if (sx < vx || sx >= vx + vcx || sy < vy || sy >= vy + vcy)
                {
                    error = $"Point ({sx},{sy}) is outside the virtual desktop [{vx},{vy} {vcx}x{vcy}].";
                    return false;
                }
                screenX = sx;
                screenY = sy;
                return true;
            }

            case "window":
            {
                if (!TryGetAnchor(handle, out anchorHwnd, out var rect, out error)) return false;
                if (x < 0 || x > rect.Width || y < 0 || y > rect.Height)
                {
                    error = $"Point ({x},{y}) is outside the window bounds ({rect.Width}x{rect.Height}).";
                    return false;
                }
                screenX = rect.Left + (int)Math.Round(x);
                screenY = rect.Top + (int)Math.Round(y);
                return true;
            }

            case "image":
            {
                var key = string.IsNullOrEmpty(handle) ? CaptureRegistry.FullScreenKey : handle!;
                var meta = _captureRegistry.Get(key);
                if (meta == null)
                {
                    error = $"No recent screenshot for {(string.IsNullOrEmpty(handle) ? "full-screen" : handle)}. " +
                        "Call windows_screenshot first (same handle / full-screen) before using space=\"image\".";
                    return false;
                }
                if (x < 0 || x >= meta.WidthPx || y < 0 || y >= meta.HeightPx)
                {
                    error = $"Point ({x},{y}) is outside the {meta.WidthPx}x{meta.HeightPx} capture.";
                    return false;
                }
                var sxScale = meta.WidthPx != 0 ? (double)meta.ScreenWidth / meta.WidthPx : 1.0;
                var syScale = meta.HeightPx != 0 ? (double)meta.ScreenHeight / meta.HeightPx : 1.0;
                screenX = meta.OriginX + (int)Math.Round(x * sxScale);
                screenY = meta.OriginY + (int)Math.Round(y * syScale);
                if (!string.IsNullOrEmpty(handle))
                {
                    anchorHwnd = _sessionManager.GetNativeHandle(handle!);
                }
                return true;
            }

            case "normalized":
            {
                if (x < 0 || x > 1 || y < 0 || y > 1)
                {
                    error = $"Normalized point ({x},{y}) must be within 0..1.";
                    return false;
                }

                int left, top, width, height;
                if (!string.IsNullOrEmpty(handle))
                {
                    if (!TryGetAnchor(handle, out anchorHwnd, out var rect, out error)) return false;
                    left = rect.Left; top = rect.Top; width = rect.Width; height = rect.Height;
                }
                else
                {
                    left = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
                    top = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
                    width = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
                    height = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
                }
                screenX = left + (int)Math.Round(x * width);
                screenY = top + (int)Math.Round(y * height);
                return true;
            }

            default:
                error = $"Unknown space '{space}'. Use screen|window|image|normalized.";
                return false;
        }
    }

    private bool TryGetAnchor(string? handle, out IntPtr hwnd, out Native.RECT rect, out string error)
    {
        hwnd = IntPtr.Zero;
        rect = default;
        error = "";

        if (string.IsNullOrEmpty(handle))
        {
            error = "handle is required for space=\"window\"/\"normalized\".";
            return false;
        }

        hwnd = _sessionManager.GetNativeHandle(handle!);
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd))
        {
            error = $"No valid native window for {handle}. Run windows_list_windows to refresh handles.";
            return false;
        }

        if (!Native.GetWindowRect(hwnd, out rect))
        {
            error = $"Could not read window bounds for {handle}.";
            return false;
        }

        return true;
    }
}
