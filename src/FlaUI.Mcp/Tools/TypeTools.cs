using System.Text.Json;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.Mcp.Core;

namespace FlaUI.Mcp.Tools;

/// <summary>
/// Type text into an element
/// </summary>
public class TypeTool : ToolBase
{
    private readonly ElementRegistry _elementRegistry;

    public TypeTool(ElementRegistry elementRegistry)
    {
        _elementRegistry = elementRegistry;
    }

    public override string Name => "windows_type";

    public override string Description => 
        "Type text into an element. The element will be focused first. " +
        "Use this for typing without clearing existing content. Use windows_fill to replace content.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            @ref = new
            {
                type = "string",
                description = "Element ref from windows_snapshot (e.g., 'w1e5'). If omitted, types to currently focused element."
            },
            text = new
            {
                type = "string",
                description = "Text to type"
            },
            submit = new
            {
                type = "boolean",
                description = "Press Enter after typing (default: false)"
            },
            timeoutMs = new
            {
                type = "integer",
                description = "Wall-clock budget in milliseconds before failing fast with a retryable error (default 10000)."
            }
        },
        required = new[] { "text" }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var text = GetStringArgument(arguments, "text");
        if (text == null)
        {
            return ErrorResult("Missing required argument: text");
        }

        var refId = GetStringArgument(arguments, "ref");
        var submit = GetBoolArgument(arguments, "submit", false);
        var timeoutMs = GetIntArgument(arguments, "timeoutMs", 10000);

        return await RunBoundedAsync(timeoutMs, Name, () =>
        {
            try
            {
                // Focus element if ref provided
                if (!string.IsNullOrEmpty(refId))
                {
                    var element = _elementRegistry.GetElement(refId);
                    if (element == null)
                    {
                        return ErrorResult($"Element not found: {refId}. Run windows_snapshot to refresh element refs.");
                    }

                    element.Focus();
                    Thread.Sleep(50); // Small delay to ensure focus
                }

                // Type the text
                Keyboard.Type(text);

                if (submit)
                {
                    Keyboard.Press(VirtualKeyShort.ENTER);
                }

                var target = string.IsNullOrEmpty(refId) ? "focused element" : refId;
                var action = submit ? "Typed and submitted" : "Typed";
                return TextResult($"{action} \"{text}\" into {target}");
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to type: {ex.Message}");
            }
        });
    }
}

/// <summary>
/// Fill (clear and type) an element
/// </summary>
public class FillTool : ToolBase
{
    private readonly ElementRegistry _elementRegistry;

    public FillTool(ElementRegistry elementRegistry)
    {
        _elementRegistry = elementRegistry;
    }

    public override string Name => "windows_fill";

    public override string Description => 
        "Clear and fill a text field with new value. Prefers Value pattern for reliability.";

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
            value = new
            {
                type = "string",
                description = "Value to fill"
            },
            timeoutMs = new
            {
                type = "integer",
                description = "Wall-clock budget in milliseconds before failing fast with a retryable error (default 10000)."
            }
        },
        required = new[] { "ref", "value" }
    };

    public override async Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var refId = GetStringArgument(arguments, "ref");
        var value = GetStringArgument(arguments, "value");

        if (string.IsNullOrEmpty(refId))
        {
            return ErrorResult("Missing required argument: ref");
        }
        if (value == null)
        {
            return ErrorResult("Missing required argument: value");
        }

        var element = _elementRegistry.GetElement(refId);
        if (element == null)
        {
            return ErrorResult($"Element not found: {refId}. Run windows_snapshot to refresh element refs.");
        }

        var timeoutMs = GetIntArgument(arguments, "timeoutMs", 10000);

        return await RunBoundedAsync(timeoutMs, Name, () =>
        {
            try
            {
                var elementName = element.Properties.Name.ValueOrDefault ?? refId;

                // Try Value pattern first
                if (element.Patterns.Value.IsSupported)
                {
                    var valuePattern = element.Patterns.Value.Pattern;
                    if (!valuePattern.IsReadOnly.ValueOrDefault)
                    {
                        valuePattern.SetValue(value);
                        return TextResult($"Filled {elementName} with \"{value}\"");
                    }
                }

                // Fall back to focus + select all + type
                element.Focus();
                Thread.Sleep(50);
                Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
                Thread.Sleep(50);
                Keyboard.Type(value);

                return TextResult($"Filled {elementName} with \"{value}\"");
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to fill {refId}: {ex.Message}");
            }
        });
    }
}
