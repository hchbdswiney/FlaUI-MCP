using FlaUI.Mcp.Tools;
using Xunit;
using Xunit.Abstractions;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// Tests for the windows_get_properties tool and the snapshot 'properties' option.
/// </summary>
[Collection("TestApps")]
public class GetPropertiesTests
{
    private readonly TestAppFixture _fixture;
    private readonly ITestOutputHelper _output;

    public GetPropertiesTests(TestAppFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task GetProperties_DisabledButton_ReportsIsEnabledFalse()
    {
        await _fixture.NavigateToTab(_fixture.WpfHandle, "Buttons", "Conditional Button");
        var buttonRef = _fixture.FindRefByName(_fixture.WpfHandle, "Conditional Button");
        Assert.NotNull(buttonRef);

        var tool = new GetPropertiesTool(_fixture.Elements);
        var result = await _fixture.CallTool(tool, new { @ref = buttonRef, properties = new[] { "isEnabled", "isOffscreen" } });
        _output.WriteLine(result);

        Assert.Contains("isEnabled: false", result);
        Assert.Contains("isOffscreen:", result);
    }

    [Fact]
    public async Task GetProperties_DumpsAll_WhenNoPropertiesRequested()
    {
        await _fixture.NavigateToTab(_fixture.WpfHandle, "Buttons", "Click Me");
        var buttonRef = _fixture.FindRefByName(_fixture.WpfHandle, "Click Me");
        Assert.NotNull(buttonRef);

        var tool = new GetPropertiesTool(_fixture.Elements);
        var result = await _fixture.CallTool(tool, new { @ref = buttonRef });
        _output.WriteLine(result);

        // A full dump should contain several lines, including a class name.
        Assert.True(result.Split('\n').Length > 3, "Expected multiple property lines in a full dump.");
        Assert.Contains("className:", result);
    }

    [Fact]
    public async Task GetProperties_UnknownName_IsReported()
    {
        await _fixture.NavigateToTab(_fixture.WpfHandle, "Buttons", "Click Me");
        var buttonRef = _fixture.FindRefByName(_fixture.WpfHandle, "Click Me");
        Assert.NotNull(buttonRef);

        var tool = new GetPropertiesTool(_fixture.Elements);
        var result = await _fixture.CallTool(tool, new { @ref = buttonRef, properties = new[] { "notARealProperty" } });
        _output.WriteLine(result);

        Assert.Contains("unknown properties ignored: notARealProperty", result);
    }
}
