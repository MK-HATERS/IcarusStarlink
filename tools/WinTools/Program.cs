using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

if (args.Length == 0)
{
    Console.WriteLine("usage: capture|list-controls|click|set-text|seed-library|select-by-text|expand|is-expanded|right-click|real-left-click ...");
    return 1;
}

switch (args[0])
{
    case "capture":
        Capture(int.Parse(args[1]), args[2], args.Length > 3 ? args[3] : null);
        break;
    case "list-controls":
        ListControls(int.Parse(args[1]));
        break;
    case "click":
        Click(int.Parse(args[1]), args[2], args[3]);
        break;
    case "set-text":
        SetText(int.Parse(args[1]), int.Parse(args[2]), args[3]);
        break;
    case "set-text-by-automation-id":
        SetTextByAutomationId(int.Parse(args[1]), args[2], args[3]);
        break;
    case "seed-library":
        SeedLibrary(args[1]);
        break;
    case "select-by-text":
        SelectByText(int.Parse(args[1]), args[2]);
        break;
    case "add-to-selection-by-text":
        AddToSelectionByText(int.Parse(args[1]), args[2]);
        break;
    case "select-combo-item":
        SelectComboItem(int.Parse(args[1]), args[2], args.Length > 3 ? int.Parse(args[3]) : 0);
        break;
    case "expand":
        Expand(int.Parse(args[1]), args[2]);
        break;
    case "is-expanded":
        IsExpanded(int.Parse(args[1]), args[2]);
        break;
    case "right-click":
        RightClick(int.Parse(args[1]), args[2]);
        break;
    case "real-left-click":
        RealLeftClick(int.Parse(args[1]), args[2]);
        break;
    case "scroll-bottom":
        ScrollBottom(int.Parse(args[1]));
        break;
    default:
        Console.WriteLine($"unknown command: {args[0]}");
        return 1;
}

return 0;

static void SeedLibrary(string extractedModsDir)
{
    void Write(string fileName, string name, string author, string description, string? variantGroup = null, string? variant = null, int? variantSort = null)
    {
        var package = new ExmodPackage
        {
            Name = name, Author = author, Version = "1.0", Description = description, FileName = fileName,
            VariantGroup = variantGroup, Variant = variant, VariantSort = variantSort,
            Rows =
            [
                new ExmodFileRow
                {
                    CurrentFile = "Crafting-D_ProcessorRecipes.json",
                    FileItems = [new ExmodFileItem { Name = "SmelterRecipe", Fields = { ["CraftTime"] = System.Text.Json.Nodes.JsonValue.Create(5) } }],
                },
            ],
        };
        var assets = new List<ExmodAssetEntry> { new("readme.md", System.Text.Encoding.UTF8.GetBytes($"# {name}\n\n{description}")) };
        ExmodFolder.Write(Path.Combine(extractedModsDir, fileName), new ExmodPackageContents(package, assets));
        Console.WriteLine($"seeded {fileName}");
    }

    Write("Faster_Processors", "Faster Processors", "TestAuthor", "Speeds up processor recipes.");
    Write("Take_Home_Tools", "Take Home Tools", "ModAuthor", "Lets you take tools home.", variantGroup: "Take_Home", variant: "Tools", variantSort: 1);
    Write("Take_Home_Almost_All", "Take Home Almost All", "ModAuthor", "Lets you take almost everything home.", variantGroup: "Take_Home", variant: "Almost All", variantSort: 2);
}

static AutomationElement GetRoot(int pid)
{
    var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
    var root = AutomationElement.RootElement.FindFirst(TreeScope.Children, condition);
    if (root is null)
    {
        throw new InvalidOperationException($"No top-level window found for pid {pid}");
    }
    return root;
}

/// <summary>
/// A WPF Popup (ComboBox dropdown, this app's Columns picker, a context menu) opens as its own
/// separate top-level HWND, not a descendant of the main window's own AutomationElement — so
/// GetRoot's single FindFirst never sees into it while it's open. Used by list-controls/click/
/// select-by-text so those commands can still find/act on content inside an open popup; Capture
/// deliberately keeps using the single-root GetRoot since a screenshot can only target one HWND at
/// a time anyway (documented limitation in the README, unchanged by this).
/// </summary>
static List<AutomationElement> GetAllRoots(int pid)
{
    var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
    var roots = AutomationElement.RootElement.FindAll(TreeScope.Children, condition).Cast<AutomationElement>().ToList();
    if (roots.Count == 0)
    {
        throw new InvalidOperationException($"No top-level window found for pid {pid}");
    }
    return roots;
}

