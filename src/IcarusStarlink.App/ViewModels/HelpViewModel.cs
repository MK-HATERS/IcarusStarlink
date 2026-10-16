using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class HelpViewModel : ObservableObject
{
    public string Title => "Help";

    public IReadOnlyList<HelpTopic> Topics { get; } =
    [
        new("Getting started", """
            # Getting started

            First launch does the setup for you: it finds your Icarus install through Steam (or asks you to browse to it), installs UnrealPak, and offers to extract the game's data — then lands on Settings so you can see what it did. If you skipped a step, or ever need to change one, here's the same setup by hand, in order:

            - Open **Settings** and set your Icarus **Content folder** (…\Icarus\Icarus\Content). Click **Auto-detect** first — it reads your real Steam library and usually finds it without you browsing manually.
            - Set **UnrealPak.exe** — on first launch the app offers to install the bundled copy next to itself (one click), or Browse… to one you already have (classic IMM ships one under its own UnrealPak folder). The bundle is UnrealPak **4.27**, the same UE4 version Icarus is built on — deliberately not "the latest": newer UE5 builds write pak formats Icarus's engine can't load. **Check** in Settings verifies your copy actually runs; **Reinstall bundled copy** repairs a broken one.
            - Click **Update data folder**. This extracts the game's current mod-relevant data so merge/edit has something real to work against — re-run it whenever the game updates.
            - Go to **Library** and import a mod (EXMODZ, a folder, or a .pak), or browse its **IMM Database** tab for one.
            """),

        new("Library", """
            # Library

            Your local collection of mods: EXMOD mods, pak mods and UE4SS (Lua) mods, all in one list. The source of truth is the **Extracted_Mods** folder itself, not a database inside the app.

            - The **ON** tick is how a mod goes into the game: ticked pak/EXMOD mods go in on the next **Merge & Install**, ticked UE4SS mods on **Apply UE4SS changes**. Select several rows (Ctrl/Shift-click) and tick one to tick them all.
            - The **PENDING** bar at the bottom says what you've changed that isn't in the game yet, with **Apply UE4SS changes** and **Merge & Install** right there.
            - The **Type** filter shows only EXMOD, Pak or UE4SS mods; the search box finds a mod by name.
            - **Import ▾**: **Import archive…** (a `.zip`/`.rar`/`.7z`/`.EXMODZ`; it looks inside and works out whether it's an EXMOD mod, a bare `.pak`, a UE4SS mod or a mix), **Import folder…**, **Import .pak…**, **Import UE4SS mod…** (lands unticked: tick it, then Apply). A prebuilt pak is converted into a real, editable EXMOD whenever UnrealPak.exe is set up and the game data is extracted.
            - If a download holds several alternatives of one mod (different sizes or presets), you're asked which one to install, since the author meant you to pick one. If Starlink can't work out how to install something, the **Install manually** window opens (see "When an install goes wrong").
            - Importing also checks how the mod was packed and warns in the Activity panel (the bell) about: leftover build files (a `.git` folder, editor session files), files outside the game's Content folder, data tables older than your game (missing rows the game has since added), and tables shipped without a single change.
            - **New mod…** creates a mod from a starting point (blank, Quality of life, a new item, or a classic IMM template; see "Making your own mods") and opens it in the editor.
            - **Check ▾**: **Check for updates**, **Update all**, **Check mods against game data** (see "Making your own mods"), **Check endorsements**, **Convert opaque mods…**.
            - Right-click a mod: **Rename…**, **Convert to EXMOD…**, **Create mod backup** / **Restore latest backup**, **See what changed vs previous version…**, **Find in Database**, **Search Nexus for this**, **Open on Nexus**, **Link to Nexus ID…**, **Endorse** / **Abstain**, **Delete**. Right-click a UE4SS row for **Open folder**, **Set minimum UE4SS version…** and the Nexus link items.
            - Double-click a mod for its details. The **Changes** tab starts with **IN SHORT**: a few lines like "D_Itemable · MaxStack ×10 on 1337 rows", worked out against your current game data, before the full list.
            - Pin/Favorite/Notes are yours to organize with; none of them affect merging. ✎ means you edited it in the editor; a warning badge means "Check mods against game data" flagged something (click it to open the flagged row).
            - The **IMM Database** tab is the community catalog: search, filter, and **Download & extract**.
            """),

        new("When an install goes wrong", """
            # When an install goes wrong

            Most downloads install by themselves. When one doesn't, Starlink says why and lets you do it by hand, instead of guessing.

            - **Install manually** opens only when an automatic install fails. The left side shows what's in the download; the right side, **Will be installed**, shows Starlink's best guesses. Leave out anything with ✕, or select a file or folder on the left and add it as a **Pak mod**, **EXMOD mod** or **UE4SS mod**. Rename a piece if you like, then **Install**. **Cancel** changes nothing.
            - **Choose which one to install** appears when a download holds several alternatives of the same mod. Pick one; installing a different one later replaces it.
            - Packing warnings (in the Activity panel after an import) don't stop the install. They tell you the mod may be outdated or carelessly packed, which is worth knowing when it misbehaves. A table "from an older game version" is the most important one: it replaces the whole table, so items the game added since are missing until the author updates it.
            """),

        new("Merge & Install", """
            # Merge & Install

            Builds one merged pak from every ticked mod, then installs it into the game.

            - Tick mods' **ON** box in **Library** (or right-click → **Add to merge queue**). Order matters: later mods win when two change the same thing, unless you pick otherwise in **Review conflicts**. **Suggest order** proposes one; **Validate mods** checks the queue against your game data and each other before you build (a renamed field, a recipe pointing at a missing item, two mods overwriting the same file).
            - **Install** rebuilds and installs in one click. It always asks first (it's what writes into your real `Content\Paks\mods`) and backs up the pak already there (last 5 kept). It reads **Update install** once something of this app's is installed.
            - **Profiles** save a whole queue plus merge options under a name: **Save profile**, **Restore profile backup** (every save backs up the one before), **Set baseline** (mods every profile always includes). **Import & migrate** holds **Export patch…** / **Import patch…**, to share a setup with a friend or a server.
            - **Merge options** apply game-wide changes after your mods, even with nothing queued:
            - **Player boosts**: Speed Boost, Player Boost, XP Boost (L1 +200%, L2 +500%, L3 +1000%).
            - **Crafting & economy**: Craft Cost (fewer ingredients; Free = creative mode), **Stacks multiplier** (higher = bigger stacks), **Slots multiplier** (higher = more backpack and container slots), **Speed Crafting** and **Faster Taming** (0% = unchanged, higher = faster, 50% = half the time).
            - **Other**: **Lighter Items** (0% = normal weight, higher = lighter, 50% = half, 100% = weightless), Unlimited Ammo, Disable Temperatures, Remove Level Cap.
            - A red conflict banner means two mods (or a mod and an option) change the same field; **Review conflicts** shows them.
            - **Advanced actions**: **Verify** (**Compare to installed**, **Compare paks…**) and **Uninstall** (**Copy to game** installs the built pak without rebuilding; **Remove from game** removes it; your Library is untouched).
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

            UE4SS is Icarus's Lua scripting framework, separate from pak/EXMOD mods and from Merge & Install.

            - UE4SS mods are rows in **Library**'s list (Type: UE4SS). Tick or untick their **ON** box, then **Apply UE4SS changes** in the pending bar: ticked mods move into the game's Mods folder, unticked ones move back out (backed up first). Nothing changes until you Apply.
            - A download that is both a Lua mod and paks becomes one mod: its paks in the list as a mod, its Lua half as a linked UE4SS row. Deleting one offers to delete both.
            - **Import UE4SS mod…** (Import menu) adds one from a zip, unticked.
            - Starlink ships with UE4SS. If you tick a UE4SS mod but the loader isn't in the game, a **UE4SS MISSING** bar says so, with **Install UE4SS** right there.
            - **Settings › UE4SS** installs, updates or reinstalls the loader (the bundled copy, or GitHub's latest if newer), **Install from zip…** installs a UE4SS release you downloaded yourself, and **Uninstall** removes it. Each asks first and backs up, since it writes into `Binaries\Win64`; your mods are moved aside before an uninstall.
            """),

        new("Downloads", """
            # Downloads

            Everything downloaded through this app, from Nexus or the IMM Database, has its own page: **Downloads**.

            - A download installs the moment it finishes. One download is one mod: its paks go to Library, a Lua part to its UE4SS row.
            - Installed downloads are hidden; tick **Show installed** to see them. **Reinstall** puts back a mod you removed; **Discard** deletes the downloaded file (the installed mod stays).
            - **Needs** shows what the mod's own README says it requires, and **Get 'X'** fetches a missing one.
            - When you have one file of a Nexus mod installed and Nexus lists a newer version of that same file (Nexus keeps each file's update chain), downloading the mod fetches that newer file, not whichever file is listed first. The older copy is replaced, not piled up.
            - If a download can't be installed automatically, see "When an install goes wrong".
            """),

        new("Coming from classic IMM", """
            # Coming from classic IMM

            Already using Jimk72's Icarus Mod Manager? Bring the whole setup across in one click — you don't have to re-download anything or rebuild your mod list by hand.

            **The one-click migration:**

            - In **Settings**, on the **Migrate from IMM** tab, click **Migrate mods + mod list…** and pick classic IMM's own `LastMergedMods.txt` (in its install folder). If you pick the `IMM_Merged_Mod.txt` from your game's Paks\mods folder instead, you'll be asked to point at your IMM folder too, since the mods themselves aren't stored near it.
            - Every mod on that list is copied straight out of IMM's own folder into your Library — offline, instantly, and at exactly the versions you were already running.
            - Each mod is matched against the community database and, failing that, searched for on Nexus, so update checks work from day one. Mods that can't be identified still work fine; they just won't check for updates until you right-click → **Link to Nexus ID…**.
            - Your merge list is rebuilt in **Merge & Install** in the same order, so you can click **Install** immediately.
            - Anything that couldn't be found is listed by name rather than silently dropped — get those from Library's **IMM Database** tab, then run the migration again.

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

            - **Download** downloads the mod and installs it (it shows on the **Downloads** page). This uses Nexus's API directly, which requires a Nexus Premium account — non-Premium accounts should use **Open page**'s own Mod Manager Download button on the website instead.
            - **Track** adds the mod to your tracked list — click it again on an already-tracked mod (shown as **Tracked** with a check) to untrack it. There's no separate list to manage; the **Tracked** filter above shows exactly what you're tracking, right here.
            - **Open page** opens the mod on nexusmods.com in your normal browser — where you're already signed in, and where non-Premium downloads happen.

            When a tracked mod you already own gets a real update on Nexus, you'll see it in the **notifications** panel (the bell icon in the header) — no separate "check for updates" click needed, since this is computed every time the page loads.

            Clicking a real **Mod Manager Download** button on Nexus's own site can hand the file straight to this app instead of downloading it manually, but that needs a one-time setup step: in **Settings**, on the **Nexus** tab under **Download handler**, click **Register**. It asks for confirmation first (it's a real Windows registry change, and it replaces another mod manager if one's already registered for this). Files land on the **Downloads** page either way and install by themselves.
            """),

        new("Saves", """
            # Saves

            Edits Icarus player data — the files under `%LocalAppData%\Icarus\Saved\PlayerData`. Player slots are found automatically, with your Steam name read from Steam's own local files (nothing goes online).

            **Safety first, always:**

            - Every **Save** and every **Restore** takes a backup of the slot's player-data files automatically before touching anything. Backups are zips in this app's own `Cache\save_backups` — never inside the game's folders.
            - **Restore** additionally writes a `pre_restore` zip of the slot as it currently is, so even restoring the wrong backup is undoable.
            - Saving and restoring are **refused while Icarus is running** — the game holds these files and rewrites them on exit, so an edit made mid-session would be lost.
            - A manual **Backup now** before experimenting is still the smart move.

            **What's editable today:** character name and XP (raw XP is the honest control — the game derives level from it); profile currencies (Ren, Exotics, Red, Stabilized, Biomass, Uranium, Licences, Respec points); character talents and account-wide Workshop research, each searchable with a rank +/− and a "Show unlearned" toggle; character/account/binary unlock flags, as searchable chips; account-wide Bestiary encounter points (with a Set max per creature); account-wide Accolade completion; the account-wide Items bank (view real item names and remove one — full item creation/stat editing isn't offered, since a malformed item is easy to create by hand and hard to notice); raw Cosmetic hash values per character (there's no known way to map these to option names, so it's a raw editor, not a picker — still useful for copying an exact look between your own characters); and your tamed **Mounts** — name, level, and species (species is a picker over the game's own real options, so you can't land on an invalid one). Everything the editor doesn't understand — item internals (durability, alterations, upgrades), Loadouts (equipped gear sets), raw Bestiary/Accolade progress counters, a mount's own real stats/stomach/saddle state, timestamps — is preserved exactly as-is when saving. The Overview shows character cards and an account snapshot; click a card to edit it. **Duplicate** and **Delete** character are on each character card (Delete is real and goes through the same backup-first flow). Extracting the game's own icons isn't built yet.
            """),

        new("Server", """
            # Server

            **Server (Beta)** has two tabs.

            **Local server**: run an Icarus dedicated server on this PC.

            - **Install new server…** downloads one with SteamCMD (**Install SteamCMD**, or **Use my SteamCMD…**); **Add existing server…** adopts one you already have. Each server is its own profile.
            - **Start**, **Stop**, **Restart**, **Check for update**, **Update**.
            - **Overview**: players, world, uptime, memory, last backup; automation (restart if it crashes, a daily restart, update on restart, start with Starlink) and Windows firewall rules.
            - **Log** (follow, hide noise, events), **Settings** (name, password, ports, world, lobby, Steam branch; **Save settings**).
            - **Mods**: **Install my merged pak** puts your Merge & Install pak on the server; **Install UE4SS from my game** and tick which UE4SS mods to copy over.
            - **Worlds & backups** (**Back up now**, **Restore selected**) and **Admins & bans** (Steam64 ids, one per line).

            **Remote (FTP)**: a server hosted elsewhere. Save sites (the password is kept in Windows Credential Manager), **Connect**, browse, upload and download, and the **Icarus server sync** buttons: **Install merged pak**, **Sync UE4SS loader**, **Sync UE4SS mods**.
            """),

        new("Settings & Diagnostics", """
            # Settings & Diagnostics

            Settings is split into tabs. Beyond **Game & tools** (covered in Getting started) and **Migrate from IMM**:

            - **Nexus** — sign in with your own Personal API Key (a plain account-wide token from Nexus's own Account Settings page — not tied to this or any other app), and optionally register the nxm:// download handler.
            - **UE4SS** — **Install / Update to / Reinstall** the loader (the copy that ships with Starlink, or GitHub's latest stable if newer), **Install from zip…** for a UE4SS release you downloaded yourself, and **Uninstall** (your own mods are moved aside first; only the framework's files are removed).
            - **Maintenance** — **Check for updates** looks at this app's own GitHub releases; **Update now** downloads and installs the new version automatically (it closes the app, applies the update, and relaunches itself — nothing of yours in `Extracted_Mods`/`Profiles`/`Cache`/your settings is ever touched), or **Open release page** if you'd rather grab it yourself. Also **Performance tracking** (off by default; logs how long Rebuild/Install/catalog-refresh actually take to a separate `app.perf` log file) and **Export diagnostics zip…**, which bundles your logs — including any crash reports and your app/OS version info — plus a sanitized copy of your settings (nothing sensitive — real keys and passwords live in Windows Credential Manager, never in a settings file) into one zip to attach to a bug report.
            - **Appearance** — three built-in themes (Icarus/Dark/Light) plus a fully custom fourth one: edit any of ~19 colors by hex value, **Copy Icarus/Dark/Light** to start from a built-in theme's own colors instead of from scratch, and **Open skin file**/**Reload skin file** if you'd rather hand-edit the JSON file directly and pull your changes back in.
            """),

        new("Making your own mods", """
            # Making your own mods

            **New mod…** (Library) asks for a name, an author and a starting point:

            - **Blank**: build it up by hand.
            - **Quality of life**: rules like "Stack sizes ×4" and "Item weight halved", built from your own game data (see "Scale rules" below).
            - **A new item, made from a game item**: **Envirosuit**, **Envirosuit module**, **Food or medicine with its own buff**, **Any item, bought in a workshop tab of your own**. These open the new-item wizard (below) already set up for that kind of item.
            - **Map icon**: a map and compass marker row. Nothing shows it by itself; your blueprint or Lua mod looks it up by its row name.
            - **Classic IMM templates**: craftable/deployable item, consumable, building piece, electric generator, water pump.

            **Changing the game's own items**

            - **Change a game row…** (in the editor) copies any row of the game's data into your mod with all its current values. Change just the fields you want; every field shows the game's value underneath, amber-bordered when yours differs.
            - **Ctrl+S** saves. Fields you left identical to the game are dropped from the file, so it only carries real changes.
            - **Search game data…** finds a row by name and every place the game refers to it (which recipes use Wood, which items grant a stat).
            - Select two or more rows to set one field on all of them at once. **⧉ Pop out** puts a view in its own window.

            **New items: New item from a game item…**

            The way new-item mods are built: start from the game item closest to what you want, and give the new item its own copies of only what you want to change.

            - **Start from**: pick the item. Its model, sounds and behaviour carry over.
            - **Name it**: the name players see; the item id (its row name in every table) follows it until you change it.
            - **What it gets its own copy of**: each table the item links to (name and icon, armour stats, what eating it does…). Ticked = its own copy, renamed and re-linked, safe to change. Unticked = it shares the game's row, and changing that row would change the original too. Food and medicine can also get **their own buff**.
            - **How players get it**: just the item; a **crafting recipe** at a bench you pick (starting from the original's ingredients, unlocked with the original's blueprint or known from the start); or an **orbital workshop purchase**, in an existing tab or a new tab of your mod's own, with research and replication costs in credits (research is paid once, replication per copy; lower is cheaper, 0 is free).
            - Everything lands in the editor as normal rows to fine-tune, and Undo takes it all back out.

            **Scale rules**

            The **Scale rules** view changes one field across a whole table, like every item's Weight ×0.5. **Multiply by**: below 1 lowers the value (0.5 = half), above 1 raises it (2 = double). **Set to**: every matching row gets exactly that number. Limit a rule to rows whose name contains a word (several words, comma separated, match any). **Apply rules** writes the rows from your current game data, and Save applies them too, so a rule mod catches up after a game update just by being saved again. The Quality of life template includes rules you can turn on, among them **Cave sickness chance set to 5%** (25% in the game).

            **Check references**

            A link to a row that doesn't exist (a typo, or a row a game update renamed) is ignored by the game at best and breaks the item at worst, with no message. **Check references** (also run on every Save) lists:

            - links to rows that exist neither in the game nor in your mod, e.g. a mesh name that's one word off;
            - item rows (names, stats) that the game doesn't have and nothing in your mod uses, usually an item a game update renamed or removed.

            Click a problem to jump to that row. Row names are compared the way the game does, ignoring capitals.

            **Good to know**

            - New meshes and icons are cooked Unreal assets, which this app doesn't make; a new item reuses the game's until you ship your own.
            - A table you copy whole replaces the game's, so rows the game adds later are missing; only change the rows you mean to.
            - Mods that change the same rows of the same table conflict; Merge & Install shows you where.

            **If a game update breaks your mod**

            **Check mods against game data** (Library › Check) compares every mod with what the game defines now. A clear-cut rename it fixes for you, backing the mod up first (**Restore latest backup** undoes it); anything less certain gets a warning badge with its best guess. Click the badge to open that row. **Weekly Changes** shows what the game itself changed.
            """),

        new("Keyboard shortcuts", """
            # Keyboard shortcuts

            Inside the EXMOD editor window:

            - **Ctrl+S** — save
            - **Ctrl+F** — focus the item filter box
            - **F3** — jump to the next item in the (possibly filtered) list
            - **Ctrl+D** — duplicate the selected item's fields under a new name
            - **Ctrl+Z** — undo the last action (adding rows, a mass edit, a wizard's new item, applying rules)

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
            - If the app ever hits an unexpected error — on the UI thread, during background startup, or inside an async action like Rebuild/Install/FTP/downloads/save-editing — it writes a crash report (the full exception, your app/OS version, and what you'd been doing right before it) to the **Logs** folder automatically, instead of the app previously either hard-crashing with nothing recorded or silently freezing with no trace at all.
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
