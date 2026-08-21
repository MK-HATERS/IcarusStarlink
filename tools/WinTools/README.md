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

- `capture <pid> <outputPath> [titleContains]` — PrintWindow screenshot to a
  PNG. Note: this only captures ONE specific HWND (the target window's own
  client area) — a WPF Popup (context menu, combo dropdown, this app's
  Columns picker) renders in its own separate top-level HWND, so it never
  appears in the capture no matter what. Unlike some of the commands below,
  there's no fix for that short of compositing two separate PrintWindow calls
  into one image, which hasn't been worth building. Omit titleContains for
  the original behavior (the main window). Pass it to target a genuinely
  separate owned Window instead — e.g. Phase 7.1's ExmodEditorWindow, opened
  via Library's Edit…/New mod… actions — by a substring of its title; added
  because such a window shows up in UI Automation as a *descendant* of its
  owner (found via list-controls' traversal), not as its own sibling root of
  the Desktop, so this searches TreeScope.Descendants for a matching
  ControlType.Window rather than picking among GetAllRoots.
- `list-controls <pid>` — dumps every descendant control's type/Name/AutomationId,
  across every top-level window owned by pid (the main window plus any
  currently-open Popup — see the GetAllRoots doc comment in Program.cs for why
  that traversal matters). Start here when a command below can't find what
  you're looking for.
- `click <pid> <controlType> <nameContains>` — finds the control whose type
  ends with `controlType` and whose Name contains `nameContains`
  (case-insensitive) across all top-level windows for pid, then invokes it via
  whichever of Invoke/SelectionItem/Toggle/ExpandCollapse pattern it supports.
  Prefers an exact Name match over a mere substring one (added Phase 6.6) — a
  button literally named "Install" would otherwise be shadowed by "Compare to
  installed" (which also contains "install") if that happened to come first
  in visual-tree order; only falls back to substring matching when nothing
  matches exactly. Note a Popup with `StaysOpen="False"` (used for this app's
  Columns picker, and by ComboBox dropdowns) can auto-dismiss between separate WinTools
  process launches — chain the "open it" and "click inside it" calls with
  `&&` in one shell command rather than two separate tool calls, or the
  second one won't find what the first one just opened. For a ComboBox
  specifically, prefer `select-combo-item` below — it hit this exact problem
  even when chained, since expanding a ComboBox and then re-querying its
  items needed a delay in between, not just being in the same shell command.
- `select-by-text <pid> <exactText>` — finds an element with that exact Name,
  walks up to the nearest ancestor supporting SelectionItemPattern, and
  selects it. Useful for TreeView/ListBox rows whose own Name is a generic
  type string rather than the row's visible text. Matches by manually walking
  Descendants and comparing `.Current.Name` (like FindByTypeAndName), not a
  native PropertyCondition — an earlier PropertyCondition-based version
  worked fine against TreeView rows but intermittently reported no match at
  all against a plain ListBox's Text peers (Phase 7.1's editor), even though
  the exact same element was trivially found by the manual-walk approach
  moments earlier via list-controls. If this still reports "no element
  found" for text that's visibly on screen, double-check for a dash
  character mismatch first (e.g. an em dash "—" in the real Display string
  vs. a plain hyphen "-" typed on the command line) before suspecting the
  tool itself — that's the actual cause it usually turns out to be.
- `select-combo-item <pid> <exactText> [comboIndex]` — finds the ComboBox at
  comboIndex (0-indexed, visual-tree order across all top-level windows for
  pid; defaults to 0, the first one — pass an index when a page has more than
  one ComboBox, e.g. Merge & Install's Profile selector plus four gameplay-
  option dropdowns), expands it, waits briefly for its popup items to
  realize, then selects the item with that exact text — all in one process
  run. Added in Phase 6.3 because a ComboBox's dropdown is its own top-level
  popup HWND that doesn't survive being expanded in one WinTools process and
  selected from in the next (the popup closes in between, even chained with
  `&&`); doing both steps inside one process keeps the popup alive
  throughout. `list-controls` shows how many ComboBoxes exist and in what
  order if you're not sure which index to use.
- `set-text <pid> <editIndex> <text>` — sets the Nth Edit control's value via
  ValuePattern (0-indexed, in visual-tree order).
- `set-text-by-automation-id <pid> <automationId> <text>` — same, but finds
  the element by exact AutomationId across all top-level windows for pid
  instead of by index. Added in Phase 6.3 for native Open/SaveFileDialogs
  (their own top-level HWND, same as a ComboBox popup): the filename box has
  a stable AutomationId (`1001` in a SaveFileDialog, `1148` in an
  OpenFileDialog — confirmed on this machine's Windows build, not guaranteed
  elsewhere; `list-controls` first if a dialog doesn't behave the same way)
  but an index into "every Edit control" is fragile there, since the
  dialog's own file list view exposes several hidden inline-rename Edit
  peers ahead of the one field that actually matters.
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
