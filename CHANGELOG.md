# Changelog

All notable changes to FlaUI-MCP will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