/// <summary>
/// titleContains targets a specific window by a substring of its title — omit it for the
/// original single-root behavior (the main window). An owned Window (like ExmodEditorWindow,
/// which sets Owner = the main window) shows up in UI Automation as a *descendant* of its owner,
/// not as its own sibling root of the Desktop the way GetAllRoots' Popup case does — so this
/// searches TreeScope.Descendants for a ControlType.Window with a matching Name, the same
/// traversal ListControls/FindByTypeAndName already use, rather than GetAllRoots. Still only ever
/// grabs one HWND; same PrintWindow limitation as always.
/// </summary>
static void Capture(int pid, string outputPath, string? titleContains = null)
{
    AutomationElement root;
    if (titleContains is null)
    {
        root = GetRoot(pid);
    }
    else
    {
        root = GetAllRoots(pid)
            .SelectMany(r => r.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)).Cast<AutomationElement>())
            .FirstOrDefault(w => w.Current.Name.Contains(titleContains, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No window for pid {pid} with title containing '{titleContains}'.");
    }

    var hwnd = new IntPtr(root.Current.NativeWindowHandle);
    NativeMethods.GetWindowRect(hwnd, out var rect);
    var width = rect.Right - rect.Left;
    var height = rect.Bottom - rect.Top;

    using var bmp = new Bitmap(width, height);
    using (var g = Graphics.FromImage(bmp))
    {
        var hdc = g.GetHdc();
        NativeMethods.PrintWindow(hwnd, hdc, 2u);
        g.ReleaseHdc(hdc);
    }
    bmp.Save(outputPath, ImageFormat.Png);
    Console.WriteLine($"saved {outputPath} ({width}x{height})");
}

static void ListControls(int pid)
{
    foreach (var root in GetAllRoots(pid))
    {
        var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        foreach (AutomationElement el in all)
        {
            var c = el.Current;
            Console.WriteLine($"{c.ControlType.ProgrammaticName,-40} Name='{c.Name}' AutomationId='{c.AutomationId}'");
        }
    }
}

// Prefers an exact Name match over a mere substring one — e.g. a button literally named "Install"
// would otherwise be shadowed by "Compare to installed" (which also contains "install") if that
// happens to come first in visual-tree order. Only falls back to substring matching when nothing
// matches exactly.
static AutomationElement FindByTypeAndName(IReadOnlyList<AutomationElement> roots, string controlType, string nameContains)
{
    AutomationElement? firstSubstringMatch = null;
    foreach (var root in roots)
    {
        var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        foreach (AutomationElement el in all)
        {
            var c = el.Current;
            if (!c.ControlType.ProgrammaticName.EndsWith(controlType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (string.Equals(c.Name, nameContains, StringComparison.OrdinalIgnoreCase))
            {
                return el;
            }
            if (firstSubstringMatch is null && c.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            {
                firstSubstringMatch = el;
            }
        }
    }
    return firstSubstringMatch ?? throw new InvalidOperationException($"No {controlType} containing '{nameContains}' found");
}

static void Click(int pid, string controlType, string nameContains)
{
    var el = FindByTypeAndName(GetAllRoots(pid), controlType, nameContains);

    if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var invokeObj))
    {
        ((InvokePattern)invokeObj).Invoke();
        Console.WriteLine($"invoked {el.Current.Name}");
        return;
    }
    if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selObj))
    {
        ((SelectionItemPattern)selObj).Select();
        Console.WriteLine($"selected {el.Current.Name}");
        return;
    }
    if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var toggleObj))
    {
        ((TogglePattern)toggleObj).Toggle();
        Console.WriteLine($"toggled {el.Current.Name}");
        return;
    }
    if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandObj))
    {
        ((ExpandCollapsePattern)expandObj).Expand();
        Console.WriteLine($"expanded {el.Current.Name}");
        return;
    }
    throw new InvalidOperationException($"Element '{el.Current.Name}' supports neither Invoke, SelectionItem, Toggle, nor ExpandCollapse");
}

static void SelectByText(int pid, string exactText)
{
    // A native PropertyCondition on AutomationElement.NameProperty was tried here originally, but
    // proved unreliable against a plain ListBox's Text peers (as opposed to the TreeView rows this
    // was first built for) — it intermittently reported no match even though the exact same
    // element was trivially findable by manually walking Descendants and comparing .Current.Name,
    // the same style FindByTypeAndName already uses. Manual comparison is the more robust of the
    // two, empirically, so this matches that approach instead of the native property filter.
    var textEl = GetAllRoots(pid)
        .SelectMany(root => root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>())
        .FirstOrDefault(el => el.Current.Name == exactText)
        ?? throw new InvalidOperationException($"No element with exact text '{exactText}' found");

    ((SelectionItemPattern)FindSelectableAncestor(textEl, exactText)).Select();
    Console.WriteLine($"selected ancestor of '{exactText}'");
}

