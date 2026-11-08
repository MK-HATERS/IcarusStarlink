using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Memory;

namespace IcarusStarlink.App.Services;

/// <summary>
/// One shared, app-lifetime cache of the two indexes built over the whole extracted data folder
/// (~300 files, ~43,000 rows): the row names ("Add item from game data", Find the broken mod) and
/// the rows' JSON ("Search game data"). A DI singleton, so the first caller pays the cost and every
/// later one (every open mod editor, every reopen) is instant. The names alone are built when only
/// they are asked for; when search is asked, one pass builds both. Invalidate() runs on the
/// WeeklyChangeReportUpdatedMessage a successful Refresh game data sends, so a stale index never
/// survives a game update, and both are let go while the app sits unused (IdleMemory).
/// </summary>
public sealed class GameDataIndexCache : IIdleReleasable
{
    private readonly object _lock = new();

    /// <summary>The row names alone (built when no search index was asked for).</summary>
    private Task<IReadOnlyList<GameDataItemRef>>? _itemsTask;

    /// <summary>
    /// Both indexes from ONE pass over the data folder: reading and parsing the files is the cost
    /// (~650 ms against the real ~300-file folder), so a session that uses both pays it once.
    /// </summary>
    private Task<(IReadOnlyList<GameDataItemRef> Items, IReadOnlyList<GameDataSearchEntry> Search)>? _searchTask;

    private DateTime _lastUsedUtc;

    public GameDataIndexCache()
    {
        WeakReferenceMessenger.Default.Register<WeeklyChangeReportUpdatedMessage>(this, (recipient, _) => ((GameDataIndexCache)recipient).Invalidate());
        IdleMemory.Register(this);
    }

    /// <summary>True once the rows' JSON is held (Search game data was used).</summary>
    internal bool HasSearchIndex
    {
        get
        {
            lock (_lock)
            {
                return _searchTask is not null;
            }
        }
    }

    public async Task<IReadOnlyList<GameDataItemRef>> GetItemIndexAsync(string dataFolder)
    {
        Task<IReadOnlyList<GameDataItemRef>> items;
        lock (_lock)
        {
            DropFaulted();
            _lastUsedUtc = DateTime.UtcNow;
            // The search pass holds the names too: no second pass when it was asked first.
            items = _searchTask is { } search
                ? ItemsOf(search)
                : _itemsTask ??= Task.Run(() => BuildIndexes(dataFolder, withSearch: false).Items);
        }

        return await items;
    }

    public async Task<IReadOnlyList<GameDataSearchEntry>> GetSearchIndexAsync(string dataFolder)
    {
        Task<(IReadOnlyList<GameDataItemRef> Items, IReadOnlyList<GameDataSearchEntry> Search)> search;
        lock (_lock)
        {
            DropFaulted();
            _lastUsedUtc = DateTime.UtcNow;
            if (_searchTask is null)
            {
                _searchTask = Task.Run(() => BuildIndexes(dataFolder, withSearch: true));
                // The search pass carries the names too; a names-only build already handed out stays with its callers.
                _itemsTask = null;
            }

            search = _searchTask;
        }

        return (await search).Search;
    }

    /// <summary>Drops the cached indexes so the next request rebuilds from whatever's currently on disk — call after a real game-data change, not on a timer.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _itemsTask = null;
            _searchTask = null;
        }
    }

    /// <summary>Lets both indexes go when nothing asked for them for <paramref name="idleFor"/>; the next request builds them again.</summary>
    public bool ReleaseIfIdle(TimeSpan idleFor)
    {
        lock (_lock)
        {
            if ((_itemsTask is null && _searchTask is null)
                || _itemsTask is { IsCompleted: false } || _searchTask is { IsCompleted: false }
                || DateTime.UtcNow - _lastUsedUtc < idleFor)
            {
                return false;
            }

            _itemsTask = null;
            _searchTask = null;
            return true;
        }
    }

    /// <summary>
    /// A build that faulted (a file locked mid-extraction, say) is retried on the next call rather
    /// than cached as a permanent failure until the next successful Refresh game data. Under _lock.
    /// </summary>
    private void DropFaulted()
    {
        if (_itemsTask is { IsFaulted: true })
        {
            _itemsTask = null;
        }

        if (_searchTask is { IsFaulted: true })
        {
            _searchTask = null;
        }
    }

    private static async Task<IReadOnlyList<GameDataItemRef>> ItemsOf(Task<(IReadOnlyList<GameDataItemRef> Items, IReadOnlyList<GameDataSearchEntry> Search)> search) =>
        (await search).Items;

    /// <summary>
    /// One pass over the extracted data folder: the row names, and with <paramref name="withSearch"/>
    /// each row's JSON too. A file that isn't DataTable-shaped (no Rows array, or not JSON at all) is
    /// skipped, not an error.
    /// </summary>
    private static (IReadOnlyList<GameDataItemRef> Items, IReadOnlyList<GameDataSearchEntry> Search) BuildIndexes(string dataFolder, bool withSearch)
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
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Not DataTable-shaped, or unreadable right now — skipped, not an error (the read
                // failures used to fault the whole build).
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
                    if (withSearch)
                    {
                        // Readable ("+", quotes, accents as they are), so a plain text search finds them.
                        search.Add(new GameDataSearchEntry(realPath, name, row.ToJsonString(ValueDiff.CompactDisplay)));
                    }
                }
            }
        }

        return (items, search);
    }
}
