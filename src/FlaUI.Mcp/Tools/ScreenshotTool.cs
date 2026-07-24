using System.Text.Json;
using FlaUI.Core.Capturing;
using FlaUI.Mcp.Core;

namespace FlaUI.Mcp.Tools;

/// <summary>
/// Take a screenshot
/// </summary>
public class ScreenshotTool : ToolBase
{
    private readonly SessionManager _sessionManager;
    private readonly ElementRegistry _elementRegistry;
    private readonly CaptureRegistry _captureRegistry;

    public ScreenshotTool(SessionManager sessionManager, ElementRegistry elementRegistry, CaptureRegistry captureRegistry)
    {
        _sessionManager = sessionManager;
        _elementRegistry = elementRegistry;
        _captureRegistry = captureRegistry;
    }

    public override string Name => "windows_screenshot";

    public override string Description =>
        "Take a screenshot of a window or specific element. Returns the image as base64-encoded PNG " +
        "plus self-describing capture metadata (origin in virtual-screen coordinates, pixel size, DPI/scale). " +
        "The metadata lets windows_click_point map an image pixel back to a physical screen coordinate - " +
        "use space=\"image\" on the most recent capture, or space=\"normalized\" for resolution-independent fractions.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            handle = new
            {
                type = "string",
                description = "Window handle. If omitted, captures the foreground window."
            },
            @ref = new
            {
                type = "string",
                description = "Element ref to capture. If omitted, captures the whole window."
            },
            fullScreen = new
            {
                type = "boolean",
                description = "Capture the entire screen (default: false)"
            },
            background = new
            {
                type = "boolean",
                description = "Use native background window capture for a window handle, falling back to normal capture if unavailable (default: false)"
            },
            savePath = new
            {
                type = "string",
                description = "Absolute local .png file path to save the screenshot. UNC and device paths are rejected."
            },
            overwrite = new
            {
                type = "boolean",
                description = "Allow savePath to replace an existing file (default: false)"
            }
        }
    };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var handle = GetStringArgument(arguments, "handle");
        var refId = GetStringArgument(arguments, "ref");
        var fullScreen = GetBoolArgument(arguments, "fullScreen", false);
        var background = GetBoolArgument(arguments, "background", false);
        var savePath = GetStringArgument(arguments, "savePath");
        var overwrite = GetBoolArgument(arguments, "overwrite", false);

        if (!TryNormalizeSavePath(savePath, overwrite, out var normalizedSavePath, out var pathError))
        {
            return Task.FromResult(ErrorResult(pathError));
        }

        try
        {
            CaptureImage capture;

            // Anchor for metadata: origin in virtual-screen coordinates, DPI source
            // window, and the CaptureRegistry key used by windows_click_point.
            int originX;
            int originY;
            IntPtr dpiHwnd = IntPtr.Zero;
            string metadataKey;

            if (background && (fullScreen || !string.IsNullOrEmpty(refId) || string.IsNullOrEmpty(handle)))
            {
                return Task.FromResult(ErrorResult("background capture requires a window handle and cannot be combined with ref or fullScreen"));
            }

            if (fullScreen)
            {
                capture = Capture.Screen();
                originX = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
                originY = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
                dpiHwnd = Native.GetForegroundWindow();
                metadataKey = CaptureRegistry.FullScreenKey;
            }
            else if (!string.IsNullOrEmpty(refId))
            {
                var element = _elementRegistry.GetElement(refId);
                if (element == null)
                {
                    return Task.FromResult(ErrorResult($"Element not found: {refId}"));
                }
                capture = Capture.Element(element);
                var bounds = element.BoundingRectangle;
                originX = bounds.Left;
                originY = bounds.Top;
                try { element.Properties.NativeWindowHandle.TryGetValue(out dpiHwnd); } catch { }
                metadataKey = refId!;
            }
            else if (!string.IsNullOrEmpty(handle))
            {
                var window = _sessionManager.GetWindow(handle);
                if (window == null)
                {
                    return Task.FromResult(ErrorResult($"Window not found: {handle}"));
                }

                var hwnd = _sessionManager.GetNativeHandle(handle);
                dpiHwnd = hwnd;
                if (hwnd != IntPtr.Zero && Native.GetWindowRect(hwnd, out var rect))
                {
                    originX = rect.Left;
                    originY = rect.Top;
                }
                else
                {
                    var bounds = window.BoundingRectangle;
                    originX = bounds.Left;
                    originY = bounds.Top;
                }
                metadataKey = handle!;

                if (background && NativeWindowCapture.TryCaptureWindow(window, out var backgroundImage, out _))
                {
                    RecordMetadata(metadataKey, originX, originY, dpiHwnd, backgroundImage);
                    return Task.FromResult(BuildScreenshotResult(backgroundImage, normalizedSavePath, overwrite,
                        _captureRegistry.Get(metadataKey)));
                }

                capture = Capture.Element(window);
            }
            else
            {
                // Capture foreground window
                var focusedElement = _sessionManager.Automation.FocusedElement();
                if (focusedElement == null)
                {
                    return Task.FromResult(ErrorResult("No focused window found"));
                }

                // Walk up to find the window
                var current = focusedElement;
                while (current != null && current.Properties.ControlType.ValueOrDefault != FlaUI.Core.Definitions.ControlType.Window)
                {
                    current = current.Parent;
                }

                if (current == null)
                {
                    return Task.FromResult(ErrorResult("Could not find window for focused element"));
                }

                capture = Capture.Element(current);
                var bounds = current.BoundingRectangle;
                originX = bounds.Left;
                originY = bounds.Top;
                try { current.Properties.NativeWindowHandle.TryGetValue(out dpiHwnd); } catch { }
                metadataKey = CaptureRegistry.FullScreenKey;
            }

            byte[] imageData;
            using (capture)
            {
                using var stream = new MemoryStream();
                capture.Bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                imageData = stream.ToArray();
            }

            RecordMetadata(metadataKey, originX, originY, dpiHwnd, imageData);

            return Task.FromResult(BuildScreenshotResult(imageData, normalizedSavePath, overwrite,
                _captureRegistry.Get(metadataKey)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorResult($"Failed to capture screenshot: {ex.Message}"));
        }
    }

    private void RecordMetadata(string key, int originX, int originY, IntPtr dpiHwnd, byte[] imageData)
    {
        int widthPx;
        int heightPx;
        try
        {
            using var ms = new MemoryStream(imageData);
            using var bmp = System.Drawing.Image.FromStream(ms);
            widthPx = bmp.Width;
            heightPx = bmp.Height;
        }
        catch
        {
            return;
        }

        var dpi = dpiHwnd != IntPtr.Zero ? Native.GetWindowDpi(dpiHwnd) : 96;
        var scale = dpi / 96.0;

        // Process is Per-Monitor-DPI-Aware v2 and captures at device resolution, so
        // image pixels map 1:1 to physical screen units.
        _captureRegistry.Record(key, new CaptureMetadata(
            originX, originY, widthPx, heightPx, widthPx, heightPx, dpi, scale));
    }

    internal static bool TryNormalizeSavePath(string? savePath, bool overwrite, out string? normalizedPath, out string error)
    {
        normalizedPath = null;
        error = "";

        if (string.IsNullOrWhiteSpace(savePath))
        {
            return true;
        }

        if (!IsPathFullyQualified(savePath))
        {
            error = $"savePath must be an absolute local path: {savePath}";
            return false;
        }

        if (savePath.StartsWith(@"\\") || savePath.StartsWith(@"\\?\") || savePath.StartsWith(@"\\.\"))
        {
            error = "savePath must be a local drive path; UNC and device paths are not allowed";
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(savePath);
        }
        catch (Exception ex)
        {
            error = $"savePath is invalid: {ex.Message}";
            return false;
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            error = "savePath must end with .png";
            return false;
        }

        if (File.Exists(fullPath) && !overwrite)
        {
            error = $"savePath already exists; pass overwrite=true to replace it: {fullPath}";
            return false;
        }

        normalizedPath = fullPath;
        return true;
    }

    // Mirrors .NET's Path.IsPathFullyQualified for Windows paths, which is unavailable on .NET Framework 4.8.
    private static bool IsPathFullyQualified(string path)
    {
        if (path.Length < 2)
        {
            return false;
        }

        if (IsDirectorySeparator(path[0]))
        {
            // UNC (\\) or device (\\?\, \\.\) paths start with two separators (or \?).
            return path[1] == '?' || IsDirectorySeparator(path[1]);
        }

        // Drive-rooted absolute path, e.g. "C:\...".
        return path.Length >= 3
            && path[1] == Path.VolumeSeparatorChar
            && IsDirectorySeparator(path[2])
            && IsValidDriveChar(path[0]);
    }

    private static bool IsDirectorySeparator(char c)
        => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;

    private static bool IsValidDriveChar(char value)
        => (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

    private static McpToolResult BuildScreenshotResult(byte[] imageData, string? savePath, bool overwrite, CaptureMetadata? metadata)
    {
        var content = new List<McpContent>();

        if (metadata != null)
        {
            content.Add(new McpContent { Type = "text", Text = FormatMetadata(metadata) });
        }

        if (!string.IsNullOrEmpty(savePath))
        {
            try
            {
                var directory = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var tempPath = Path.Combine(directory ?? Directory.GetCurrentDirectory(), $"{Path.GetFileName(savePath)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllBytes(tempPath, imageData);
                    if (overwrite && File.Exists(savePath))
                    {
                        File.Delete(savePath);
                    }
                    File.Move(tempPath, savePath);
                }
                finally
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
            }
            catch (Exception ex)
            {
                return ErrorResult($"Failed to save screenshot to {savePath}: {ex.Message}");
            }

            content.Add(new McpContent { Type = "text", Text = $"Screenshot saved to {savePath}" });
        }

        content.Add(new McpContent { Type = "image", Data = Convert.ToBase64String(imageData), MimeType = "image/png" });

        return new McpToolResult { Content = content };
    }

    private static string FormatMetadata(CaptureMetadata m) =>
        "capture: " + JsonSerializer.Serialize(new
        {
            origin = new { x = m.OriginX, y = m.OriginY },
            widthPx = m.WidthPx,
            heightPx = m.HeightPx,
            screenWidth = m.ScreenWidth,
            screenHeight = m.ScreenHeight,
            dpi = m.Dpi,
            scale = m.Scale
        }, McpProtocol.JsonOptions) +
        "\nUse windows_click_point with space=\"image\" (pixels within this capture) or " +
        "space=\"normalized\" (0..1 fractions) to click a point you see here.";
}
