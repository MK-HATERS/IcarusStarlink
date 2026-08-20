using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

if (args.Length == 0)
{
    Console.WriteLine("usage: capture|list-controls|click|set-text|seed-library|select-by-text|expand|is-expanded|right-click ...");
    return 1;
}

switch (args[0])
{
    case "capture":
        Capture(int.Parse(args[1]), args[2]);
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
    case "seed-library":
        SeedLibrary(args[1]);
        break;
    case "select-by-text":
        SelectByText(int.Parse(args[1]), args[2]);
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

static void Capture(int pid, string outputPath)
{
    var root = GetRoot(pid);
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

static AutomationElement FindByTypeAndName(IReadOnlyList<AutomationElement> roots, string controlType, string nameContains)
{
    foreach (var root in roots)
    {
        var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        foreach (AutomationElement el in all)
        {
            var c = el.Current;
            if (c.ControlType.ProgrammaticName.EndsWith(controlType, StringComparison.OrdinalIgnoreCase)
                && c.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            {
                return el;
            }
        }
    }
    throw new InvalidOperationException($"No {controlType} containing '{nameContains}' found");
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
    throw new InvalidOperationException($"Element '{el.Current.Name}' supports neither Invoke, SelectionItem, nor Toggle");
}

static void SelectByText(int pid, string exactText)
{
    var textEl = GetAllRoots(pid)
        .SelectMany(root => root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, exactText)).Cast<AutomationElement>())
        .FirstOrDefault()
        ?? throw new InvalidOperationException($"No element with exact text '{exactText}' found");

    var walker = TreeWalker.ControlViewWalker;
    var current = textEl;
    while (current is not null)
    {
        if (current.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern))
        {
            ((SelectionItemPattern)pattern).Select();
            Console.WriteLine($"selected ancestor of '{exactText}'");
            return;
        }
        current = walker.GetParent(current);
    }
    throw new InvalidOperationException($"No selectable ancestor found for '{exactText}'");
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

internal static class NativeMethods
{
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;

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
