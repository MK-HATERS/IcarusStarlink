# Icarus Starlink

A mod manager for [Icarus](https://store.steampowered.com/app/1149460/ICARUS/), built as a
clean-room successor to two existing tools: **Icarus Workshop** (Nexus mod 304, currently
unavailable) and **classic IMM** (Jimk72's original Icarus Mod Manager, which Icarus Workshop
itself was built on top of). Nothing here is copied from either — this is a fresh implementation
built from the public feature spec, the real EXMOD/EXMODZ mod format (reverse-engineered from real
sample files), and original research into Icarus's own save/pak formats.

Icarus Starlink now covers everything both of those tools do, plus a number of things neither of
them has.

## What it does

**Library** — import mods as EXMODZ archives, folders, or prebuilt `.pak` files; organize with
pin/favorite/notes and automatic variant grouping; a full EXMOD editor (item-field, raw file JSON,
and full-package JSON views, amber-highlighted diffs against the real base game data, mass edit
across selected items, undo, add-item-from-game-data, and a cross-file "what else references this"
search over the whole extracted data folder).

**Merge & Install** — a merge queue with conflict detection and a manual per-field conflict picker,
gameplay-option toggles (stack/slot size, craft cost, XP/speed/player boosts, remove weight,
unlimited ammo, disable temperatures — values sourced from classic IMM's own documented behavior),
profiles with exportable/importable patches, and a rebuild → install pipeline that backs up
whatever it's about to replace.

**Downloads & Nexus** — browses the community mod catalog (the Daedalus and Jimk72 projects) with
sortable/filterable columns, plus a native Nexus browsing/search view backed by the real Nexus API
(images, tracking, per-card update badges) with `nxm://` protocol handling for one-click downloads
from the website.

**UE4SS** — enable/disable installed UE4SS (Lua scripting framework) mods without touching the
framework's own built-in ones, install/update the loader itself, and — new this session — link a
UE4SS mod to its real Nexus page so it participates in update-checking the same way Library mods
do.

**Server** — FTP file management for a dedicated Icarus server (upload/download/browse/delete),
with saved site credentials in Windows Credential Manager.

**Saves** — a player save editor (characters, currencies, talents, account/character/binary flags)
with mandatory automatic backups before every write and hard safety gates (refuses to touch a save
while the game is running).

**Migration & verification** — import a mod list straight from a classic IMM install and match it
against your Library, plus a pak comparison tool that unpacks and diffs two `.pak` files field by
field (used to verify this app's own merge output is identical to classic IMM's).

**Also**: three built-in themes plus a fully user-authorable custom skin, an in-app auto-updater,
diagnostics export, and an in-app Help page.

## What it doesn't (yet)

- **Save editor**: cosmetics, items/loadouts, bestiary, and accolades aren't editable yet (slots,
  backup/restore, characters, currencies, talents, and flags are). Local icon extraction from the
  game's own paks was researched and found feasible but hasn't been built.
- **Nexus single-click SSO login** — Nexus's SSO flow needs an application slug only their staff
  can issue; the app uses a manual paste-your-API-key flow instead.
- The EXMOD editor's panes don't detach into separate floating windows (the editor window itself
  does pop out, and multiple mods can be edited in separate windows at once — just not individual
  panes within one).

## Building

Requires the .NET 10 SDK on Windows (WPF).

```
dotnet build IcarusStarlink.slnx
dotnet test IcarusStarlink.slnx
```

`tools/WinTools` is a small UI-automation CLI used during development to drive and screenshot the
running app — not part of the shipped product.
