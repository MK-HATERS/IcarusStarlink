using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.IO;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Compare;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Import;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.App.ViewModels;

/// <summary>What the New mod dialog asked for: the mod's name, author, starting point and version.</summary>
public sealed record NewModRequest(string Name, string Author, ModTemplate Template, string Version);

/// <summary>Importing mods from files and folders, and New mod.</summary>
public sealed partial class LibraryViewModel
{
    /// <summary>
    /// Goes through ImportPaths, the same as dropping the folder on the page — so a folder holding a
    /// bare .pak or a UE4SS mod is recognised too, not only one with an .EXMOD inside (this used to
    /// call the EXMOD-only import directly and failed with "No .EXMOD file found" for those).
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ImportFolder()
    {
        if (_dialogService.PickFolder("Select the mod's extracted folder") is { } folder)
        {
            await ImportPaths([folder]);
        }
    }

    /// <summary>
    /// Auto-detects what's inside instead of requiring the user to already know — an EXMODZ is
    /// itself just a zip with a different extension, and a mod author's own download could just as
    /// easily be a plain .zip/.rar/.7z containing an EXMOD-shaped mod, a bare prebuilt .pak, or a
    /// UE4SS mod folder. Same ExtractedModClassifier the Nexus pending-download Activate flow
    /// already uses for exactly this "don't yet know what's inside" situation — this is that
    /// detection's other real caller, a manual local-file import with no Nexus provenance to tag.
    /// Multiselect, routed through ImportPaths below, so picking several archives at once works the
    /// same as dragging several onto the page.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ImportFile()
    {
        var paths = _dialogService.PickFiles("Select one or more mod archives", "Mod archive (*.EXMODZ, *.zip, *.rar, *.7z)|*.EXMODZ;*.zip;*.rar;*.7z");
        if (paths.Count == 0)
        {
            return;
        }

        await ImportPaths(paths);
    }

    /// <summary>A hint for the end of the current import batch's summary (set by the manual install).</summary>
    private string? _importNote;

    /// <summary>
    /// Mod Organizer's manual install, offered only when importing an archive failed: choose what in
    /// it to install and what each piece is (ManualInstaller / ManualInstallWindow). True when
    /// something was installed; false when the user cancelled (the caller reports the failure).
    /// </summary>
    private async Task<bool> InstallArchiveManuallyAfterFailureAsync(string path, string reason)
    {
        // Off the UI thread, like every unpack and copy on the import path: a big .7z froze the window.
        var scratch = await Task.Run(() => ManualInstaller.UnpackForManualInstall(path));
        try
        {
            if (Downloads.ManualInstall(scratch, Path.GetFileNameWithoutExtension(path), reason, ManualInstaller.Suggest(scratch)) is not { Count: > 0 } plan)
            {
                return false;
            }

            var installed = await ManualInstaller.InstallAsync(
                plan, _repository, _ue4ssModRepository, _prebuiltPakImporter, _dataFolder, _settingsService.Current.UnrealPakExePath,
                source: null, nexusModId: null, catalogEntryId: null, ue4ssNamesInUse: null);
            _activityLog.Log($"Installed {installed.Count} part(s) of '{Path.GetFileName(path)}' manually.", ActivityEntryKind.Success);
            if (installed.Any(i => i.Kind == PendingDownloadActivationKind.Ue4ssMod))
            {
                _importNote = "UE4SS mods land unticked: tick them, then Apply UE4SS changes.";
            }

            return true;
        }
        finally
        {
            await Task.Run(() => ManualInstaller.TryDelete(scratch));
        }
    }

    /// <summary>Lint findings from an import (ModLints via the classifier) — each one a real "this part of the mod is broken" note the user should see, kept in the Activity panel so it survives the next status line.</summary>
    private void LogImportWarnings(string path, IReadOnlyList<string> warnings)
    {
        foreach (var warning in warnings)
        {
            _activityLog.Log($"{Path.GetFileName(path)}: {warning}", ActivityEntryKind.Warning);
        }
    }

    /// <summary>
    /// What the classifier imported, in the Activity panel: the primary mod, the lint warnings, and
    /// the batch summary's hint when a UE4SS half was staged (it lands unticked).
    /// </summary>
    private void LogClassifiedImport(string path, ClassifiedImport result)
    {
        _activityLog.Log(
            result.Kind == PendingDownloadActivationKind.Library ? $"Imported '{result.EntryName}'." : $"Imported '{result.FolderName}' as a UE4SS mod.",
            ActivityEntryKind.Success);
        LogImportWarnings(path, result.Warnings);
        if (result.Items.Any(i => i.Kind == PendingDownloadActivationKind.Ue4ssMod))
        {
            _importNote = "UE4SS mods land unticked: tick them, then Apply UE4SS changes.";
        }
    }

