using System.Diagnostics;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace FlaUI.Mcp.Core;

/// <summary>
/// Options that bound how much of a UI Automation tree a snapshot walks.
/// Used to produce shallow snapshots that stay fast on large grids and deep
/// modal window trees instead of timing out.
/// </summary>
public sealed record SnapshotOptions
{
    /// <summary>Maximum tree depth to descend (0 = window only). Default 10.</summary>
    public int MaxDepth { get; init; } = 10;

    /// <summary>
    /// Maximum number of children emitted per node before truncating with a
    /// marker. Null = unlimited. Useful for large grids/lists.
    /// </summary>
    public int? MaxChildrenPerNode { get; init; }

    /// <summary>
    /// Maximum total number of elements to emit across the whole snapshot before
    /// stopping with a marker. Null = unlimited. A hard cap on total work.
    /// </summary>
    public int? MaxElements { get; init; }

    /// <summary>
    /// Optional wall-clock budget for building the snapshot. When exceeded the
    /// walk stops and a marker is emitted. Null = no time limit.
    /// </summary>
    public TimeSpan? TimeBudget { get; init; }

    /// <summary>
    /// Optional list of friendly UIA property names (e.g. "helpText", "className",
    /// "isKeyboardFocusable") to emit inline on each element as [name=value].
    /// Null/empty = no extra properties (default). Unknown names are ignored with a note.
    /// </summary>
    public IReadOnlyList<string>? Properties { get; init; }

    /// <summary>Default options preserving the original full-depth behavior.</summary>
    public static SnapshotOptions Default { get; } = new();
}

/// <summary>
/// Builds agent-friendly accessibility snapshots from UI Automation trees
/// </summary>
public class SnapshotBuilder
{
    private readonly ElementRegistry _elementRegistry;
    private readonly int _maxDepth;

    public SnapshotBuilder(ElementRegistry elementRegistry, int maxDepth = 10)
    {
        _elementRegistry = elementRegistry;
        _maxDepth = maxDepth;
    }

    public string BuildSnapshot(string windowHandle, AutomationElement root)
    {
        return BuildSnapshot(windowHandle, root, new SnapshotOptions { MaxDepth = _maxDepth });
    }

    public string BuildSnapshot(string windowHandle, AutomationElement root, SnapshotOptions options)
    {
        // Clear previous elements for this window
        _elementRegistry.ClearWindow(windowHandle);

        var sb = new StringBuilder();
        var state = new SnapshotState(options);
        ResolveRequestedProperties(root, state);
        BuildElementSnapshot(sb, windowHandle, root, 0, state);

        if (state.Truncated)
        {
            sb.AppendLine(
                "- ... snapshot truncated by limits (increase maxDepth/maxChildren/maxElements " +
                "or target a specific element to see more)");
        }

        if (state.UnknownProperties.Count > 0)
        {
            sb.AppendLine(
                $"- ... unknown properties ignored: {string.Join(", ", state.UnknownProperties)}");
        }

        return sb.ToString();
    }

    private static void ResolveRequestedProperties(AutomationElement root, SnapshotState state)
    {
        var requested = state.Options.Properties;
        if (requested == null || requested.Count == 0) return;

        UiaPropertyCatalog catalog;
        try
        {
            catalog = UiaPropertyCatalog.For(root);
        }
        catch
        {
            return;
        }

        foreach (var name in requested)
        {
            if (catalog.TryResolve(name, out var propertyId))
            {
                state.ResolvedProperties.Add((name.Trim(), propertyId));
            }
            else if (!string.IsNullOrWhiteSpace(name))
            {
                state.UnknownProperties.Add(name.Trim());
            }
        }
    }

    /// <summary>Mutable bookkeeping for a single snapshot build.</summary>
    private sealed class SnapshotState
    {
        public SnapshotState(SnapshotOptions options)
        {
            Options = options;
            Stopwatch = options.TimeBudget.HasValue ? Stopwatch.StartNew() : null;
        }