/// <summary>
/// Adds an element to the CURRENT selection (Ctrl/Shift-click equivalent) instead of replacing it
/// — for exercising a ListBox's multi-select (e.g. the EXMOD editor's mass-edit feature, Phase
/// 7.3), which plain select-by-text/Select() can't reach since Select() always clears whatever
/// else was already selected.
/// </summary>
static void AddToSelectionByText(int pid, string exactText)
{
    var textEl = GetAllRoots(pid)
        .SelectMany(root => root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>())
        .FirstOrDefault(el => el.Current.Name == exactText)
        ?? throw new InvalidOperationException($"No element with exact text '{exactText}' found");

    ((SelectionItemPattern)FindSelectableAncestor(textEl, exactText)).AddToSelection();
    Console.WriteLine($"added '{exactText}' to selection");
}

static object FindSelectableAncestor(AutomationElement textEl, string exactText)
{
    var walker = TreeWalker.ControlViewWalker;
    var current = textEl;
    while (current is not null)
    {
        if (current.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern))
        {
            return pattern;
        }
        current = walker.GetParent(current);
    }
    throw new InvalidOperationException($"No selectable ancestor found for '{exactText}'");
}

// WPF ComboBox popups are their own top-level HWND (see GetAllRoots' remarks) and, empirically,
// don't survive being expanded in one WinTools process and selected from in the next — the popup
// closes in between. This does expand + select in a single process run so the popup stays open.
// comboIndex picks which ComboBox on the page (0-indexed, visual-tree order) when there's more
// than one — FindByTypeAndName alone always grabs the first.
static void SelectComboItem(int pid, string exactText, int comboIndex = 0)
{
    var combos = GetAllRoots(pid)
        .SelectMany(root => root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox)).Cast<AutomationElement>())
        .ToList();
    if (comboIndex >= combos.Count)
    {
        throw new InvalidOperationException($"Only {combos.Count} ComboBox(es) found, index {comboIndex} out of range");
    }
    var combo = combos[comboIndex];
    ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
    System.Threading.Thread.Sleep(300);

    var walker = TreeWalker.ControlViewWalker;
    var candidates = GetAllRoots(pid)
        .SelectMany(root => root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, exactText)).Cast<AutomationElement>())
        .ToList();
    if (candidates.Count == 0)
    {
        throw new InvalidOperationException($"No element with exact text '{exactText}' found after expanding combo");
    }

    foreach (var textEl in candidates)
    {
        var current = textEl;
        while (current is not null)
        {
            if (current.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern))
            {
                ((SelectionItemPattern)pattern).Select();
                Console.WriteLine($"selected combo item '{exactText}'");
                return;
            }
            current = walker.GetParent(current);
        }
    }
    throw new InvalidOperationException($"No selectable ancestor found for combo item '{exactText}'");
}

static AutomationElement FindTreeViewItemAncestor(int pid, string exactText)
{
    var root = GetRoot(pid);
    var textEl = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, exactText))
        .Cast<AutomationElement>()
        .FirstOrDefault()
        ?? throw new InvalidOperationException($"No element with exact text '{exactText}' found");

    var walker = TreeWalker.ControlViewWalker;
    var current = textEl;
    while (current is not null)
    {
        if (current.Current.ControlType == ControlType.TreeItem)
        {
            return current;
        }
        current = walker.GetParent(current);
    }
    throw new InvalidOperationException($"No TreeItem ancestor found for '{exactText}'");
}

static void Expand(int pid, string exactText)
{
    var el = FindTreeViewItemAncestor(pid, exactText);
    var pattern = (ExpandCollapsePattern)el.GetCurrentPattern(ExpandCollapsePattern.Pattern);
    pattern.Expand();
    Console.WriteLine($"expanded '{exactText}'");
}

static void IsExpanded(int pid, string exactText)
{
    var el = FindTreeViewItemAncestor(pid, exactText);
    var pattern = (ExpandCollapsePattern)el.GetCurrentPattern(ExpandCollapsePattern.Pattern);
    Console.WriteLine($"{exactText}: {pattern.Current.ExpandCollapseState}");
}

