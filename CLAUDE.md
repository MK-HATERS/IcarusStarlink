# Icarus Starlink (repo folder `IMM`)

WPF mod manager for ICARUS (UE 4.27): .NET 10, CommunityToolkit.Mvvm, xUnit. Public source-available repo `MK-HATERS/IcarusStarlink`, branch `master`, releases on GitHub + Nexus (mod 330). Machine paths (game, installs, Nexus folder, Hook Scout) are in `CLAUDE.local.md` (git-ignored).

@CLAUDE.local.md

## Skills (load the one that fits instead of rediscovering)

`starlink-facts` (ICARUS facts: EXMOD format and merge rules, paks and mount points, saves, UE4SS, dedicated server, Nexus download shapes; topic files beside it) · `library-model` (the Library/Downloads UX model the user settled after four corrections: read before touching Library, Downloads, classifier, UE4SS rows) · `release` (commit, version, publish, zip, release.json, beta channel) · `nexus-upload` · `run-app` (dev build + WinTools UI automation) · `test` · `game-data` (look at game tables without reading them; game updates; row history) · `parallel-review` (worktree pattern for multi-agent reviews).

UE4SS **Lua** facts live in the Hook Scout repo's `icarus-facts` skill (path in `CLAUDE.local.md`); don't duplicate them here.

## Layout

| Project | Holds |
|---|---|
| `src/IcarusStarlink.App` | WPF app. `AppServices.cs` = DI wiring. `ViewModels/`, `Services/` and `Views/` each have one folder per page: `Library`, `Downloads`, `Database`, `Merge` (incl. Find the broken mod, patches), `Editor` (mod editor, Editor page, fix window), `Ue4ss` (config windows), `Saves`, `Server`, `Settings`, `Shell` (main window, Help, app update, startup), `GameUpdate`, `WeeklyChanges`, plus `Views/Shared` (common dialogs, attached helpers). **Namespaces did NOT follow the folders** (still `IcarusStarlink.App.ViewModels` / `.Services` / `.Views`): keep it that way. A page's VM is split into partial files by feature (`LibraryViewModel.*.cs`, `MergeInstallViewModel.*.cs`…). `Themes/Styles.xaml`, `Assets/UE4SS/` (bundled UE4SS zip, 8 MB) |
| `src/IcarusStarlink.PakIO` | Everything about mod files: `Exmod/` (package, health checker, editors, row history, scale rules), `Container/` (EXMODZ/zip reading, size limits), `Import/`, `Rebuild/RebuildService.cs` (the merge build), `Install/` (game folder install, manifests, fingerprints), `Pak/` (UnrealPak, mount points), `Assets/` (CUE4Parse previews, blueprint compare, curves), `Safety/` (path guards, lints), `GameplayToggles/` |
| `src/IcarusStarlink.Diffing` | Field-level diff/merge engine (`MergeEngine`, rules, `TableDiffer`) |
| `src/IcarusStarlink.Core` | Models + contracts, no IO: `Library/`, `Ue4ss/`, `Server/` (ini, SteamCMD, automation, firewall, logs), `Nexus/`, `Settings/AppSettings.cs` |
| `src/IcarusStarlink.Storage` | Folder/JSON stores: `Library/FolderLibraryRepository.cs`, `Ue4ss/Ue4ssModRepository.cs`, pending downloads, profiles, saves, FTP, credentials |
| `src/IcarusStarlink.Catalog` | HTTP clients: Nexus, GitHub (cached), Daedalus/Jimk72 catalogs, UE4SS releases, app update (`release.json`) |
| `src/IcarusStarlink.Updater` | Self-updater (trimmed single file). The old-name `IcarusStarlink.App.exe` launcher was dropped in 4.1; `StarlinkExe.OldFileName` / `ExeRenameMigration` still handle installs that have one |
| `src/ThirdParty/UeSaveGame` | Vendored (Apache) save-blob reader. **Don't edit** (csproj-only changes); a hook refuses it |
| `tests/<Project>.Tests` | xUnit, ~2,950 tests. App VM tests build a `Harness`/`TestHarness` per test (Grep `class TestHarness` in `LibraryViewModelTests.cs`, `class Harness` in `DownloadsViewModelTests.cs`) |
| `tools/WinTools` | UI Automation CLI for the running app (`/run-app`). `tools/GameRowHistory` builds the row-history resource (`/game-data`) |

