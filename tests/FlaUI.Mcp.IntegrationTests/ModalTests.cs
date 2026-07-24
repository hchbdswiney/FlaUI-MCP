using System.Diagnostics;
using FlaUI.Core.Input;
using FlaUI.Mcp.Tools;
using Xunit.Abstractions;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// Tests for modal-dialog handling: Win32-backed window enumeration that survives
/// a blocking modal, resolving the active modal via windows_get_active_modal, and
/// deterministic UIA-pattern interaction with modal controls (no SendInput).
/// </summary>
[Collection("TestApps")]
public class ModalTests
{
    private readonly TestAppFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ModalTests(TestAppFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task GetActiveModal_ResolvesModalOverLargeGrid_Fast_AndScoped()
    {
        // Realize the Stress tab (large 1000-row grid) then open a small modal over it.
        await _fixture.NavigateToTab(_fixture.WinFormsHandle, "Stress", "Open Modal Over Grid");
        var openRef = _fixture.FindRefByName(_fixture.WinFormsHandle, "Open Modal Over Grid");
        Assert.NotNull(openRef);

        await OpenModalAsync(openRef!);
        try
        {
            var sw = Stopwatch.StartNew();
            var modalTool = new GetActiveModalTool(_fixture.Session, _fixture.Elements);
            var result = await PollModalAsync(modalTool, new { ownerHandle = _fixture.WinFormsHandle });
            sw.Stop();

            _output.WriteLine($"get_active_modal took {sw.ElapsedMilliseconds}ms");
            _output.WriteLine(result);

            Assert.Contains("Grid Blocking Modal", result);
            Assert.Contains("Accept", result);
            // Scoped to the modal subtree: must NOT contain the huge grid behind it.
            Assert.DoesNotContain("Stress Row", result);
            // Should be quick even though a 1000-row grid is present behind the modal.
            Assert.True(sw.ElapsedMilliseconds < 10000, $"Modal resolution too slow: {sw.ElapsedMilliseconds}ms");
        }
        finally
        {
            await DismissModalAsync("Cancel");
        }
    }

    [Fact]
    public async Task ListWindows_Succeeds_WhileModalOpen()
    {
        await _fixture.NavigateToTab(_fixture.WinFormsHandle, "Dialogs", "Open Modal Dialog");
        var openRef = _fixture.FindRefByName(_fixture.WinFormsHandle, "Open Modal Dialog");
        Assert.NotNull(openRef);

        await OpenModalAsync(openRef!);
        try
        {
            var listTool = new ListWindowsTool(_fixture.Session);
            var result = await _fixture.CallTool(listTool, new { });
            _output.WriteLine(result);

            // Win32-backed enumeration must not throw 0x80131505 while a modal is up.
            Assert.DoesNotContain("Failed to list windows", result);
            Assert.Contains("Test Modal Dialog", result);
            Assert.Contains("modal owner=", result);
        }
        finally
        {
            await DismissModalAsync("OK");
        }
    }

    [Fact]
    public async Task ModalButton_InvokedByRef_ClosesModal_NoSendInput()
    {
        await _fixture.NavigateToTab(_fixture.WinFormsHandle, "Dialogs", "Open Modal Dialog");
        var openRef = _fixture.FindRefByName(_fixture.WinFormsHandle, "Open Modal Dialog");
        Assert.NotNull(openRef);

        await OpenModalAsync(openRef!);

        var modalTool = new GetActiveModalTool(_fixture.Session, _fixture.Elements);
        var snapshot = await PollModalAsync(modalTool, new { ownerHandle = _fixture.WinFormsHandle });
        _output.WriteLine(snapshot);
        Assert.Contains("Test Modal Dialog", snapshot);

        var okRef = TestAppFixture.FindRefInSnapshot(snapshot, "OK");
        Assert.NotNull(okRef);

        // Deterministic InvokePattern click by ref - not keystrokes.
        var clickTool = new ClickTool(_fixture.Elements);
        var clickResult = await _fixture.CallTool(clickTool, new { @ref = okRef, timeoutMs = 3000 });
        _output.WriteLine(clickResult);
        Assert.Contains("Invoked", clickResult);

        // Modal should be gone; get_active_modal reports none for this owner.
        await Task.Delay(500);
        var after = await _fixture.CallTool(modalTool, new { ownerHandle = _fixture.WinFormsHandle });
        _output.WriteLine(after);
        Assert.Contains("No active modal", after);
    }

    [Fact]
    public async Task GetActiveModal_ResolvesClassicMessageBox()
    {
        await _fixture.NavigateToTab(_fixture.WinFormsHandle, "Dialogs", "Open Message Box");
        var openRef = _fixture.FindRefByName(_fixture.WinFormsHandle, "Open Message Box");
        Assert.NotNull(openRef);

        await OpenModalAsync(openRef!);
        try
        {
            var modalTool = new GetActiveModalTool(_fixture.Session, _fixture.Elements);
            var result = await PollModalAsync(modalTool, new { ownerHandle = _fixture.WinFormsHandle });
            _output.WriteLine(result);

            // Classic message boxes use the #32770 dialog window class.
            Assert.Contains("#32770", result);
            Assert.Contains("Confirm", result);
        }
        finally
        {
            await DismissModalAsync("No");
        }
    }

    /// <summary>
    /// Click a button that opens a modal using a non-blocking mouse click. Invoking
    /// via UIA would block until the modal closes (ShowDialog runs synchronously),
    /// so tests use SendInput-based mouse clicks to bring the modal up and keep the
    /// UIA client responsive for windows_get_active_modal.
    /// </summary>
    private async Task OpenModalAsync(string buttonRef)
    {
        var element = _fixture.Elements.GetElement(buttonRef);
        Assert.NotNull(element);
        Mouse.Click(element!.GetClickablePoint());
        await Task.Delay(800);
    }

    private async Task<string> PollModalAsync(GetActiveModalTool tool, object args)
    {
        var sw = Stopwatch.StartNew();
        var result = "";
        while (sw.ElapsedMilliseconds < 8000)
        {
            result = await _fixture.CallTool(tool, args);
            if (result.Contains("Modal dialog")) return result;
            await Task.Delay(200);
        }
        return result;
    }

    /// <summary>
    /// Dismiss the currently open modal by mouse-clicking a button with the given
    /// name (non-blocking), falling back to Escape, so a failed test never leaves a
    /// modal blocking the shared test app for other tests.
    /// </summary>
    private async Task DismissModalAsync(string buttonName)
    {
        try
        {
            var modalTool = new GetActiveModalTool(_fixture.Session, _fixture.Elements);
            var snapshot = await _fixture.CallTool(modalTool, new { ownerHandle = _fixture.WinFormsHandle });
            var buttonRef = TestAppFixture.FindRefInSnapshot(snapshot, buttonName);
            var element = buttonRef != null ? _fixture.Elements.GetElement(buttonRef) : null;
            if (element != null)
            {
                Mouse.Click(element.GetClickablePoint());
                await Task.Delay(400);
            }
        }
        catch { /* best effort */ }

        // Fallback: Escape via keyboard in case the button click did not land.
        try
        {
            Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
            await Task.Delay(200);
        }
        catch { /* best effort */ }
    }
}