// Screen-coordinate based, not UI-Automation-pattern based: WPF's ContextMenu doesn't open via
// any Invoke/Expand pattern, so this is the one command here that actually moves the real cursor
// and synthesizes a real click. On a multi-monitor setup with mixed DPI scaling, the
// BoundingRectangle-derived coordinates can land off the intended element — verified working on
// a single-monitor/uniform-DPI setup only.
static void RightClick(int pid, string exactText)
{
    var root = GetRoot(pid);
    var textEl = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, exactText))
        .Cast<AutomationElement>()
        .FirstOrDefault()
        ?? throw new InvalidOperationException($"No element with exact text '{exactText}' found");

    var rect = textEl.Current.BoundingRectangle;
    var x = (int)(rect.Left + rect.Width / 2);
    var y = (int)(rect.Top + rect.Height / 2);
    NativeMethods.SetCursorPos(x, y);
    NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
    NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
    Console.WriteLine($"right-clicked '{exactText}' at ({x},{y})");
}

// A genuine synthesized left click via real cursor movement + mouse_event, not a UI Automation
// pattern invocation — added specifically because SelectionItemPattern.Select() (what
// select-by-text uses) does not reliably exercise the same code path a real left-click does for
// some controls (e.g. TreeView's own internal selection bookkeeping), so a bug that only
// reproduces via genuine mouse input can silently not reproduce through automation-pattern-based
// selection alone. Same single-monitor/uniform-DPI caveat as right-click.
static void RealLeftClick(int pid, string exactText)
{
    var root = GetRoot(pid);
    var textEl = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, exactText))
        .Cast<AutomationElement>()
        .FirstOrDefault()
        ?? throw new InvalidOperationException($"No element with exact text '{exactText}' found");

    var rect = textEl.Current.BoundingRectangle;
    var x = (int)(rect.Left + rect.Width / 2);
    var y = (int)(rect.Top + rect.Height / 2);
    NativeMethods.SetCursorPos(x, y);
    NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
    NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    Console.WriteLine($"left-clicked '{exactText}' at ({x},{y})");
}

// Scrolls the first ScrollPattern-supporting element (this app's pages are typically one page-level
// ScrollViewer wrapping everything) all the way down — for capturing a screenshot of content below
// the fold, since none of the click/select commands above need visibility to act on an element (UIA
// patterns dispatch directly), only Capture does.
static void ScrollBottom(int pid)
{
    // More than one ScrollPattern element can exist on a page (e.g. the left nav ListBox has its
    // own implicit one) — skip past any that aren't actually vertically scrollable (nothing below
    // their own fold) rather than assuming the first one found in visual-tree order is the page's
    // own content ScrollViewer.
    var scrollables = GetAllRoots(pid)
        .SelectMany(root => root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>())
        .Where(el => el.TryGetCurrentPattern(ScrollPattern.Pattern, out _))
        .ToList();

    foreach (var el in scrollables)
    {
        var pattern = (ScrollPattern)el.GetCurrentPattern(ScrollPattern.Pattern);
        if (pattern.Current.VerticallyScrollable)
        {
            pattern.SetScrollPercent(ScrollPattern.NoScroll, 100);
            Console.WriteLine("scrolled to bottom");
            return;
        }
    }

    Console.WriteLine($"found {scrollables.Count} scrollable element(s), but none are vertically scrollable (nothing below the fold)");
}

static void SetText(int pid, int editIndex, string text)
{
    var root = GetRoot(pid);
    var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
    if (editIndex >= edits.Count)
    {
        throw new InvalidOperationException($"Only {edits.Count} edit controls found, index {editIndex} out of range");
    }
    var el = edits[editIndex];
    var pattern = (ValuePattern)el.GetCurrentPattern(ValuePattern.Pattern);
    pattern.SetValue(text);
    Console.WriteLine($"set edit[{editIndex}] = {text}");
}

// For native common-dialog controls (Open/SaveFileDialog), which are their own top-level HWND
// (see GetAllRoots) with a rich, stable set of AutomationIds (e.g. the filename box is always
// "1001") — an index into "every Edit control" is fragile there since the dialog's file list view
// also exposes several hidden inline-rename Edit peers ahead of the one field that actually matters.
static void SetTextByAutomationId(int pid, string automationId, string text)
{
    var el = GetAllRoots(pid)
        .SelectMany(root => root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId)).Cast<AutomationElement>())
        .FirstOrDefault()
        ?? throw new InvalidOperationException($"No element with AutomationId '{automationId}' found");

    var pattern = (ValuePattern)el.GetCurrentPattern(ValuePattern.Pattern);
    pattern.SetValue(text);
    Console.WriteLine($"set [{automationId}] = {text}");
}

internal static class NativeMethods
{
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, UIntPtr dwExtraInfo);

    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
