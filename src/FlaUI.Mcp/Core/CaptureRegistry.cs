namespace FlaUI.Mcp.Core;

/// <summary>
/// Self-describing metadata for a screenshot so any pixel in the image can be
/// mapped back to a physical screen coordinate. With the process running
/// Per-Monitor-DPI-Aware v2 and capturing at device resolution, image pixels map
/// 1:1 to screen pixels, so <see cref="ScreenWidth"/> equals <see cref="WidthPx"/>.
/// </summary>
public sealed record CaptureMetadata(
    int OriginX,
    int OriginY,
    int WidthPx,
    int HeightPx,
    int ScreenWidth,
    int ScreenHeight,
    int Dpi,
    double Scale);

/// <summary>
/// Remembers the most recent screenshot's capture metadata per anchor so
/// windows_click_point can convert image-space pixels back to screen coordinates.
/// Keyed by window handle; full-screen captures use <see cref="FullScreenKey"/>.
/// </summary>
public sealed class CaptureRegistry
{
    public const string FullScreenKey = "__fullscreen__";

    private readonly Dictionary<string, CaptureMetadata> _captures = new();
    private readonly object _gate = new();

    public void Record(string key, CaptureMetadata metadata)
    {
        lock (_gate)
        {
            _captures[key] = metadata;
        }
    }

    public CaptureMetadata? Get(string key)
    {
        lock (_gate)
        {
            return _captures.TryGetValue(key, out var value) ? value : null;
        }
    }
}