    /// <summary>
    /// Before an import writes anything: when it is a mod already in the Library (one copy of each
    /// half, found by ImportMatchFinder), asks Replace it / Keep both / Cancel. Replace swaps the old
    /// copy out through ModReplacementService and returns what it did, for the status line. Keep
    /// both, no match, several matches (copies the user keeps on purpose, asked nothing) or an
    /// import read from inside the installed copy itself (Replace would delete what it reads) return
    /// null and the import goes on as before. Cancel throws ImportCancelledException; nothing has
    /// changed by then. A failed Replace throws with the reason (the old copy is put back first).
    /// </summary>
    /// <param name="stagedPath">What to import from: the .pak, the user's EXMOD folder, or the copy or unpacked archive in the temp folder.</param>
    /// <param name="isArchive">An archive, whose own import offers the manual install on a failure: a failed Replace says how to get to it.</param>
    private async Task<string?> ReplacedExistingCopyAsync(string path, string stagedPath, Func<IncomingModNames> readIncoming, bool isArchive = false)
    {
        ImportMatches matches;
        try
        {
            var incoming = await Task.Run(readIncoming);
            matches = ImportMatchFinder.Find(incoming, _repository.GetAll(), SafeUe4ssNames());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Can't tell what is inside yet: imported as before, and the import reports any real problem.
            return null;
        }

        if (!matches.IsSingle)
        {
            return null;
        }

        var oldEntry = matches.Library.FirstOrDefault();
        var oldUe4ss = matches.Ue4ss.FirstOrDefault();
        if (IsInsideInstalledCopy(stagedPath, oldEntry, oldUe4ss))
        {
            // The installed copy's own folder: a Replace would delete it mid-import. Imported as a copy, as before.
            return null;
        }

        var parts = new List<string>();
        if (oldEntry is not null)
        {
            parts.Add($"'{oldEntry.Name}' (data mod)");
        }

        if (oldUe4ss is not null)
        {
            parts.Add($"'{oldUe4ss}' (UE4SS mod)");
        }

        var fileName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var answer = _dialogService.AskReplaceExisting(
            $"'{fileName}' is already in your Library: {string.Join(" and ", parts)}.\n\n"
            + "Replace it: the old copy is backed up first, and its pin, notes and Nexus link stay.\n"
            + "Keep both: adds a second copy beside it.");
        if (answer == ReplaceExistingAnswer.Cancel)
        {
            throw new ImportCancelledException($"Didn't import '{fileName}'.");
        }

        if (answer == ReplaceExistingAnswer.KeepBoth)
        {
            return null;
        }

        ClassifiedImport? imported = null;
        var result = await Downloads.ReplaceWithLocalImportAsync(stagedPath, path, oldEntry, oldUe4ss, c => imported = c);
        if (result.Cancelled)
        {
            _activityLog.Log(result.Message ?? $"Didn't replace '{fileName}'.", ActivityEntryKind.Info);
            throw new ImportCancelledException(result.Message ?? $"Didn't replace '{fileName}'.");
        }

        if (!result.Succeeded)
        {
            // Icarus running is a failure too (no Error): its message says to close the game.
            var failed = result.Message ?? "The old copy couldn't be replaced.";
            if (isArchive && result.RolledBack && result.Error is not null and not DownloadsViewModel.OldCopyNotRemovedException)
            {
                // Not offered here: a manual install would add a second copy beside the one just put back.
                failed += " To choose what to install by hand, import it again and pick Keep both.";
            }

            throw new InvalidOperationException(failed);
        }

        var done = $"Replaced {string.Join(" and ", parts)} with '{fileName}'.";
        _activityLog.Log(done + result.Summary, ActivityEntryKind.Success);
        if (imported is not null)
        {
            LogImportWarnings(path, imported.Warnings);
        }

        // A replaced UE4SS mod keeps its on/off state; a new Lua half lands off, as any import's does.
        if (oldUe4ss is null && result.Items.Any(i => i.Kind == PendingDownloadActivationKind.Ue4ssMod))
        {
            _importNote = "UE4SS mods land unticked: tick them, then Apply UE4SS changes.";
        }

        return done;
    }

