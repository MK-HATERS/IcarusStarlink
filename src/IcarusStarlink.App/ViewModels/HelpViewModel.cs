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
            - Go to **Library** and import a mod (EXMODZ, a folder, or a .pak), or browse its **IMM Database** tab for one.
            """),

        new("Library", """
            # Library

            Your local collection of extracted mods — the source of truth is the **Extracted_Mods** folder itself, not any database inside the app.

            - **Import ▾** brings a mod in three ways: **Import archive…** (a `.zip`/`.rar`/`.7z`/`.EXMODZ` — it looks at what's actually inside and recognizes an EXMOD-shaped mod, a bare `.pak`, or a UE4SS mod automatically, so you don't need to know which kind it is ahead of time), **Import folder…**, or **Import .pak…** (a prebuilt pak with no `.EXMOD` has no name/author of its own and can't be browsed or edited — it's an opaque entry, and importing one offers to link it to a Nexus ID right away). A prebuilt pak converts into a real, editable EXMOD automatically whenever that's possible (UnrealPak.exe set up and game data extracted) — only when it isn't yet does it stay opaque.
            - **New mod…** creates a blank EXMOD (just a name and author) and opens it straight in the editor.
            - **Check ▾** holds three whole-library checks: **Check for updates** (every Nexus/Database-sourced mod against its real current version), **Check mods against game data** (see "Making your own mods" below), and **Convert opaque mods…** (retries converting every still-opaque `.pak` right now — useful right after setting up UnrealPak.exe, instead of waiting for the automatic retry that already runs quietly after every "Update data folder"). A converted mod's own original `.pak` is kept behind the scenes so a later game update can re-derive a fresher diff — once you've hand-edited a converted mod yourself, it's never silently touched again.
            - Select one or more mods (Ctrl/Shift-click for more than one) and right-click → **Add to merge queue** — the same action shows as a toolbar button once you've selected 2 or more.
            - Right-click a mod for more: **Rename…**, **Create/Restore mod backup**, **See what changed vs previous version…**, **Find in Database**/**Search Nexus for this**, **Open on Nexus**, **Link to Nexus ID…**, **Delete**.
            - The blue **Database**/**Nexus** pill and the orange **Update** pill are clickable: Database jumps to that mod's row on the IMM Database tab, Nexus jumps to its card on the native Nexus page, and Update runs the actual update (or opens the right place to get one).
            - Right-click the column header bar to show or hide **Author/Version/Source/Status/Latest/Imported** — pick whichever columns you actually want to see.
            - Pin/Favorite/Notes are yours to organize with — none of them affect merging.
            - The ✎ glyph means a mod was edited in the EXMOD editor; 📦 means it's a rebuilt merged pak re-imported as its own entry; a warning badge means "Check mods against game data" flagged something — click it to open the mod with the flagged item pre-selected.
            - Several versions of the same mod (different bag sizes, presets, etc.) show up grouped as one family if the EXMOD declares `variantGroup` — expand the group and add whichever variant you want to the queue.
            - The **UE4SS mods** tab is a separate list, covered on its own page below.
            """),

        new("Merge & Install", """
            # Merge & Install

            The core workflow: build one merged pak from everything in your queue, then install it into the game.

            - In **Library**, select one or more mods (Ctrl/Shift-click for more than one) and right-click → **Add to merge queue**. Order matters — later entries win when two mods change the same thing, unless you override that (see Merge conflicts below).
            - **Install** rebuilds and installs in one click — it always asks for confirmation first (this is the one action in the whole app that writes into your real `Content\Paks\mods`), and always backs up whatever pak is already there (last 5 kept). The button relabels to **Update install** once something of this app's own is already installed.
            - **Profiles** (the bar at the top) save a whole queue + merge options as one named preset. **Save** always backs up whatever was there before overwriting it — **Restore backup** brings that back if a save went wrong (a bad edit, an accidental Clear). **Set baseline** marks the current queue as mods every profile should always include — set it once and those mods auto-add (lowest priority) to any queue you load or build from then on; click it again with an empty queue to clear it. **Import & migrate** (an expandable section) holds **Export/Import patch** (shares a profile with a friend or a server — catalog mods are referenced by name, anything else including anything you edited locally gets bundled into the file) and **Import IMM mod list…** (see "Coming from classic IMM").
            - **Merge options** (another expandable section, collapsed by default — its own header always shows a summary of what's currently on) apply gameplay-wide toggles as a final pass after your queue merges, and work even with an empty queue: Speed/Player/XP Boost, Craft Cost, Stacks/Slots multipliers, Faster Taming, Remove Weight, Unlimited Ammo, Disable Temperatures, Remove Level Cap. A small (i) icon next to each leveled option shows its real documented values.
            - If two or more queued mods (or a mod and an enabled Merge option) touch the same field, a red conflict banner appears with a count — click **Review conflicts** to see exactly what disagrees and optionally pick a specific mod's value per field (see Merge conflicts below).
            - **Advanced actions** (also expandable): **Verify** holds **Compare to installed** (previews exactly what Install would change) and **Compare paks…** (diffs any two `.pak` files — see "Comparing versions & paks"); **Uninstall** holds **Copy to game** (installs the already-staged pak without rebuilding — for when Icarus was running and Install failed) and **Remove from game** (uninstalls this app's own pak from the real game folder; your Library is untouched).
            """),

        new("Merge conflicts", """
            # Merge conflicts

            When two or more mods in your queue change the **same** field, the default is simple: whichever mod is later in the queue wins. Most of the time that's exactly what you want.

            If you want more control, click **Review conflicts**:

            - It shows every field two or more queued mods actually disagree on (fields they all happen to agree on aren't shown — there's nothing to pick between).
            - For each one, pick a specific mod's value from the dropdown, or leave it on **Default** to keep the usual last-mod-wins behavior.
            - Picks apply to your **next** Rebuild only, and reset automatically the moment you change the queue at all (add, remove, reorder, or clear) — a pick only means what it meant against the exact queue it was made from.
            """),

        new("Weekly Changes", """
            # Weekly Changes

            What actually changed in the game's own data the last time it updated — a real, row-by-row diff, not a changelog someone wrote by hand.

            Every time you click **Update data folder** (Settings), this app keeps a record of what it just extracted. Run it once before a game update and again after, and this page compares the two: which files changed, which items/fields were added, removed, or edited, old value beside new. Genuinely useful for figuring out why a mod that used to work suddenly doesn't (see "if a game update breaks your mod" under "Making your own mods").

            Nothing to click here beyond browsing — pick a file on the left to see its own changes on the right. If you see "No changes tracked yet," you only have one extraction on record; run **Update data folder** again after the next game update to get a real comparison.
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

        new("Finding & downloading mods", """
            # Finding & downloading mods

            Two more tabs on the **Library** page, alongside the **Mods** tab covered above:

            - **IMM Database** — a live, community-maintained catalog. Search, filter by author/category, sort any column, and see at a glance whether a mod is already extracted, outdated, or not downloaded yet.
            - **Mods**' own pending-download list also holds files you've downloaded from Nexus, waiting to install — this is where a real Nexus "Mod Manager Download" click lands (once registered in Settings), or where the **Nexus** page's own per-mod Download button lands a file either way. Activate a file to import it into your Library, or discard it.

            Tracking mods you're following, and browsing/searching Nexus itself, both live on the **Nexus** page now — see its own Help topic.
            """),

        new("Coming from classic IMM", """
            # Coming from classic IMM

            Already using Jimk72's Icarus Mod Manager? Bring the whole setup across in one click — you don't have to re-download anything or rebuild your mod list by hand.

            **The one-click migration:**

            - In **Settings**, on the **Migrate from IMM** tab, click **Import IMM mod list…** and pick classic IMM's own `LastMergedMods.txt` (in its install folder). If you pick the `IMM_Merged_Mod.txt` from your game's Paks\mods folder instead, you'll be asked to point at your IMM folder too, since the mods themselves aren't stored near it.
            - Every mod on that list is copied straight out of IMM's own folder into your Library — offline, instantly, and at exactly the versions you were already running.
            - Each mod is matched against the community database and, failing that, searched for on Nexus, so update checks work from day one. Mods that can't be identified still work fine; they just won't check for updates until you right-click → **Link to Nexus ID…**.
            - Your merge list is rebuilt in **Merge & Install** in the same order, so you can click **Install** immediately.
            - Anything that couldn't be found is listed by name rather than silently dropped — get those from **Library**'s own **IMM Database** tab, then run the migration again.

            Classic IMM's own files are only ever read. Your existing classic-IMM pak is backed up before this app replaces it.

            **Check the two tools agree (optional, but reassuring):**

            After rebuilding here, use **Compare paks…** (also on Merge & Install) with classic IMM's own installed `IMM_Merged_Mod_P.pak` as the second pak. Every game file both packs touch should come back with no differences — that's a direct, value-by-value confirmation that this app merged your list the same way classic IMM did.

            Nothing about importing a list touches classic IMM's own files — it only reads them.
            """),

        new("Comparing versions & paks", """
            # Comparing versions & paks

            Two different "what actually changed?" tools — a third, **Weekly Changes**, compares the game's own data over time instead of a mod or a pak (see its own Help topic).

            **See what a mod author changed in an update**

            Right-click any mod in **Library** → **See what changed vs previous version…**, or answer **Yes** when an update asks. You get a value-by-value list: which items and fields the author changed (old value beside new), what they added or removed, and which asset files (models, textures, icons) changed.

            This needs an earlier copy to compare against. You'll have one automatically after any update this app installs, or you can take one yourself at any time with right-click → **Create mod backup**. Comparing is read-only — nothing is restored or overwritten.

            **Compare two paks**

            **Compare paks…** on Merge & Install unpacks any two `.pak` files and diffs their contents: game data is compared value by value (formatting differences don't count), everything else byte for byte. Useful for confirming a rebuilt pack matches one from another tool, or for seeing what a prebuilt pak actually contains. Both paks are only read — extraction happens in a temp folder that's cleaned up afterwards.
            """),

        new("Nexus", """
            # Nexus

            A native list of Icarus mods on Nexus, driven by the API key you save once in **Settings** — there's no embedded web browser and no second sign-in. **All** shows the whole catalog, most-endorsed first (works even without a key — click **Load more** for further pages); **Trending / Latest / Updated** are Nexus's own curated lists (need a signed-in key); the search box switches to live results, covering every Icarus mod on Nexus.

            Because the list is ours rather than a web page, each mod shows what YOUR machine already knows about it: **In Library**, **Downloaded**, **Tracked**, and an **Update** badge — a precise comparison between the live Nexus version and your own Library copy's version, not a guess. Three toggles filter the list: **Hide ones you have**, **Updates only**, and **Tracked**.

            Per mod:

            - **Download** pulls the main file straight into Library's **Mods** tab. This uses Nexus's API directly, which requires a Nexus Premium account — non-Premium accounts should use **Open page**'s own Mod Manager Download button on the website instead.
            - **Track** adds the mod to your tracked list — click it again on an already-tracked mod (shown as **Tracked** with a check) to untrack it. There's no separate list to manage; the **Tracked** filter above shows exactly what you're tracking, right here.
            - **Open page** opens the mod on nexusmods.com in your normal browser — where you're already signed in, and where non-Premium downloads happen.

            When a tracked mod you already own gets a real update on Nexus, you'll see it in the **notifications** panel (the bell icon in the header) — no separate "check for updates" click needed, since this is computed every time the page loads.

            Clicking a real **Mod Manager Download** button on Nexus's own site can hand the file straight to this app instead of downloading it manually, but that needs a one-time setup step: in **Settings**, on the **Nexus** tab under **Download handler**, click **Register**. It asks for confirmation first (it's a real Windows registry change, and it replaces another mod manager if one's already registered for this). Files land in Library's **Mods** tab either way — you always Activate them from there.
            """),

        new("Saves", """
            # Saves

            Edits Icarus player data — the files under `%LocalAppData%\Icarus\Saved\PlayerData`. Player slots are found automatically, with your Steam name read from Steam's own local files (nothing goes online).

            **Safety first, always:**

            - Every **Save** and every **Restore** takes a full backup of the whole slot automatically before touching anything. Backups are zips in this app's own `Cache\save_backups` — never inside the game's folders.
            - **Restore** additionally writes a `pre_restore` zip of the slot as it currently is, so even restoring the wrong backup is undoable.
            - Saving and restoring are **refused while Icarus is running** — the game holds these files and rewrites them on exit, so an edit made mid-session would be lost.
            - A manual **Backup now** before experimenting is still the smart move.

            **What's editable today:** character name and XP (raw XP is the honest control — the game derives level from it); profile currencies (Ren, Exotics, Red, Stabilized, Biomass, Uranium, Licences, Respec points); character talents and account-wide Workshop research, each searchable with a rank +/− and a "Show unlearned" toggle; character/account/binary unlock flags, as searchable chips; account-wide Bestiary encounter points (with a Set max per creature); account-wide Accolade completion; the account-wide Items bank (view real item names and remove one — full item creation/stat editing isn't offered, since a malformed item is easy to create by hand and hard to notice); raw Cosmetic hash values per character (there's no known way to map these to option names, so it's a raw editor, not a picker — still useful for copying an exact look between your own characters); and your tamed **Mounts** — name, level, and species (species is a picker over the game's own real options, so you can't land on an invalid one). Everything the editor doesn't understand — item internals (durability, alterations, upgrades), Loadouts (equipped gear sets), raw Bestiary/Accolade progress counters, a mount's own real stats/stomach/saddle state, timestamps — is preserved exactly as-is when saving. The Overview shows character cards and an account snapshot; click a card to edit it. Duplicating/removing a character slot and extracting the game's own icons aren't built yet.
            """),

        new("Server (FTP)", """
            # Server (FTP)

            File management for a dedicated Icarus server over FTP — not a full remote-control agent, just uploading/downloading files to a server's own mods folder.

            - **Site Manager** saves host/port/username/remote path per site; the password goes into Windows Credential Manager, never a plain file. Reconnecting to a saved site needs nothing further from you.
            - Once connected: browse folders, **Upload…**/**Download…** files, **Delete** (with confirmation — a remote delete can't be undone from here).
            """),

        new("Settings & Diagnostics", """
            # Settings & Diagnostics

            Settings is split into tabs. Beyond **Game & tools** (covered in Getting started) and **Migrate from IMM**:

            - **Nexus** — sign in with your own Personal API Key (a plain account-wide token from Nexus's own Account Settings page — not tied to this or any other app), and optionally register the nxm:// download handler.
            - **UE4SS** — install/update the loader itself, separate from the per-mod enable/disable on the Library page. **Uninstall** removes it completely — your own installed mods are preserved and moved aside first, only the framework's own bundled files are removed.
            - **Maintenance** — **Check for updates** looks at this app's own GitHub releases; **Update now** downloads and installs the new version automatically (it closes the app, applies the update, and relaunches itself — nothing of yours in `Extracted_Mods`/`Profiles`/`Cache`/your settings is ever touched), or **Open release page** if you'd rather grab it yourself. Also **Performance tracking** (off by default; logs how long Rebuild/Install/catalog-refresh actually take to a separate `app.perf` log file) and **Export diagnostics zip…**, which bundles your logs plus a sanitized copy of your settings (nothing sensitive — real keys and passwords live in Windows Credential Manager, never in a settings file) into one zip to attach to a bug report.
            - **Appearance** — three built-in themes (Icarus/Dark/Light) plus a fully custom fourth one: edit any of ~19 colors by hex value, **Copy Icarus/Dark/Light** to start from a built-in theme's own colors instead of from scratch, and **Open skin file**/**Reload skin file** if you'd rather hand-edit the JSON file directly and pull your changes back in.
            """),

        new("Making your own mods", """
            # Making your own mods

            Covers field-value-only mods — editing values the game already defines (stats, costs, sizes, and the like). Adding new custom assets or blueprints is its own, separate skill this app doesn't teach.

            **Start to finish:**

            - **New mod…** (Library's toolbar) creates a blank EXMOD from just a name and author, and opens it straight in the editor.
            - **Add item from game data…** (in the editor) picks any real in-game item and copies its complete current values in as a starting point — safer than typing a field name by hand, since it guarantees the field actually exists right now. Check **Hide items already in this mod** to see only what you haven't touched yet — handy for confirming you've covered everything you meant to.
            - Edit whatever fields you want changed. Every field shows the real base game value underneath, amber-highlighted the moment your value differs from it — that's your at-a-glance confirmation you're actually changing something.
            - **Ctrl+S** (or the Save button) writes it to disk. The ✎ glyph appears next to the mod in Library from then on.
            - **⧉ Pop out** detaches whichever view is currently showing (Item fields / File JSON / Full EXMOD JSON) into its own window, so you can see two views side by side instead of switching back and forth — its scroll position stays in step with wherever else that same view is open.

            **Example: doubling a container's storage**

            A simple, real worked example. Say you want a storage container to hold twice as many slots:

            - **New mod…**, then **Add item from game data…** and pick the container (e.g. a wooden storage box).
            - Find its `StartingSlots` field — that's the item's real inventory slot count.
            - Change the value to double the number shown underneath in amber.
            - **Ctrl+S**. Queue it in Merge & Install and Install to try it in-game.

            The same pattern works for any numeric field you can find on an item — a weapon's damage, a recipe's resource cost, a building piece's stability. **Search game data…** (also in the editor) is the tool for the other half of "which field do I need?": type a value or a row name and see every file/item that names it, plus every other place in the game's data that references it — useful for tracing how something connects before you commit to editing it.

            **If a game update breaks your mod:**

            Click **Check mods against game data** (Library's toolbar) any time. It diffs every mod you own against whatever the game currently defines and does two things: anything it's confident about — an unambiguous rename with matching fields — it fixes automatically, backing the mod up first (undo any single fix with **Restore latest backup**). Anything less certain gets a warning badge instead, with its best guess, if it has one, right in the badge's own tooltip — click the badge to open that item in the editor and decide for yourself. **Weekly Changes** (its own page) is the other half of this: a real diff of what the game itself changed, which is often the fastest way to see why something broke.

            It won't ever guess silently beyond that. There's no reliable way to tell "this field was renamed" from "this field was removed" from "this just happens to look similar" with full confidence, so anything it isn't sure about stays exactly as honest as it looks: flagged, not fixed.
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
            - The **UE4SS loader install/update/uninstall**, the **nxm:// protocol registration**, and **Update now** (this app updating itself) all work the same way — each touches something more sensitive than a normal mod folder, so each asks first too.
            - Every Rebuild independently re-reads the pak it just built and confirms every file it staged for packing is actually retrievable from it — not just trusting how many files it handed to UnrealPak.exe. If UnrealPak itself ever silently dropped something, this is what would catch it, and it shows up as a warning under Build & install rather than a quietly incomplete pak.
            - Profiles back themselves up on every Save, the same as mods — **Restore backup** in Merge & Install undoes a bad one.
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
