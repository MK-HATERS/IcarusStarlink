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
- `add-to-selection-by-text <pid> <exactText>` — same lookup as select-by-text,
  but calls AddToSelection() instead of Select(), so it adds to whatever's
  already selected instead of replacing it (a Ctrl/Shift-click equivalent).
  Added Phase 7.3 for the EXMOD editor's mass-edit feature, which needs a
  real multi-select in a ListBox — select-by-text alone can only ever select
  one item at a time.
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
- `right-click <pid> <exactText>` / `context-menu-click <pid> <exactText> <menuItemText>` /
  `flyout-click <pid> <buttonText> <menuItemText>` / `real-left-click <pid> <exactText>` /
  `double-click <pid> <exactText>` — the commands that aren't pattern-based:
  move the real cursor to the element's BoundingRectangle center and
  synthesize a real click via `SendInput`, since WPF ContextMenus don't open
  through any Automation pattern (`context-menu-click`/`flyout-click` also
  hunt for and Invoke() a named MenuItem afterward, in the same process run,
  since a popup opened in one WinTools process doesn't survive into the next
  — same reasoning as `select-combo-item` above). `flyout-click` opens its
  menu via `InvokePattern.Invoke()` on the button instead of a synthetic
  click, so it doesn't share the caveat below.

  **Known-broken on at least one dev machine as of the Library
  rename/delete verification pass**: a synthetic right-click here can fail
  to open a WPF ContextMenu at all, with no error — `mouse_event` and
  `SendInput` both "succeed", the cursor lands on the correct element
  (verified via BoundingRectangle math and a debug print of the computed
  coordinates), yet zero new top-level popup HWND ever appears afterward.
  Ruled out, in order: (1) DPI virtualization from this process having no
  app.manifest — fixed via `SetProcessDpiAwarenessContext` at startup
  regardless, since it's a real latent bug independent of this issue, but
  didn't fix it here (the target window sat entirely on the primary,
  100%-scaled monitor, so virtualization wasn't actually in play for this
  specific case); (2) `mouse_event` vs the modern `SendInput` — switched to
  `SendInput`, no change; (3) the target window not being the real OS
  foreground window — confirmed via `GetForegroundWindow()`, and neither
  `SetForegroundWindow` nor the standard `AttachThreadInput` /
  Alt-key-tap workarounds could make it become foreground (blocked by
  Windows' foreground-lock-timeout restriction on an unrelated background
  process); (4) a real left-click first, to activate the window the way a
  human's first click on a background window normally does — added as a
  best-effort step since it's harmless, but `GetForegroundWindow()` read
  back as 0 (no window anywhere holding input focus) immediately after the
  full click sequence even though the session was confirmed active and
  unlocked (`query session`, `OpenInputDesktop` both checked out normal).
  That result points at something upstream of WinTools itself — how its own
  calling process/console is hosted — rather than a fixable coordinate,
  timing, or DPI bug; not resolved this pass. Originally flagged (Phase 3.5)
  as "only verified working on a single-monitor/uniform-DPI setup" — that
  framing undersold it: DPI/multi-monitor was investigated this pass and
  ruled out as the actual cause. If this bites again, `list-controls`
  right after the click attempt (or the debug prints this investigation
  added and then removed — see git history on this file) is the fastest way
  to tell "menu opened and closed before I could query it" apart from "menu
  never opened at all" (the latter is what happened here — only the
  window's own title-bar System menu was ever found).
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
