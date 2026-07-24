using System.Text;
using System.Text.Json;
using PlaywrightWindows.Mcp.Core;

namespace PlaywrightWindows.Mcp.Tools;

/// <summary>
/// Inspect UI Automation properties of a single element by ref.
/// </summary>
public class GetPropertiesTool : ToolBase
{
    private readonly ElementRegistry _elementRegistry;

    public GetPropertiesTool(ElementRegistry elementRegistry)
    {
        _elementRegistry = elementRegistry;
    }

    public override string Name => "windows_get_properties";

    public override string Description =>
        "Inspect UI Automation properties of a single element (by ref). " +
        "Pass a 'properties' list of friendly names to read specific ones, or omit it to dump " +
        "all supported properties. Useful for diagnosing why an element is hidden or not " +
        "interactable (e.g. isEnabled, isOffscreen, isKeyboardFocusable, boundingRectangle). " +
        "Note: only standard UIA properties are available - vendor-specific .NET control " +
        "properties (e.g. Infragistics UltraTab.Visible) are not exposed via UI Automation.";

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
            properties = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Optional list of friendly UIA property names to read (e.g. helpText, className, " +
                    "isEnabled, isOffscreen, isKeyboardFocusable, boundingRectangle, itemStatus). " +
                    "If omitted, all supported properties are returned."
            },
            includeUnset = new
            {
                type = "boolean",
                description = "When true, also list requested properties that are not supported/unset. Default false."
            }
        },
        required = new[] { "ref" }
    };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var refId = GetStringArgument(arguments, "ref");
        if (string.IsNullOrEmpty(refId))
        {
            return Task.FromResult(ErrorResult("Missing required argument: ref"));
        }

        var element = _elementRegistry.GetElement(refId);
        if (element == null)
        {
            return Task.FromResult(ErrorResult($"Element not found: {refId}. Run windows_snapshot to refresh element refs."));
        }

        var requested = GetStringArrayArgument(arguments, "properties");
        var includeUnset = GetBoolArgument(arguments, "includeUnset", false);

        try
        {
            var catalog = UiaPropertyCatalog.For(element);
            var sb = new StringBuilder();
            var unknown = new List<string>();

            IEnumerable<string> names = requested is { Length: > 0 } ? requested : catalog.Names;

            foreach (var name in names)
            {
                if (!catalog.TryResolve(name, out var propertyId))
                {
                    unknown.Add(name);
                    continue;
                }

                if (UiaPropertyCatalog.TryReadValue(element, propertyId, out var value))
                {
                    sb.AppendLine($"{name}: {value}");
                }
                else if (includeUnset)
                {
                    sb.AppendLine($"{name}: (unset)");
                }
            }

            if (unknown.Count > 0)
            {
                sb.AppendLine($"unknown properties ignored: {string.Join(", ", unknown)}");
            }

            var output = sb.ToString().TrimEnd();
            return Task.FromResult(TextResult(output.Length > 0 ? output : "(no properties available)"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorResult($"Failed to get properties from {refId}: {ex.Message}"));
        }
    }
}
