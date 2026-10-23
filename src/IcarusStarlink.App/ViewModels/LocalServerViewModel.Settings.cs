using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
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
        if (s is null)
        {
            ValidationMessage = "Couldn't read this server's ServerSettings.ini, so the game settings below are blank. Check the file before you save.";
            s = new DedicatedServerSettings();
        }

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

        foreach (var name in DedicatedServerLayout.ListProspects(p.InstallFolder, p.SavedDirSuffix))
        {
            Prospects.Add(name);
        }

        WorldSettings.Reload();
        OnPropertyChanged(nameof(InstallFolder));
        OnPropertyChanged(nameof(SettingsIniPath));
        OnPropertyChanged(nameof(CommandLinePreview));
        OnPropertyChanged(nameof(LogPath));
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
            var names = DedicatedServerLayout.ListProspects(p.InstallFolder, p.SavedDirSuffix).ToList();
            if (!names.SequenceEqual(Prospects))
            {
                Prospects.Clear();
                foreach (var name in names)
                {
                    Prospects.Add(name);
                }
            }

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
            var ini = ServerSettingsIni.Load(iniPath);
            ini.Write(settings);
            ini.Save(iniPath);
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
            StatusMessage = Running is not null ? "Saved. Restart the server to apply." : "Saved.";
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
}
