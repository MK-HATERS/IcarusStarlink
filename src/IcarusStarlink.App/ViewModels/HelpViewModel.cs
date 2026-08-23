using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class HelpViewModel : ObservableObject
{
    public string Title => "Help";

    public IReadOnlyList<HelpTopic> Topics { get; } =
    [
        new("Getting started", """
            # Getting started

            First-time setup, in order:

            - Open **Settings** and set your Icarus **Content folder** (…\Icarus\Icarus\Content). Click **Auto-detect** first — it reads your real Steam library and usually finds it without you browsing manually.
            - Set **UnrealPak.exe** — on first launch the app offers to install the bundled copy next to itself (one click), or Browse… to one you already have (classic IMM ships one under its own UnrealPak folder). The bundle is UnrealPak **4.27**, the same UE4 version Icarus is built on — deliberately not "the latest": newer UE5 builds write pak formats Icarus's engine can't load. **Check** in Settings verifies your copy actually runs; **Reinstall bundled copy** repairs a broken one.
            - Click **Update data folder**. This extracts the game's current mod-relevant data so merge/edit has something real to work against — re-run it whenever the game updates.
            - Go to **Library** and import a mod (EXMODZ, a folder, or a .pak), or browse **Downloads** for one.
            """),

        new("Library", """
            # Library

            Your local collection of extracted mods — the source of truth is the **Extracted_Mods** folder itself, not any database inside the app.

            - **Import EXMODZ… / Import folder… / Import .pak…** bring a mod in three different ways. A .pak import has no .EXMOD, so it can't be browsed or edited — it's an opaque entry.
            - **New mod…** creates a blank EXMOD (just a name and author) and opens it straight in the editor.
            - Pin/Favorite/Notes are yours to organize with — none of them affect merging.
            - The ✎ glyph means a mod was edited in the EXMOD editor; 📦 means it's a rebuilt merged pak re-imported as its own entry.
            - Several versions of the same mod (different bag sizes, presets, etc.) show up grouped as one family if the EXMOD declares `variantGroup` — expand the group and add whichever variant you want to the queue.
            - The **UE4SS mods** tab is a separate list, covered on its own page below.
            """),

        new("Merge & Install", """
            # Merge & Install

            The core workflow: build one merged pak from everything in your queue, then install it into the game.

            - Drag mods from your **Library** pane into the **queue** with **Add to queue**. Order matters — later entries win when two mods change the same thing, unless you override that (see Merge conflicts below).
            - **Rebuild** merges the queue against real base game data and packs a `.pak` — this only ever writes to this app's own staging folder, never your game.
            - **Install** is the one button in the whole app that writes into your real `Content\Paks\mods` — it always asks for confirmation first, and always backs up whatever pak is already there (last 5 kept).
            - **Compare to installed** previews exactly what Install would change before you click it.
            - **Profiles** (the bar at the top) save a whole queue + merge options as one named preset. **Export/Import patch** shares a profile with a friend or a server — mods already in the community catalog are referenced by name; anything else (including anything you edited locally) gets bundled into the file.
            - **Merge options** on the right apply gameplay-wide toggles (Speed Boost, Remove Weight, Unlimited Ammo, and others) as a final pass after your queue merges — they work even with an empty queue.
            """),

        new("Merge conflicts", """
            # Merge conflicts

            When two or more mods in your queue change the **same** field, the default is simple: whichever mod is later in the queue wins. Most of the time that's exactly what you want.

            If you want more control, click **Review conflicts**:

            - It shows every field two or more queued mods actually disagree on (fields they all happen to agree on aren't shown — there's nothing to pick between).
            - For each one, pick a specific mod's value from the dropdown, or leave it on **Default** to keep the usual last-mod-wins behavior.
            - Picks apply to your **next** Rebuild only, and reset automatically the moment you change the queue at all (add, remove, reorder, or clear) — a pick only means what it meant against the exact queue it was made from.
            """),

        new("UE4SS mods", """
            # UE4SS mods

            UE4SS is Icarus's separate Lua/scripting mod framework — a different system from the EXMOD/pak mods above, unrelated to Rebuild and Install.

            - The **UE4SS mods** tab (on the Library page) shows every mod folder currently in your game's real Mods folder, plus anything staged here but not currently enabled.
            - Check or uncheck mods, then click **Apply** — checked mods get moved into the game's Mods folder, unchecked ones get moved back out here (backed up first). Nothing happens until you click Apply.
            - **Import…** stages a UE4SS mod from a downloaded zip, disabled by default.
            - Settings has a separate **Install/Update UE4SS** action for the loader itself (the framework, not a mod) — that's a different, more sensitive action with its own confirmation, since it writes into `Binaries\Win64` rather than the Mods subfolder.
            - **Uninstall UE4SS** (Settings, next to Install) removes the loader completely. Your own mods are moved back to this app's staging first — the confirmation names them before anything happens — everything removed is backed up (last 5 kept), and Icarus's own files are never touched.
            """),

        new("Downloads", """
            # Downloads

            Three tabs for finding and fetching mods:

            - **IMM Database** — a live, community-maintained catalog. Search, filter by author/category, sort any column, and see at a glance whether a mod is already extracted, outdated, or not downloaded yet.
            - **Nexus Mods** — your own personal tracked list. Add a mod by pasting its Nexus URL; **Check for updates** needs a signed-in Nexus API key (Settings) and reports mods Nexus itself says were recently updated — a real but coarse signal, not an exact version diff.
            - **Pending Downloads** — where a real Nexus "Mod Manager Download" click lands (see the Nexus page for how that's wired up), or where a manually pasted `nxm://` link goes. Activate a file to import it into your Library, or discard it.
            """),

        new("Coming from classic IMM", """
            # Coming from classic IMM

            Already using Jimk72's Icarus Mod Manager? Bring the whole setup across in one click — you don't have to re-download anything or rebuild your mod list by hand.

            **The one-click migration:**

            - In **Settings**, under **Migrate from classic IMM**, click **Import IMM mod list…** and pick classic IMM's own `LastMergedMods.txt` (in its install folder). If you pick the `IMM_Merged_Mod.txt` from your game's Paks\mods folder instead, you'll be asked to point at your IMM folder too, since the mods themselves aren't stored near it.
            - Every mod on that list is copied straight out of IMM's own folder into your Library — offline, instantly, and at exactly the versions you were already running.
            - Each mod is matched against the community database and, failing that, searched for on Nexus, so update checks work from day one. Mods that can't be identified still work fine; they just won't check for updates until you right-click → **Link to Nexus ID…**.
            - Your merge list is rebuilt in **Merge & Install** in the same order, so you can click **Install** immediately.
            - Anything that couldn't be found is listed by name rather than silently dropped — get those from **Downloads**, then run the migration again.

            Classic IMM's own files are only ever read. Your existing classic-IMM pak is backed up before this app replaces it.

            **Check the two tools agree (optional, but reassuring):**

            After rebuilding here, use **Compare paks…** (also on Merge & Install) with classic IMM's own installed `IMM_Merged_Mod_P.pak` as the second pak. Every game file both packs touch should come back with no differences — that's a direct, value-by-value confirmation that this app merged your list the same way classic IMM did.

            Nothing about importing a list touches classic IMM's own files — it only reads them.
            """),

        new("Comparing versions & paks", """
            # Comparing versions & paks

            Two different "what actually changed?" tools.

            **See what a mod author changed in an update**

            Right-click any mod in **Library** → **See what changed vs previous version…**, or answer **Yes** when an update asks. You get a value-by-value list: which items and fields the author changed (old value beside new), what they added or removed, and which asset files (models, textures, icons) changed.

            This needs an earlier copy to compare against. You'll have one automatically after any update this app installs, or you can take one yourself at any time with right-click → **Create mod backup**. Comparing is read-only — nothing is restored or overwritten.

            **Compare two paks**

            **Compare paks…** on Merge & Install unpacks any two `.pak` files and diffs their contents: game data is compared value by value (formatting differences don't count), everything else byte for byte. Useful for confirming a rebuilt pack matches one from another tool, or for seeing what a prebuilt pak actually contains. Both paks are only read — extraction happens in a temp folder that's cleaned up afterwards.
            """),

        new("Nexus", """
            # Nexus

            A native list of Icarus mods on Nexus, driven by the API key you save once in **Settings** — there's no embedded web browser and no second sign-in. Search covers every Icarus mod on Nexus; the **Trending / Latest / Updated** pills are Nexus's own curated lists (clear the search box to use them).

            Because the list is ours rather than a web page, each mod shows what YOUR machine knows about it: **In Library**, **Downloaded**, **Tracked**, and an **Update** badge when Nexus has a newer version than your copy.

            Per mod:

            - **Download** pulls the main file straight into Downloads' **Pending Downloads**. This uses Nexus's API directly, which requires a Nexus Premium account.
            - **Track** adds it to the Nexus watchlist on the Downloads page, so it gets checked for updates.
            - **Open page** opens the mod on nexusmods.com in your normal browser — where you're already signed in, and where non-Premium downloads happen.

            Clicking a real **Mod Manager Download** button on Nexus's own site can hand the file straight to this app instead of downloading it manually, but that needs a one-time setup step: in **Settings → Nexus download handler**, click **Register**. It asks for confirmation first (it's a real Windows registry change, and it replaces another mod manager if one's already registered for this). Files land in **Pending Downloads** either way — you always Activate them from there.
            """),

        new("Server (FTP)", """
            # Server (FTP)

            File management for a dedicated Icarus server over FTP — not a full remote-control agent, just uploading/downloading files to a server's own mods folder.

            - **Site Manager** saves host/port/username/remote path per site; the password goes into Windows Credential Manager, never a plain file. Reconnecting to a saved site needs nothing further from you.
            - Once connected: browse folders, **Upload…**/**Download…** files, **Delete** (with confirmation — a remote delete can't be undone from here).
            """),

        new("Settings & Diagnostics", """
            # Settings & Diagnostics

            Beyond the Content folder / UnrealPak setup covered in Getting started:

            - **Nexus Mods** — sign in with your own Personal API Key (a plain account-wide token from Nexus's own Account Settings page — not tied to this or any other app).
            - **UE4SS** — install/update the loader itself, separate from the per-mod enable/disable on the Library page.
            - **Performance tracking** — off by default. Turn it on to log how long Rebuild/Install/catalog-refresh actually take, to a separate `app.perf` log file — useful if something feels slow and you want real numbers for a bug report.
            - **Export diagnostics zip…** bundles your logs plus a sanitized copy of your settings (nothing sensitive — real keys and passwords live in Windows Credential Manager, never in a settings file) into one zip to attach to a bug report.
            """),

        new("Keyboard shortcuts", """
            # Keyboard shortcuts

            Inside the EXMOD editor window:

            - **Ctrl+S** — save
            - **Ctrl+F** — focus the item filter box
            - **F3** — jump to the next item in the (possibly filtered) list
            - **Ctrl+D** — duplicate the selected item's fields under a new name

            Select two or more items (Ctrl/Shift-click) to reveal a mass-edit bar — set one field across every selected item at once.
            """),

        new("Safety & backups", """
            # Safety & backups

            A few things this app is deliberately careful about:

            - **Install** (the one action that writes into your real game folder) always asks for confirmation, and always backs up whatever pak is already there first (last 5 kept).
            - The **UE4SS loader install/update** and the **nxm:// protocol registration** work the same way — both touch something more sensitive than a normal mod folder, so both ask first too.
            - Your library is just the **Extracted_Mods** folder plus a small per-mod notes/pin sidecar — nothing about it is locked into a database only this app can read. If you ever stop using this app, your mods are still just files.
            - If something goes wrong, **Export diagnostics zip…** (Settings) is the fastest way to get logs into a bug report.
            """),
    ];

    [ObservableProperty]
    private HelpTopic? _selectedTopic;

    public HelpViewModel()
    {
        _selectedTopic = Topics[0];
    }
}
