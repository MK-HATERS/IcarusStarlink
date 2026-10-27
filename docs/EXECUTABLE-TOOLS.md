# Executable tools and local builds

UnrealPak is executable code, and UE4SS loads executable code inside the game. Selecting a
file, seeing usage output, or compiling source does not prove it safe. Review the source and
suppliers you choose to trust. These changes address a trust gap, not evidence of malware.

## UnrealPak: supply your own UE 4.27 copy

Starlink no longer downloads the maintainer-hosted UnrealPak ZIP, extracts a legacy
`Tools/UnrealPak.zip`, or silently adopts a tool beside the app. No authenticated Epic payload
is pinned here. A hash from the same distributor establishes matching bytes, not Epic provenance.
Existing configured paths remain configured; review them and replace them in Settings if their
origin is uncertain.

Obtain UE 4.27 directly from Epic through the Epic Games Launcher, if available to your account,
and use its `Engine/Binaries/Win64/UnrealPak.exe`; alternatively build from
[Epic's UnrealEngine source](https://github.com/EpicGames/UnrealEngine).
Access may require accepting Epic's terms, linking Epic and GitHub accounts, and accepting the
organization invitation. A 404 without that access does not establish that the source is absent.
See [Epic's source access instructions](https://dev.epicgames.com/documentation/en-us/unreal-engine/downloading-source-code-in-unreal-engine)
and [UE 4.27 project generation guidance](https://dev.epicgames.com/documentation/en-us/unreal-engine/project-files-for-ides?application_version=4.27).

For a source build on Windows:

1. Choose and review a specific UE **4.27** source revision from Epic; record the full commit SHA.
   Do not use a moving branch as your permanent pin or substitute a UE5 build.
2. Clone Epic's repository, check out that SHA, and follow the README at that revision for the
   matching Visual Studio C++ toolchain, Windows SDK and prerequisites. Run its `Setup.bat`
   and `GenerateProjectFiles.bat`. Setup downloads additional dependencies that you still trust.
3. Build the **UnrealPak** program target for **Win64 / Development** in the generated solution,
   or use `Engine\Build\BatchFiles\Build.bat UnrealPak Win64 Development` from that checkout.
   Consult the selected revision's instructions for toolchain requirements.
4. In Starlink, Settings → Game & tools → UnrealPak → Browse selects the resulting
   `Engine\Binaries\Win64\UnrealPak.exe`. Keep its sibling DLLs, configuration, plugins and
   expected Engine layout intact; copying only the EXE can break it. Save settings.
   **Check** executes it; it tests operation, not publisher identity. Refresh game data and
   build a test pak before relying on compatibility with your Icarus version.

No Epic binaries are included in this contribution. Observe Epic's license when using or sharing
engine tools. You do not need UE4SS for ordinary data/pak management.

## UE4SS: optional code execution

UE4SS is an unofficial framework. Its loader, Lua scripts and native C++ mods run with the
permissions of the game process (or server account). They can access what that account can
access. Starlink does not install the loader at startup or as a prerequisite for data/pak mods.
Every game install/reinstall/update, local ZIP install, and server loader copy asks for explicit
confirmation with an execution warning. Cancel leaves the loader alone.

You can leave UE4SS absent, keep an existing installation, or use **Install from zip** in
Settings to select a local/self-built distribution. Checking for updates does not replace it;
replacement requires an explicit install confirmation. Local ZIP names are not authenticated
build metadata: even an upstream-looking filename is recorded as a local source, not as an
upstream-verified build. After launch UE4SS may report a version/commit in its own log; that
self-report is not provenance verification. Backups preserve the replaced loader; mod folders
and customized settings are kept during updates.

The bundled ZIP is `UE4SS_v3.0.1-1181-g83330635.zip`, from upstream commit
`8333063595fa3c49ad307f3b0f732accbb760d25`. `BundledUe4ss` retains its hardcoded SHA-256 check
before use. That checks the pinned ZIP bytes, not safety. Self-built ZIPs differ and must not
be assigned that hash or described as verified upstream artifacts. Online UE4SS and manager
updates reject missing, malformed, unsupported or mismatching SHA-256 digests; GitHub asset
metadata verifies matching bytes, not source review or independent publisher authenticity.

To build your own UE4SS, start with [UE4SS upstream](https://github.com/UE4SS-RE/RE-UE4SS),
check out the full commit above (or another reviewed revision compatible with your mods), and
initialize its pinned submodules with `git submodule update --init --recursive`. Follow the
README/build instructions **at that revision** and the [upstream documentation](https://docs.ue4ss.com/).
Build prerequisites and some submodules can require Epic-linked access. Package your resulting
`dwmapi.dll` with `ue4ss/UE4SS.dll` and its runtime dependencies/configuration, or the supported
flat release layout, into a ZIP and select it locally. Native mods may require the exact UE4SS
build they were compiled against. A successful build alone does not establish compatibility.

## Build a reviewed manager revision

Use a source revision you have reviewed, record its full SHA, and review `LICENSE.md` before
building or redistributing modified copies. The license explicitly allows upstream pull requests;
it imposes restrictions on forks and redistribution.

```powershell
git clone https://github.com/MK-HATERS/IcarusStarlink.git
cd IcarusStarlink
git checkout <reviewed-full-commit-sha>
git rev-parse HEAD
dotnet --version
dotnet restore IcarusStarlink.slnx
dotnet build IcarusStarlink.slnx --no-restore
dotnet test IcarusStarlink.slnx --no-build
dotnet run --project src/IcarusStarlink.App
```

Use Windows and the SDK selected by `global.json` (10.0.303 at the time of this change).
Review package references, NuGet sources, submodules and third-party notices. Building still
trusts the compiler/SDK, package feeds and packages, native dependencies, build scripts,
embedded UE4SS ZIP, OS, and selected source. Building the manager does not rebuild its embedded
UE4SS binary. Leave that loader uninstalled or select your own ZIP. Disable automatic manager
update checks in Settings if you want to manage pinned updates manually; accepting a self-update
replaces your build with the downloaded release.

## Credential Manager and residual trust

The reviewed credential integration reads/writes named app generic credentials for Nexus and
FTP; it does not enumerate the credential vault. This is not evidence of credential theft.
Removing that integration would not isolate executable code running as the same Windows user.
A tool or mod can still use that user's accessible files, network and OS APIs. Use a separate
restricted Windows account or an appropriate isolated environment if account separation is needed.
