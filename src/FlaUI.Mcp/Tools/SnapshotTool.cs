using System.Text.Json;
using FlaUI.Core.AutomationElements;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// Take accessibility snapshot of a window - THE KEY TOOL FOR AGENTS
/// </summary>
public class SnapshotTool : ToolBase
{
    private readonly SessionManager _sessionManager;
    private readonly ElementRegistry _elementRegistry;
    private readonly SnapshotBuilder _snapshotBuilder;

    public SnapshotTool(SessionManager sessionManager, ElementRegistry elementRegistry)
    {
        _sessionManager = sessionManager;
        _elementRegistry = elementRegistry;
        _snapshotBuilder = new SnapshotBuilder(elementRegistry);
    }

    public override string Name => "windows_snapshot";

    public override string Description => 
        "Capture accessibility snapshot of a window. Returns a structured tree with element refs " +
        "that can be used with windows_click, windows_type, etc. This is the primary tool for " +
        "understanding window contents - use it before interacting with elements. " +
        "For large grids or deep modal windows that time out, take a shallow snapshot with " +
        "maxDepth/maxChildren/maxElements, then re-snapshot a specific element for more detail.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            handle = new
            {
                type = "string",
                description = "Window handle from windows_launch or windows_list_windows. If omitted, uses the most recently launched window."
            },
            maxDepth = new
            {
                type = "integer",
                description = "Maximum tree depth to descend (0 = window only). Lower values give a fast shallow snapshot. Default 10."
            },
            maxChildren = new
            {
                type = "integer",
                description = "Maximum children shown per node before truncating with a marker. Useful for large grids/lists. Default: unlimited."
            },
            maxElements = new
            {
                type = "integer",
                description = "Maximum total elements to emit before stopping with a marker. Hard cap on total work to avoid timeouts. Default: unlimited."
            },
            properties = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Optional list of friendly UIA property names to emit inline on each element as [name=value]. " +
                    "Examples: helpText, className, isKeyboardFocusable, boundingRectangle, itemStatus, automationId, frameworkId. " +
                    "Unknown names are ignored with a note. Use windows_get_properties on a single element to discover all available properties."
            }
        }
    };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var handle = GetStringArgument(arguments, "handle");
        var options = new SnapshotOptions
        {
            MaxDepth = GetIntArgument(arguments, "maxDepth", 10),
            MaxChildrenPerNode = GetIntArgument(arguments, "maxChildren"),
            MaxElements = GetIntArgument(arguments, "maxElements"),
            Properties = GetStringArrayArgument(arguments, "properties")
        };

        try
        {
            FlaUI.Core.AutomationElements.Window? window = null;

            if (!string.IsNullOrEmpty(handle))
            {
                window = _sessionManager.GetWindow(handle);
                if (window == null)
                {
                    return Task.FromResult(ErrorResult($"Window not found: {handle}"));
                }
            }
            else
            {
                // Get the foreground window
                var desktop = _sessionManager.Automation.GetDesktop();
                var focusedElement = _sessionManager.Automation.FocusedElement();
                
                if (focusedElement != null)
                {
                    // Walk up to find the window
                    var current = focusedElement;
                    while (current != null)
                    {
                        if (current.Properties.ControlType.ValueOrDefault == FlaUI.Core.Definitions.ControlType.Window)
                        {
                            window = current.AsWindow();
                            break;
                        }
                        current = current.Parent;
                    }
                }

                if (window == null)
                {
                    return Task.FromResult(ErrorResult("No window specified and no focused window found. Use windows_list_windows to see available windows."));
                }

                // Register this window
                handle = _sessionManager.RegisterWindow(window);
            }

            var snapshot = _snapshotBuilder.BuildSnapshot(handle!, window, options);
            return Task.FromResult(TextResult(snapshot));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorResult($"Failed to capture snapshot: {ex.Message}"));
        }
    }
}
