using System.Text.Json.Nodes;

namespace IcarusStarlink.PakIO.Exmod;

public static partial class ModHealthChecker
{
    /// <summary>
    /// What the game can't use as the mod writes it, whatever game version the mod was made for: a
    /// table the game doesn't have (the merge skips its rows), a field the table doesn't have or a
    /// value of another kind (the game ignores it), and text under another row's text key.
    /// </summary>
    private static IEnumerable<ModIssue> FormatIssues(ExmodPackage package, GameTableIndex gameIndex)
    {
        // Without extracted game data every table looks unknown; the rest of the check says so.
        if (!gameIndex.HasAnyTable())
        {
            return [];
        }

        var rows = package.Rows.Where(r => !ExmodSentinelFiles.IsEndOfModMarker(r.CurrentFile)).ToList();
        return [.. UnknownTables(rows, gameIndex), .. FieldIssues(package, gameIndex), .. SharedTextKeys(rows, gameIndex)];
    }

    private static IEnumerable<ModIssue> UnknownTables(IReadOnlyList<ExmodFileRow> rows, GameTableIndex gameIndex)
    {
        foreach (var table in rows.Where(r => gameIndex.Rows(r.CurrentFile) is null).GroupBy(r => r.CurrentFile, StringComparer.OrdinalIgnoreCase))
        {
            var count = table.Sum(r => r.FileItems.Count);
            var name = GameTableIndex.TableNameOf(table.Key);
            var match = gameIndex.CurrentFileFor(name) is { } found && !string.Equals(found, table.Key, StringComparison.OrdinalIgnoreCase) ? found : null;
            var skipped = $"The game has no table '{table.Key}', so the merge skips {(count == 1 ? "its row" : $"all {count} of its rows")}.";
            yield return new ModIssue(ModIssueKind.UnknownTable, table.Key, "", null, null, null, null, match,
                match is null ? ModIssueFix.KeepMod : ModIssueFix.RenameTable, IsSure: match is not null,
                match is null ? skipped : $"{skipped} The game keeps {name} as '{match}'.");
        }
    }

    private static IEnumerable<ModIssue> FieldIssues(ExmodPackage package, GameTableIndex gameIndex)
    {
        foreach (var finding in ExmodFieldValidityChecker.Check(package, gameIndex))
        {
            var value = package.Rows.Where(r => string.Equals(r.CurrentFile, finding.CurrentFile, StringComparison.OrdinalIgnoreCase))
                .SelectMany(r => r.FileItems).LastOrDefault(i => i.Name == finding.ItemName)?.Fields.GetValueOrDefault(finding.FieldName);
            if (finding.IsUnknownField)
            {
                // Matching the game's spelling is safe either way; a close name is only a guess.
                yield return new ModIssue(ModIssueKind.UnknownField, finding.CurrentFile, finding.ItemName, finding.FieldName, value?.DeepClone(), null, null,
                    finding.SuggestedField, finding.SuggestedField is null ? ModIssueFix.KeepMod : ModIssueFix.RenameField, IsSure: finding.IsCaseOnly, finding.Reason);
            }
            else
            {
                var gameValue = gameIndex.Row(finding.CurrentFile, finding.ItemName)?[finding.FieldName] ?? gameIndex.Defaults(finding.CurrentFile)?[finding.FieldName];
                yield return new ModIssue(ModIssueKind.WrongType, finding.CurrentFile, finding.ItemName, finding.FieldName, value?.DeepClone(), gameValue?.DeepClone(), null, null,
                    ModIssueFix.KeepMod, IsSure: false, finding.Reason);
            }
        }
    }

    /// <summary>
    /// Text whose key ("&lt;Row&gt;-&lt;Field&gt;") belongs to another row that has other words: a row
    /// copied from another without its key changed. Reusing a game row's key with the game's own
    /// words is fine (the text and its translations are the game's), and so is any key nothing
    /// else uses.
    /// </summary>
    private static IEnumerable<ModIssue> SharedTextKeys(IReadOnlyList<ExmodFileRow> rows, GameTableIndex gameIndex)
    {
        var texts = new List<(string File, string Item, string Field, JsonNode Value, string Table, string Key, string Text)>();
        foreach (var row in rows)
        {
            foreach (var item in row.FileItems.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()))
            {
                foreach (var (field, value) in item.Fields)
                {
                    if (value is JsonValue v && v.TryGetValue<string>(out var s) && NsLocText.TryParse(s, out var table, out var key, out var text))
                    {
                        texts.Add((row.CurrentFile, item.Name, field, value, table, key, text));
                    }
                }
            }
        }

        var modTexts = texts.ToLookup(t => $"{t.Table}|{t.Key}", StringComparer.OrdinalIgnoreCase);
        foreach (var t in texts)
        {
            var ownKey = $"{t.Item}-{t.Field}";
            if (string.Equals(t.Key, ownKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The game row the key belongs to, unless the mod sets that text itself: the game's own
            // words under it are a fine reuse, other words a clash.
            string? owner = null;
            var dash = t.Key.LastIndexOf('-');
            if (dash > 0)
            {
                var (keyRow, keyField) = (t.Key[..dash], t.Key[(dash + 1)..]);
                var file = gameIndex.CurrentFileFor(t.Table) ?? t.File;
                var modSetsIt = texts.Any(o => string.Equals(o.Item, keyRow, StringComparison.OrdinalIgnoreCase) && o.Field == keyField
                    && string.Equals(o.File, file, StringComparison.OrdinalIgnoreCase));
                if (!modSetsIt && gameIndex.Row(file, keyRow)?[keyField] is JsonValue game && game.TryGetValue<string>(out var gameText)
                    && NsLocText.TryParse(gameText, out _, out var gameKey, out var gameWords)
                    && string.Equals(gameKey, t.Key, StringComparison.OrdinalIgnoreCase))
                {
                    if (gameWords == t.Text)
                    {
                        continue;
                    }

                    owner = $"the game's '{keyRow}'";
                }
            }

            // Otherwise another of the mod's own texts under this key with other words.
            if (owner is null)
            {
                var other = modTexts[$"{t.Table}|{t.Key}"].FirstOrDefault(o => o.Text != t.Text && !(o.Item == t.Item && o.Field == t.Field));
                if (other.Item is null)
                {
                    continue;
                }

                owner = $"'{other.Item}'";
            }

            var rekeyed = NsLocText.WithKey(((JsonValue)t.Value).GetValue<string>(), ownKey);
            yield return new ModIssue(ModIssueKind.SharedTextKey, t.File, t.Item, t.Field, t.Value.DeepClone(), null,
                rekeyed is null ? null : JsonValue.Create(rekeyed), ownKey, ModIssueFix.OwnTextKey, IsSure: false,
                $"Uses the text key of {owner} ('{t.Key}'), which has other words. Two texts under one key can show one in place of the other in game; its own key '{ownKey}' keeps these words.");
        }
    }

    /// <summary>Moves a table's rows to the CurrentFile the game has (UnknownTable), joining that table's entry when the mod has one already.</summary>
    private static bool MoveTable(ExmodPackage package, string from, string to)
    {
        var moving = package.Rows.Where(r => string.Equals(r.CurrentFile, from, StringComparison.OrdinalIgnoreCase)).ToList();
        if (moving.Count == 0)
        {
            return false;
        }

        if (package.Rows.FirstOrDefault(r => string.Equals(r.CurrentFile, to, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            foreach (var row in moving)
            {
                existing.FileItems.AddRange(row.FileItems);
                package.Rows.Remove(row);
            }
        }
        else
        {
            moving.ForEach(r => r.CurrentFile = to);
        }

        return true;
    }
}
