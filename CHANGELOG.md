# Changelog

All notable changes to FlaUI-MCP will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.7.0-net48] - 2026-07-24

### Added
- `windows_click_native` MCP tool - a UI-Automation-independent fallback that forces a
  window/dialog to the OS foreground via Win32 (`AllowSetForegroundWindow` +
  `AttachThreadInput` + `BringWindowToTop`/`SetForegroundWindow`) and issues a real mouse
  click (`SendInput`) at a captioned child control's rectangle. Matches children via
  `EnumChildWindows` with the mnemonic `&` stripped (`&Yes` matches `Yes`), supports
  `childClass`/`index` disambiguation, and posts a secondary `WM_*BUTTONDOWN/UP` to the child.
  A `#32770` `WM_COMMAND` fast-path is used only when the modal class is genuinely `#32770`;
  rich WinForms/Infragistics `UltraButton` dialogs (which ignore `BM_CLICK`/`WM_COMMAND`) are
  actuated by the real click. Works on a modal even while it blocks its owner and UIA is
  timing out.
- `windows_dismiss_modal` MCP tool - a convenience specialization that resolves the modal
  blocking an app (or an explicit `modalHandle`) using the Win32 modal finder (no UIA
  snapshot), clicks a captioned button (`Yes`/`No`/`OK`/`Cancel`/…), and re-checks that the
  modal is gone and the owner re-enabled.
- `windows_click_point` MCP tool - the guaranteed last-resort coordinate click for controls
  with no UIA peer and no child `HWND` (e.g. custom-drawn Infragistics buttons). Accepts
  `screen`, `window`, `image`, or `normalized` coordinate spaces, translates the point to a
  physical virtual-desktop coordinate, and clicks via `SendInput`
  (`MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK`). Out-of-range points are rejected instead
  of clicking the wrong place.

### Changed
- `windows_screenshot` now returns self-describing capture metadata alongside the PNG
  (`origin` in virtual-screen coordinates - can be negative on multi-monitor -, `widthPx`/
  `heightPx`, `screenWidth`/`screenHeight`, `dpi`/`scale`). The most recent capture per
  handle (or full-screen) is remembered so `windows_click_point` with `space="image"` maps an
  image pixel straight back to the correct physical screen coordinate. The process runs
  Per-Monitor-DPI-Aware v2 and captures at device resolution, so image pixels map 1:1 to
  screen pixels.
- `windows_focus` now falls back to the pure Win32 foreground sequence when the UI Automation
  focus path times out or fails (so it works on UIA-dead modals) and reports which path won
  (`path=uia` or `path=win32`).

## [0.6.0-net48] - 2026-07-24

### Added
- `windows_get_active_modal` MCP tool that resolves the modal dialog currently blocking a
  window (both classic `#32770` message boxes and rich WinForms/Infragistics modal forms)
  using Win32 (`GetWindow`/`GW_ENABLEDPOPUP`, foreground fallback) instead of UI Automation
  enumeration, then attaches to it with `AutomationElement.FromHandle`. Returns the modal's
  handle, title, class, owner, and a shallow accessibility snapshot already scoped to the
  modal subtree - so it never descends into a huge grid behind the disabled parent and
  returns quickly even over a 3,000+ row grid.
- `windows_snapshot` gained a `scope` option (`descendants` | `subtree-from-handle`). The
  `subtree-from-handle` mode re-resolves the element directly from the window's native HWND
  so the walk is guaranteed to stay within that window and can never leak into a sibling grid.
- `timeoutMs` option added to `windows_snapshot`, `windows_click`, `windows_fill`,
  `windows_type`, and `windows_focus`. Interaction tools bound each UIA call and fail fast
  with a clear, retryable error instead of hanging until the global tool timeout.

### Changed
- `windows_list_windows` is now backed by Win32 `EnumWindows` (not UI Automation), so it
  keeps working while a modal dialog blocks an app (no more `0x80131505` timeouts). Results
  now include the window class, pid, enabled state, owner handle, and a `modal` flag.
- Renamed the MCP server identity from `playwright-windows` to `flaui-mcp`, and renamed the
  internal namespace from `PlaywrightWindows.Mcp` to `FlaUI.Mcp` to match the project name.
  FlaUI-MCP still follows Playwright's accessibility-snapshot + element-ref interaction
  pattern - it is inspired by Playwright but is a distinct FlaUI/UI Automation tool.

