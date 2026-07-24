using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Mcp.Core;

namespace FlaUI.Mcp.Tools;

/// <summary>
/// Click an element by ref
/// </summary>
public class ClickTool : ToolBase
{
    private readonly ElementRegistry _elementRegistry;

    public ClickTool(ElementRegistry elementRegistry)
    {
        _elementRegistry = elementRegistry;
    }

    public override string Name => "windows_click";

    public override string Description => 
        "Click an element by its ref (from windows_snapshot). Prefers Invoke pattern for reliability, " +
        "falls back to mouse click if needed.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            @ref = new
            {
                type = "string",
                description = "Element ref from windows_snapshot (e.g., 'w1e5')"
            },
            button = new
            {
                type = "string",
                @enum = new[] { "left", "right", "middle" },
                description = "Mouse button to click (default: left)"
            },
            doubleClick = new
            {
                type = "boolean",
                description = "Whether to double-click (default: false)"
            },
            timeoutMs = new
            {
                type = "integer",
                description = "Wall-clock budget in milliseconds before failing fast with a retryable error " +
                    "(default 10000). Prevents hanging on a stalled provider."
            }
        },
        required = new[] { "ref" }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var refId = GetStringArgument(arguments, "ref");
        if (string.IsNullOrEmpty(refId))
        {
            return ErrorResult("Missing required argument: ref");
        }

        var button = GetStringArgument(arguments, "button") ?? "left";
        var doubleClick = GetBoolArgument(arguments, "doubleClick", false);
        var timeoutMs = GetIntArgument(arguments, "timeoutMs", 10000);

        var element = _elementRegistry.GetElement(refId);
        if (element == null)
        {
            return ErrorResult($"Element not found: {refId}. Run windows_snapshot to refresh element refs.");
        }

        return await RunBoundedAsync(timeoutMs, Name, () =>
        {
            try
            {
                var elementName = element.Properties.Name.ValueOrDefault ?? refId;

                // Try Invoke pattern first (most reliable for buttons)
                if (button == "left" && !doubleClick && element.Patterns.Invoke.IsSupported)
                {
                    element.Patterns.Invoke.Pattern.Invoke();
                    return TextResult($"Invoked {elementName}");
                }

                // Try Toggle pattern for checkboxes
                if (button == "left" && !doubleClick && element.Patterns.Toggle.IsSupported)
                {
                    element.Patterns.Toggle.Pattern.Toggle();
                    var newState = element.Patterns.Toggle.Pattern.ToggleState.ValueOrDefault;
                    return TextResult($"Toggled {elementName} to {newState}");
                }

                // Try SelectionItem pattern for list items
                if (button == "left" && !doubleClick && element.Patterns.SelectionItem.IsSupported)
                {
                    element.Patterns.SelectionItem.Pattern.Select();
                    return TextResult($"Selected {elementName}");
                }

                // Fall back to mouse click
                var clickPoint = element.GetClickablePoint();

                var mouseButton = button switch
                {
                    "right" => MouseButton.Right,
                    "middle" => MouseButton.Middle,
                    _ => MouseButton.Left
                };

                if (doubleClick)
                {
                    Mouse.DoubleClick(clickPoint, mouseButton);
                    return TextResult($"Double-clicked {elementName}");
                }
                else
                {
                    Mouse.Click(clickPoint, mouseButton);
                    return TextResult($"Clicked {elementName}");
                }
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to click {refId}: {ex.Message}");
            }
        });
    }
}
