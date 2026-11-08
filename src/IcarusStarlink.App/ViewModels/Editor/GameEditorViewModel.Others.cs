using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// What the user's other turned-on mods do to the items here: a mark in the list, a line on each
/// card naming the mod whose value the merge keeps, and a question at Save when a value saved
/// would still lose. Read from Merge &amp; Install's own queue and saved winners (IMergeQueueReader)
/// when the page is shown and after a Save, never on every keystroke.
/// </summary>
public sealed partial class GameEditorViewModel
{
    private readonly IMergeQueueReader? _queueReader;

    /// <summary>The queue as last read; null until the page was shown once.</summary>
    private MergeQueueState? _queueState;

    private EditorQueueEffects _effects = EditorQueueEffects.Empty;

    /// <summary>Only the newest read is shown (a Save's read can overtake the page's).</summary>
    private int _othersVersion;

    private Task _othersTask = Task.CompletedTask;

    /// <summary>"Other turned-on mods change 12 of these items" above the list; null when none do.</summary>
    [ObservableProperty]
    private string? _otherModsText;

    /// <summary>Tests: the read a Save or a new Save to started.</summary>
    internal Task WaitForOtherModsAsync() => _othersTask;

    /// <summary>
    /// Reads what the other turned-on mods do (the page was shown, or a Save changed the mod), or
    /// with <paramref name="reread"/> false works it out again from the last read for a new Save to.
    /// Never throws: the page works without it.
    /// </summary>
    public Task RefreshOtherModsAsync(bool reread = true)
    {
        _othersTask = RefreshOtherModsCoreAsync(reread);
        return _othersTask;
    }

