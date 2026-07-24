using System.Linq;
using FlaUI.UIA3;
using PlaywrightWindows.Mcp.Core;
using Xunit;

namespace FlaUI.Mcp.Tests;

/// <summary>
/// Unit tests for the reflection-built UIA property catalog.
/// Uses the live desktop element (no app launch required) to build the catalog.
/// </summary>
[Collection("Desktop")]
public class UiaPropertyCatalogTests
{
    [Fact]
    public void Resolves_Common_Friendly_Names()
    {
        using var automation = new UIA3Automation();
        var desktop = automation.GetDesktop();
        var catalog = UiaPropertyCatalog.For(desktop);

        Assert.True(catalog.TryResolve("isEnabled", out _));
        Assert.True(catalog.TryResolve("isOffscreen", out _));
        Assert.True(catalog.TryResolve("helpText", out _));
        Assert.True(catalog.TryResolve("className", out _));
        Assert.True(catalog.TryResolve("boundingRectangle", out _));
    }

    [Fact]
    public void Resolution_Is_Case_Insensitive()
    {
        using var automation = new UIA3Automation();
        var desktop = automation.GetDesktop();
        var catalog = UiaPropertyCatalog.For(desktop);

        Assert.True(catalog.TryResolve("ISENABLED", out var a));
        Assert.True(catalog.TryResolve("isenabled", out var b));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Unknown_Name_Returns_False()
    {
        using var automation = new UIA3Automation();
        var desktop = automation.GetDesktop();
        var catalog = UiaPropertyCatalog.For(desktop);

        Assert.False(catalog.TryResolve("notARealProperty", out _));
        Assert.False(catalog.TryResolve("", out _));
    }

    [Fact]
    public void Names_Are_Populated()
    {
        using var automation = new UIA3Automation();
        var desktop = automation.GetDesktop();
        var catalog = UiaPropertyCatalog.For(desktop);

        Assert.NotEmpty(catalog.Names);
        Assert.Contains("isEnabled", catalog.Names);
    }

    [Fact]
    public void FormatValue_Renders_Compactly()
    {
        Assert.Equal("true", UiaPropertyCatalog.FormatValue(true));
        Assert.Equal("false", UiaPropertyCatalog.FormatValue(false));
        Assert.Equal("hello", UiaPropertyCatalog.FormatValue("hello"));
        Assert.Equal("1,2,3", UiaPropertyCatalog.FormatValue(new[] { "1", "2", "3" }));
        Assert.Equal("1,2,3", UiaPropertyCatalog.FormatValue(new[] { 1, 2, 3 }));
        Assert.Equal(string.Empty, UiaPropertyCatalog.FormatValue(null));
    }
}
