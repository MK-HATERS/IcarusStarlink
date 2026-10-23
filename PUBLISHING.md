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

## Beta releases (GitHub only)

The app's Beta update channel (Settings › Maintenance) reads the newest beta from a
prerelease tagged `beta` that holds only `release.json`. Stable users never see betas, because
`releases/latest` skips prereleases. Both steps below must stay prereleases.

1. `<Version>` in `IcarusStarlink.App.csproj` = `X.Y.Z-beta.N`; tag `vX.Y.Z-beta.N`.
2. Publish and zip as for a normal release; `release.json` has `"version": "X.Y.Z-beta.N"`
   (exactly the tag without its `v`: the download URL is built from it).
3. `gh release create vX.Y.Z-beta.N <zip> release.json --prerelease --title "vX.Y.Z beta N" --notes-file notes.md`
4. Point the channel at it: the first time
   `gh release create beta release.json --prerelease --title "Beta channel" --notes "Points the Beta update channel at the newest beta."`,
   after that `gh release upload beta release.json --clobber`. **Never** publish `beta` as a
   normal release: every Stable user would be offered the beta.
5. Check: `curl -sL https://github.com/MK-HATERS/IcarusStarlink/releases/download/beta/release.json`.

A stable release after betas is a normal release; Beta users get it too.

## Decided

**UnrealPak.exe.** Kept as it is (decided 2026-10-16): `UnrealPakInstaller.cs` fetches the
UE 4.27 `UnrealPak.exe` from a fixed GitHub release in this repo on first use, the same approach
classic IMM has used for years. Not bundled into the app zip, and not discussed in the README.
