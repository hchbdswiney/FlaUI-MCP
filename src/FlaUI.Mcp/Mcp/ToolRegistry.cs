using System.Text.Json;

namespace FlaUI.Mcp;

/// <summary>
/// Registry for MCP tools - maps tool names to handlers
/// </summary>
public class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new();
    private readonly TimeSpan _toolTimeout;

    public ToolRegistry(TimeSpan? toolTimeout = null)
    {
        _toolTimeout = toolTimeout ?? TimeSpan.FromSeconds(30);
        if (_toolTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(toolTimeout), "Tool timeout must be greater than zero.");
        }
    }

    public void RegisterTool(ITool tool)
    {
        _tools[tool.Name] = tool;
    }

    public List<McpTool> GetToolDefinitions()
    {
        return _tools.Values.Select(t => t.GetDefinition()).ToList();
    }

    public async Task<McpToolResult> ExecuteToolAsync(string name, JsonElement? arguments)
    {
        if (!_tools.TryGetValue(name, out var tool))
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Unknown tool: {name}" }
                },
                IsError = true
            };
        }

        try
        {
            var toolTask = Task.Run(() => tool.ExecuteAsync(arguments));
            var timeoutTask = Task.Delay(_toolTimeout);

            if (await Task.WhenAny(toolTask, timeoutTask) == timeoutTask)
            {
                _ = toolTask.ContinueWith(
                    task => { _ = task.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted);

                return new McpToolResult
                {
                    Content = new List<McpContent>
                    {
                        new()
                        {
                            Type = "text",
                            Text = $"Tool '{name}' timed out after {(int)_toolTimeout.TotalMilliseconds}ms. " +
                                   "A modal dialog or blocked UI Automation provider may still be running in the background. " +
                                   "Dismiss the blocking UI and retry the request."
                        }
                    },
                    IsError = true
                };
            }

            return await toolTask;
        }
        catch (Exception ex)
        {
            return new McpToolResult
            {
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = $"Error: {ex.Message}" }
                },
                IsError = true
            };
        }
    }
}

/// <summary>
/// Interface for MCP tools
/// </summary>
public interface ITool
{
    string Name { get; }
    McpTool GetDefinition();
    Task<McpToolResult> ExecuteAsync(JsonElement? arguments);
}

/// <summary>
/// Base class for tools with common utilities
/// </summary>
public abstract class ToolBase : ITool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract object InputSchema { get; }

    public McpTool GetDefinition() => new()
    {
        Name = Name,
        Description = Description,
        InputSchema = InputSchema
    };

    public abstract Task<McpToolResult> ExecuteAsync(JsonElement? arguments);

    protected static McpToolResult TextResult(string text) => new()
    {
        Content = new List<McpContent>
        {
            new() { Type = "text", Text = text }
        }
    };

    protected static McpToolResult ErrorResult(string message) => new()
    {
        Content = new List<McpContent>
        {
            new() { Type = "text", Text = message }
        },
        IsError = true
    };

    protected static McpToolResult ImageResult(byte[] imageData, string mimeType = "image/png") => new()
    {
        Content = new List<McpContent>
        {
            new() 
            { 
                Type = "image", 
                Data = Convert.ToBase64String(imageData),
                MimeType = mimeType
            }
        }
    };

    protected T? GetArgument<T>(JsonElement? arguments, string name)
    {
        if (arguments == null) return default;
        if (!arguments.Value.TryGetProperty(name, out var prop)) return default;
        return JsonSerializer.Deserialize<T>(prop.GetRawText(), McpProtocol.JsonOptions);
    }

    protected string? GetStringArgument(JsonElement? arguments, string name)
    {
        if (arguments == null) return null;
        if (!arguments.Value.TryGetProperty(name, out var prop)) return null;
        return prop.GetString();
    }

    protected bool GetBoolArgument(JsonElement? arguments, string name, bool defaultValue = false)
    {
        if (arguments == null) return defaultValue;
        if (!arguments.Value.TryGetProperty(name, out var prop)) return defaultValue;
        return prop.GetBoolean();
    }

    protected int? GetIntArgument(JsonElement? arguments, string name)
    {
        if (arguments == null) return null;
        if (!arguments.Value.TryGetProperty(name, out var prop)) return null;

        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(prop.GetString(), out var value) => value,
            _ => null
        };
    }

    protected int GetIntArgument(JsonElement? arguments, string name, int defaultValue)
        => GetIntArgument(arguments, name) ?? defaultValue;

    protected string[]? GetStringArrayArgument(JsonElement? arguments, string name)
    {
        if (arguments == null) return null;
        if (!arguments.Value.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind != JsonValueKind.Array) return null;

        var values = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            var value = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value!);
            }
        }

        return values.ToArray();
    }

    /// <summary>
    /// Run a synchronous UI Automation operation with a hard wall-clock bound so a
    /// stalled cross-process call fails fast (with a clear, retryable error) instead
    /// of hanging until the outer tool timeout. When <paramref name="timeoutMs"/> is
    /// null or non-positive the work runs without an extra bound.
    /// </summary>
    protected static async Task<McpToolResult> RunBoundedAsync(int? timeoutMs, string toolName, Func<McpToolResult> work)
    {
        if (timeoutMs is null or <= 0)
        {
            return await Task.Run(work).ConfigureAwait(false);
        }

        var workTask = Task.Run(work);
        if (await Task.WhenAny(workTask, Task.Delay(timeoutMs.Value)).ConfigureAwait(false) == workTask)
        {
            return await workTask.ConfigureAwait(false);
        }

        // Observe any later fault so it does not surface as an unobserved exception.
        _ = workTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);

        return ErrorResult(
            $"{toolName} timed out after {timeoutMs.Value}ms. The target may be blocked by a modal " +
            "dialog or a busy UI Automation provider. Resolve the modal with windows_get_active_modal " +
            "and interact with its scoped refs, or narrow the snapshot scope, then retry.");
    }
}
