using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>A prospect type the server can create: "Olympus (Open world)" for OpenWorld_Olympus.</summary>
public sealed record ProspectChoice(string Name, string Label, string Kind)
{
    public string Display => Name.Length == 0 ? Label : $"{Label}  ·  {Kind}";

    public bool IsOutpost => Kind == "Outpost";
}

/// <summary>A difficulty for a new prospect: the server takes 1 (easy) to 4 (extreme).</summary>
public sealed record ProspectDifficulty(int Value, string Label);

/// <summary>
/// "Create prospect on start" as choices instead of a typed code. The server's CreateProspect takes
/// "[ProspectType] [Difficulty 1-4] [Hardcore True/False] [SaveName]" (RocketWerkz's server wiki,
/// e.g. "Tier1_Forest_Recon_0 3 false TestProspect01"); the prospect types are the game's own
/// D_ProspectList rows, read from the extracted game data. Without game data, the text box stays.
/// </summary>
public sealed partial class LocalServerViewModel
{
    private static readonly ProspectChoice NoProspect = new("", "Don't create one", "");
    private IReadOnlyList<ProspectChoice>? _prospectChoices;
    private bool _syncingProspect;

    /// <summary>The extracted game data (for the prospect list); set by the app.</summary>
    public string? DataFolder { get; init; }

    public IReadOnlyList<ProspectDifficulty> ProspectDifficulties { get; } =
        [new(1, "Easy"), new(2, "Medium"), new(3, "Hard"), new(4, "Extreme")];

    /// <summary>"Don't create one", then the open worlds, outposts and missions the game has.</summary>
    public IReadOnlyList<ProspectChoice> ProspectChoices => _prospectChoices ??= LoadProspectChoices();

    /// <summary>Game data has the list: show the choices; otherwise the typed text box.</summary>
    public bool HasProspectChoices => ProspectChoices.Count > 1;

    public bool HasNoProspectChoices => !HasProspectChoices;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreatingProspect), nameof(ProspectHint))]
    private ProspectChoice? _newProspectType;

    [ObservableProperty]
    private ProspectDifficulty? _newProspectDifficulty;

    [ObservableProperty]
    private bool _newProspectHardcore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProspectHint))]
    private string _newProspectName = "";

    public bool IsCreatingProspect => NewProspectType is { Name.Length: > 0 };

    public string ProspectHint =>
        NewProspectType is { IsOutpost: true } && string.IsNullOrWhiteSpace(NewProspectName)
            ? "An outpost needs a world name."
            : "Set this back to \"Don't create one\" after the first start, or every restart makes a fresh world.";

    partial void OnNewProspectTypeChanged(ProspectChoice? value) => ComposeCreateProspect();

    partial void OnNewProspectDifficultyChanged(ProspectDifficulty? value) => ComposeCreateProspect();

    partial void OnNewProspectHardcoreChanged(bool value) => ComposeCreateProspect();

    partial void OnNewProspectNameChanged(string value) => ComposeCreateProspect();

    /// <summary>The setting the ini holds, from the choices: "OpenWorld_Olympus 2 False MyWorld".</summary>
    private void ComposeCreateProspect()
    {
        if (_syncingProspect)
        {
            return;
        }

        _syncingProspect = true;
        var name = Regex.Replace(NewProspectName.Trim(), @"\s+", "_");
        CreateProspect = IsCreatingProspect
            ? $"{NewProspectType!.Name} {(NewProspectDifficulty ?? ProspectDifficulties[1]).Value} {(NewProspectHardcore ? "True" : "False")}{(name.Length > 0 ? " " + name : "")}"
            : "";
        _syncingProspect = false;
    }

    /// <summary>The choices, from the setting (loaded from the ini, or typed when there's no list).</summary>
    partial void OnCreateProspectChanged(string value)
    {
        if (_syncingProspect)
        {
            return;
        }

        _syncingProspect = true;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        NewProspectType = parts.Length == 0 ? NoProspect
            : ProspectChoices.FirstOrDefault(c => string.Equals(c.Name, parts[0], StringComparison.OrdinalIgnoreCase))
              ?? new ProspectChoice(parts[0], parts[0], "not in your game data");
        NewProspectDifficulty = parts.Length > 1 && int.TryParse(parts[1], out var d) ? ProspectDifficulties.FirstOrDefault(x => x.Value == d) ?? ProspectDifficulties[1] : ProspectDifficulties[1];
        NewProspectHardcore = parts.Length > 2 && bool.TryParse(parts[2], out var hardcore) && hardcore;
        NewProspectName = parts.Length > 3 ? string.Join(' ', parts[3..]) : "";
        _syncingProspect = false;
    }

    private List<ProspectChoice> LoadProspectChoices()
    {
        var choices = new List<ProspectChoice> { NoProspect };
        if (DataFolder is null || !Directory.Exists(DataFolder))
        {
            return choices;
        }

        try
        {
            var game = new GameTableIndex(DataFolder);
            if (game.CurrentFileFor("D_ProspectList") is not { } file)
            {
                return choices;
            }

            choices.AddRange((game.Rows(file) ?? [])
                .Select(kv =>
                {
                    var row = kv.Value as JsonObject;
                    var kind = row?["bIsOpenWorld"]?.GetValue<bool>() == true ? "Open world"
                        : kv.Key.StartsWith("Outpost", StringComparison.OrdinalIgnoreCase) ? "Outpost"
                        : "Mission";
                    var label = NsLocText.TextOf(row?["DropName"]?.GetValue<string>())?.Trim() is { Length: > 0 } shown ? shown : kv.Key.Replace('_', ' ');
                    return new ProspectChoice(kv.Key, label, kind);
                })
                .OrderBy(c => c.Kind switch { "Open world" => 0, "Outpost" => 1, _ => 2 })
                .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // No list: the typed text box is shown instead.
        }

        return choices;
    }
}
