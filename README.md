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
across selected items, undo, add-item-from-game-data, and a cross-file "what else references this"
search over the whole extracted data folder); proactive staleness detection that flags mods whose
targets a game update may have renamed or removed, with confidence-tiered auto-repair (always
backed up first).

**Merge & Install** — a merge queue with proactive conflict detection and a manual per-field
conflict picker, a baseline mod list that auto-joins every profile's queue, gameplay-option toggles
(stack/slot size, craft cost, XP/speed/player/taming boosts, remove weight, unlimited ammo, disable
temperatures, remove level cap — values sourced from classic IMM's own documented behavior, or a
real community mod's where classic IMM never covered one) that participate in the same conflict
detection as regular mods, profiles with exportable/importable patches, and a rebuild → install
pipeline that backs up whatever it's about to replace.

**Weekly Changes** — a real, row-by-row diff of what the game's own data changed between two
"Update data folder" runs, so you can see exactly why a mod broke after a patch instead of guessing.

**Downloads & Nexus** — browses the community mod catalog (the Daedalus and Jimk72 projects) with
sortable/filterable columns, plus a native Nexus browsing/search view backed by the real Nexus API
(images, live search, per-card local-status badges, tracking) with `nxm://` protocol handling for
one-click downloads from the website.

**UE4SS** — enable/disable installed UE4SS (Lua scripting framework) mods without touching the
framework's own built-in ones, install/update/fully uninstall the loader itself (uninstall
correctly tells your own mods apart from the framework's bundled ones and only ever removes the
latter), and link a UE4SS mod to its real Nexus page so it participates in update-checking the
same way Library mods do.

**Server** — FTP file management for a dedicated Icarus server (upload/download/browse/delete),
with saved site credentials in Windows Credential Manager and FileZilla-style reconnect.

**Saves** — a full player save editor: characters (name, XP), currencies, talents (character and
account-wide Workshop research), account/character/binary unlock flags, Bestiary encounter
progress, Accolade completion, the account-wide item bank, and per-character cosmetic values — with
mandatory automatic backups before every write and hard safety gates (refuses to touch a save while
the game is running).

**Migration & verification** — import a mod list straight from a classic IMM install and match it
against your Library, plus two comparison tools: one that diffs any two `.pak` files field by field
(used to verify this app's own merge output is byte-identical to classic IMM's), and one that shows
exactly what a mod's author changed between the version you have and an update.

**Also**: three built-in themes plus a fully user-authorable custom skin (edit any of ~19 colors,
or hand-edit the underlying JSON), a real in-app auto-updater (downloads, applies, and relaunches
itself — never touches your mods, profiles, or settings), diagnostics export, and an in-app Help
page covering every feature above.

## What it doesn't (yet)

- **Nexus single-click SSO login** — Nexus's SSO flow needs an application slug only their staff
  can issue; the app uses a manual paste-your-API-key flow instead.
- The EXMOD editor's panes don't detach into separate floating windows (the editor window itself
  does pop out, and multiple mods can be edited in separate windows at once — just not individual
  panes within one).
- **UnrealPak.exe is bundled, not downloaded**, and pinned to the UE4 build Icarus itself uses
  (4.27) rather than "whatever's newest" — a newer UE5 UnrealPak writes pak formats Icarus's own
  engine can't load. Whether Epic's own EULA permits redistributing UnrealPak.exe at all has not
  been independently verified — if you're taking this app to a wider public release, that's worth
  a human legal read before shipping the bundled copy.

## Building

Requires the .NET 10 SDK on Windows (WPF).

```console
dotnet build IcarusStarlink.slnx
dotnet test IcarusStarlink.slnx
```

`tools/WinTools` is a small UI-automation CLI used during development to drive and screenshot the
running app — not part of the shipped product.

## Disclaimer

Icarus Starlink is an independent, fan-made tool and is not affiliated with, endorsed by, or
associated with RocketWerkz or the Icarus development team. Icarus and all related assets are the
property of their respective owners.