        public SnapshotOptions Options { get; }
        public int EmittedCount { get; set; }
        public bool Truncated { get; set; }
        public Stopwatch? Stopwatch { get; }
        public List<(string Label, FlaUI.Core.Identifiers.PropertyId Id)> ResolvedProperties { get; } = new();
        public List<string> UnknownProperties { get; } = new();

        public bool ElementBudgetReached =>
            Options.MaxElements.HasValue && EmittedCount >= Options.MaxElements.Value;

        public bool TimeBudgetExceeded =>
            Stopwatch != null && Options.TimeBudget.HasValue && Stopwatch.Elapsed >= Options.TimeBudget.Value;
    }

    private void BuildElementSnapshot(
        StringBuilder sb, string windowHandle, AutomationElement element, int depth, SnapshotState state)
    {
        if (depth > state.Options.MaxDepth) return;

        if (state.ElementBudgetReached || state.TimeBudgetExceeded)
        {
            state.Truncated = true;
            return;
        }

        // Skip elements with no meaningful content
        var name = GetElementName(element);
        var role = GetElementRole(element);
        
        // Skip some noise elements, but keep elements with names or important roles
        if (ShouldSkipElement(element, name, role)) return;

        // Register element and get ref
        var refId = _elementRegistry.Register(windowHandle, element);
        state.EmittedCount++;

        // Build the line
        var indent = new string(' ', depth * 2);
        var line = BuildElementLine(element, refId, name, role, state);
        sb.AppendLine($"{indent}- {line}");

        // Don't descend past the depth limit
        if (depth >= state.Options.MaxDepth) return;

        // Process children
        try
        {
            var children = element.FindAllChildren();
            var childIndent = new string(' ', (depth + 1) * 2);
            var limit = state.Options.MaxChildrenPerNode;
            var processed = 0;

            foreach (var child in children)
            {
                if (state.ElementBudgetReached || state.TimeBudgetExceeded)
                {
                    state.Truncated = true;
                    break;
                }

                if (limit.HasValue && processed >= limit.Value)
                {
                    var remaining = children.Length - processed;
                    sb.AppendLine(
                        $"{childIndent}- ... ({remaining} more children not shown; increase maxChildren)");
                    state.Truncated = true;
                    break;
                }

                BuildElementSnapshot(sb, windowHandle, child, depth + 1, state);
                processed++;
            }
        }
        catch
        {
            // Some elements throw when accessing children
        }
    }

    private string BuildElementLine(AutomationElement element, string refId, string? name, string role, SnapshotState state)
    {
        var parts = new List<string>();

        // Role first
        parts.Add(role);

        // Name in quotes if present
        if (!string.IsNullOrEmpty(name))
        {
            parts.Add($"\"{EscapeName(name)}\"");
        }

        // Ref
        parts.Add($"[ref={refId}]");

        // State indicators
        var states = GetStateIndicators(element);
        if (states.Count > 0)
        {
            parts.AddRange(states.Select(s => $"[{s}]"));
        }

        // Requested extra UIA properties (opt-in)
        foreach (var (label, propertyId) in state.ResolvedProperties)
        {
            if (UiaPropertyCatalog.TryReadValue(element, propertyId, out var value))
            {
                parts.Add($"[{label}={EscapeName(value)}]");
            }
        }

        return string.Join(" ", parts);
    }

