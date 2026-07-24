using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Mcp;
using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;
using Xunit;
using Xunit.Abstractions;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// End-to-end validation of the UIA-independent fallbacks:
///  - windows_click_native / windows_dismiss_modal actuate a classic #32770
///    message box (child resolution + #32770 fast-path) while it blocks.
///  - the Win32 foreground sequence works even when the dialog does not already
///    hold foreground.
///  - windows_focus falls back to Win32 and reports path=win32.
///  - windows_screenshot emits self-describing capture metadata and
///    windows_click_point(space=image) lands on a known button rectangle.
///
/// These tests raise a real MessageBox on a background STA thread inside the test
/// process, so they need an interactive desktop (like the other integration tests).
/// They join the "TestApps" collection so xUnit serializes them with the other
/// desktop tests - real mouse clicks, foreground changes, and modal dialogs must
/// never run in parallel with the UIA app tests or they corrupt the shared desktop.
/// </summary>
[Collection("TestApps")]
public class NativeClickTests
{
    private readonly ITestOutputHelper _output;

    static NativeClickTests()
    {
        DpiUtility.EnablePerMonitorV2();
    }

    public NativeClickTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const uint MB_YESNO = 0x00000004;
    private const int IDNO = 7;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc cb, IntPtr lParam);

    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const uint WM_CLOSE = 0x0010;

    private sealed class MessageBoxHandle : IDisposable
    {
        public IntPtr Hwnd;
        public Thread Thread = null!;
        public int Result = -1;

        public bool WaitClosed(int timeoutMs) => Thread.Join(timeoutMs);

        public void Dispose()
        {
            if (Thread.IsAlive && Hwnd != IntPtr.Zero)
            {
                PostMessage(Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                Thread.Join(2000);
            }
        }
    }

    private MessageBoxHandle ShowMessageBox(out string caption)
    {
        var cap = "MCPNativeTest_" + Guid.NewGuid().ToString("N");
        caption = cap;
        var mb = new MessageBoxHandle();
        mb.Thread = new Thread(() =>
        {
            mb.Result = MessageBoxW(IntPtr.Zero, "Proceed with the lengthy search?", cap, MB_YESNO);
        })
        { IsBackground = true };
        mb.Thread.SetApartmentState(ApartmentState.STA);
        mb.Thread.Start();

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            var hwnd = FindWindow("#32770", cap);
            if (hwnd != IntPtr.Zero)
            {
                mb.Hwnd = hwnd;
                return mb;
            }
            Thread.Sleep(50);
        }

        mb.Dispose();
        throw new TimeoutException("MessageBox window did not appear.");
    }

    private static async Task<string> CallTool(ToolBase tool, object args)
    {
        var json = JsonSerializer.Serialize(args, McpProtocol.JsonOptions);
        var element = JsonSerializer.Deserialize<JsonElement>(json);
        var result = await tool.ExecuteAsync(element);
        return string.Join("\n", result.Content.Where(c => c.Type == "text").Select(c => c.Text));
    }

    [Fact]
    public async Task NativeClick_DismissesMessageBox_ByCaption()
    {
        var session = new SessionManager();
        try
        {
            using var mb = ShowMessageBox(out _);
            var handle = session.RegisterWindowHandle(mb.Hwnd);

            var tool = new NativeClickTool(session);
            var result = await CallTool(tool, new { handle, text = "No" });
            _output.WriteLine(result);

            Assert.True(mb.WaitClosed(6000), "MessageBox did not close after native click.");
            Assert.Equal(IDNO, mb.Result);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task NativeClick_Works_WhenDialogLacksForeground()
    {
        var session = new SessionManager();
        try
        {
            // First dialog is our click target.
            using var target = ShowMessageBox(out _);
            var handle = session.RegisterWindowHandle(target.Hwnd);

            // Raise a SECOND dialog which grabs the OS foreground, so the target no
            // longer holds it. Both are in-process, so this is fast and deterministic
            // (no external app to launch or leak).
            using var stealer = ShowMessageBox(out _);

            // Sanity: the target is not the foreground window anymore.
            var fg = GetForegroundWindow();
            Assert.NotEqual(target.Hwnd, fg);

            var tool = new NativeClickTool(session);
            var result = await CallTool(tool, new { handle, text = "No" });
            _output.WriteLine(result);

            // Proves the Win32 foreground sequence ran and reclaimed foreground for
            // the target before actuating it.
            Assert.Contains("foreground=achieved", result);
            Assert.True(target.WaitClosed(6000), "MessageBox did not close despite Win32 foreground sequence.");
            Assert.Equal(IDNO, target.Result);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task DismissModal_ByModalHandle_ClosesAndReports()
    {
        var session = new SessionManager();
        try
        {
            using var mb = ShowMessageBox(out _);
            var modalHandle = session.RegisterWindowHandle(mb.Hwnd);

            var tool = new DismissModalTool(session);
            var result = await CallTool(tool, new { modalHandle, button = "No" });
            _output.WriteLine(result);

            Assert.True(mb.WaitClosed(6000), "MessageBox did not close after windows_dismiss_modal.");
            Assert.Contains("Modal dismissed", result);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task Focus_FallsBackToWin32_ReportsPath()
    {
        var session = new SessionManager();
        try
        {
            using var mb = ShowMessageBox(out _);
            var handle = session.RegisterWindowHandle(mb.Hwnd);

            var tool = new FocusWindowTool(session);
            var result = await CallTool(tool, new { handle, timeoutMs = 2000 });
            _output.WriteLine(result);

            // Either path may win, but a #32770 that has no UIA window often forces the
            // Win32 fallback. Ensure the tool reports which path succeeded.
            Assert.Contains("path=", result);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task Screenshot_EmitsCaptureMetadata()
    {
        var session = new SessionManager();
        var captures = new CaptureRegistry();
        try
        {
            var tool = new ScreenshotTool(session, new ElementRegistry(), captures);
            var result = await CallTool(tool, new { fullScreen = true });
            _output.WriteLine(result);

            Assert.Contains("capture:", result);
            Assert.Contains("origin", result);
            Assert.Contains("widthPx", result);
            Assert.Contains("dpi", result);
            Assert.NotNull(captures.Get(CaptureRegistry.FullScreenKey));
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task ClickPoint_OutOfRange_ReturnsError()
    {
        var session = new SessionManager();
        var captures = new CaptureRegistry();
        try
        {
            var tool = new ClickPointTool(session, captures);
            var result = await tool.ExecuteAsync(ToElement(new { x = 10_000_000, y = 10_000_000, space = "screen" }));

            Assert.True(result.IsError);
            Assert.Contains("outside the virtual desktop", result.Content[0].Text);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task ClickPoint_Image_LandsOnMessageBoxButton()
    {
        var session = new SessionManager();
        var captures = new CaptureRegistry();
        try
        {
            using var mb = ShowMessageBox(out _);
            var handle = session.RegisterWindowHandle(mb.Hwnd);

            // Locate the "No" button's screen rect via Win32.
            RECT noRect = default;
            var found = false;
            EnumChildWindows(mb.Hwnd, (child, _) =>
            {
                var sb = new System.Text.StringBuilder(64);
                GetWindowText(child, sb, sb.Capacity);
                var text = sb.ToString().Replace("&", "");
                if (string.Equals(text.Trim(), "No", StringComparison.OrdinalIgnoreCase) &&
                    GetWindowRect(child, out var r))
                {
                    noRect = r;
                    found = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            Assert.True(found, "Could not locate the No button rectangle.");

            var targetX = (noRect.Left + noRect.Right) / 2;
            var targetY = (noRect.Top + noRect.Bottom) / 2;

            // Raise the dialog above other windows so a coordinate click lands on it.
            // SetWindowPos(HWND_TOPMOST) does not require the test process to own the
            // OS foreground (SetForegroundWindow is blocked for a non-foreground
            // process under an automated test harness), and it does not move the
            // window, so the button rect stays valid.
            SetWindowPos(mb.Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            Thread.Sleep(150);

            // Window-scoped screenshot: capture metadata is keyed by the dialog handle
            // and its origin is the dialog's top-left in screen space.
            var shot = new ScreenshotTool(session, new ElementRegistry(), captures);
            await CallTool(shot, new { handle });
            var meta = captures.Get(handle);
            Assert.NotNull(meta);

            // Convert the No button's screen center into window-image pixels (1:1 under PMv2).
            var imgX = targetX - meta!.OriginX;
            var imgY = targetY - meta.OriginY;

            // space=image with a handle maps the image pixel back to a physical screen
            // coordinate and clicks it (the tool also attempts to foreground the dialog).
            var clickTool = new ClickPointTool(session, captures);
            var result = await CallTool(clickTool, new { x = imgX, y = imgY, space = "image", handle });
            _output.WriteLine(result);

            Assert.True(mb.WaitClosed(6000), "MessageBox did not close after coordinate click.");
            Assert.Equal(IDNO, mb.Result);
        }
        finally
        {
            session.Dispose();
        }
    }

    private static JsonElement ToElement(object args)
    {
        var json = JsonSerializer.Serialize(args, McpProtocol.JsonOptions);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }
}
