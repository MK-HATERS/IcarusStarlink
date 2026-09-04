using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Services;

/// <summary>
/// One shared, app-lifetime cache of the two indexes ExmodEditorViewModel's "Add item from game
/// data…"/"Search game data…" build over the whole extracted data folder (~300 files, ~43,000
/// rows). Before this existed, every open editor window built its own copy from scratch the first
/// time either feature was used — harmless with one mod open, real duplicated parsing (and
/// memory, since GameDataSearchEntry keeps each row's serialized JSON) with several editors open
/// at once for different mods. A DI singleton shared across every ExmodEditorViewModel instance,
/// so the first editor to ask pays the cost and every later one (and every later reopen) is
/// instant, until Invalidate() runs — wired to the same WeeklyChangeReportUpdatedMessage a
/// successful "Update data folder" already broadcasts, so a stale index never survives a real game
/// update.
/// </summary>
public sealed class GameDataIndexCache
{
    private readonly object _lock = new();

    /// <summary>
    /// Both indexes come from ONE shared build over the data folder now — they used to each run
    /// their own independent Directory.EnumerateFiles/File.ReadAllText/JsonNode.Parse pass, so a
    /// session that used both "Add item from game data" and "Search game data" paid the dominant
    /// file-read+parse cost twice (measured ~650-670ms per pass against this app's real ~300-file
    /// data folder). GetItemIndexAsync/GetSearchIndexAsync both project out of this single task.
    /// </summary>
    private Task<(IReadOnlyList<GameDataItemRef> Items, IReadOnlyList<GameDataSearchEntry> Search)>? _indexesTask;

    public GameDataIndexCache() =>
        WeakReferenceMessenger.Default.Register<WeeklyChangeReportUpdatedMessage>(this, (recipient, _) => ((GameDataIndexCache)recipient).Invalidate());

    public async Task<IReadOnlyList<GameDataItemRef>> GetItemIndexAsync(string dataFolder) => (await GetIndexesAsync(dataFolder)).Items;

    public async Task<IReadOnlyList<GameDataSearchEntry>> GetSearchIndexAsync(string dataFolder) => (await GetIndexesAsync(dataFolder)).Search;

    /// <summary>Drops the cached indexes so the next request rebuilds from whatever's currently on disk — call after a real game-data change, not on a timer.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _indexesTask = null;
        }
    }

    private Task<(IReadOnlyList<GameDataItemRef> Items, IReadOnlyList<GameDataSearchEntry> Search)> GetIndexesAsync(string dataFolder)
    {
        lock (_lock)
        {
            return _indexesTask ??= Task.Run(() => BuildIndexes(dataFolder));
        }
    }

    /// <summary>One pass over the extracted data folder, producing both indexes together — a file that isn't DataTable-shaped (no Rows array, or not JSON at all) is skipped, not an error.</summary>
    private static (IReadOnlyList<GameDataItemRef> Items, IReadOnlyList<GameDataSearchEntry> Search) BuildIndexes(string dataFolder)
    {
        var items = new List<GameDataItemRef>();
        var search = new List<GameDataSearchEntry>();
        if (!Directory.Exists(dataFolder))
        {
            return (items, search);
        }

        foreach (var filePath in Directory.EnumerateFiles(dataFolder, "*.json", SearchOption.AllDirectories))
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(File.ReadAllText(filePath));
            }
            catch (JsonException)
            {
                continue;
            }

            if (parsed is not JsonObject fileObject || fileObject["Rows"] is not JsonArray rows)
            {
                continue;
            }

            var realPath = Path.GetRelativePath(dataFolder, filePath).Replace('\\', '/');
            var currentFile = realPath.Replace('/', '-');
            foreach (var rowNode in rows)
            {
                if (rowNode is JsonObject row && row["Name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var name))
                {
                    items.Add(new GameDataItemRef(currentFile, realPath, name));
                    search.Add(new GameDataSearchEntry(realPath, name, row.ToJsonString()));
                }
            }
        }

        return (items, search);
    }
}
