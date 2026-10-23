# UeSaveGame (vendored)

Crystal Ferrai's UeSaveGame, <https://github.com/CrystalFerrai/UeSaveGame>, Apache License 2.0
(`license.txt` here). Copied at commit `c716f8fcde5e01749893a5b2937e1169f98f29a3`, the commit
IcarusSaveLib (its Icarus save library, which pins it as a submodule) builds against. It isn't
published on NuGet, so the source is kept here.

Starlink uses it to read and write a saved mount's RecorderBlob (Mounts.json): the Unreal
tagged-property data that holds a mount's name, experience, talents, genetics and lineage.
Checked on the user's save before use: every mount in Mounts.json and its 12 backups (61) reads
and writes back byte for byte.

Changes from upstream (Apache-2.0 §4(b)): `UeSaveGame.csproj` only, `<Platforms>x64</Platforms>`
removed so it builds with the rest of the solution, and the vendored code's nullable warnings
silenced. No source file is changed.
