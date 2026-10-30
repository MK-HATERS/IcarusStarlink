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
3. Run `IcarusStarlink.exe` from that folder (before 3.0 it was `IcarusStarlink.App.exe`; if an older install still has that file, Starlink offers to remove it once your shortcuts point at the new one). First launch sets itself up: it finds your
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

[Library](#library) · [Check mods against game data](#check-mods-against-game-data) · [Merge & Install](#merge--install) · [Weekly Changes](#weekly-changes) · [Map](#map) · [Downloads & Nexus](#downloads--nexus) · [UE4SS](#ue4ss) · [Server](#server) · [Saves](#saves) · [Migration & verification](#migration--verification) · [Also](#also)

### Library

Import mods as archives (`.zip`/`.rar`/`.7z`/`.EXMODZ`, auto-detected — recognizes an
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

### Check mods against game data

Finds what a game update left behind in a mod: values copied
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

### Merge & Install

A merge queue with proactive conflict detection (including two mods adding a
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

### Weekly Changes

A real, row-by-row diff of what the game's own data changed between two
"Update data folder" runs, so you can see exactly why a mod broke after a patch instead of guessing.

### Map

Every world the game has, drawn from the game's own files: Olympus, Styx, Prometheus,
Elysium, every outpost, and worlds the game has but doesn't offer yet (tagged **In development**,
like ARK), each saying what it's played as (open world, missions, outpost). Tick any mix of
**Creatures**, **Fishing** and **Biomes** zones — click a spot for the creatures that spawn there
(their levels and each one's share), the fish that bite (fish quality, chance for each fish) and
the biome, with zones an installed mod changes marked. On top: dropship **drop sites**, the in-game
**grid**, **caves** tiered by their cave worms (1 up to 3, 2 for 4-6, 3 for 7 or more) with a tier
filter, hand-made **big caves** with the biome at each entrance, oil and enzyme **geysers**, **Great
Hunt dens** and **world boss** spawn spots, spots where **deep ore** and **exotics** can appear,
**lakes and rivers**, **fishing spots**, and **mission spots** by their mission's name (POTSHOT,
HABITAT, Blacksand…). Search finds creatures, fish, places and grid squares ("I7"); a picked place
has **Zoom to it** and **Copy** ("Olympus H2: Tier 3 cave · 7-11 cave worms"); **Map only** hides
everything over the picture. The maps are made once and kept: **Reload** redraws only what a game
update changed and says what changed, **Rebuild all maps** redraws everything, and Update
everything can do it for you (off by default). Your choices of what to show are remembered.

### Downloads & Nexus

Browses the community mod catalog (the Daedalus and Jimk72 projects) with
sortable/filterable columns, plus a native Nexus browsing/search view backed by the real Nexus API
(images, live search, per-card local-status badges, tracking) with `nxm://` protocol handling for
one-click downloads from the website.

### UE4SS

The official UE4SS loader ships inside the app (hash-checked) and installs in one
click, no download needed. Enable/disable installed UE4SS (Lua scripting framework) mods without
touching the framework's own built-in ones — "on" means UE4SS really starts the mod (a `1` in
`mods.txt` or an `enabled.txt` in its folder, UE4SS's own rule), so a mod sitting in the game that
UE4SS skips shows as **Off in UE4SS** and one tick turns it on — install/update/fully uninstall
the loader itself (uninstall correctly tells your own mods apart from the framework's bundled ones and only ever removes the
latter), link a UE4SS mod to its real Nexus page so it participates in update-checking the same way
Library mods do, and declare a mod's own minimum required UE4SS version (there's no per-mod manifest
to read this from automatically) to get a warning if your installed loader is older.

### Server

Run an Icarus dedicated server on this PC: install it with SteamCMD (or adopt one you
already have), start/stop/restart/update with SteamCMD's progress shown under the server's status,
a live log, server settings, creating a world on start, automation (restart on crash, daily
restart, update on restart, hourly update checks, start with the app), an Overview that says what
the server is doing **now**, what automation does **next** and what just **happened**, a **Connect**
card with the address friends type in (your home-network address, and your public one only when
you ask), Windows firewall rules, installing your merged pak and UE4SS mods, world backups and
restore, admins and bans, and each world's custom settings. For a server hosted elsewhere there's FTP file management
(upload/download/browse/delete), with saved site credentials in Windows Credential Manager and
FileZilla-style reconnect; one-click sync of a merged pak, the UE4SS loader, and UE4SS mods to a
connected server, with per-site remote path overrides for a host whose folder layout differs from
the built-in default.

### Saves

A full player save editor: characters (name, XP, duplicate/delete a slot), currencies,
talents (character and account-wide Workshop research), account/character/binary unlock flags,
Bestiary encounter progress (with each creature's picture), Accolade completion, the account-wide
item bank (with real item/creature icons resolved straight from the base game's own compiled
content, not text-only), and
tamed mounts (name, level, species, delete), and each world's custom world settings
(Arachnophobia Mode, Mission Structure Cleanup and the rest of the game's own list; Difficulty and
Hardcore are shown but never changed) — with mandatory automatic backups before every write
and hard safety gates (refuses to touch a save while the game is running, re-checked immediately
before the write itself).

### Migration & verification

Import a mod list straight from a classic IMM install and match it
against your Library, plus two comparison tools: one that diffs any two `.pak` files field by field
(used to verify this app's own merge output is byte-identical to classic IMM's), and one that shows
exactly what a mod's author changed between the version you have and an update.

### Also

A **Play** button in the header that says Starting… and then Running while Icarus is open
(a click while it runs brings the game forward, and Steam is never asked twice); three built-in themes plus a fully user-authorable custom skin (edit any of ~19 colors,
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
  server; `api.ipify.org`, only when you press "Show my public IP" on the Server page (it answers
  with your public IP address, which is shown and not saved); and, only if you configure one, your
  own FTP server on the Server tab. Nothing else. There is no telemetry, analytics, or usage tracking of any kind anywhere in this app.
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

### 4.2.0 (latest)

**Added**
- **Map** page (under Game): every world the game has, drawn from the game's own files. Olympus, Styx, Prometheus, Elysium and every outpost, plus worlds the game has but doesn't offer yet, tagged In development (ARK, for one). Each one says what it's played as: open world, missions or outpost.
- Creature, fishing and biome zones: tick any mix and click a spot to see the creatures that spawn there (their levels and each one's share), the fish that bite (fish quality, chance for each fish) and the biome. Zones an installed mod changes say so, with the game's own list beside them.
- On the map: dropship drop sites (zoom in for each one's landing spots), the in-game grid, caves by tier (1 up to 3 cave worms, 2 for 4-6, 3 for 7 or more; more worms usually means a better cave) with a tier filter, hand-made big caves with the biome at each entrance, oil and enzyme geysers, Great Hunt dens, world boss spawn spots, spots where deep ore and exotics can appear, lakes and rivers, fishing spots, and mission spots by their mission's name (POTSHOT, HABITAT, BLACK MARKET, Blacksand…).
- Click a cave for its cave worms, length, the creature zone it's in and the ores it can have. Any place: **Zoom to it**, and **Copy** to paste to friends ("Olympus H2: Tier 3 cave · 7-11 cave worms").
- Search finds creatures, fish, places ("tier 3", "geyser", a mission's name) and grid squares ("I7"); Enter zooms to what it found. **Map only** (the eye) hides everything drawn over the map. A key says what each marker means. What you tick is remembered.
- The maps are made once and kept. **Reload** redraws only what a game update changed and says what changed; **Rebuild all maps** redraws everything. Tick **Redraw during Update everything** to have Update everything do it after a game update (off by default).
- Server page: SteamCMD's progress now shows right under the server's status, with Cancel and the full output.
- Server Overview: **Now** says what the server is doing, **Coming up** what automation does next, and **Recent events** what just happened.
- Server **Connect** card: the address friends type into Direct Connect, with Copy. **Show my public IP** asks api.ipify.org for your internet address, only when you press it.
- **Play** in the header says Starting… after a click and Running (green dot) while Icarus is open. A click while it runs brings the game's window forward, and Steam is never asked twice.

**Changed**
- Saves › Bestiary: creature pictures are big enough to see (cut to the creature, in a bigger tile).
- Ctrl+1 to Ctrl+9 now include Map. Ctrl+0 is always Help.

**Fixed**
- Server: Check for update finds a newer build again. SteamCMD's answer was read wrongly, so the check, and the hourly automatic update, never saw an update.
- Server: Check for update and Update can be clicked as soon as you add your first server (they stayed greyed out).
- Server: a check's answer stays with the server it asked about, even if you pick another server before it finishes.
- Server: an hourly update check that never gets an answer gives up after 5 minutes and says so in Recent events. Before, it kept SteamCMD busy for good, which quietly stopped every automation (crash restarts, daily restarts).
- Server: after you update a server by hand, automation no longer stops it later to install the same build again.
- Server: only one SteamCMD run at a time. An Update waits for another server's check instead of running alongside it and mixing up the progress box.
- Saves: pictures are read again after Refresh game data, so a picture a game update changed shows without restarting Starlink.

### Earlier versions

Click a version to open its notes.

<details>
<summary><b>4.1.0</b></summary>

**Added**
- Your Nexus endorsements show in the Library by themselves: mods you've endorsed get a bright green thumb the first time the Library opens, no "Check endorsements" click needed.
- Mods you uploaded to Nexus yourself have no endorse thumb (Nexus doesn't let authors endorse their own mods).
- The Update pill on a UE4SS mod can be clicked: it opens the mod on the Nexus page (or points at a download already waiting on the Downloads page), the same as on data mods.
- After Starlink updates, it asks if you'd like to endorse Starlink on Nexus, with one click to do it. If you've already endorsed it, you're never asked.

**Changed**
- The merged pak is now called ISL-Merged_P.pak. It only takes the name zz_zz_ISL-Merged_9999_P.pak when ArcFission Library is turned on, because it has to load after that library's own pak. Your game and each server go by their own ArcFission Library, and the old name is cleaned up automatically the next time you install.
- The endorsed thumb is brighter and easier to see.
- The old IcarusStarlink.App.exe launcher (kept since 3.0, when the app was renamed IcarusStarlink.exe) is no longer included. If you still start Starlink from a shortcut to IcarusStarlink.App.exe, it keeps working, and Starlink still offers to move your shortcuts over. Starlink 2.x can't update itself to this version: download it once from GitHub or Nexus.

**Fixed**
- Server: "Resume the last prospect" is now a plain on/off box. Before, a box that looked off could leave the setting blank, which the server can't read, so it always went back to the last prospect. Open your server's settings and save once to fix an existing server.
- Server: saving no longer asks "ServerSettings.ini was changed outside Starlink" when only the server itself rewrote the file (it does that every time it runs).

</details>

<details>
<summary><b>4.0.1</b></summary>

### Fixed
- **Saves › Save check**: a character flag that a finished mission gives (Olympus' Glacier Extraction gives "Voxel Meta Resources Visible") was called left over, with a button to turn it off. Missions live on the account, not the character, and the check now counts them. Don't use that button in 4.0.0: it would turn off a real mission reward.

No breaking changes: your Library, profiles, settings and saves carry over.

Full changelog: https://github.com/MK-HATERS/IcarusStarlink/compare/v4.0.0...v4.0.1

</details>

<details>
<summary><b>4.0.0</b></summary>

### Added
- **Find the broken mod**: a guided search in rounds that finds which mod breaks something in game. Start it from Library › Check ▾ or a mod's right-click menu.
- **Downloads**: a Downloading list shows each download's progress, size and speed, with its own Cancel.
- **Mod editor**:
  - Fields are in sections, with Only changed fields, a back-to-game button and Add a field by name.
  - A plain tip under each field says what it does in the game, and stats quote the game's own line.
  - Take out of this mod (Delete) and Rename this row (F2), which also moves the mod's links to the new name.
- **Scale rules**: starter rules and suggestions from your mod's own rows. A field a rule sets is marked, and a value you typed by hand can be kept or given back to the rule.
- **Saves**:
  - Lists follow the game's order, with blueprints by tier and bench.
  - The Worlds tab lists each world's backups with Restore.
  - A bad number box turns red as you type.
- **Server**: buttons grey out while the server can't be changed (hover to see why). Save and Start notice when ServerSettings.ini was edited outside Starlink.
- **UE4SS config window**: plain names, groups, key fields and Reset to default. Unsaved edits are kept across files, and closing asks first.
- **Merge options**: Free Item Repair and No Weather (Beta: not tried in game yet).
- **Database**: New tags and a count for mods added since your last visit.
- **Settings**:
  - Size of text and controls (90% to 150%).
  - Backups to keep, set for each kind of backup.
  - A note on how Advanced log and Performance tracking differ.
- **Look**:
  - Header links (Play, Mods, UE4SS, Game, Server) with their own icons, and a one-click Theme button.
  - Ctrl+1–9 to switch pages and Ctrl+F to search.
  - Colours for mod types, sources, statuses and headings, and themed scrollbars.
  - Screen readers now read names, not code.

### Changed
- A download with a data part and a Lua part is always two linked rows in Library, whether the Lua part is on or off. Delete removes both.
- Discard on a download that's still installed asks first.
- UE4SS's own shared folder is always on: it can't be unticked or deleted.
- The mod editor's File JSON and Full EXMOD JSON views are gone; Fields edits every field.
- Craft Cost leaves vendor trades at their normal price.
- Tooltips everywhere rewritten in plain words.
- The game rewrites a save's DLC list from what your account owns each time it starts; the Saves page wording now says so.

### Fixed
- **Downloads and imports**: a fake or cut-off .pak (a web page, a broken download) is refused, even inside a bundle or a shared list.
- **UE4SS configs**:
  - They keep their own encoding: ANSI text with accents isn't mangled on Save or mod update.
  - Save no longer undoes changes made to the file while the window was open.
  - Odd downloaded configs no longer freeze the window.
- **Share a mod list**: a patch writes only plain settings into UE4SS mods, never code.
- **Saves**:
  - Save names a bad number box instead of saying "Saved".
  - Restore refuses a backup with no Profile.json.
  - Restored files get the time of the restore, so Steam Cloud doesn't put its older copy back.
  - Talent, accolade and character entries the page can't read are kept.
- **Library**:
  - UE4SS Apply and deleting a mod whose Lua part is in the game wait for Icarus to be closed.
  - A Lua part no longer gets left behind on Delete.
- **Server**: settings, bans and mod files are safe from blank forms and changes during an update. A player name in the log can't make a crash look like a clean stop. Remote uploads report honestly when they break off.
- **Text size**: menus, tooltips and question boxes now grow with the rest.
- The UnrealPak setup question no longer appears twice on first launch.

No breaking changes: your Library, profiles, settings and saves carry over.

Full changelog: https://github.com/MK-HATERS/IcarusStarlink/compare/v3.4.0...v4.0.0

</details>

<details>
<summary><b>3.4.0</b></summary>

**Added**
- Share a mod list, rebuilt. A patch now carries where each mod came from (its Nexus or IMM Database ids), pak mods, and the UE4SS mods on in your game with their settings (untick "Include UE4SS mod settings" to leave those out). Importing one recreates the profile, offers the mods you lack as one download through the Downloads page, writes the patch's settings into its UE4SS mods, offers to turn them on, and lists any copy at a different version in the warnings. Patches from earlier versions still import.
- Server page › Mods › Share with players: Export patch for players (what the server runs: its mod list and its UE4SS mods with the server's settings), Apply a patch to this server (imports it, then puts its UE4SS mods and settings on the server), and Compare a patch (one row per mod across the patch, your game and the server).
- Server page: a UE4SS mod with the same files but different settings on the server now reads "Same mod, different settings", with Match settings… to pick which copy both use (the replaced file is backed up first).
- Edit config: config.lua opens first; a mod's config.default.lua gives every setting its "Default:" and Reset to default; Lua settings get sliders, choices, drop-downs and restart notes from the comment tags the MkHaters mods use; a setting marked [hidden] stays out of the fields.

**Changed**
- Updating a UE4SS mod keeps your settings. Your values go into the new version's config, new settings arrive at their defaults, and settings the mod no longer has are listed with your old values so you can set a renamed one again. The Library marks the mod and Edit config shows what changed until you press Done reviewing. A config the merge can't carry (a table over several lines) is kept as it was.
- Reinstall original takes the shipped config, as downloaded.

**Fixed**
- The Server page said "Same as your game" while the server ran different settings for a UE4SS mod.
- A file only the server's copy has (Pet Commander's Invulnerable.ini, the world's tames) no longer reads as different settings.
- Edit config no longer lists a mod's code modules (mk_config.lua, mt_config.lua) as config files.

</details>

<details>
<summary><b>3.3.0</b></summary>

**Added**
- Mod config window: values no longer show the comment after them; a list in the comment becomes a drop-down, on/off settings are switches, numbers get a box with their range (or a slider when the mod asks for one), and settings that need a restart say so. There's a search box too. Save refuses a value the mod can't read as a number ("1,5", "ten").
- Editor: six more starter items (Oxygen Tank, Biofuel Generator, Stone Furnace, Ice Box, Wood Bow, Basic Bandage), and generator fields read Output and Fuel efficiency.

**Changed**
- The player's starting stats merge one stat at a time, as classic IMM does: a mod that changes 3 stats changes only those 3, and two mods changing different stats both apply. Conflicts show per stat.
- Speed, Player and XP Boost and Disable Temperatures keep the stats your queued mods set.
- An update from Nexus is only installed once it matches GitHub's checksum for the same version.
- ArcFission: Starlink now keeps the highest-numbered data pak, follows the ArcFission Library's own rules for which mods and rows count, and warns when the library is in a renamed folder. The server copy no longer takes the game's ArcFission state files.

**Fixed**
- A mod listing only some of the player's starting stats removed all the others from the merge (no health, stamina or oxygen).
- Closing Starlink while it starts (or Windows shutting down) no longer crashes.
- The update window could take a different file from the Nexus page as the update; a slow Nexus made the whole update check fail silently; an older Starlink file was offered as an "update".
- Two automatic "sure" fixes in the mod check could change values that work in game.
- A mod saved during a build, or a refresh finishing late, could show the wrong "Changed" / "In game" status; the Changes waiting bar could leave a mod out.
- Fix… in Check the merge now says why when it can't open; Review re-checks mods edited outside Starlink.
- ArcFission rows: accented text, Lua line breaks and some number forms were misread; a mod needing a newer library is left out as the game leaves it out.
- Config window: a ';' inside quotes was cut off; sliders could write "NaN"; a key listed twice now uses the last one, as mods read it.

</details>

<details>
<summary><b>3.2.0</b></summary>

### Added
- **The mod check finds more of what the game can't use.** In the Library's warning badge and fix window:
  - **tables the game doesn't have** (the merge skipped all their rows). When the game has that table in another folder, moving the rows there is a sure fix.
  - **fields the game doesn't have**, such as a typo or a field a game update renamed. A field that only differs in capital letters is matched to the game's spelling as a sure fix; a close name is offered to check.
  - **values of the wrong kind**, such as text where the game has a number.
  - **text under another row's key** (a copied item that still uses the original's name key). The fix gives it its own key.
- **Check the merge**: one window for everything Merge & Install finds, in three tabs:
  - **Conflicts**: the picker you know.
  - **Between mods**: two mods bringing the same file, talent tree clashes, and an add-on queued without the mod it needs (it names that mod).
  - **Per mod**: the same check as the Library's badge. **Fix…** opens that mod's fix window.
  - **Copy report** copies everything it found, with a short code for each kind (ISL-…), for bug reports.
- **ArcFission mods (Wireless Power, Lumber Yard…) work straight away.** Their rows are read from the mods themselves, so Merge & Install has them before the game has ever started with them: no "start the game, quit, merge again" any more. After an Icarus update they can't bring old game values back. Install removes ArcFission's older 19 MB data copies (the game was still loading them), and so does **Install my merged pak** on a local server, which moves them into the server's backups.

### Changed
- **Merge & Install is tidier.** One red bar ("3 conflicts · 1 clash between mods · 2 mods with problems") with one **Review**, instead of two badges, two bars and two review buttons.
- **The Library's warning badges follow Merge & Install's check**, so the two never disagree.
- **Clearer Library words:**
  - **Changes waiting** (was "Not in game yet"; it also counts mods waiting to be taken out).
  - **Changed** for a mod changed since the last install (was "Updated", easy to mix up with the Update pill).
  - **Will be removed** for a mod you unticked that's still in the game (was a green "In game").
- **Safer buttons:** **Clear**, **Delete profile** and **Restore profile backup** now ask first. Clear keeps the list as a backup point first, so Restore profile backup brings it back.
- **Set baseline is gone.** It pushed a saved list of mods into every profile, and removing one of them didn't stick.
- **Build warnings name the mod and row.** The "created as a new item" note now only shows for rows that look like an out-of-date edit, not for every new row a mod adds.
- The Library's selection bar keeps its space, so the list no longer jumps down when you click a mod.
- The folded side menu no longer cuts its items off on the right.
- Help matches the current controls.

### Fixed
- A mod writing a game row's name in other capitals (for example `pickaxe_mk2` for `Pickaxe_MK2`) added a half-empty second row instead of changing the game's row. Two mods spelling one row differently weren't shown as a conflict either.
- A Fix… that couldn't open (the mod is open in the editor, or a check is running) now says why on the page you clicked it from.

No breaking changes; your mods, profiles and settings carry over. Full changelog: https://github.com/MK-HATERS/IcarusStarlink/compare/v3.1.0...v3.2.0

</details>

<details>
<summary><b>3.1.0</b></summary>

**New**
- **A real update window.** Update now opens a window that shows each step: getting the download, downloading (with MB and speed), checking the file against the published checksum, unpacking, then closing Starlink to install. Cancel works at any point before the install, and nothing is changed until then. While Starlink is closed, a small "Installing…" window shows until it opens again.
- **Stable updates come from Starlink's Nexus page** when you're signed in to Nexus (Settings › Nexus); otherwise from GitHub, as before. Betas still come from GitHub.
  - **Nexus Premium:** the update downloads straight from Nexus.
  - **Other accounts:** the Nexus page opens on the right file. Press Slow download (or Mod Manager Download) there, and the update window picks it up. You can also choose "Get it from GitHub instead".
  - Every download is checked against the same published checksum, wherever it came from.
  - Settings › Maintenance says where your updates come from.
- Pressing **Mod Manager Download** on Starlink's own Nexus page (or Download on its card in the Database page) now offers it as an update, instead of importing Starlink into your Library as a mod.

**Changed**
- Clearer menu icons: Database, Saves, Weekly Changes, Settings, Help and Library, also in the folded menu.
- The title bar says "mod manager for Icarus by MkHaters".

This update installs the old way, because it's run by the version you have now. The new window shows from your next update on.

Full changelog: https://github.com/MK-HATERS/IcarusStarlink/compare/v3.0.1...v3.1.0

</details>

<details>
<summary><b>3.0.1</b></summary>

**New**
- **The Library tells you when a mod changed since your last Merge & Install.** A mod you updated in Starlink, imported again, or changed outside Starlink now shows **Updated** (for example "v1.0 → v1.1 · needs Merge & Install"), and the bar at the bottom counts it. This is checked whenever the Library changes and when you switch back to Starlink. It starts with your next Merge & Install, because that build is the first one Starlink records.

**Fixed: Server › Mods, the UE4SS table**
- The Server column now says whether a mod is **on or off** on the server, like the Your game column. Unticking a mod switches it off on the server but leaves it there; it used to look as if nothing happened.
- A row reads the server again right after you tick or untick it.
- A mod you removed from your Library reads "Not in your game — Remove takes it off", highlighted.
- A **Refresh** button, and the table updates by itself when you add, update or remove a UE4SS mod in the Library.

Full changelog: https://github.com/MK-HATERS/IcarusStarlink/compare/v3.0.0...v3.0.1

</details>

<details>
<summary><b>3.0.0</b></summary>

A big one: a rebuilt Saves page, a new exe name, a review of every page, and a polish pass across the whole app. Back up your saves before trying the new Saves page (Saves › Backup & restore).

**The app is now `IcarusStarlink.exe`**
- It used to be `IcarusStarlink.App.exe`. That name is now a tiny launcher, kept so this update can finish.
- The first time the new version starts, Starlink offers to move your desktop, Start menu and taskbar shortcuts and your Nexus download links over to the new exe. Then it deletes the old one and checks it's gone.
- If anything can't be moved, it keeps the old exe so nothing breaks, and asks again next time.

**Saves page (out of Beta)**
- **Tabs:**
  - Talents grouped by tree.
  - Workshop research, with costs.
  - Missions by map.
  - Unlocks grouped by what each unlocks.
  - Accolades by category; tiered sets like Wolf Killer I–V share one row.
  - Bestiary by biome, with a progress bar per creature.
  - Characters and currencies in tidy cards.
  - Long lists fold, and every group shows a count.
- **Missions and rewards:** marking a mission done turns on the unlock it rewards, and the other way round, with a message saying so. Every reward says how to get it.
- **Save check** on Overview: finds learned blueprints whose unlock is off, finished missions with their reward off, ranks above the game's maximum, and more. Sure fixes are one click, or use "Fix all sure ones". DLC and Supporter Edition content gets an optional note per pack.
- **Leftovers from old mods:** talents, research and items from mods you no longer have can be taken out. Rows from mods you still have are kept.
- **Points:** for example "Level 313 · Talents 583 of 106", worked out from what's installed, with an **over budget** label and a plain explanation. **Change points per level** makes a Library mod with your own point curves.
- **Mount and pet editor:** rename, level, lineage, look, genetics and talents, with the talent budget from the installed mods.
- **Worlds:** edit a world's custom settings. A world saved under an id now shows its map name.
- **Safety:** a Save bar that says what's unsaved, a dot on every changed row, and a question before you switch worlds or close with unsaved edits.

**A review of every page**
- **Library:**
  - Data mods and UE4SS mods in two panels.
  - Restore, Delete, Reinstall and Get update wait while a mod is being checked, converted or edited.
  - One package per Nexus download.
  - Filter counts stay current.
  - Update all doesn't ask for every mod.
  - Empty panels say how to see your mods again.
- **Editor:**
  - Scale rules no longer wipe values you typed by hand, and Refresh game data keeps your typed values.
  - Number fields line up, and times show as hours and minutes.
  - Each change can be undone with ×.
  - Closing Starlink warns about unsaved editor changes.
- **Merge & Install:**
  - When an EXMOD and a pak clash, the later mod in the queue really wins, and the warning names it.
  - Custom Stacks/Slots values are kept.
  - Install can't run twice at once.
  - Roll back copies the old pak in before removing anything.
- **Database:**
  - Nexus cards fill the window and their buttons line up.
  - Search by mod ID or link.
  - A stuck download gives up after 60 seconds, and there's a Cancel downloads button.
- **Downloads:** clean mod names instead of file names, readable dates.
- **Server:**
  - Servers sharing one install can't change files while another runs.
  - Restart and Update wait for SteamCMD.
  - Backups no longer freeze the window.
  - Log lines are coloured by level.
  - Passwords are hidden, with a show button.
  - Worlds show when they were last saved and their size.
  - Admins & bans flags lines that aren't Steam64 ids.
  - One table shows which UE4SS mods are out of date.
- **Settings:**
  - Update channels: Stable or Beta.
  - "Check for updates when Starlink starts".
  - Every tab uses the same cards.
- **Help:** a search box and readable line widths.
- **Weekly Changes:** proper empty states and wording.
- **Whole app:**
  - The left menu is grouped (Find mods · Your mods · Game) and folds to icons.
  - Startup windows open one at a time.
  - Nexus links clicked while Starlink is starting are no longer lost.
  - Starlink takes Nexus download links back from other mod managers and passes other games' links on to them.
  - A corrupt settings file is kept as a backup.
  - The updater waits up to 3 minutes for Starlink to close and reopens it either way.
  - Closing warns about unsaved edits and running work.

Uses Crystal Ferrai's UeSaveGame (Apache 2.0) for mount data. Full changelog: https://github.com/MK-HATERS/IcarusStarlink/compare/v2.9.1...v3.0.0

</details>

Versions 1.x and 2.x: see the **[Releases page](https://github.com/MK-HATERS/IcarusStarlink/releases)**, which also has every release's download.

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
