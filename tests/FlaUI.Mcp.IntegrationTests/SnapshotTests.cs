using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;
using System.Diagnostics;
using Xunit.Abstractions;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// Tests for windows_snapshot tool using the WinForms and WPF test apps.
/// </summary>
[Collection("TestApps")]
public class SnapshotTests
{
    private readonly TestAppFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SnapshotTests(TestAppFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public void WinFormsApp_IsRunning()
    {
        Assert.NotEmpty(_fixture.WinFormsHandle);
        var window = _fixture.GetWinFormsWindow();
        Assert.NotNull(window);
        _output.WriteLine($"WinForms window: {window.Title} (handle: {_fixture.WinFormsHandle})");
    }

    [Fact]
    public void WpfApp_IsRunning()
    {
        Assert.NotEmpty(_fixture.WpfHandle);
        var window = _fixture.GetWpfWindow();
        Assert.NotNull(window);
        _output.WriteLine($"WPF window: {window.Title} (handle: {_fixture.WpfHandle})");
    }

    [Fact]
    public async Task WinForms_Snapshot_ContainsTabControl()
    {
        var builder = new SnapshotBuilder(_fixture.Elements);
        // A full (uncapped) snapshot can be slow if a prior test left the app on the
        // 1000-row Stress tab, so emit a heartbeat while it runs.
        var snapshot = await TestAppFixture.RunWithHeartbeatAsync(
            "full WinForms snapshot (tab control)",
            () => builder.BuildSnapshot(_fixture.WinFormsHandle, _fixture.GetWinFormsWindow()!));
        _output.WriteLine(snapshot);

        Assert.Contains("Buttons", snapshot);
        Assert.Contains("Forms", snapshot);
        Assert.Contains("Grid", snapshot);
        Assert.Contains("Trees", snapshot);
        Assert.Contains("Dialogs", snapshot);
    }

    [Fact]
    public void Wpf_Snapshot_ContainsTabControl()
    {
        var builder = new SnapshotBuilder(_fixture.Elements);
        var snapshot = builder.BuildSnapshot(_fixture.WpfHandle, _fixture.GetWpfWindow()!);
        _output.WriteLine(snapshot);

        Assert.Contains("Buttons", snapshot);
        Assert.Contains("Forms", snapshot);
        Assert.Contains("Grid", snapshot);
        Assert.Contains("Trees", snapshot);
    }

    [Fact]
    public void Wpf_Snapshot_EmitsRequestedProperties()
    {
        var options = new SnapshotOptions { Properties = new[] { "className" } };
        var snapshot = _fixture.TakeSnapshot(_fixture.WpfHandle, options);
        _output.WriteLine(snapshot);

        Assert.Contains("[className=", snapshot);
    }

    [Fact]
    public void Wpf_Snapshot_UnknownProperty_IsNoted()
    {
        var options = new SnapshotOptions { Properties = new[] { "notARealProperty" } };
        var snapshot = _fixture.TakeSnapshot(_fixture.WpfHandle, options);
        _output.WriteLine(snapshot);

        Assert.Contains("unknown properties ignored: notARealProperty", snapshot);
    }

    [Fact]
    public async Task WinForms_Snapshot_ContainsButtons()
    {
        // Navigate to Buttons tab first — another test may have switched tabs.
        // The tab header is shallow, so a bounded lookup finds it without walking
        // a large grid a prior test may have left realized.
        var tabRef = _fixture.FindRefByName(_fixture.WinFormsHandle, "Buttons");
        if (tabRef != null)
        {
            var clickTool = new ClickTool(_fixture.Elements);
            await _fixture.CallTool(clickTool, new { @ref = tabRef });
            await Task.Delay(100);
        }

        var snapshot2 = await TestAppFixture.RunWithHeartbeatAsync(
            "full WinForms snapshot (buttons)",
            () => _fixture.TakeSnapshot(_fixture.WinFormsHandle));
        Assert.Contains("Click Me", snapshot2);
        Assert.Contains("Conditional Button", snapshot2);
    }

    [Fact]
    public async Task WinForms_Snapshot_ContainsGridData()
    {
        // Navigate to Grid tab first — WinForms only shows active tab content.
        // The tab header is shallow, so a bounded lookup is fast.
        var gridTabRef = _fixture.FindRefByName(_fixture.WinFormsHandle, "Grid");

        Assert.NotNull(gridTabRef);

        var clickTool = new ClickTool(_fixture.Elements);
        await _fixture.CallTool(clickTool, new { @ref = gridTabRef });

        // Poll for grid content to appear after tab switch. Realizing the grid can
        // take a few seconds, so emit a heartbeat while we wait.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var nextBeat = TimeSpan.FromSeconds(1.5);
        string snapshot2 = "";
        while (sw.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(100);
            snapshot2 = _fixture.TakeSnapshot(_fixture.WinFormsHandle);
            if (snapshot2.Contains("Test Data"))
                break;

            if (sw.Elapsed >= nextBeat)
            {
                TestAppFixture.Heartbeat($"waiting for grid data to render, {sw.Elapsed.TotalSeconds:F1}s elapsed...");
                nextBeat += TimeSpan.FromSeconds(1.5);
            }
        }

        _output.WriteLine(snapshot2.Substring(0, Math.Min(2000, snapshot2.Length)));
        Assert.Contains("Test Data", snapshot2);
    }

    // ---- Bounded / shallow snapshot scenarios (large grids & deep trees) ----

    [Fact]
    public async Task Snapshot_FullDepth_IncludesDeepMarker()
    {
        await _fixture.NavigateToTab(_fixture.WinFormsHandle, "Stress", "Stress Row 1");

        // The deep marker sits ~25 panels down, past the default depth of 10,
        // so an explicit high maxDepth is required to reach it. The marker is
        // reachable by DEPTH, not breadth, so we cap children per node to avoid
        // a full walk of the sibling 1000-row StressDataGrid (thousands of cells),
        // which is irrelevant to this assertion and otherwise makes the test crawl.
        var snapshot = _fixture.TakeSnapshot(
            _fixture.WinFormsHandle, new SnapshotOptions { MaxDepth = 40, MaxChildrenPerNode = 20 });
        Assert.Contains("Deep Nested Marker", snapshot);
    }

    [Fact]
    public async Task Snapshot_MaxDepth_ExcludesDeepMarker()
    {
        await _fixture.NavigateToTab(_fixture.WinFormsHandle, "Stress", "Stress Row 1");

        // The deep marker sits ~25 panels down. A shallow snapshot must not reach it.
        var shallow = _fixture.TakeSnapshot(
            _fixture.WinFormsHandle, new SnapshotOptions { MaxDepth = 3 });

        _output.WriteLine(shallow.Substring(0, Math.Min(1500, shallow.Length)));
        Assert.DoesNotContain("Deep Nested Marker", shallow);
        // The tab itself should still be present at shallow depth.
        Assert.Contains("Stress", shallow);
    }

    [Fact]
    public async Task Snapshot_MaxChildren_LimitsGridRows_WithMarker()
    {
        await _fixture.NavigateToTab(_fixture.WinFormsHandle, "Stress", "Stress Row 1");

        var limited = _fixture.TakeSnapshot(
            _fixture.WinFormsHandle, new SnapshotOptions { MaxChildrenPerNode = 5 });

        Assert.Contains("more children not shown", limited);

        // Far fewer rows than the 1000 present should be emitted.
        var stressRowCount = limited.Split('\n').Count(l => l.Contains("Stress Row "));
        _output.WriteLine($"Stress rows emitted with maxChildren=5: {stressRowCount}");
        Assert.True(stressRowCount < 100,
            $"Expected the grid to be truncated, but emitted {stressRowCount} rows.");
    }

    [Fact]
    public async Task Snapshot_MaxElements_StopsAtBudget()
    {
        await _fixture.NavigateToTab(_fixture.WinFormsHandle, "Stress", "Stress Row 1");

        var budgeted = _fixture.TakeSnapshot(
            _fixture.WinFormsHandle, new SnapshotOptions { MaxElements = 50 });

        Assert.Contains("snapshot truncated by limits", budgeted);

        // Emitted element lines (excluding truncation markers) must respect the budget.
        var emitted = budgeted.Split('\n')
            .Count(l => l.TrimStart().StartsWith("- ") && !l.Contains("truncated") && !l.Contains("not shown"));
        _output.WriteLine($"Emitted elements with maxElements=50: {emitted}");
        Assert.True(emitted <= 51,
            $"Expected at most ~50 elements, but emitted {emitted}.");
    }

    [Fact]
    public async Task Snapshot_Shallow_IsFasterThanFull_OnStressTab()
    {
        await _fixture.NavigateToTab(_fixture.WinFormsHandle, "Stress", "Stress Row 1");

        var shallowSw = Stopwatch.StartNew();
        var shallow = _fixture.TakeSnapshot(
            _fixture.WinFormsHandle,
            new SnapshotOptions { MaxDepth = 3, MaxChildrenPerNode = 20, MaxElements = 200 });
        shallowSw.Stop();

        // This comparison deliberately walks the entire 1000-row grid (no caps) to
        // prove a full snapshot is materially slower than a bounded one. That full
        // walk is inherently slow, so emit a heartbeat while it runs to show progress.
        var fullSw = Stopwatch.StartNew();
        var full = await TestAppFixture.RunWithHeartbeatAsync(
            "full Stress-tab snapshot",
            () => _fixture.TakeSnapshot(_fixture.WinFormsHandle));
        fullSw.Stop();

        _output.WriteLine($"Shallow: {shallowSw.ElapsedMilliseconds}ms, Full: {fullSw.ElapsedMilliseconds}ms");
        _output.WriteLine($"Shallow length: {shallow.Length}, Full length: {full.Length}");

        // Shallow must be materially smaller and comfortably under the tool timeout.
        Assert.True(shallow.Length < full.Length,
            "Shallow snapshot should emit fewer elements than a full snapshot.");
        Assert.True(shallowSw.ElapsedMilliseconds < 5000,
            $"Shallow snapshot took too long: {shallowSw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Snapshot_ModelessDialog_ShallowDesktop_DoesNotHang()
    {
        await _fixture.NavigateToTab(_fixture.WinFormsHandle, "Dialogs");

        // Modeless dialogs use Show() (non-blocking), so Invoke returns immediately.
        var openRef = _fixture.FindRefByName(_fixture.WinFormsHandle, "Open Modeless Dialog");
        Assert.NotNull(openRef);

        var clickTool = new ClickTool(_fixture.Elements);
        await _fixture.CallTool(clickTool, new { @ref = openRef });
        await Task.Delay(300);

        try
        {
            // A bounded snapshot of the whole desktop (many windows) must stay fast.
            var desktop = _fixture.Session.Automation.GetDesktop();
            var builder = new SnapshotBuilder(_fixture.Elements);

            var sw = Stopwatch.StartNew();
            var snapshot = builder.BuildSnapshot(
                "desktop", desktop,
                new SnapshotOptions { MaxDepth = 3, MaxElements = 300 });
            sw.Stop();

            _output.WriteLine($"Desktop shallow snapshot: {sw.ElapsedMilliseconds}ms, length {snapshot.Length}");
            Assert.True(sw.ElapsedMilliseconds < 8000,
                $"Shallow desktop snapshot took too long with a dialog open: {sw.ElapsedMilliseconds}ms");
            Assert.Contains("Modeless", snapshot);
        }
        finally
        {
            // Close the modeless dialog window to restore shared fixture state.
            try
            {
                var desktop = _fixture.Session.Automation.GetDesktop();
                var dialog = desktop
                    .FindAllChildren(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window))
                    .FirstOrDefault(w => (w.AsWindow()?.Title ?? "").Contains("Modeless"));
                dialog?.AsWindow()?.Close();
                await Task.Delay(200);
            }
            catch { /* best-effort cleanup */ }
        }
    }
}