    private string GetElementRole(AutomationElement element)
    {
        try
        {
            var controlType = element.Properties.ControlType.ValueOrDefault;
            return controlType switch
            {
                ControlType.Button => "button",
                ControlType.Edit => "textbox",
                ControlType.Text => "text",
                ControlType.CheckBox => "checkbox",
                ControlType.RadioButton => "radio",
                ControlType.ComboBox => "combobox",
                ControlType.List => "list",
                ControlType.ListItem => "listitem",
                ControlType.Menu => "menu",
                ControlType.MenuItem => "menuitem",
                ControlType.MenuBar => "menubar",
                ControlType.Tree => "tree",
                ControlType.TreeItem => "treeitem",
                ControlType.Tab => "tablist",
                ControlType.TabItem => "tab",
                ControlType.Table => "table",
                ControlType.DataItem => "row",
                ControlType.Header => "header",
                ControlType.HeaderItem => "columnheader",
                ControlType.Slider => "slider",
                ControlType.Spinner => "spinbutton",
                ControlType.ProgressBar => "progressbar",
                ControlType.Hyperlink => "link",
                ControlType.Image => "image",
                ControlType.Pane => "group",
                ControlType.Group => "group",
                ControlType.Window => "window",
                ControlType.Document => "document",
                ControlType.ToolBar => "toolbar",
                ControlType.ToolTip => "tooltip",
                ControlType.ScrollBar => "scrollbar",
                ControlType.StatusBar => "status",
                ControlType.Separator => "separator",
                ControlType.Thumb => "thumb",
                ControlType.TitleBar => "titlebar",
                ControlType.DataGrid => "grid",
                ControlType.Custom => "custom",
                _ => "element"
            };
        }
        catch
        {
            return "element";
        }
    }

    private string? GetElementName(AutomationElement element)
    {
        try
        {
            var name = element.Properties.Name.ValueOrDefault;
            if (!string.IsNullOrWhiteSpace(name)) return name;

            // Try automation ID as fallback for identification
            var automationId = element.Properties.AutomationId.ValueOrDefault;
            if (!string.IsNullOrWhiteSpace(automationId) && automationId.Length < 50)
            {
                return $"[{automationId}]";
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private List<string> GetStateIndicators(AutomationElement element)
    {
        var states = new List<string>();

        try
        {
            if (!element.Properties.IsEnabled.ValueOrDefault)
                states.Add("disabled");

            if (element.Properties.IsOffscreen.ValueOrDefault)
                states.Add("offscreen");

            // Check for readonly (ValuePattern)
            if (element.Patterns.Value.IsSupported)
            {
                var valuePattern = element.Patterns.Value.Pattern;
                if (valuePattern.IsReadOnly.ValueOrDefault)
                    states.Add("readonly");
            }

            // Check toggle state
            if (element.Patterns.Toggle.IsSupported)
            {
                var toggleState = element.Patterns.Toggle.Pattern.ToggleState.ValueOrDefault;
                if (toggleState == ToggleState.On)
                    states.Add("checked");
                else if (toggleState == ToggleState.Indeterminate)
                    states.Add("indeterminate");
            }

            // Check selection state
            if (element.Patterns.SelectionItem.IsSupported)
            {
                if (element.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault)
                    states.Add("selected");
            }

            // Check expanded state
            if (element.Patterns.ExpandCollapse.IsSupported)
            {
                var expandState = element.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.ValueOrDefault;
                if (expandState == ExpandCollapseState.Expanded)
                    states.Add("expanded");
                else if (expandState == ExpandCollapseState.Collapsed)
                    states.Add("collapsed");
            }
        }
        catch
        {
            // Ignore state query errors
        }

        return states;
    }

    private bool ShouldSkipElement(AutomationElement element, string? name, string role)
    {
        // Always include named elements
        if (!string.IsNullOrEmpty(name)) return false;

        // Always include actionable element types
        if (role is "button" or "textbox" or "checkbox" or "radio" or "combobox" 
            or "listitem" or "menuitem" or "tab" or "treeitem" or "link" or "slider")
        {
            return false;
        }

        // Include structural elements that might contain others
        if (role is "window" or "group" or "list" or "tree" or "tablist" 
            or "menu" or "menubar" or "toolbar" or "grid" or "table")
        {
            return false;
        }

        // Skip decorative/structural elements without names
        if (role is "element" or "thumb" or "scrollbar" or "separator" or "titlebar")
        {
            return true;
        }

        return false;
    }

    private string EscapeName(string name)
    {
        return name
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "");
    }
}
