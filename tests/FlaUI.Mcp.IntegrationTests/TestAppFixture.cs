using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Mcp;
using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// Shared fixture that launches the WinForms and WPF test apps once per test collection.
/// Provides SessionManager and ElementRegistry for all tests to share.
/// Implements IAsyncLifetime for proper async setup/teardown.
/// </summary>
public class TestAppFixture : IAsyncLifetime
{
    private const int WindowPollIntervalMs = 250;
    private const int WindowPollTimeoutMs = 15000;

    public SessionManager Session { get; } = new();
    public ElementRegistry Elements { get; } = new();

    public string WinFormsHandle { get; private set; } = "";
    public string WpfHandle { get; private set; } = "";

    private Process? _winFormsProcess;
    private Process? _wpfProcess;

    public async Task InitializeAsync()
    {
        var winFormsPath = FindTestAppPath("WinFormsTestApp")
            ?? throw new Exception(
                "WinFormsTestApp not found. Build it first: dotnet build tests/TestApps/WinFormsTestApp");
        var wpfPath = FindTestAppPath("WpfTestApp")
            ?? throw new Exception(
                "WpfTestApp not found. Build it first: dotnet build tests/TestApps/WpfTestApp");

        _winFormsProcess = LaunchTestApp(winFormsPath);
        _wpfProcess = LaunchTestApp(wpfPath);
        var winFormsProcessId = _winFormsProcess.Id;
        var wpfProcessId = _wpfProcess.Id;

        // Poll for both windows to appear
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < WindowPollTimeoutMs
               && (WinFormsHandle == "" || WpfHandle == ""))
        {
            await Task.Delay(WindowPollIntervalMs);

            var desktop = Session.Automation.GetDesktop();
            var windows = desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window));
            foreach (var w in windows)
            {
                var win = w.AsWindow();
                var processId = w.Properties.ProcessId.ValueOrDefault;
                if (win?.Title == "FlaUI-MCP Test App"
                    && processId == winFormsProcessId
                    && WinFormsHandle == "")
                {
                    WinFormsHandle = Session.RegisterWindow(win);
                }
                else if (win?.Title == "FlaUI-MCP WPF Test App"
                         && processId == wpfProcessId
                         && WpfHandle == "")
                {
                    WpfHandle = Session.RegisterWindow(win);
                }
            }
        }

        if (WinFormsHandle == "")
            throw new Exception("Timed out waiting for WinForms test app window.");
        if (WpfHandle == "")
            throw new Exception("Timed out waiting for WPF test app window.");
    }

    public Task DisposeAsync()
    {
        CloseProcess(_winFormsProcess);
        CloseProcess(_wpfProcess);
        Session.Dispose();
        return Task.CompletedTask;
    }

    private static Process LaunchTestApp(string path)
    {
        var psi = new ProcessStartInfo(path) { UseShellExecute = true };
        return Process.Start(psi) ?? throw new InvalidOperationException($"Failed to launch test app: {path}");
    }

    private static string? FindTestAppPath(string appName)
    {
        var baseDir = AppContext.BaseDirectory;

        // Walk up to find the repo root (contains src/)
        var dir = new DirectoryInfo(baseDir);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        if (dir == null) return null;

        var searchPaths = new[]
        {
            Path.Combine(dir.FullName, "tests", "TestApps", appName, "bin", "Debug", "net8.0-windows", $"{appName}.exe"),
            Path.Combine(dir.FullName, "tests", "TestApps", appName, "bin", "Release", "net8.0-windows", $"{appName}.exe"),
        };

        return searchPaths.FirstOrDefault(File.Exists);
    }

    public Window? GetWinFormsWindow() => Session.GetWindow(WinFormsHandle);
    public Window? GetWpfWindow() => Session.GetWindow(WpfHandle);

    /// <summary>
    /// Call an MCP tool with the given arguments and return the text result.
    /// Shared helper to avoid duplication across test classes.
    /// </summary>
    public async Task<string> CallTool(ToolBase tool, object args)
    {
        var json = JsonSerializer.Serialize(args, McpProtocol.JsonOptions);
        var element = JsonSerializer.Deserialize<JsonElement>(json);
        var result = await tool.ExecuteAsync(element);
        return result.Content.FirstOrDefault()?.Text ?? "";
    }

    /// <summary>
    /// Run a genuinely slow synchronous operation (e.g. a full snapshot of a large
    /// grid) while emitting a periodic heartbeat so a long-running test does not look
    /// hung. Heartbeats are written to the raw standard-error stream (which the xUnit
    /// runner does not redirect/buffer, unlike <see cref="Console.Error"/>), so they
    /// appear live in the terminal while the operation is in flight.
    /// </summary>
    public static async Task<T> RunWithHeartbeatAsync<T>(
        string label, Func<T> work, TimeSpan? interval = null)
    {
        var beat = interval ?? TimeSpan.FromSeconds(1.5);
        var sw = Stopwatch.StartNew();

        WriteHeartbeat($"[heartbeat] {label}: starting...");

        var task = Task.Run(work);
        while (!task.IsCompleted)
        {
            var done = await Task.WhenAny(task, Task.Delay(beat));
            if (done == task) break;
            WriteHeartbeat($"[heartbeat] {label}: still working, {sw.Elapsed.TotalSeconds:F1}s elapsed...");
        }

        WriteHeartbeat($"[heartbeat] {label}: done in {sw.Elapsed.TotalSeconds:F1}s.");
        return await task;
    }

    private static void WriteHeartbeat(string message)
    {
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(message + Environment.NewLine);
            var stderr = Console.OpenStandardError();
            stderr.Write(bytes, 0, bytes.Length);
            stderr.Flush();
        }
        catch { /* feedback only - never fail a test over logging */ }
    }

    /// <summary>
    /// Take a snapshot of a window and return the text.
    /// </summary>
    public string TakeSnapshot(string handle)
    {
        var builder = new SnapshotBuilder(Elements);
        var window = Session.GetWindow(handle)!;
        return builder.BuildSnapshot(handle, window);
    }

    /// <summary>
    /// Take a bounded snapshot of a window using the given options.
    /// </summary>
    public string TakeSnapshot(string handle, SnapshotOptions options)
    {
        var builder = new SnapshotBuilder(Elements);
        var window = Session.GetWindow(handle)!;
        return builder.BuildSnapshot(handle, window, options);
    }

    /// <summary>
    /// Bounded snapshot options used by the tab-navigation and single-element
    /// lookup helpers. Capping breadth and total elements stops these helpers from
    /// walking large grids (e.g. the 1000-row Stress grid) just to find a tab
    /// header, button, or first row - every named target they look for is shallow.
    /// This keeps the large-grid integration tests fast without changing what the
    /// product's full snapshot does (tests that need a full walk call TakeSnapshot
    /// without options directly).
    /// </summary>
    private static readonly SnapshotOptions NavigationSnapshotOptions = new()
    {
        MaxDepth = 12,
        MaxChildrenPerNode = 25,
        MaxElements = 500
    };

    /// <summary>
    /// Click a tab by name and wait for its content to appear (polls for a marker).
    /// WinForms only realizes the active tab's child controls, so tests must
    /// switch tabs before asserting on their contents.
    /// </summary>
    public async Task NavigateToTab(string handle, string tabName, string? contentMarker = null)
    {
        var tabRef = FindRefByName(handle, tabName);
        if (tabRef == null) return;

        var clickTool = new ClickTool(Elements);
        await CallTool(clickTool, new { @ref = tabRef });

        if (contentMarker == null)
        {
            await Task.Delay(200);
            return;
        }

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(100);
            // Bounded snapshot: the marker (tab content / first grid row) is shallow,
            // so we must not walk the entire large grid on every poll iteration.
            if (TakeSnapshot(handle, NavigationSnapshotOptions).Contains(contentMarker))
                return;
        }
    }

    /// <summary>
    /// Find an element ref by name in the snapshot of the given window.
    /// Uses a bounded snapshot (named controls are shallow) so this stays fast
    /// even on tabs hosting large grids. Takes a fresh snapshot each time — use the
    /// overload accepting a pre-built snapshot when multiple lookups are needed.
    /// </summary>
    public string? FindRefByName(string handle, string name)
    {
        var snapshot = TakeSnapshot(handle, NavigationSnapshotOptions);
        return FindRefInSnapshot(snapshot, name);
    }

    /// <summary>
    /// Find an element ref by name in a pre-built snapshot string.
    /// Use this when making multiple lookups against the same snapshot.
    /// </summary>
    public static string? FindRefInSnapshot(string snapshot, string name)
    {
        foreach (var line in snapshot.Split('\n'))
        {
            if (line.Contains($"\"{name}\"") && line.Contains("[ref="))
            {
                var refStart = line.IndexOf("[ref=") + 5;
                var refEnd = line.IndexOf("]", refStart);
                return line.Substring(refStart, refEnd - refStart);
            }
        }
        return null;
    }

    private static void CloseProcess(Process? process)
    {
        if (process == null || process.HasExited) return;
        try
        {
            process.CloseMainWindow();
            if (!process.WaitForExit(3000))
            {
                process.Kill();
            }
        }
        catch
        {
            try { process.Kill(); } catch { }
        }
    }
}

/// <summary>
/// Collection definition so all test classes share the same fixture (same app instances).
/// </summary>
[CollectionDefinition("TestApps")]
public class TestAppCollection : ICollectionFixture<TestAppFixture>
{
}
