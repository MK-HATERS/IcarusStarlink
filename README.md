# Icarus Starlink

A mod manager for [Icarus](https://store.steampowered.com/app/1149460/ICARUS/), built as a
clean-room successor to two existing tools: **Icarus Workshop** (Nexus mod 304, currently
unavailable) and **classic IMM** (Jimk72's original Icarus Mod Manager, which Icarus Workshop
itself was built on top of). Nothing here is copied from either — this is a fresh implementation
built from the public feature spec, the real EXMOD/EXMODZ mod format (reverse-engineered from real
sample files), and original research into Icarus's own save/pak formats.

In short: it extracts and diffs the game's own data, applies mods as field-level changes against
that data (not whole-file overwrites), merges any number of mods together with real conflict
detection, and packs and installs the result — while also giving you a proper library, a save
editor, native Nexus browsing, and a mod-authoring editor, all in one app. Icarus Starlink now
covers everything both of the tools above do, plus a number of things neither of them has.

*Icarus Starlink is an independent, fan-made tool — not affiliated with, endorsed by, or
associated with RocketWerkz or the Icarus development team. See the full [Disclaimer](#disclaimer)
below.*

## Installation

1. Download the latest release zip from the
   **[Releases page](https://github.com/MK-HATERS/IcarusStarlink/releases/latest)**.
2. Extract the zip anywhere — it unpacks into a single `IcarusStarlink` folder. It's a
   self-contained, portable build, so nothing else needs to be installed separately (no .NET
   runtime download required).
3. Run `IcarusStarlink.exe` from that folder (before 3.0 it was `IcarusStarlink.App.exe`; that name is now a small launcher that Starlink offers to remove once your shortcuts point at the new one). First launch sets itself up: it finds your
   Icarus install through Steam (or asks you to browse to it), installs UnrealPak, and offers to
   extract the game's data. Everything it set lives in **Settings** if you ever need to change
   it — see [Getting started](#faq--troubleshooting) below if anything's unclear.

Windows SmartScreen or your antivirus may flag the download the first time you run it — see
[Antivirus flagged this download?](#faq--troubleshooting) below for why, and a link to a current
scan of the release.

## Uninstalling

Run `Uninstall.bat` in the app's own folder. It closes the app if it's running, asks whether to
keep or wipe your mods/profiles/settings (type `YES` to confirm either way), always removes the
regenerable folders it created (Logs, Data, Staged_Build, Staged_UE4SS), and then removes its own
program files — it deliberately leaves the now-empty folder and itself behind, with a closing
message saying so. Your Icarus game install itself is never touched.

## What it does

**Library** — import mods as archives (`.zip`/`.rar`/`.7z`/`.EXMODZ`, auto-detected — recognizes an
EXMOD-shaped mod, a bare `.pak`, or a UE4SS mod from what's actually inside), folders, or prebuilt
`.pak` files; organize with pin/favorite/notes/rename and automatic variant grouping; user-
configurable columns (right-click the header bar); a full EXMOD editor (item-field, raw file JSON,
and full-package JSON views, amber-highlighted diffs against the real base game data, mass edit
across selected items, undo, add-item-from-game-data, Save reconstructing the mod so any field left
byte-identical to the real base game data — the usual result of add-item-from-game-data cloning a
whole row you only meant to change one field of — never makes it into the saved file, a pop-out for
any pane with its scroll position kept in sync with wherever else that same view is open, and a
cross-file "what else references this" search over the whole extracted data folder); real asset
preview for a mod's own textures, static/skeletal meshes (3D, orbit camera), sound (playback), and
materials (texture/color/
scalar parameters, including a base-game fallback so a modded material overriding an existing
base-game parent can still show that parent's own texture/color values, not just an empty list) —
covers opaque prebuilt-`.pak` imports too, not just EXMOD mods; proactive staleness detection that
flags mods whose targets a game update may have renamed or removed, with confidence-tiered
auto-repair (always backed up first). A prebuilt `.pak` mod converts into a real, editable EXMOD
automatically wherever it's possible to (at import, on demand via **Convert opaque mods…**, or
silently after the next "Update data folder") — its own original `.pak` is kept behind the scenes
so a later game update can re-derive a fresher diff instead of the conversion being frozen at
whatever the game looked like the day it first converted; once a converted mod is hand-edited, it's
never silently touched again.

**Check mods against game data** — finds what a game update left behind in a mod: values copied
from an old version of the game, rows the game renamed or removed, links to rows that no longer
exist (the kind of thing that stops a canteen being drinkable), and lists that mix old and new.
Each finding says how sure it is. **Fix sure problems** applies only the certain ones (backed up
first); everything else opens in a fix window with a side-by-side, line-by-line view of the mod's
value against the game's, where you choose what to keep. Your choices are remembered, so the same
question isn't asked again after the next game update. A link to a row another Library mod adds
counts as a dependency, not a problem. A mod's own cooked files (meshes, materials, blueprints) are
checked too: every game file they use must still be where they look for it. Where the game only
moved one (same name, same contents, another folder), the fix points the mod to the new place;
what the game removed is listed for the mod's author to update.

**Merge & Install** — a merge queue with proactive conflict detection (including two mods adding a
same-named new item, a queue-wide check for a mod referencing something only another queued mod
declares, and a check of the talent, blueprint and workshop trees as the whole queue leaves them:
nodes on top of each other, nodes that wait for each other and can never be unlocked, nodes needing
another tree's) and a manual per-field conflict picker that shows the live base-game value plus a
buff/nerf hint alongside each candidate; array-shaped fields (recipe lists, loot tables) combine
automatically when every mod's change is a clean addition rather than picking just one and dropping
the rest; a **Check the merge** window with every finding in three tabs (conflicts, clashes between
mods such as two mods bringing the same file or an add-on queued without the mod it needs, and each
queued mod's own problems from the Library's mod check, with a Fix… button and a copyable report
with short codes); gameplay-option toggles
(stack/slot size, craft cost, XP/speed/player/taming boosts, remove weight, unlimited ammo, disable
temperatures, remove level cap — values sourced from classic IMM's own documented behavior, or a
real community mod's where classic IMM never covered one) that participate in the same conflict
detection as regular mods, profiles (with their own backup/restore) with exportable/importable
patches (the profile as it runs: data mods by source, the UE4SS mods on in the game and their
settings; importing one downloads what you lack, and a server host can export, apply and compare one), and a rebuild → install pipeline that backs up whatever it's about to replace and
independently re-reads the pak it just built to confirm nothing staged for packing went missing.
Conflicts can be decided per line: pick the winning mod for each conflicting field, and the
profile remembers it. The queue and your last merge are kept across restarts. Everything installs
as one merged pak, `ISL-Merged_P.pak` (`zz_zz_ISL-Merged_9999_P.pak` while ArcFission Library is
on, so it loads after that library's pak; a server goes by its own ArcFission Library). ArcFission
mods (Wireless Power, Lumber Yard…) have their rows read straight from the mods' own `rows.lua` and
merged at low priority, so they're in the merge before the game has ever run with them; ArcFission
Library's own runtime data pak stays and loads before the merge, so the two don't override each other.

**Weekly Changes** — a real, row-by-row diff of what the game's own data changed between two
"Update data folder" runs, so you can see exactly why a mod broke after a patch instead of guessing.

**Downloads & Nexus** — browses the community mod catalog (the Daedalus and Jimk72 projects) with
sortable/filterable columns, plus a native Nexus browsing/search view backed by the real Nexus API
(images, live search, per-card local-status badges, tracking) with `nxm://` protocol handling for
one-click downloads from the website.

**UE4SS** — the official UE4SS loader ships inside the app (hash-checked) and installs in one
click, no download needed. Enable/disable installed UE4SS (Lua scripting framework) mods without
touching the framework's own built-in ones — "on" means UE4SS really starts the mod (a `1` in
`mods.txt` or an `enabled.txt` in its folder, UE4SS's own rule), so a mod sitting in the game that
UE4SS skips shows as **Off in UE4SS** and one tick turns it on — install/update/fully uninstall
the loader itself (uninstall correctly tells your own mods apart from the framework's bundled ones and only ever removes the
latter), link a UE4SS mod to its real Nexus page so it participates in update-checking the same way
Library mods do, and declare a mod's own minimum required UE4SS version (there's no per-mod manifest
to read this from automatically) to get a warning if your installed loader is older.

**Server** — run an Icarus dedicated server on this PC: install it with SteamCMD (or adopt one you
already have), start/stop/restart/update, a live log, server settings, creating a world on start,
automation (restart on crash, daily restart, update on restart, start with the app), Windows
firewall rules, installing your merged pak and UE4SS mods, world backups and restore, admins and
bans, and each world's custom settings. For a server hosted elsewhere there's FTP file management
(upload/download/browse/delete), with saved site credentials in Windows Credential Manager and
FileZilla-style reconnect; one-click sync of a merged pak, the UE4SS loader, and UE4SS mods to a
connected server, with per-site remote path overrides for a host whose folder layout differs from
the built-in default.

**Saves** — a full player save editor: characters (name, XP, duplicate/delete a slot), currencies,
talents (character and account-wide Workshop research), account/character/binary unlock flags,
Bestiary encounter progress, Accolade completion, the account-wide item bank (with real item/
creature icons resolved straight from the base game's own compiled content, not text-only), and
tamed mounts (name, level, species, delete), and each world's custom world settings
(Arachnophobia Mode, Mission Structure Cleanup and the rest of the game's own list; Difficulty and
Hardcore are shown but never changed) — with mandatory automatic backups before every write
and hard safety gates (refuses to touch a save while the game is running, re-checked immediately
before the write itself).

**Migration & verification** — import a mod list straight from a classic IMM install and match it
against your Library, plus two comparison tools: one that diffs any two `.pak` files field by field
(used to verify this app's own merge output is byte-identical to classic IMM's), and one that shows
exactly what a mod's author changed between the version you have and an update.

**Also**: three built-in themes plus a fully user-authorable custom skin (edit any of ~19 colors,
or hand-edit the underlying JSON), a real in-app auto-updater (verifies the download's integrity,
downloads, applies with automatic rollback if it fails partway, and relaunches itself — never
touches your mods, profiles, or settings), real crash reporting (every unhandled exception —
whether it's on the UI thread, the background startup sequence, or inside any async action like
Rebuild/Install/FTP/downloads/save-editing — writes a crash report with the full exception, app/OS
version, and recent activity history to the Logs folder, instead of the app previously either
hard-crashing with nothing recorded or silently freezing/doing-nothing with no trace at all),
a Basic log that's always on and capped so it never bloats (an Advanced log can be switched on
in Settings for troubleshooting and turns itself off after 24 hours), diagnostics export (now also
bundling app/OS version info and any crash reports alongside the regular logs), and an in-app Help page covering every feature above.

## What it doesn't (yet)

- **Nexus single-click SSO login** — Nexus's SSO flow needs an application slug only their staff
  can issue; the app uses a manual paste-your-API-key flow instead.
- Material preview's base-game fallback only actually recovers texture/color values when the
  resolved parent is a plain base-game material — a common case (confirmed live against real mods:
  28 real textures and 17 real colors recovered for a real modded material that came back completely
  empty before this fallback existed), but if the parent turns out to be *another* material instance
  rather than a plain material, the same underlying CachedExpressionData gap this fallback works
  around can still leave that one empty too (see `CueUassetMaterialDecoder`'s own doc comment for
  why).
- The Save editor's item/mount/creature icons resolve from the same base-game content as above, so
  they inherit that same edge case, plus one more: a very small number of older save entries store an
  item's row name slightly differently than the current game data does (one real example found:
  `LegendaryWeapon_ApeClub` in a save vs. `LegendaryWeapon_Ape_Club` in the live data table) — those
  rows already showed the wrong (raw ID) display name before this feature existed, and now also show
  no icon, for the same underlying reason: the row simply isn't found under the name the save uses.
- Sound preview is implemented and correct, but Icarus's own shipped content uses FMOD exclusively
  (confirmed against the real game's asset registry: zero native UE4 USoundWave assets anywhere in
  86,000+ real assets) — a real mod would need to ship a genuinely unusual asset for this to ever
  actually trigger. Playing an FMOD `.bank` file would be a real, separate, larger feature.

## What this app touches on your system

A cautious user (or a Nexus moderator) reviewing a tool that edits real save files and updates
itself deserves a straight answer to "what does this actually do to my computer." In one place,
gathered from the feature descriptions above:

- **Your Icarus game install folder** — reads/extracts the game's own data, and writes merged
  `.pak` files into `Content\Paks\mods`. Every write that replaces something is backed up first,
  with one exception: older copies of ArcFission Library's own data pak
  (`ZZ_ArcFission_Data_<n>_P.pak`, about 19 MB each, which that library rebuilds from the game's
  data) are removed at Install without a backup; its current one is never touched. On a local
  dedicated server they are moved into the server's backups instead.
- **UE4SS mod config files** (`Binaries\Win64\ue4ss\Mods\<mod>\config.lua` and the like, in the
  game and on a local dedicated server) — written only when you save in Edit config, press Match
  settings on the Server page, or import a patch that carries settings; the file being replaced is
  backed up first. A patch's bundled mods are unpacked under your temp folder while they are
  imported and removed afterwards.
- **Your save files** — reads and writes them only through the Save editor (and the world settings
  editor, on the Saves and Server pages), always backing up before a write, and refuses to touch a
  save while the game (or that server) is running (checked again immediately before the write
  itself).
- **Windows Credential Manager** — your Nexus API key and any FTP site passwords you save (Server
  tab) are stored here, the same OS-level store Windows itself uses, never in a plain text file.
- **The Windows `nxm://` protocol handler** (`HKEY_CURRENT_USER` only, no admin rights needed) —
  registered so "Download with Manager" on the Nexus website opens straight into this app.
- **Network calls this app makes**: the Nexus API (only once you've entered an API key, for
  browsing/search/downloads); GitHub (this app's own release check on every launch, the community
  mod catalog, and — only when you click Install/Update in Settings — UnrealPak.exe and a newer
  UE4SS loader); SteamCMD and Steam's own servers, only when you install or update a dedicated
  server; and, only if you configure one, your own FTP server on the Server tab. Nothing
  else. There is no telemetry, analytics, or usage tracking of any kind anywhere in this app.
- **Self-update** — downloads only from this project's own GitHub Releases
  (`github.com/MK-HATERS/IcarusStarlink`), or, on the Stable channel when you're signed in to Nexus,
  from this app's own Nexus page (mod 330); either way it verifies the download's SHA-256 against the
  digest published with the GitHub release, and always asks for confirmation before applying
  anything; a failed update rolls back automatically rather than leaving a half-updated install.
- **The app never requests administrator/elevated permissions for anything.**

The startup GitHub/Nexus version checks above are read-only "is a newer version available"
requests — nothing is ever downloaded or installed without you clicking an explicit confirm
dialog first.

## FAQ / Troubleshooting

**Where are my backups?** Automatic backups (before a save write, before a mod update, before a
merge/install) live under this app's own `Backups` folder, next to the exe.

**The app won't start / crashed on launch.** It writes a crash report (the full exception, your
app/OS version, and recent activity) to the `Logs` folder next to the exe — attach that when
[reporting a bug](#support--bug-reports).

**A mod won't merge / I'm getting a conflict.** Merge & Install's own conflict picker shows the
live base-game value alongside each candidate, including a buff/nerf hint — most conflicts are a
genuine choice between two mods that touch the same thing, not a bug.

**Antivirus flagged this download?** Two features that commonly trip antivirus/SmartScreen
heuristics on any tool that has them: this is a self-contained .NET publish (the whole .NET
runtime bundled into one folder, not just the app's own code), and the app self-updates by
downloading and replacing its own executable — both are legitimate, common patterns, but also
exactly what a heuristic scanner is trained to be suspicious of. See the current release's own
description on the [Releases page](https://github.com/MK-HATERS/IcarusStarlink/releases/latest)
for a VirusTotal scan link.

**How do I get started after installing?** Just run it — first launch finds your Icarus install
through Steam (or asks you to browse to it), installs UnrealPak, and offers to extract the game's
data. If you skipped any of that, it's all in Settings: Icarus Content folder (Auto-detect or
Browse) → UnrealPak → Install → Update data folder. Then import mods on the Library tab. The in-app **Help** page walks through this in more detail and covers every feature.

## Support & bug reports

Found a bug, or something doesn't work the way this README says it should? Please open an issue:
**<https://github.com/MK-HATERS/IcarusStarlink/issues>** — attaching the diagnostics export
(Settings → Diagnostics, bundles logs, sanitized settings, and any crash reports — never your API
keys or tokens) makes it much faster to track down.

## Changelog

Every release's own notes are on the
**[Releases page](https://github.com/MK-HATERS/IcarusStarlink/releases)** — each one lists what
changed since the last version.

## Building

Requires the .NET 10 SDK on Windows (WPF).

```console
dotnet build IcarusStarlink.slnx
dotnet test IcarusStarlink.slnx
```

`tools/WinTools` is a small UI-automation CLI used during development to drive and screenshot the
running app — not part of the shipped product.

## Credits

None of this exists without the people who actually make Icarus modding a real thing to build a
tool for:

- **[Jimk72](https://github.com/Jimk72)** — creator of the original Icarus Mod Manager ("classic
  IMM"), whose EXMOD/EXMODZ format this app reads and writes, and whose own published changelog
  was the real, documented source for several of this app's built-in gameplay-option values.
  Nothing here is copied from classic IMM's own (closed-source) code — this is a clean-room
  implementation built against the format and behavior it documents — but the format itself, and
  years of prior art on how to handle it well, are Jimk72's.
- **Every Icarus mod author**, named or not, whose real mods this app was built and tested against.
  A mod manager is only as good as the mods it actually has to handle, and modding a game in the
  first place — figuring out its data, sharing what you learn, publishing something someone else
  can use — is the actual hard, generous work that makes a tool like this worth building at all.
- **[Project Daedalus](https://github.com/AgentKush/daedalus-static-poc)** and Jimk72's own mod
  catalog — the two real, public community mod databases this app's Library integrates with.
- **[AgentKush](https://github.com/AgentKush)** — the [Icarus Save file
  Toolkit](https://github.com/AgentKush/Icarus-Save-file-Toolkit) documented the real on-disk save
  file layout this app's Save editor is built against (studied as a format reference only — no
  code from that project is used here).
- **[GODOFMINECRAFT4/IcarusData](https://github.com/GODOFMINECRAFT4/IcarusData)** and
  **[IcarusWiki/Data](https://github.com/IcarusWiki/Data)**: public, update-by-update histories of
  the game's extracted data. ISL's game row history (which rows the game has had and removed
  since Week 9, so an old copy in a mod can be told from new content) is built from their commit
  histories; only row names and version labels are kept, none of the files themselves.
- **[Nexus Mods](https://www.nexusmods.com/icarus)** — the platform and the real API this app's
  Nexus integration talks to.
- **[CUE4Parse](https://github.com/FabianFG/CUE4Parse)** — the Unreal Engine asset-parsing library
  underneath this app's own data extraction, diffing, and asset-preview features. The de facto
  standard UE asset library in the modding/datamining community; this app builds on it, doesn't
  fork or modify it.
- **Every third-party open-source library this app is built on** — see
  [`THIRD-PARTY-LICENSES.md`](THIRD-PARTY-LICENSES.md) for the full list with licenses.

## License

Source-available, not open-source: you're free to download, use, and read the source of any
official release for any purpose — but modifying, repackaging, or redistributing it (including
forks) requires asking first. See [`LICENSE.md`](LICENSE.md) for the full terms and the reasoning
behind them.

## Disclaimer

Icarus Starlink is an independent, fan-made tool and is not affiliated with, endorsed by, or
associated with RocketWerkz or the Icarus development team. Icarus and all related assets are the
property of their respective owners.
