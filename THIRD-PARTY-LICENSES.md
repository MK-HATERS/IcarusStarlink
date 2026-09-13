# Third-party licenses

Icarus Starlink is built on a number of open-source libraries. This file lists every package
compiled into the shipped, self-contained release build, grouped by license, with each
package's own copyright notice reproduced or linked where its license requires it. None of these
projects endorse or are affiliated with Icarus Starlink — they're credited here because their
license terms ask for it (or because it's simply the right thing to do).

This list was generated from the actual resolved NuGet dependency closure of a real build
(`src/IcarusStarlink.App/obj/project.assets.json`), not hand-maintained from memory — see
`README.md`'s own Credits section for a shorter, human-facing version of the same list.

## Apache License 2.0

- **[CUE4Parse](https://github.com/FabianFG/CUE4Parse)** and **CUE4Parse-Conversion** (1.2.2.202608) — Copyright FabianFG and contributors. The core Unreal Engine asset-parsing/conversion library this app's own data extraction, diffing, and asset-preview features are built on. CUE4Parse's own upstream NOTICE file lists the further third-party components *it* bundles — reproduced in full below, per Apache-2.0 §4(d).
- **Serilog**, **Serilog.Extensions.Hosting**, **Serilog.Extensions.Logging**, **Serilog.Sinks.Console**, **Serilog.Sinks.File** — Copyright Serilog Contributors.
- **SQLitePCLRaw.bundle_e_sqlite3**, **SQLitePCLRaw.core**, **SQLitePCLRaw.lib.e_sqlite3**, **SQLitePCLRaw.provider.e_sqlite3** (2.1.12) — Copyright Kim Christensen, Eric Sink et al.

Full license text: <https://www.apache.org/licenses/LICENSE-2.0>

## MIT License

AssetRipper.TextureDecoder (2.6.2) · BouncyCastle.Cryptography (2.6.2) ·
CommunityToolkit.HighPerformance / CommunityToolkit.Mvvm (8.4.2) · FixedMathSharp (4.0.1) ·
FluentFTP (54.2.0) · Fmod5Sharp (3.1.0) · GenericReader (2.3.0) · IndexRange (1.1.0) ·
Infrablack.UE4Config (0.7.2.97) · K4os.Compression.LZ4 / K4os.Compression.LZ4.Streams /
K4os.Hash.xxHash (1.3.8 / 1.0.8) · LZMA-SDK (22.1.1, MIT wrapper around the public-domain LZMA
SDK) · MemoryPack / MemoryPack.Core / MemoryPack.Generator (1.21.4) ·
Microsoft.Bcl.Memory, Microsoft.Data.Sqlite(.Core), Microsoft.Extensions.\* (Configuration,
DependencyInjection, Diagnostics, FileProviders, FileSystemGlobbing, Hosting, Http, Logging,
Options, Primitives — all 10.0.11) · Microsoft.NETCore.Platforms (1.1.0) ·
NAudio.Core (2.2.1) · NETStandard.Library (1.6.1) · Newtonsoft.Json (13.0.4) ·
OffiUtils (3.2.0) · OggVorbisEncoder (1.2.2) · Oodle.NET / OodleSharp (2.2.0 / 0.0.1) ·
SharpCompress (1.0.0) · SharpGLTF.Core / .Runtime / .Toolkit (1.0.6) ·
SkiaSharp / SkiaSharp.NativeAssets.Win32 / .macOS (2.88.9) · SubstreamSharp (1.0.3) ·
System.IO.Hashing (10.0.8) · System.Numerics.Tensors (10.0.10) · VGAudio (2.2.1) ·
Zlib-ng.NET (1.2.0) · ZstdSharp.Port (0.8.8)

Full license text: <https://opensource.org/license/mit>

## BSD 2-Clause License

- **Blake3** (2.2.1) — Copyright (c) 2020, Alexandre Mutel.

Full license text: <https://opensource.org/license/bsd-2-clause>

## Six Labors Split License 1.0

- **SixLabors.ImageSharp** (3.1.12) — Copyright (c) Six Labors. Pulled in only as an internal
  implementation detail of CUE4Parse-Conversion (no IcarusStarlink project references it
  directly), which keeps it on this license's Apache-2.0-equivalent "Transitive Package
  Dependency" track rather than requiring a commercial license. Full terms:
  <https://github.com/SixLabors/ImageSharp/blob/main/LICENSE>

---

## CUE4Parse's own third-party NOTICE (reproduced per Apache-2.0 §4(d))

CUE4Parse bundles material from further third-party libraries; several of those (K4os.\*,
Newtonsoft.Json, Serilog, ZstdSharp.Port, Blake3, BouncyCastle.Cryptography, FixedMathSharp,
GenericReader, OffiUtils, Oodle.NET, Zlib-ng.NET, Fmod5Sharp, VGAudio, SubstreamSharp,
AssetRipper.TextureDecoder) are already listed above as this app's own direct/transitive NuGet
dependencies too. The rest are reproduced here verbatim from CUE4Parse's own upstream `NOTICE`
file (<https://github.com/FabianFG/CUE4Parse/blob/master/NOTICE>) since they're compiled into
CUE4Parse.dll itself rather than appearing as separate NuGet packages in this app's own
dependency tree:

- **UAssetAPI** — Copyright (c) 2023 Atenfyr. MIT License.
- **DotNetZip** — Copyright (c) 2006–2011 Dino Chiesa. MS-PL License.
- **System.Memory**, **System.Runtime.CompilerServices.Unsafe** — Copyright (c) .NET Foundation and Contributors. MIT License.
- **CriWareLibrary** — Copyright (c) 2025 Jacob Tarun. MIT License.
- **vgmstream** (portions) — Copyright (c) 2008–2025 Adam Gashlin, Fastelbja, Ronny Elfert, bnnm, Christopher Snowhill, NicknineTheEagle, bxaimc, Thealexbarney, CyberBotX, et al. Original ISC-style license.
- **crunch** — Copyright (c) 2010–2016 Richard Geldreich, Jr. and Binomial LLC. ZLIB license.
- **SwiftShader** (ETC decoder, ported/modified to C#) — Copyright 2016 The SwiftShader Authors. Apache License 2.0.
- **FFVII-Rebirth-Mesh-Patcher** — Copyright (c) 2026 nikolaybutnik. MIT License.

---

*This file is regenerated by re-deriving the package list from `project.assets.json` and the
license metadata each package's own `.nuspec`/upstream repo carries — if you add or remove a
`PackageReference` anywhere under `src/`, regenerate this list before the next release rather
than hand-editing it out of sync.*
