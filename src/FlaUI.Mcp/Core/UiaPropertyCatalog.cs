using System.Collections;
using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.AutomationElements.Infrastructure;
using FlaUI.Core.Identifiers;

namespace PlaywrightWindows.Mcp.Core;

/// <summary>
/// Resolves friendly, camelCase property names (e.g. "helpText", "isEnabled",
/// "boundingRectangle") to UI Automation <see cref="PropertyId"/>s and reads their
/// values from elements.
///
/// The catalog is built by reflecting over <see cref="IAutomationElementPropertyIds"/>,
/// so it automatically covers every standard UIA property that the installed FlaUI
/// version knows about - no hand-maintained list to keep in sync.
/// </summary>
public sealed class UiaPropertyCatalog
{
    private static readonly ConcurrentDictionary<AutomationType, UiaPropertyCatalog> Cache = new();

    private readonly Dictionary<string, PropertyId> _byName;

    private UiaPropertyCatalog(Dictionary<string, PropertyId> byName)
    {
        _byName = byName;
    }

    /// <summary>All friendly property names known to this catalog, sorted alphabetically.</summary>
    public IReadOnlyCollection<string> Names => _byName.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Gets (or builds and caches) the catalog for the automation that owns
    /// <paramref name="element"/>.
    /// </summary>
    public static UiaPropertyCatalog For(AutomationElement element)
    {
        var automation = element.FrameworkAutomationElement.Automation;
        return Cache.GetOrAdd(automation.AutomationType, _ => Build(automation));
    }

    private static UiaPropertyCatalog Build(AutomationBase automation)
    {
        var map = new Dictionary<string, PropertyId>(StringComparer.OrdinalIgnoreCase);
        var propertyIds = automation.PropertyLibrary.Element;

        foreach (var prop in typeof(IAutomationElementPropertyIds).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            try
            {
                if (prop.GetValue(propertyIds) is not PropertyId id) continue;
                if (Equals(id, PropertyId.NotSupportedByFramework)) continue;

                var friendly = ToCamelCase(prop.Name);
                map[friendly] = id;
            }
            catch
            {
                // Skip any property id getter that throws for this framework.
            }
        }

        return new UiaPropertyCatalog(map);
    }

    /// <summary>Resolves a friendly property name to its <see cref="PropertyId"/>.</summary>
    public bool TryResolve(string name, out PropertyId propertyId)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return _byName.TryGetValue(name.Trim(), out propertyId!);
        }

        propertyId = PropertyId.NotSupportedByFramework;
        return false;
    }

    /// <summary>
    /// Reads a property value from an element and formats it as a concise string.
    /// Returns false when the property is not supported / not set on the element.
    /// </summary>
    public static bool TryReadValue(AutomationElement element, PropertyId propertyId, out string formatted)
    {
        try
        {
            if (element.FrameworkAutomationElement.TryGetPropertyValue(propertyId, out object? value) && value != null)
            {
                formatted = FormatValue(value);
                return true;
            }
        }
        catch
        {
            // Property read failed for this element - treat as unset.
        }

        formatted = string.Empty;
        return false;
    }

    /// <summary>Renders a UIA property value compactly for agent-friendly output.</summary>
    public static string FormatValue(object? value)
    {
        switch (value)
        {
            case null:
                return string.Empty;
            case bool b:
                return b ? "true" : "false";
            case string s:
                return s;
            case Rectangle rect:
                return $"{rect.X},{rect.Y},{rect.Width},{rect.Height}";
            case Point pt:
                return $"{pt.X},{pt.Y}";
            case AutomationElement:
                return "AutomationElement";
            case AutomationElement[] elements:
                return $"AutomationElement[{elements.Length}]";
            case string[] strings:
                return string.Join(",", strings);
            case IEnumerable enumerable when value is not string:
                return string.Join(",", enumerable.Cast<object?>().Select(o => o?.ToString() ?? string.Empty));
            default:
                return value.ToString() ?? string.Empty;
        }
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsLower(name[0])) return name;
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }
}
