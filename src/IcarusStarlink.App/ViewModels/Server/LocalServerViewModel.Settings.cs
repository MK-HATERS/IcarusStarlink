using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.ViewModels;

/// <summary>The Settings form: launch options (stored in the profile) and ServerSettings.ini (the server's own file), loaded on select and written on Save.</summary>
public sealed partial class LocalServerViewModel
{
    // ---- Launch options (stored in the profile) ----

    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string _steamServerName = "";
    [ObservableProperty] private string _portText = "";
    [ObservableProperty] private string _queryPortText = "";
    [ObservableProperty] private string _multihome = "";
    [ObservableProperty] private string _savedDirSuffix = "";
    [ObservableProperty] private string _logFileName = "";

    public string InstallFolder => SelectedProfile?.InstallFolder ?? "";

    // ---- ServerSettings.ini (stored in the server's own file) ----

    [ObservableProperty] private string _joinPassword = "";
    [ObservableProperty] private string _adminPassword = "";
    [ObservableProperty] private string _maxPlayersText = "";
    [ObservableProperty] private string _shutdownIfNotJoinedForText = "";
    [ObservableProperty] private string _shutdownIfEmptyForText = "";
    [ObservableProperty] private string _gameSaveFrequencyText = "";
    [ObservableProperty] private string _loadProspect = "";
    [ObservableProperty] private string _createProspect = "";
    [ObservableProperty] private bool? _allowNonAdminsToLaunchProspects;
    [ObservableProperty] private bool? _allowNonAdminsToDeleteProspects;
    [ObservableProperty] private bool? _resumeProspect;
    [ObservableProperty] private bool? _saveGameOnExit;
    [ObservableProperty] private bool? _fiberFoliageRespawn;
    [ObservableProperty] private bool? _largeStonesRespawn;

    /// <summary>Written by the server — shown, never edited.</summary>
    [ObservableProperty] private string? _lastProspectName;

    public ObservableCollection<string> Prospects { get; } = [];

    /// <summary>The Worlds tab's list: the same worlds with their date, size and which one loads on start.</summary>
    public ObservableCollection<ServerProspectRow> ProspectRows { get; } = [];

    partial void OnLoadProspectChanged(string value) => MarkProspectRows();

    partial void OnLastProspectNameChanged(string? value) => MarkProspectRows();

    private void FillProspects(IReadOnlyList<ProspectFile> files)
    {
        // The names feed the editable Load prospect box: only touched when they change, so a
        // refresh doesn't knock what's typed there.
        if (!files.Select(f => f.Name).SequenceEqual(Prospects))
        {
            Prospects.Clear();
            foreach (var file in files)
            {
                Prospects.Add(file.Name);
            }
        }

        ProspectRows.Clear();
        foreach (var file in files)
        {
            ProspectRows.Add(new ServerProspectRow(file));
        }

        MarkProspectRows();
    }