Nav pages: Library (`LibraryView` + `LibraryViewModel`, two panels: data mods left, UE4SS right), Downloads, Nexus, Database, Merge & Install, Saves (tabs), Editor (`GameEditorView`), Weekly Changes, Server (Local server tab = `LocalServerView`, Remote FTP = `ServerView`), Settings (tabs incl. UE4SS config), Help (`HelpViewModel` holds the topic text).

## Commands

| Do | Command |
|---|---|
| Build | `dotnet build IcarusStarlink.slnx` (stop the dev app first: MSB3027 locks) |
| Test one project / one class | `dotnet test tests/IcarusStarlink.App.Tests --filter "FullyQualifiedName~LibraryViewModelTests"` (whole suite takes minutes: run the project you touched, the full suite before a release) |
| Run the dev app | `src/IcarusStarlink.App/bin/Debug/net10.0-windows/IcarusStarlink.exe` (runtime data lands beside it). It can't start while the user's own Starlink is open (global mutex `IcarusStarlink-SingleInstance`): ask, never close theirs |
| Drive / screenshot it | `tools/WinTools` (`/run-app`) |
| Publish (release or dev build) | `/release`; dev build goes to the folder named in `CLAUDE.local.md` |
| Game table row | Hook Scout's `tools/row.py` (`/game-data`); never Read a `D_*.json` table whole |

## Don't read whole (token sinks)

A hook refuses these; this is why:
- Files over 64 KB without offset/limit: `Views/Library/LibraryView.xaml` (115 KB), `Views/Server/LocalServerView.xaml`, `Themes/Styles.xaml` (95 KB: Grep the style key), the big VM tests. Grep for the member or `x:Name`, then Read that range.
- `bin/`, `obj/`, `*.dll/.exe/.zip/.pak/.gz`, `Assets/UE4SS/*.zip`, `PakIO/Resources/game_row_history.json.gz`.
- The app's runtime data folders (`Data\<Category>\D_*.json` tables, often over 1 MB; `Extracted_Mods`): use `row.py`, `Library_Meta\<Mod>.json` and `Logs\icarusstarlink-<date>.log` (tail it).
- Shared-module and partial-class pattern: a page's VM is several files; Grep the member name across `ViewModels/<Page>ViewModel*.cs` instead of reading them all.

## How the user works (rules)

- **Plain wording** in anything the user or players read (UI text, release notes, Help, replies): short words, say what a control does; every slider says what lower and higher do.
- **Release only on an explicit "push and release" for that work.** Commit locally and ask. A beta is asked for by name. Never deploy to the user's own installs without a per-release yes.
- Never close the user's running Starlink or game; stop only processes whose Path is under this repo (a hook refuses a bare `Stop-Process IcarusStarlink*`).
- Manual checks use the user's **real mods and downloads** (the old IMM folder and `<install>\Pending_Downloads`), not synthetic fixtures; unit tests stay synthetic. For any "the download landed wrong" report, list the real zip in `Pending_Downloads` first.
- Fixes get a regression test where a seam exists, and a revert-check (undo the fix by file copy, not `git stash`, see `/parallel-review`; the test must fail).
- Review depth follows risk: code that parses downloads, zips, paks, saves or network data gets the deep multi-angle review; VM/UI code gets a normal pass (`.claude/rules/untrusted-input.md`).
- Write C# and XAML with the Edit/Write tools, never through perl/sed/heredocs (backslashes and `\f`/`\b` got mangled into real control characters more than once).
- The user relays bug reports from Starlink's Nexus bug tracker (sometimes as screenshots): treat them as real, current reports.

## Code gotchas (each cost real time)

- A JSON number built in code (`JsonValue<int>`) fails `TryGetValue<double>`: read it via `ToJsonString`.
- C# 14 makes `field` a keyword inside property accessors: don't name a local `field` there.
- WPF `ListBox` sets `SelectedItem = null` when its ItemsSource is cleared: guard filter refreshes.
- Long-lived timers must use `Application.Current.Dispatcher`: VMs are built on the splash-screen thread, where a `DispatcherTimer` never ticks.
- Commit messages: `<Area>: <what changed>` in plain words, grouped by theme, attribution line from the session reminder. Version bump is its own last commit.
- Delete a mod's previous copy only right before the first write, never before a question the user can still cancel.