    /// <summary>True when <paramref name="stagedPath"/> is the old copy's own folder (Library or staged UE4SS) or inside it.</summary>
    private bool IsInsideInstalledCopy(string stagedPath, LibraryEntry? oldEntry, string? oldUe4ss)
    {
        try
        {
            var staged = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagedPath));
            var folders = new List<string>();
            if (oldEntry is not null)
            {
                folders.Add(_repository.GetFolderPath(oldEntry.FolderName));
            }

            if (oldUe4ss is not null)
            {
                folders.Add(_ue4ssModRepository.GetFolderPath(oldUe4ss));
            }

            return folders
                .Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)))
                .Any(root => staged.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || staged.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The old copy's folder can't be found: it can't be what is being imported.
            return false;
        }
    }

    /// <summary>The name the classifier gives an import whose root is itself a UE4SS mod: the file or folder name without a Nexus download's id, version and time.</summary>
    private static string IncomingRootName(string path) =>
        NexusDownloadFileName.StripNexusSuffix(Path.GetFileNameWithoutExtension(path));

    /// <summary>
    /// Imports one already-known path — a real folder, a bare .pak, or (falling through to the
    /// same auto-detection ImportFile's own dialog already uses) any other file, sniffed as an
    /// archive by its actual content rather than trusted by extension. Shared by drag-and-drop and
    /// every multi-select import path below, so "what kind of thing is this path" is decided in
    /// exactly one place. The unpacking, copying and temp deletes run on a worker thread (a 300 MB
    /// .7z dropped on the page froze the window for the whole unpack); the imports themselves stay
    /// on the UI thread: they ask questions (which archive?) and write the repository, whose cache
    /// and search index aren't made for two threads.
    /// </summary>
    private async Task ImportOnePath(string path)
    {
        if (Directory.Exists(path))
        {
            // An EXMOD-shaped folder goes through the classifier like an archive does: it also
            // stages the download's loose Lua half (MyMod\Scripts\main.lua) as its own UE4SS row,
            // runs the lints and takes back a half-done import. It used to go straight to the EXMOD
            // import and the Lua half was lost. No copy needed: the classifier's EXMOD branch only
            // reads the folder (the imports copy from it) and never unpacks anything into it.
            if (Directory.EnumerateFiles(path, "*.EXMOD", SearchOption.AllDirectories).Any())
            {
                if (await ReplacedExistingCopyAsync(path, path, () => ImportMatchFinder.ReadFolder(path, IncomingRootName(path))) is not null)
                {
                    return;
                }

                var exmodResult = await ExtractedModClassifier.ClassifyAndImport(
                    path, path, _repository, _ue4ssModRepository,
                    _prebuiltPakImporter, _dataFolder, _settingsService.Current.UnrealPakExePath, chooseArchive: Downloads.ChooseArchive);
                LogClassifiedImport(path, exmodResult);
                return;
            }

            // No .EXMOD inside — a folder holding a bare .pak or a UE4SS mod gets the same content
            // sniffing an archive does (it used to fail with "No .EXMOD file found"). Classified from a COPY: the classifier unpacks any nested archive it finds
            // into the folder it's given, and that must never be the user's own folder.
            var tempCopy = Path.Combine(Path.GetTempPath(), $"IcarusStarlink_Import_{Guid.NewGuid():N}");
            try
            {
                await Task.Run(() => FolderBackup.CopyDirectory(path, tempCopy));
                if (await ReplacedExistingCopyAsync(path, tempCopy, () => ImportMatchFinder.ReadFolder(tempCopy, IncomingRootName(path))) is not null)
                {
                    return;
                }

                var folderResult = await ExtractedModClassifier.ClassifyAndImport(
                    tempCopy, path, _repository, _ue4ssModRepository,
                    _prebuiltPakImporter, _dataFolder, _settingsService.Current.UnrealPakExePath, chooseArchive: Downloads.ChooseArchive);
                LogClassifiedImport(path, folderResult);
            }
            finally
            {
                // Best-effort temp cleanup.
                await Task.Run(() => Directories.TryDelete(tempCopy));
            }

            return;
        }

        if (path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
        {
            if (await ReplacedExistingCopyAsync(path, path, () => ImportMatchFinder.ForPak(path)) is not null)
            {
                return;
            }

            var entry = await _prebuiltPakImporter.ImportAsync(path, _dataFolder, _settingsService.Current.UnrealPakExePath);
            _activityLog.Log($"Imported '{entry.Name}' v{entry.Version}.", ActivityEntryKind.Success);
            LogImportWarnings(path, entry.ImportWarnings);
            return;
        }

        var tempExtractDirectory = Path.Combine(Path.GetTempPath(), $"IcarusStarlink_Import_{Guid.NewGuid():N}");
        try
        {
            await Task.Run(() => AnyArchiveExtractor.ExtractToDirectory(path, tempExtractDirectory));
            if (await ReplacedExistingCopyAsync(
                path, tempExtractDirectory, () => ImportMatchFinder.ReadFolder(tempExtractDirectory, IncomingRootName(path)), isArchive: true) is not null)
            {
                return;
            }

            ClassifiedImport archiveResult;
            try
            {
                archiveResult = await ExtractedModClassifier.ClassifyAndImport(
                    tempExtractDirectory, path, _repository, _ue4ssModRepository,
                    _prebuiltPakImporter, _dataFolder, _settingsService.Current.UnrealPakExePath, chooseArchive: Downloads.ChooseArchive);
            }
            catch (Exception ex) when (ex is not ImportCancelledException)
            {
                // Couldn't sort it out automatically: let the user say what's what (Mod Organizer's
                // manual install). Cancelling that reports the original failure as before.
                if (await InstallArchiveManuallyAfterFailureAsync(path, ex.Message))
                {
                    return;
                }

                throw;
            }

            LogClassifiedImport(path, archiveResult);
        }
        finally
        {
            // Best-effort scratch cleanup — a locked file here (e.g. antivirus mid-scan)
            // shouldn't turn a successful import into a reported failure.
            await Task.Run(() => Directories.TryDelete(tempExtractDirectory));
        }
    }

    /// <summary>
    /// Shared entry point for drag-and-drop (LibraryView's own code-behind Drop handler) and every
    /// multi-select "Import…" dialog — each path is imported independently via ImportOnePath, so
    /// one bad file in a batch (a corrupt archive, a folder with no .EXMOD/.pak inside) can't abort
    /// the rest. Deliberately no per-file "link to Nexus?" follow-up here, unlike ImportPak's own
    /// single-file prompt — asking that once per imported file would make importing several at once
    /// unusable; "Link to Nexus ID…" stays reachable afterward from each row's own context menu.
    /// </summary>
    public async Task ImportPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        // One import at a time: the window stays usable while one unpacks, and a second drop
        // started a second import beside it (two "which one?" choosers, one shared result line).
        if (_isImporting)
        {
            ReportProblem("An import is still running — try again when it's done.");
            return;
        }

        _isImporting = true;
        try
        {
            await ImportPathsOneAtATime(paths);
        }
        finally
        {
            _isImporting = false;
        }
    }

    private bool _isImporting;

    private async Task ImportPathsOneAtATime(IReadOnlyList<string> paths)
    {
        var importedCount = 0;
        var cancelled = 0;
        var failures = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                await ImportOnePath(path);
                importedCount++;
            }
            catch (ImportCancelledException)
            {
                // The "which one?" chooser was closed: the user said no, nothing failed. It used to
                // read "Imported 0 of 1 — …" with an amber "Import failed" in Activity.
                cancelled++;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}: {ex.Message}");
            }
        }

        // Its own handler reloads both panels (the UE4SS rows too).
        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());

        StatusMessage = (failures.Count > 0
            ? $"Imported {importedCount} of {paths.Count}{(cancelled > 0 ? $" ({cancelled} cancelled)" : "")} — {string.Join("; ", failures)}"
            : importedCount == 0 && cancelled > 0 ? "Import cancelled — nothing was changed."
            : cancelled > 0 ? $"Imported {importedCount} mod(s), {cancelled} cancelled."
            : paths.Count == 1 ? "Imported 1 mod."
            : $"Imported {importedCount} mod(s).")
            + (_importNote is { } note ? " " + note : "");
        _importNote = null;
        if (failures.Count > 0)
        {
            _activityLog.Log($"Import failed: {string.Join("; ", failures)}", ActivityEntryKind.Warning);
        }
    }

    /// <summary>
    /// A prebuilt .pak carries no embedded name/author of its own (unlike an EXMOD), so right after
    /// import this offers to link it to a real Nexus mod ID immediately — rather than leaving that
    /// to the separate "Link to Nexus ID…" context-menu action on an already-imported mod, which is
    /// easy to never discover for something imported this way in the first place.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ImportPak()
    {
        if (_dialogService.PickFile("Select a prebuilt .pak file", "Unreal pak package (*.pak)|*.pak") is not { } pakPath)
        {
            return;
        }

        // Asks Replace it / Keep both / Cancel like every other import when the pak is already in.
        string? replaced;
        try
        {
            replaced = await ReplacedExistingCopyAsync(pakPath, pakPath, () => ImportMatchFinder.ForPak(pakPath));
        }
        catch (ImportCancelledException)
        {
            StatusMessage = "Import cancelled — nothing was changed.";
            return;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import failed: {ex.Message}";
            _activityLog.Log($"Import of '{Path.GetFileName(pakPath)}' failed: {ex.Message}", ActivityEntryKind.Warning);
            return;
        }

        if (replaced is not null)
        {
            // The old copy's Nexus link is kept, so there is nothing to ask about linking.
            StatusMessage = replaced;
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            return;
        }

        var entry = await TryImportPakAsync(pakPath);
        if (entry is null)
        {
            return;
        }

        var linkPrompt = _dialogService.Confirm(
            $"'{entry.Name}' doesn't have a name/author of its own yet — imported .pak files don't carry that until you tell it where they came from.\n\nIs this a Nexus mod? Link it now so Starlink can show its real name and check for updates.",
            "Link to Nexus?", ThemedConfirmSeverity.Question);
        if (!(linkPrompt))
        {
            return;
        }

        var nexusDialog = new LinkNexusDialog { Owner = Application.Current.MainWindow };
        if (nexusDialog.ShowDialog() != true)
        {
            return;
        }

        await LinkFolderToNexusAsync(entry.FolderName, entry.Name, nexusDialog.NexusModId);
    }

    /// <summary>Shows the New mod dialog; null when cancelled (tests swap in a fake).</summary>
    internal Func<NewModRequest?> AskForNewMod { get; set; } = () =>
    {
        var dialog = new NewModDialog { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true
            ? new NewModRequest(dialog.ModName, dialog.ModAuthor, dialog.SelectedTemplate, dialog.ModVersion)
            : null;
    };

    [RelayCommand]
    private void NewMod()
    {
        if (AskForNewMod() is not { } request)
        {
            return;
        }

        try
        {
            var entry = _repository.CreateBlankMod(request.Name, request.Author, request.Template, request.Version);
            StatusMessage = $"Created '{entry.Name}'.";
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            _activityLog.Log($"Created new mod '{entry.Name}'.", ActivityEntryKind.Success);
            OpenEditor(entry.FolderName, startWizard: ModTemplateContent.ItemPresetFor(request.Template));
        }
        catch (Exception ex)
        {
            // Same UI boundary as ImportPaths — folder creation can fail for the same reasons any
            // other Extracted_Mods write can (permission denied, disk full, ...).
            StatusMessage = $"Couldn't create the mod: {ex.Message}";
        }
    }

    /// <summary>
    /// Tries converting the pak into a real, editable EXMOD first (IPrebuiltPakImporter, which
    /// itself falls back to importing it as an opaque pak whenever conversion isn't possible right
    /// now — UnrealPak.exe not set up, the extracted game data missing, or the pak itself can't be
    /// read). Async because conversion genuinely extracts and diffs the pak.
    /// </summary>
    private async Task<LibraryEntry?> TryImportPakAsync(string pakFilePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null)
    {
        try
        {
            var entry = await _prebuiltPakImporter.ImportAsync(
                pakFilePath, _dataFolder, _settingsService.Current.UnrealPakExePath, source, nexusModId, catalogEntryId);

            StatusMessage = entry.IsOpaquePak
                ? $"Imported '{entry.Name}'."
                : $"Imported '{entry.Name}' as a real, editable mod — its data changes are now visible in the mod editor.";
            LogImportWarnings(pakFilePath, entry.ImportWarnings);
            StatusMessage += ExtractedModClassifier.WarningSummary(entry.ImportWarnings);
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            _activityLog.Log($"Imported '{entry.Name}' v{entry.Version}.", ActivityEntryKind.Success);
            return entry;
        }
        catch (Exception ex)
        {
            // Same UI-boundary catch ImportPaths uses — a user-initiated import can fail for
            // many reasons (malformed pak, permission denied, disk full, ...).
            StatusMessage = $"Import failed: {ex.Message}";
            _activityLog.Log($"Import of '{Path.GetFileName(pakFilePath)}' failed: {ex.Message}", ActivityEntryKind.Warning);
            return null;
        }
    }
}