## [0.5.0-net48] - 2026-07-24

### Added
- `windows_get_properties` MCP tool to inspect the UI Automation properties of a single
  element by ref. Pass a `properties` list of friendly names to read specific ones, or
  omit it to dump all supported properties - useful for diagnosing why an element is
  hidden or not interactable (for example `isEnabled`, `isOffscreen`, `isKeyboardFocusable`,
  `boundingRectangle`).
- `windows_snapshot` now accepts an optional `properties` list to emit any standard UIA
  property inline on each element as `[name=value]` (for example
  `["className", "helpText", "isKeyboardFocusable"]`). Property names are resolved from the
  full standard UIA property set via reflection, and unknown names are ignored with a note.

### Notes
- Only standard UI Automation properties are exposed. Vendor-specific .NET control
  properties (for example Infragistics `UltraTab.Visible`/`Enabled`) are not available
  through UI Automation; use their UIA equivalents `isOffscreen` (≈ visible) and `isEnabled`.

## [0.4.0-net48] - 2026-07-24

### Added
- `windows_snapshot` now accepts optional `maxDepth`, `maxChildren`, and `maxElements`
  bounds to produce fast **shallow snapshots** of large grids and deep modal window
  trees that would otherwise time out. Truncated output includes explicit markers
  (for example `... (N more children not shown; increase maxChildren)` and
  `... snapshot truncated by limits (...)`); hidden elements are not assigned refs
  until a deeper or targeted snapshot is taken. The same bounds are also supported by
  the `snapshot` action of `windows_batch`.

## [0.3.0-net48] - 2026-07-23

### Changed
- Retargeted the MCP server (and test projects) from `net8.0-windows` to `.NET Framework 4.8`.
  This is maintained as a separate, `-net48`-tagged release line off the `net48` branch to
  work around FlaUI native UI Automation calls (for example against Telerik/Infragistics
  controls) misbehaving under the .NET Core runtime. Releases ship a single AnyCPU
  `FlaUI-MCP-*-net48.zip`; because .NET Framework 4.8 ships with Windows, no runtime
  download is required and the build runs on both x64 and ARM64.

## [0.2.0] - 2026-07-08

### Fixed
- Screenshots are now correct on scaled displays (DPI > 100%). Added `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` as the first call in the process entry point so UIA coordinates match physical pixels.
- Tool execution now times out after 30 seconds instead of hanging indefinitely when UI Automation blocks.

### Added
- Solution file (`FlaUI.Mcp.slnx`)
- xUnit test project (`tests/FlaUI.Mcp.Tests`) with DPI regression test
- `windows_send_keys` MCP tool for sending key presses and key chords.
- Opt-in `background` mode for `windows_screenshot` handle captures, with blank-frame fallback to the normal capture path.
- `savePath` and `overwrite` options for `windows_screenshot`, with local PNG path validation and atomic writes.
- Desktop integration test project with WinForms and WPF test applications.

## [0.1.0] - 2024-02-02

### Added
- Initial release
- **Core MCP Tools:**
  - `windows_launch` - Launch Windows applications
  - `windows_snapshot` - Capture accessibility tree with element refs
  - `windows_click` - Click elements by ref (uses Invoke pattern when available)
  - `windows_type` - Type text into elements
  - `windows_fill` - Clear and fill text fields
  - `windows_get_text` - Get element text content
  - `windows_screenshot` - Capture window/element screenshots
  - `windows_list_windows` - List all open windows
  - `windows_focus` - Bring window to foreground
  - `windows_close` - Close windows
  - `windows_batch` - Execute multiple actions in a single call

- **Architecture:**
  - MCP protocol handler (JSON-RPC over stdio)
  - Element registry for ref ↔ AutomationElement mapping
  - Snapshot builder for agent-friendly accessibility tree format
  - Session manager for tracking launched applications

- **Documentation:**
  - README with installation and usage instructions
  - GitHub Actions for CI/CD
  - MIT License

### Technical Details
- Built on [FlaUI](https://github.com/FlaUI/FlaUI) for Windows UI Automation
- Uses UIA3 for modern app support (WPF, UWP, Win32)
- Targets .NET 8.0-windows
- Prefers control patterns (Invoke, Value, Toggle) over mouse simulation
