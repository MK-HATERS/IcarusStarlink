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

## What it does

**Library** — import mods as archives (`.zip`/`.rar`/`.7z`/`.EXMODZ`, auto-detected — recognizes an
EXMOD-shaped mod, a bare `.pak`, or a UE4SS mod from what's actually inside), folders, or prebuilt
`.pak` files; organize with pin/favorite/notes/rename and automatic variant grouping; user-
configurable columns (right-click the header bar); a full EXMOD editor (item-field, raw file JSON,
and full-package JSON views, amber-highlighted diffs against the real base game data, mass edit
across selected items, undo, add-item-from-game-data, a pop-out for any pane with its scroll
position kept in sync with wherever else that same view is open, and a cross-file "what else
references this" search over the whole extracted data folder); real asset preview for a mod's own
textures, static/skeletal meshes (3D, orbit camera), sound (playback), and materials (texture/color/
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

**Merge & Install** — a merge queue with proactive conflict detection (including two mods adding a
same-named new item, and a queue-wide check for a mod referencing something only another queued mod
declares) and a manual per-field conflict picker that shows the live base-game value plus a
buff/nerf hint alongside each candidate; array-shaped fields (recipe lists, loot tables) combine
automatically when every mod's change is a clean addition rather than picking just one and dropping
the rest; a baseline mod list that auto-joins every profile's queue, gameplay-option toggles
(stack/slot size, craft cost, XP/speed/player/taming boosts, remove weight, unlimited ammo, disable
temperatures, remove level cap — values sourced from classic IMM's own documented behavior, or a
real community mod's where classic IMM never covered one) that participate in the same conflict
detection as regular mods, profiles (with their own backup/restore) with exportable/importable
patches, and a rebuild → install pipeline that backs up whatever it's about to replace and
independently re-reads the pak it just built to confirm nothing staged for packing went missing.

**Weekly Changes** — a real, row-by-row diff of what the game's own data changed between two
"Update data folder" runs, so you can see exactly why a mod broke after a patch instead of guessing.

**Downloads & Nexus** — browses the community mod catalog (the Daedalus and Jimk72 projects) with
sortable/filterable columns, plus a native Nexus browsing/search view backed by the real Nexus API
(images, live search, per-card local-status badges, tracking) with `nxm://` protocol handling for
one-click downloads from the website.

**UE4SS** — enable/disable installed UE4SS (Lua scripting framework) mods without touching the
framework's own built-in ones, install/update/fully uninstall the loader itself (uninstall
correctly tells your own mods apart from the framework's bundled ones and only ever removes the
latter), link a UE4SS mod to its real Nexus page so it participates in update-checking the same way
Library mods do, and declare a mod's own minimum required UE4SS version (there's no per-mod manifest
to read this from automatically) to get a warning if your installed loader is older.

**Server** — FTP file management for a dedicated Icarus server (upload/download/browse/delete),
with saved site credentials in Windows Credential Manager and FileZilla-style reconnect; one-click
sync of a merged pak, the UE4SS loader, and UE4SS mods to a connected server, with per-site remote
path overrides for a host whose folder layout differs from the built-in default.

**Saves** — a full player save editor: characters (name, XP, duplicate/delete a slot), currencies,
talents (character and account-wide Workshop research), account/character/binary unlock flags,
Bestiary encounter progress, Accolade completion, the account-wide item bank (with real item/
creature icons resolved straight from the base game's own compiled content, not text-only), and
tamed mounts (name, level, species, delete) — with mandatory automatic backups before every write
and hard safety gates (refuses to touch a save while the game is running, re-checked immediately
before the write itself).

**Migration & verification** — import a mod list straight from a classic IMM install and match it
against your Library, plus two comparison tools: one that diffs any two `.pak` files field by field
(used to verify this app's own merge output is byte-identical to classic IMM's), and one that shows
exactly what a mod's author changed between the version you have and an update.

**Also**: three built-in themes plus a fully user-authorable custom skin (edit any of ~19 colors,
or hand-edit the underlying JSON), a real in-app auto-updater (verifies the download's integrity,
downloads, applies with automatic rollback if it fails partway, and relaunches itself — never
touches your mods, profiles, or settings), diagnostics export, and an in-app Help page covering
every feature above.

## What it doesn't (yet)

- **Nexus single-click SSO login** — Nexus's SSO flow needs an application slug only their staff
  can issue; the app uses a manual paste-your-API-key flow instead.
- **UnrealPak.exe is bundled, not downloaded**, and pinned to the UE4 build Icarus itself uses
  (4.27) rather than "whatever's newest" — a newer UE5 UnrealPak writes pak formats Icarus's own
  engine can't load. Real, concrete exposure here, not just an unverified worry: the Unreal Engine
  EULA defines "Engine Tools" as editors/tools included in the Engine Code (UnrealPak.exe is
  exactly that) and states any public Distribution of Engine Tools must go through Epic's own
  Marketplace (Fab) or a fork of Epic's GitHub UnrealEngine Network — neither of which is what
  bundling it into a GitHub release does. Classic IMM has bundled UnrealPak.exe the same way for
  years with no known enforcement action, which is real-world precedent this is tolerated in
  practice, not evidence it's actually permitted. This isn't legal advice — get a human legal read
  on the exact current EULA text before a wider public release.
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
- **[Nexus Mods](https://www.nexusmods.com/icarus)** — the platform and the real API this app's
  Nexus integration talks to.

## Disclaimer

Icarus Starlink is an independent, fan-made tool and is not affiliated with, endorsed by, or
associated with RocketWerkz or the Icarus development team. Icarus and all related assets are the
property of their respective owners.