    private async Task RefreshOtherModsCoreAsync(bool reread)
    {
        if (_queueReader is null)
        {
            return;
        }

        var version = ++_othersVersion;
        var targetId = SelectedTarget is { IsNew: false, FolderName: { } folder } ? folder : null;
        try
        {
            var state = !reread && _queueState is { } known ? known : await _queueReader.ReadQueueAsync();
            var effects = await Task.Run(() => EditorQueueEffects.Build(state, targetId));
            if (version != _othersVersion)
            {
                return;
            }

            _queueState = state;
            _effects = effects;
            ApplyOtherMods();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "The Editor page couldn't read what the other turned-on mods change");
        }
    }

    /// <summary>The list's marks and the shown cards' lines from the last read.</summary>
    private void ApplyOtherMods()
    {
        var rows = _effects.RowsChangedByOthers;
        var count = 0;
        foreach (var item in _allItems)
        {
            item.IsChangedByOtherMods = rows.Count > 0 && item.Info.Rows.Any(r => rows.Contains((r.File.ToLowerInvariant(), r.Row.ToLowerInvariant())));
            count += item.IsChangedByOtherMods ? 1 : 0;
        }

        OtherModsText = count switch
        {
            0 => null,
            1 => "Your other turned-on mods change 1 item here (marked ◆).",
            _ => $"Your other turned-on mods change {count:N0} items here (marked ◆).",
        };
        UpdateOtherModLines(Sections);
    }

    /// <summary>Each card's lines: a value another turned-on mod changes, what the merge keeps and whose it is, and a multiplier applied after the mods.</summary>
    private void UpdateOtherModLines(IEnumerable<GameItemSectionViewModel> cards)
    {
        foreach (var card in cards)
        {
            card.OtherModLines = OtherModLinesFor(card);
        }
    }

    private List<string> OtherModLinesFor(GameItemSectionViewModel card)
    {
        var lines = new List<string>();
        var file = card.Section.CurrentFile;
        var target = _effects.TargetIndex;
        var multiplied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var outcome in _effects.For(file, card.Section.RowName))
        {
            var winner = outcome.WinnerModIndex is { } w ? _effects.NameOf(w) : null;
            var from = winner is null
                ? $"{string.Join(" and ", outcome.ModIndexes.Select(_effects.NameOf))} combined"
                : outcome.WinnerModIndex == target ? "this mod" : winner;
            var others = outcome.ModIndexes.Where(i => i != target).Select(_effects.NameOf).ToList();
            var tail = outcome.WinnerModIndex == target && target >= 0 ? $" (it wins over {string.Join(", ", others)})"
                : outcome.ModIndexes.Contains(target) && target >= 0 ? ". This mod's value loses"
                : "";
            if (outcome.PickedIndex is not null)
            {
                tail += " (your pick in Check the merge)";
            }

            var fields = card.Fields.Where(f => f.AddedEntry is null && EditorQueueEffects.FieldKey(file, f.Field.Path) == outcome.FieldName).ToList();
            if (fields.Count == 0)
            {
                lines.Add($"{StatEntries.Display(outcome.FieldName)}: changed by {from}{tail}.");
                continue;
            }

            foreach (var field in fields)
            {
                var merged = outcome.Resolved.IsFieldRemoved ? null : ValueAt(outcome.Resolved.NewValue, field.Field.Path, outcome.FieldName != field.Field.Path.Top);
                var game = EditorValueText.Display(field.Field.GameValue);
                var now = EditorQueueEffects.Shown(merged);
                if (fields.Count > 1 && now == game)
                {
                    continue; // one entry of a group the mod changes elsewhere
                }

                var after = Multiplier(file, field.Field.Path) is { } times ? $", then {times} on top" : "";
                if (after.Length > 0)
                {
                    multiplied.Add(field.Field.Path.Key);
                }

                lines.Add($"{field.Label}: game {(field.Field.GameValue is null ? "not set" : game)} · with your turned-on mods {now}{after}, from {from}{tail}.");
            }
        }

        // Stacks and slots multipliers apply after every mod, so they're said on their own.
        foreach (var field in card.Fields.Where(f => !multiplied.Contains(f.Field.Path.Key)))
        {
            if (Multiplier(file, field.Field.Path) is { } times)
            {
                lines.Add($"{field.Label}: Merge & Install's {times} option multiplies it when you build.");
            }
        }

        return lines;
    }

    /// <summary>"Stacks ×4" for a stack size and "Slots ×2" for slots, when that Merge &amp; Install option is on.</summary>
    private string? Multiplier(string currentFile, EditPath path)
    {
        if (path.Steps.Count > 0 || _queueState is not { } state)
        {
            return null;
        }

        if (state.StacksMultiplier is > 0 and var stacks && path.Top == "MaxStack" && string.Equals(currentFile, "Traits-D_Itemable.json", StringComparison.OrdinalIgnoreCase))
        {
            return "Stacks " + EditorQueueEffects.Times(stacks);
        }

        if (state.SlotsMultiplier is > 0 and var slots && path.Top == "StartingSlots" && string.Equals(currentFile, "Inventory-D_InventoryInfo.json", StringComparison.OrdinalIgnoreCase))
        {
            return "Slots " + EditorQueueEffects.Times(slots);
        }

        return null;
    }

    /// <summary>The value at the field's place inside the merged top-level value (a stat inside a group, an amount inside a list).</summary>
    private static JsonNode? ValueAt(JsonNode? value, EditPath path, bool firstStepIsInTheName)
    {
        foreach (var step in path.Steps.Skip(firstStepIsInTheName ? 1 : 0))
        {
            value = (step, value) switch
            {
                (string key, JsonObject obj) => obj[key],
                (int index, JsonArray list) when index < list.Count => list[index],
                _ => null,
            };
        }

        return value;
    }

    /// <summary>
    /// Asked at Save when another turned-on mod would still win a value saved: true = save, then
    /// move this mod to the end of the merge list; false = save anyway; null = don't save. The
    /// second argument says whether moving it would help. Tests swap in an answer.
    /// </summary>
    internal Func<string, bool, bool?> AskWhenValueLoses { get; set; } = (message, canMove) =>
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        return canMove
            ? Views.ThemedConfirmDialog.ShowWithCancel(owner, message, "Another mod wins", Views.ThemedConfirmSeverity.Warning, "Move my mod last", "Save anyway")
            : Views.ThemedConfirmDialog.ShowWithWords(owner, message, "Another mod wins", Views.ThemedConfirmSeverity.Warning, "Save anyway", "Cancel") ? false : null;
    };

    /// <summary>
    /// Before Save writes: the values saved into <paramref name="folder"/> that another turned-on mod
    /// would still win, asked about. Returns false when the user cancels; <paramref name="moveLast"/>
    /// when they chose to move the mod last. Nothing to ask when the mod isn't turned on.
    /// </summary>
    private bool AskAboutLosingValues(string folder, ExmodPackage package, IEnumerable<(string Name, IReadOnlyList<GameItemSectionViewModel> Cards)> edited, out bool moveLast)
    {
        moveLast = false;
        if (_queueState is not { } state)
        {
            return true;
        }

        var effects = EditorQueueEffects.ForTarget(state, folder);
        if (effects.TargetIndex < 0)
        {
            return true;
        }

        var saved = new List<(string File, string Row, string Field, string What)>();
        foreach (var (itemName, cards) in edited)
        {
            var title = FindItem(itemName)?.Title ?? itemName;
            // A card given its own copy writes a new row no other mod has.
            foreach (var card in cards.Where(c => !(c.IsShared && c.OwnCopy && !c.Section.IsOwnCopy)))
            {
                foreach (var change in card.Changes())
                {
                    var label = card.Fields.FirstOrDefault(f => f.Field.Path.Key == change.Path.Key)?.Label ?? change.Path.Top;
                    saved.Add((card.Section.CurrentFile, card.Section.RowName, EditorQueueEffects.FieldKey(card.Section.CurrentFile, change.Path), $"{title} · {label}"));
                }
            }
        }

        var changes = ExmodFieldChangeMapper.ToFieldChanges(package, new DefaultSemanticClassifier());
        var losses = effects.Losses(changes, saved);
        if (losses.Count == 0)
        {
            return true;
        }

        var stillLosing = effects.Losses(changes, saved, moveTargetLast: true);
        var canMove = stillLosing.Count < losses.Count;
        var shown = losses.Take(6).Select(l => $"• {l.What}: {l.WinnerName} sets {l.WinnerValue}" + (l.ByPick ? " (your pick in Check the merge)." : " (later in the merge list)."));
        var more = losses.Count > 6 ? $"\n• and {losses.Count - 6} more" : "";
        var name = SelectedTarget?.Display ?? folder;
        var message = $"Another turned-on mod still wins {(losses.Count == 1 ? "a value" : $"{losses.Count} values")} you're saving, so the game won't use yours:\n"
            + string.Join("\n", shown) + more + "\n\n"
            + (canMove
                ? $"Move my mod last: saves, then puts '{name}' at the end of the merge list in Merge & Install, so its values win."
                    + (stillLosing.Count > 0 ? $" {stillLosing.Count} value(s) still lose to your picks in Check the merge." : "") + "\n"
                : "A pick in Check the merge decides these; change it there to use your values.\n")
            + "Save anyway: saves; the other mod's values stay in the game.";
        switch (AskWhenValueLoses(message, canMove))
        {
            case null:
                StatusMessage = "Not saved. Your typed changes are still here.";
                return false;
            case true:
                moveLast = canMove;
                return true;
            default:
                return true;
        }
    }

    /// <summary>After a Save chose "Move my mod last": moves it in Merge &amp; Install's list, and says what happened.</summary>
    private string MoveSavedModLast(string folder, string name) =>
        _queueReader?.MoveToEnd(folder) == true
            ? $" '{name}' is now last in the merge list, so its values win."
            : $" Couldn't move '{name}' in the merge list (a build may be running): move it down in Merge & Install.";
}
