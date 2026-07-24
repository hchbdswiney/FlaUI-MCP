using FlaUI.Mcp.Core;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class CaptureRegistryTests
{
    [Fact]
    public void Get_ReturnsNull_WhenNoCaptureRecorded()
    {
        var registry = new CaptureRegistry();
        Assert.Null(registry.Get("w1"));
        Assert.Null(registry.Get(CaptureRegistry.FullScreenKey));
    }

    [Fact]
    public void Record_ThenGet_ReturnsMetadata()
    {
        var registry = new CaptureRegistry();
        var meta = new CaptureMetadata(-100, 200, 800, 600, 800, 600, 144, 1.5);

        registry.Record("w5", meta);

        Assert.Same(meta, registry.Get("w5"));
    }

    [Fact]
    public void Record_OverwritesPreviousCapture_ForSameKey()
    {
        var registry = new CaptureRegistry();
        registry.Record("w1", new CaptureMetadata(0, 0, 100, 100, 100, 100, 96, 1.0));
        var latest = new CaptureMetadata(10, 10, 200, 200, 200, 200, 96, 1.0);

        registry.Record("w1", latest);

        Assert.Same(latest, registry.Get("w1"));
    }

    [Fact]
    public void FullScreenAndWindowKeys_AreTrackedIndependently()
    {
        var registry = new CaptureRegistry();
        var full = new CaptureMetadata(-1920, 0, 1920, 1080, 1920, 1080, 96, 1.0);
        var window = new CaptureMetadata(100, 100, 400, 300, 400, 300, 120, 1.25);

        registry.Record(CaptureRegistry.FullScreenKey, full);
        registry.Record("w3", window);

        Assert.Same(full, registry.Get(CaptureRegistry.FullScreenKey));
        Assert.Same(window, registry.Get("w3"));
    }
}
