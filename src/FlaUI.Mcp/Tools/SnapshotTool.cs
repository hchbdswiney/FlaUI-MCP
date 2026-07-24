using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core;

namespace FlaUI.Mcp.Tools;

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
            },
            scope = new
            {
                type = "string",
                @enum = new[] { "descendants", "subtree-from-handle" },
                description = "How to root the snapshot. 'descendants' (default) walks the cached window element. " +
                    "'subtree-from-handle' re-resolves the element directly from the window's native HWND via " +
                    "AutomationElement.FromHandle, guaranteeing the walk is scoped to exactly that window and can " +
                    "never leak into a sibling grid behind a disabled parent. Use this for modal dialogs."
            },
            timeoutMs = new
            {
                type = "integer",
                description = "Wall-clock budget in milliseconds. The walk stops and emits a truncation marker when " +
                    "exceeded, and a hard bound fails fast if the provider stalls. Default: unbounded (relies on the " +
                    "global tool timeout). Recommended for large grids / deep modal trees."
            }
        }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var handle = GetStringArgument(arguments, "handle");
        var scope = GetStringArgument(arguments, "scope");
        var timeoutMs = GetIntArgument(arguments, "timeoutMs");
        var options = new SnapshotOptions
        {
            MaxDepth = GetIntArgument(arguments, "maxDepth", 10),
            MaxChildrenPerNode = GetIntArgument(arguments, "maxChildren"),
            MaxElements = GetIntArgument(arguments, "maxElements"),
            Properties = GetStringArrayArgument(arguments, "properties"),
            TimeBudget = timeoutMs.HasValue ? TimeSpan.FromMilliseconds(timeoutMs.Value) : null
        };

        return await RunBoundedAsync(timeoutMs, Name, () =>
        {
            FlaUI.Core.AutomationElements.Window? window = null;

            if (!string.IsNullOrEmpty(handle))
            {
                window = string.Equals(scope, "subtree-from-handle", StringComparison.OrdinalIgnoreCase)
                    ? _sessionManager.ResolveFromHandle(handle!) ?? _sessionManager.GetWindow(handle!)
                    : _sessionManager.GetWindow(handle!);
                if (window == null)
                {
                    return ErrorResult($"Window not found: {handle}");
                }
            }
            else
            {
                // Get the foreground window
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
                    return ErrorResult("No window specified and no focused window found. Use windows_list_windows to see available windows.");
                }

                // Register this window
                handle = _sessionManager.RegisterWindow(window);
            }

            var snapshot = _snapshotBuilder.BuildSnapshot(handle!, window, options);
            return TextResult(snapshot);
        });
    }
}