    private void MarkProspectRows()
    {
        var load = LoadProspect.Trim();
        foreach (var row in ProspectRows)
        {
            row.IsLoadOnStart = load.Length > 0 && string.Equals(row.Name, load, StringComparison.OrdinalIgnoreCase);
            row.IsLastPlayed = string.Equals(row.Name, LastProspectName?.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The World settings tab: each saved world's custom settings.</summary>
    public WorldSettingsViewModel WorldSettings { get; }

    /// <summary>The ini key every guide warns about: empty means anyone who types /AdminLogin becomes admin.</summary>
    public bool AdminPasswordIsEmpty => string.IsNullOrWhiteSpace(AdminPassword);

    partial void OnAdminPasswordChanged(string value) => OnPropertyChanged(nameof(AdminPasswordIsEmpty));

    public string SettingsIniPath => SelectedProfile is null ? "" : DedicatedServerLayout.SettingsIniPath(SelectedProfile.InstallFolder, SavedDirSuffix);

    /// <summary>The exact line Start runs, rebuilt from the form as you type.</summary>
    public string CommandLinePreview => SelectedProfile is null ? "" : DedicatedServerCommandLine.BuildDisplayLine(BuildProfileFromForm(SelectedProfile, strict: false) ?? SelectedProfile);

    partial void OnSteamServerNameChanged(string value) => OnPropertyChanged(nameof(CommandLinePreview));
    partial void OnPortTextChanged(string value) => OnPropertyChanged(nameof(CommandLinePreview));
    partial void OnQueryPortTextChanged(string value) => OnPropertyChanged(nameof(CommandLinePreview));
    partial void OnMultihomeChanged(string value) => OnPropertyChanged(nameof(CommandLinePreview));
    partial void OnLogFileNameChanged(string value) => OnPropertyChanged(nameof(CommandLinePreview));

    partial void OnSavedDirSuffixChanged(string value)
    {
        OnPropertyChanged(nameof(CommandLinePreview));
        OnPropertyChanged(nameof(SettingsIniPath));
    }

    /// <summary>The server's ServerSettings.ini as it is on disk now (not the form, which may hold unsaved edits); null when it can't be read.</summary>
    private static DedicatedServerSettings? ReadIniSettings(DedicatedServerProfile p)
    {
        try
        {
            return ServerSettingsIni.Load(DedicatedServerLayout.SettingsIniPath(p.InstallFolder, p.SavedDirSuffix)).Read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void LoadForm()
    {
        ValidationMessage = null;
        Prospects.Clear();
        ProspectRows.Clear();
        if (SelectedProfile is not { } p)
        {
            ProfileName = SteamServerName = PortText = QueryPortText = Multihome = SavedDirSuffix = LogFileName = "";
            LoadAutomation(null);
            JoinPassword = AdminPassword = MaxPlayersText = ShutdownIfNotJoinedForText = ShutdownIfEmptyForText = GameSaveFrequencyText = LoadProspect = CreateProspect = "";
            OnPropertyChanged(nameof(InstallFolder));
            return;
        }

        ProfileName = p.Name;
        LoadAutomation(p);
        Branch = SteamCmd.NormalizeBranch(p.Branch);
        SteamServerName = p.SteamServerName;
        PortText = p.Port.ToString(CultureInfo.InvariantCulture);
        QueryPortText = p.QueryPort.ToString(CultureInfo.InvariantCulture);
        Multihome = p.Multihome;
        SavedDirSuffix = p.SavedDirSuffix;
        LogFileName = p.LogFileName;

        // Guarded like ReadIniSettings: this runs inside the SelectedProfile setter, so a locked or
        // unreadable ini used to throw straight out of a binding.
        var s = ReadIniSettings(p);
        _iniUnreadable = s is null;
        if (s is null)
        {
            ValidationMessage = "Couldn't read this server's ServerSettings.ini, so the game settings below are blank. Check the file before you save.";
            s = new DedicatedServerSettings();
        }

        ApplyIniToForm(s);
        RememberIniOnDisk(_iniUnreadable ? null : DedicatedServerLayout.SettingsIniPath(p.InstallFolder, p.SavedDirSuffix));
        FillProspects(ReadProspectFiles(p));

        WorldSettings.Reload();
        OnPropertyChanged(nameof(InstallFolder));
        OnPropertyChanged(nameof(SettingsIniPath));
        OnPropertyChanged(nameof(CommandLinePreview));
        OnPropertyChanged(nameof(LogPath));
    }

    /// <summary>
    /// The form's game settings came from an ini that couldn't be read (they are blanks, not the
    /// server's settings): Save and Start must not write them over the real file.
    /// </summary>
    private bool _iniUnreadable;

    private void ApplyIniToForm(DedicatedServerSettings s)
    {
        JoinPassword = s.JoinPassword ?? "";
        AdminPassword = s.AdminPassword ?? "";
        MaxPlayersText = s.MaxPlayers?.ToString(CultureInfo.InvariantCulture) ?? "";
        ShutdownIfNotJoinedForText = FormatNumber(s.ShutdownIfNotJoinedFor);
        ShutdownIfEmptyForText = FormatNumber(s.ShutdownIfEmptyFor);
        GameSaveFrequencyText = FormatNumber(s.GameSaveFrequency);
        LoadProspect = s.LoadProspect ?? "";
        CreateProspect = s.CreateProspect ?? "";
        AllowNonAdminsToLaunchProspects = s.AllowNonAdminsToLaunchProspects;
        AllowNonAdminsToDeleteProspects = s.AllowNonAdminsToDeleteProspects;
        ResumeProspect = s.ResumeProspect;
        SaveGameOnExit = s.SaveGameOnExit;
        FiberFoliageRespawn = s.FiberFoliageRespawn;
        LargeStonesRespawn = s.LargeStonesRespawn;
        LastProspectName = s.LastProspectName;
    }

    private static IReadOnlyList<ProspectFile> ReadProspectFiles(DedicatedServerProfile p)
    {
        try
        {
            return DedicatedServerLayout.ListProspectFiles(p.InstallFolder, p.SavedDirSuffix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Re-reads the saved worlds and LastProspectName without reloading the rest of the form (which
    /// would throw away unsaved edits). Used when the server starts or stops.
    /// </summary>
    private void RefreshProspectList(DedicatedServerProfile p)
    {
        try
        {
            LastProspectName = ServerSettingsIni.Load(DedicatedServerLayout.SettingsIniPath(p.InstallFolder, p.SavedDirSuffix)).Read().LastProspectName;
            // The rows are always refilled: a world's date and size change even when the names don't.
            FillProspects(DedicatedServerLayout.ListProspectFiles(p.InstallFolder, p.SavedDirSuffix));

            // The server just wrote its worlds: show what they hold now.
            WorldSettings.Reload();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The server may still hold the files — the next start/stop tries again.
        }
    }

    /// <summary>Validates the form; on success writes the profile and the ini. Returns false (with ValidationMessage set) when anything is out of range.</summary>
    internal bool TrySave()
    {
        if (SelectedProfile is not { } current)
        {
            return false;
        }

        // A locked ini on select left the game settings blank; written as they are, Save (and
        // Start, which saves first) wiped the passwords, the prospect and every other setting.
        if (_iniUnreadable)
        {
            if (ReadIniSettings(current) is not { } onDisk)
            {
                ValidationMessage = "ServerSettings.ini still can't be read, so nothing was saved: it would have replaced the server's passwords and settings with the blank form. Close whatever has the file open, then try again.";
                return false;
            }

            _iniUnreadable = false;
            ApplyIniToForm(onDisk);
            RememberIniOnDisk(DedicatedServerLayout.SettingsIniPath(current.InstallFolder, current.SavedDirSuffix));
            ValidationMessage = "ServerSettings.ini can be read now, so its settings are loaded below in place of the blanks. Check them, then save again.";
            return false;
        }

        // The form was read when the server was selected; the file may have been edited by hand
        // (or rewritten by the server) since. A clean form takes the newer file first; edits here
        // ask before they are written over it.
        var ini = ReconcileIniWithDisk(DedicatedServerLayout.SettingsIniPath(current.InstallFolder, SavedDirSuffix.Trim()), ask: true);
        if (ini == IniReconcile.Cancelled)
        {
            return false;
        }

        var updated = BuildProfileFromForm(current, strict: true);
        var settings = updated is null ? null : BuildSettingsFromForm();
        if (updated is null || settings is null)
        {
            return false;
        }

        if (Profiles.Any(p => p.Id != updated.Id
                && string.Equals(p.InstallFolder, updated.InstallFolder, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.SavedDirSuffix, updated.SavedDirSuffix, StringComparison.OrdinalIgnoreCase)))
        {
            ValidationMessage = "Another profile already uses this install with the same Saved suffix — give this one its own suffix (e.g. _2) so their worlds and settings don't collide.";
            return false;
        }

        try
        {
            var iniPath = DedicatedServerLayout.SettingsIniPath(updated.InstallFolder, updated.SavedDirSuffix);
            var file = ServerSettingsIni.Load(iniPath);
            file.Write(settings);
            file.Save(iniPath);
            RememberIniOnDisk(iniPath);
        }
        catch (Exception ex)
        {
            ValidationMessage = $"Couldn't write ServerSettings.ini: {ex.Message}";
            return false;
        }

        var index = Profiles.IndexOf(current);
        var stored = SaveToStore(updated);
        SyncFirewallPortsAfterSave(current, updated);

        // Swap the list entry and re-select it without reloading the form from disk (it already
        // holds what was saved) — the ListBox briefly pushes a null selection while the item is
        // replaced, which must not clear the form either.
        _suppressFormReload = true;
        try
        {
            if (index >= 0)
            {
                Profiles[index] = updated;
            }

            SelectedProfile = updated;
        }
        finally
        {
            _suppressFormReload = false;
        }

        OnPropertyChanged(nameof(LogPath));
        AttachLog();

        ValidationMessage = null;
        if (stored)
        {
            StatusMessage = (Running is not null ? "Saved. Restart the server to apply." : "Saved.")
                + (ini == IniReconcile.Reloaded ? " ServerSettings.ini had been changed outside Starlink, so its settings were loaded into the form first." : "");
        }
        return true;
    }

    private DedicatedServerProfile? BuildProfileFromForm(DedicatedServerProfile current, bool strict)
    {
        string? error = null;
        if (!TryParsePort(PortText, out var port))
        {
            error = "Game port must be a number from 1 to 65535.";
        }
        else if (!TryParsePort(QueryPortText, out var queryPort))
        {
            error = "Query port must be a number from 1 to 65535.";
        }
        else if (port == queryPort)
        {
            error = "Game port and query port must differ.";
        }
        else if (string.IsNullOrWhiteSpace(ProfileName))
        {
            error = "Give the profile a name.";
        }
        else if (SavedDirSuffix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || LogFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "Saved suffix and log file name can't contain \\ / : * ? \" < > |.";
        }
        else
        {
            var built = new DedicatedServerProfile
            {
                Id = current.Id,
                Name = ProfileName.Trim(),
                InstallFolder = current.InstallFolder,
                SteamServerName = DedicatedServerCommandLine.Sanitize(SteamServerName),
                Port = port,
                QueryPort = queryPort,
                Multihome = Multihome.Trim(),
                SavedDirSuffix = SavedDirSuffix.Trim(),
                LogFileName = LogFileName.Trim(),
                Branch = SteamCmd.NormalizeBranch(Branch),
            };
            CopyAutomation(current, built);
            return built;
        }

        if (strict)
        {
            ValidationMessage = error;
        }

        return null;
    }

    private DedicatedServerSettings? BuildSettingsFromForm()
    {
        int? maxPlayers = null;
        if (MaxPlayersText.Trim().Length > 0)
        {
            if (!int.TryParse(MaxPlayersText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mp)
                || mp < DedicatedServerDefaults.MinPlayers || mp > DedicatedServerDefaults.MaxPlayers)
            {
                ValidationMessage = $"Max players must be {DedicatedServerDefaults.MinPlayers} to {DedicatedServerDefaults.MaxPlayers} (or blank for the game default).";
                return null;
            }

            maxPlayers = mp;
        }

        if (!TryParseOptionalNumber(ShutdownIfNotJoinedForText, out var notJoined)
            || !TryParseOptionalNumber(ShutdownIfEmptyForText, out var empty))
        {
            ValidationMessage = "Shutdown timers are seconds: a number, -1 for never, or blank for the game default.";
            return null;
        }

        if (!TryParseOptionalNumber(GameSaveFrequencyText, out var saveFrequency) || saveFrequency <= 0)
        {
            ValidationMessage = "Autosave interval is minutes: a positive number, or blank for the game default.";
            return null;
        }

        return new DedicatedServerSettings
        {
            SessionName = DedicatedServerCommandLine.Sanitize(SteamServerName) is { Length: > 0 } n ? n : null,
            JoinPassword = Blank(JoinPassword),
            AdminPassword = Blank(AdminPassword),
            MaxPlayers = maxPlayers,
            ShutdownIfNotJoinedFor = notJoined,
            ShutdownIfEmptyFor = empty,
            GameSaveFrequency = saveFrequency,
            LoadProspect = Blank(LoadProspect),
            CreateProspect = Blank(CreateProspect),
            AllowNonAdminsToLaunchProspects = AllowNonAdminsToLaunchProspects,
            AllowNonAdminsToDeleteProspects = AllowNonAdminsToDeleteProspects,
            ResumeProspect = ResumeProspect,
            SaveGameOnExit = SaveGameOnExit,
            FiberFoliageRespawn = FiberFoliageRespawn,
            LargeStonesRespawn = LargeStonesRespawn,
        };
    }

    private static bool TryParsePort(string text, out int port) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;

    private static bool TryParseOptionalNumber(string text, out double? value)
    {
        value = null;
        if (text.Trim().Length == 0)
        {
            return true;
        }

        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            value = v;
            return true;
        }

        return false;
    }

    private static string FormatNumber(double? value) =>
        value is null ? "" : value.Value.ToString(value.Value % 1 == 0 ? "0" : "0.###", CultureInfo.InvariantCulture);

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // ---- ServerSettings.ini changed on disk since the form read it ----

    /// <summary>What the ini looked like on disk when the form last read or wrote it.</summary>
    private readonly record struct IniStamp(string Path, bool Exists, long Length, DateTime WriteUtc, string? Hash);

    /// <summary>The ini as the form last read or wrote it; null when it couldn't be read (the unreadable-ini guard covers that).</summary>
    private IniStamp? _iniStamp;

    /// <summary>The form's ini fields as they were then: unequal to FormIniSnapshot() means unsaved edits.</summary>
    private string? _iniFormSnapshot;

    internal enum IniReconcile { Unchanged, Reloaded, KeptMine, Cancelled }

    private static IniStamp StampIni(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return new IniStamp(path, false, 0, default, null);
        }

        string? hash = null;
        try
        {
            hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked right now: the size and time still tell a change apart.
        }

        return new IniStamp(path, true, info.Length, info.LastWriteTimeUtc, hash);
    }

    private static bool SameIni(IniStamp a, IniStamp b) =>
        a.Exists == b.Exists && a.Length == b.Length && a.WriteUtc == b.WriteUtc
        && (a.Hash is null || b.Hash is null || a.Hash == b.Hash);

    /// <summary>The ini fields the form edits (LastProspectName is the server's, shown only; the session name comes from the profile).</summary>
    private string FormIniSnapshot() => string.Join("\u001f",
        JoinPassword, AdminPassword, MaxPlayersText, ShutdownIfNotJoinedForText, ShutdownIfEmptyForText, GameSaveFrequencyText, LoadProspect, CreateProspect,
        AllowNonAdminsToLaunchProspects, AllowNonAdminsToDeleteProspects, ResumeProspect, SaveGameOnExit, FiberFoliageRespawn, LargeStonesRespawn);

    /// <summary>The form has ini settings typed in that aren't in the file yet.</summary>
    internal bool HasUnsavedIniEdits => _iniFormSnapshot is not null && _iniFormSnapshot != FormIniSnapshot();

    /// <summary>The form now matches the ini at this path (just read or just written); null forgets it (the file couldn't be read).</summary>
    private void RememberIniOnDisk(string? path)
    {
        _iniStamp = path is null ? null : StampIni(path);
        _iniFormSnapshot = path is null ? null : FormIniSnapshot();
    }

    /// <summary>
    /// Before the form is written to the ini at <paramref name="path"/> (Save, and Start, which
    /// saves first), or when the page is shown: when the file changed since the form read it, a
    /// form with no unsaved ini edits takes the file's settings; one with edits asks (only when
    /// <paramref name="ask"/>: the page being shown never asks, Save does). Cancelled means write nothing.
    /// </summary>
    internal IniReconcile ReconcileIniWithDisk(string path, bool ask)
    {
        if (_iniStamp is not { } stamp || !string.Equals(stamp.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            // Never read (unreadable at select: the guard above handles it), or Save goes to
            // another Saved folder than the one the form came from: nothing to compare with.
            return IniReconcile.Unchanged;
        }

        var now = StampIni(path);
        if (SameIni(stamp, now))
        {
            return IniReconcile.Unchanged;
        }

        DedicatedServerSettings onDisk;
        try
        {
            onDisk = ServerSettingsIni.Load(path).Read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!ask)
            {
                return IniReconcile.Unchanged;
            }

            // The same refusal as an ini unreadable at select: writing now could replace edits we can't see.
            ValidationMessage = "ServerSettings.ini was changed outside Starlink and can't be read right now, so nothing was saved: it would have been written over without a look. Close whatever has the file open, then try again.";
            return IniReconcile.Cancelled;
        }

        if (!HasUnsavedIniEdits)
        {
            ApplyIniToForm(onDisk);
            RememberIniOnDisk(path);
            return IniReconcile.Reloaded;
        }

        if (!ask)
        {
            // Edits here are kept; Save (or Start) asks before either side is lost.
            return IniReconcile.Unchanged;
        }

        var answer = _dialogService.ConfirmWithCancel(
            "ServerSettings.ini was changed outside Starlink (by hand, or by the server itself) since this page read it, and you have unsaved changes here.\n\n" +
            "Reload it: the form takes the file's settings, and your unsaved changes here are lost.\n" +
            "Keep mine: your settings here are written over the file's.",
            "ServerSettings.ini changed", ThemedConfirmSeverity.Warning, "Reload it", "Keep mine");
        switch (answer)
        {
            case true:
                ApplyIniToForm(onDisk);
                RememberIniOnDisk(path);
                return IniReconcile.Reloaded;
            case false:
                return IniReconcile.KeptMine;
            default:
                ValidationMessage = "Nothing was saved. ServerSettings.ini and your changes here are both as they were.";
                return IniReconcile.Cancelled;
        }
    }

    /// <summary>The page was shown: a form with no unsaved edits takes an ini changed meanwhile (never asks).</summary>
    private void ReloadIniIfChangedAndClean()
    {
        if (SelectedProfile is { } p)
        {
            ReconcileIniWithDisk(DedicatedServerLayout.SettingsIniPath(p.InstallFolder, p.SavedDirSuffix), ask: false);
        }
    }
}
