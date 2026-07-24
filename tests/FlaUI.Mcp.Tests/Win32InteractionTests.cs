using FlaUI.Mcp.Core;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class Win32InteractionTests
{
    [Theory]
    [InlineData("&Yes", "Yes")]
    [InlineData("&No", "No")]
    [InlineData("Save &As", "Save As")]
    [InlineData("Plain", "Plain")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("Fish && Chips", "Fish & Chips")]
    [InlineData("&&Leading", "&Leading")]
    public void StripMnemonic_RemovesSingleAmpersand_KeepsDoubled(string? input, string expected)
    {
        Assert.Equal(expected, Win32Interaction.StripMnemonic(input));
    }

    [Theory]
    [InlineData("left", "Left")]
    [InlineData("LEFT", "Left")]
    [InlineData("right", "Right")]
    [InlineData("middle", "Middle")]
    [InlineData("unknown", "Left")]
    [InlineData(null, "Left")]
    public void ParseButton_MapsKnownButtons_DefaultsLeft(string? input, string expected)
    {
        Assert.Equal(expected, Win32Interaction.ParseButton(input).ToString());
    }
}
