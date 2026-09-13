# Publishing checklist (Nexus Mods)

Internal notes for cutting a Nexus Mods file-page upload — not user-facing, not linked from
README.md. Re-check this before every upload, not just the first one.

## Paste-ready "Permissions and Credits" text

Nexus's file-upload flow has its own dedicated field for stating reuse terms, separate from any
linked license document. Paste this (or the current equivalent from `LICENSE.md`, if it's
changed since):

> Source-available, not open-source. You may download and use this freely, including modding your
> own copy of Icarus — but you may not modify, repackage, or redistribute it (including forks)
> without asking first. Full terms: <https://github.com/MK-HATERS/IcarusStarlink/blob/master/LICENSE.md>

## Before every upload

- [ ] **Category**: upload as a Modding Tool / Utility, not as a regular mod.
- [ ] **VirusTotal**: run this release's actual `IcarusStarlink.App.exe` (or the full zip) through
      <https://www.virustotal.com/> and link the report in the file description. Do this per
      release — a new build is a new binary, an old scan doesn't cover it.
- [ ] **Description** pulls from `README.md`'s own "What this app touches on your system" and
      "FAQ / Troubleshooting" sections so the disclosures on Nexus match what's actually true of
      the build being uploaded.
- [ ] **Version number** in the Nexus upload matches the git tag/`<Version>` actually being
      published.
- [ ] **Screenshots**: at least a few (Library, Merge & Install, Save editor, Nexus browser) —
      none currently exist anywhere in this repo; take fresh ones against the current UI before
      the first upload.

## Outstanding — needs a human decision, not a code fix

**UnrealPak.exe redistribution.** `UnrealPakInstaller.cs` fetches `UnrealPak.exe` (Epic's own
compiled Unreal Engine 4.27 tool) from a public GitHub release asset in this repo on first use.
README.md's own "What it doesn't (yet)" section already documents this candidly: the Unreal
Engine EULA defines this as an "Engine Tool," and public Distribution of Engine Tools is meant to
go through Epic's Fab marketplace or a fork of Epic's own GitHub UnrealEngine org — a plain public
GitHub release asset satisfies neither. Classic IMM bundling the same exe for years without
enforcement is real-world precedent this is *tolerated*, not evidence it's *permitted*.

This isn't something to silently work around in code without deciding the actual approach first —
options, roughly in order of how much they change: (a) get an actual legal read on the current
EULA text before a wider public release, (b) source `UnrealPak.exe` from the user's own
already-licensed Unreal Engine/Epic Games Launcher install at runtime instead of hosting it here,
(c) drop the auto-install convenience and point users at an Epic-authorized source themselves.
Resolve or consciously accept this risk before a Nexus upload — it's the single highest-risk item
found in the last full readiness pass over this repo.
