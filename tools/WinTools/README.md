# WinTools

A small UI Automation CLI for manually driving/screenshotting the running
`IcarusStarlink.App` WPF window during development — not part of the shipped
product, not referenced by `IcarusStarlink.slnx`. Built up over Phase 3/3.5
while verifying UI behavior end-to-end; kept here instead of the ephemeral
scratchpad so it doesn't have to be reinvented next session.

Build: `dotnet build tools\WinTools\WinTools.csproj`
Run: `tools\WinTools\bin\Debug\net10.0-windows\WinTools.exe <command> <pid> ...`

Get the running app's PID first (e.g. `tasklist` or `Get-Process IcarusStarlink.App`).

## Commands

- `capture <pid> <outputPath>` — PrintWindow screenshot to a PNG. Note: this
  only captures the target window's own client area — popups (context menus,
  combo dropdowns, tooltips) render in a separate top-level HWND and won't
  appear in the capture.
- `list-controls <pid>` — dumps every descendant control's type/Name/AutomationId.
  Start here when a command below can't find what you're looking for.
- `click <pid> <controlType> <nameContains>` — finds the first control whose
  type ends with `controlType` and whose Name contains `nameContains`
  (case-insensitive), then invokes it via whichever of Invoke/SelectionItem/
  Toggle pattern it supports.
- `select-by-text <pid> <exactText>` — finds an element with that exact Name,
  walks up to the nearest ancestor supporting SelectionItemPattern, and
  selects it. Useful for TreeView/ListBox rows whose own Name is a generic
  type string rather than the row's visible text.
- `set-text <pid> <editIndex> <text>` — sets the Nth Edit control's value via
  ValuePattern (0-indexed, in visual-tree order).
- `expand <pid> <exactText>` / `is-expanded <pid> <exactText>` — finds the
  nearest TreeItem ancestor of an element with that exact Name and
  expands/queries it via ExpandCollapsePattern.
- `right-click <pid> <exactText>` — the one command that isn't pattern-based:
  moves the real cursor to the element's BoundingRectangle center and
  synthesizes a real right-click via `mouse_event`, since WPF ContextMenus
  don't open through any Automation pattern. Only verified working on a
  single-monitor/uniform-DPI setup — on a multi-monitor/mixed-DPI layout the
  derived screen coordinates can miss the element (hit this during Phase 3.5;
  never resolved, just worked around by verifying that specific case via code
  review instead).
- `seed-library <extractedModsDir>` — writes 3 synthetic EXMOD fixture mods
  (including a 2-member `Take_Home` variant family) directly via
  `ExmodFolder.Write`, for quick manual Library testing without a real
  `.EXMODZ`. Prefer real mods from the user's classic-IMM install
  (`C:\Personal\Icarus Software\Extracted_Mods`) over this when testing
  anything format-sensitive — see the `feedback-icarusstarlink-real-data-testing`
  memory for why.

## Typical flow

```powershell
# stop any previous instance first if you're about to rebuild
Get-Process IcarusStarlink.App -ErrorAction SilentlyContinue | Stop-Process -Force

dotnet build IcarusStarlink.slnx
Start-Process src\IcarusStarlink.App\bin\Debug\net10.0-windows\IcarusStarlink.App.exe
# note the PID, or: (Get-Process IcarusStarlink.App).Id

tools\WinTools\bin\Debug\net10.0-windows\WinTools.exe click <pid> ListItem "Id = library"
tools\WinTools\bin\Debug\net10.0-windows\WinTools.exe capture <pid> screenshot.png
```
