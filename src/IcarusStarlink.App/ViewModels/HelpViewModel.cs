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
            - Set **UnrealPak.exe** — on first launch the app offers to install the bundled copy next to itself (one click), or Browse… to one you already have (classic IMM ships one under its own UnrealPak folder). The bundle is UnrealPak **4.27**, the same UE4 version Icarus is built on — deliberately not "the latest": newer UE5 builds write pak formats Icarus's engine can't load. **Check** in Settings verifies your copy actually runs; **Reinstall UnrealPak** repairs a broken one.
            - Click **Refresh game data**. This extracts the game's current mod-relevant data so merge/edit has something real to work against — re-run it whenever the game updates.
            - Go to **Library** and import a mod (EXMODZ, a folder, or a .pak), or find one on the **Database** page (Nexus and the IMM Database).
            """),

        new("Library", """
            # Library

            Your local collection of mods in two lists: **Data mods** on the left (EXMOD and pak mods, which Merge & Install builds into the game) and **UE4SS mods** on the right (Lua scripts, which **Apply** turns on or off). The source of truth is the **Extracted_Mods** folder itself, not a database inside the app.

            - The **ON** tick is how a mod goes into the game: ticked data mods go in on the next **Merge & Install**, ticked UE4SS mods on **Apply**. Select several rows (Ctrl/Shift-click) and tick one to tick them all.
            - Each list has a **CHANGES WAITING** bar at the bottom when you've changed something the game doesn't have yet (mods to put in, mods to take out, mods changed since the last install), with **Merge & Install →** (opens that page) or **Apply** (does it) right there. A row's status says the same: **Needs Merge & Install**, **Changed**, **Will be removed**, **In game**.
            - The chips filter both lists: **On**, **Off**, **Changes waiting**, **Updates** (a newer version online), **Problems** (what the mod check found, a mod replacing a high-risk game file, or a UE4SS mod that can't run) and **Favourites**. The search box finds a mod by name.
            - **Import ▾**: **Import archive…** (a `.zip`/`.rar`/`.7z`/`.EXMODZ`; it looks inside and works out whether it's an EXMOD mod, a bare `.pak`, a UE4SS mod or a mix), **Import folder…** (an unpacked mod folder, recognised the same way: EXMOD, pak or UE4SS), **Import .pak…**, **Import UE4SS mod…** (lands unticked: tick it, then **Apply**). A prebuilt pak is converted into a real, editable EXMOD whenever UnrealPak.exe is set up and the game data is extracted.
            - If a download holds several alternatives of one mod (different sizes or presets), you're asked which one to install, since the author meant you to pick one. If Starlink can't work out how to install something, the **Install manually** window opens (see "When an install goes wrong").
            - Importing also checks how the mod was packed and warns in the Activity panel (the bell) about: leftover build files (a `.git` folder, editor session files), files outside the game's Content folder, data tables older than your game (missing rows the game has since added), and tables shipped without a single change.
            - **New mod…** creates a mod from a starting point (blank, Quality of life, a new item, or a classic IMM template; see "Making your own mods") and opens it in the mod editor.
            - **Check ▾**: **Check for updates**, **Update all**, **Check mods against game data** and **Fix sure problems in every mod** (see "Making your own mods"), **Check endorsements**, **What the last game update changed…** (see "After a game update"), **Make prebuilt paks editable…**. **Check mods against game data** only looks: fixes are made from a mod's warning badge or **Fix sure problems in every mod**. What the update, game-data and endorsement checks found is listed in the folding **Check results** section (**Clear** empties it).
            - Right-click a mod: **Rename…**, **Convert to EXMOD…**, **Create mod backup** / **Restore latest backup**, **Reinstall original** (imports the mod again from the download ISL kept, exactly as its author made it; fixes and edits since are dropped, after a backup), **See what changed vs previous version…**, **Find in Database**, **Search Nexus for this**, **Open on Nexus**, **Link to Nexus ID…**, **Endorse** / **Don't endorse (stop asking)**, **Delete**. Right-click a UE4SS row for **Open folder**, **Set minimum UE4SS version…** and the Nexus link items.
            - Double-click a mod for its details. **Notes** and the **Changes** tab's **In short** fold away. **In short** is a few lines like "D_Itemable · MaxStack ×10 on 1337 rows", worked out against your current game data, before the full list.
            - Pin/Favorite/Notes are yours to organize with; none of them affect merging. ✎ means you changed it in the mod editor; the wrench means ISL's mod check fixed it (those fixes are made again after game updates); a faint "Made for an older game: between Week 222 and 4 Sep 2026" line under a mod's name is read from the game rows it carries (some were removed from the game after then); a **High risk** or **Medium risk** pill means the mod replaces game files with its own copies (hover it to see which): High is something the whole game builds on, like the player character or the base every building piece uses, so after a game update try without that mod first if things break; Medium is one blueprint or screen. It also names any other mod replacing the same file with a different copy, since only one can win; a warning badge means the mod doesn't fully fit the current game (click it to see what and fix it). Right-click › **Check against the game & fix…** does the same for one mod.
            - A greyed-out button or menu item still shows its tooltip, which says why it's greyed out.
            """),

        new("Editor", """
            # Editor

            Change the game's own items: make the default Envirosuit lighter, give it more oxygen slots, make Bread's buff last longer, a recipe cheaper. New mod… (Library) is the other half: it only ever makes new items, never changes the game's.

            - **Pick an item** on the left by its in-game name (search: envirosuit, bread, axe…).
            - **Each card is one game row the item uses:** Name, icon, weight · Armour · Eating or using it · Its buff · Durability · Decay and spoiling · Tool damage · Recipe · Workshop price. Every number says what lower and higher do, and shows the game's value on the right; an orange border means yours differs. ↺ puts the game's value back. **Only changed values** hides the values still at the game's.
            - **Shared rows:** a card marked "shared by N items" is used by other items too (the basic Envirosuit's decay row is shared by 1,895 items). Choose **Give this item its own copy** (the default: only this item changes) or **Change it for all**.
            - **Stats:** armour, module and buff cards can **Add stat**: any stat the game knows, like cold resistance or max health.
            - Models, sounds and how an item is picked up or used are kept as the game's; changing those needs new art.

            **Saving**

            - **Save to:** **A new mod** (give it a name), or one of **your own mods**: ones made in Starlink. Mods from Nexus or the IMM Database aren't listed, since an update would replace your changes and they aren't yours.
            - Only the values you changed go into the mod. Putting a value back to the game's takes it out again.
            - After saving, **Turn on & go to Merge & Install** ticks the mod and takes you there (or tick it in **Library** yourself). Open it in the mod editor to see exactly what it holds.
            """),
        new("When an install goes wrong", """
            # When an install goes wrong

            Most downloads install by themselves. When one doesn't, Starlink says why and lets you do it by hand, instead of guessing.

            - **Install manually** opens only when an automatic install fails. The left side shows what's in the download; the right side, **Will be installed**, shows Starlink's best guesses. Leave out anything with ✕, or select a file or folder on the left and add it as a **Pak mod**, **EXMOD mod** or **UE4SS mod**. Rename a piece if you like, then **Install**. It's all or nothing: if one piece can't go in, the others are taken back out. **Cancel** changes nothing.
            - **Choose which one to install** appears when a download holds several alternatives of the same mod. Pick one; installing a different one later replaces it. Cancelling leaves the copy you already have exactly as it was.
            - Packing warnings (in the Activity panel after an import) don't stop the install. They tell you the mod may be outdated or carelessly packed, which is worth knowing when it misbehaves. A table "from an older game version" is the most important one: it replaces the whole table, so items the game added since are missing until the author updates it.
            """),

        new("Merge & Install", """
            # Merge & Install

            Builds one merged pak from every ticked mod, then installs it into the game.

            - Tick mods' **ON** box in **Library** (select several rows with Ctrl or Shift and tick one to tick them all). Order matters: later mods win when two change the same thing, unless you pick otherwise in **Check the merge**. **More ▾ › Suggest order** proposes one. **Check the merge** looks at the queue before you build, in three tabs: **Conflicts** (values two mods change differently), **Between mods** (two mods overwriting the same file; the talent, blueprint and workshop trees as the whole queue leaves them: two nodes on top of each other, nodes that wait for each other so none can be unlocked, a node needing one from another tree; and a mod that needs another mod you haven't queued) and **Per mod** (the same check as the Library's warning badge; **Fix…** opens that mod's fix window). **Copy report** copies everything it found, each with a short code. None of it stops the merge.
            - **ArcFission mods** (WirelessPower, LumberYard…) come with ArcFission Library, which writes its own data pak (ZZ_ArcFission_Data_N_P.pak) into the mods folder when the game starts. Merge & Install reads the rows those mods add straight from the mods themselves (their ArcFission\rows.lua), so they're in the merge before the game has ever run with them, and installs the merge as zz_zz_ISL-Merged_9999_P.pak, which loads after the library's pak. You don't need to start the game first, or restart it after the library says so, for the mods' items to be there. The library's newest pak is left where it is (it would only write it again); its older copies are removed at Install. If ArcFission Library is uninstalled or turned off, no ArcFission rows go into the merge, and Install offers to back up and remove its leftover pak. When an ArcFission mod is added, removed or updated, Library's CHANGES WAITING bar asks for Merge & Install again.
            - **Install** rebuilds and installs in one click. It always asks first (it's what writes into your real `Content\Paks\mods`) and backs up the pak already there (last 3 kept). It reads **Update install** once something of this app's is installed.
            - Your ticks, their order and the merge options save themselves into the profile in use ("Default" until you make others), and the last profile you used is picked again on the next start. If the game has mods that aren't ticked (Merge & Install would take them out), the CHANGES WAITING bar in Library offers **Tick the N mods in the game**, which ticks them again in the order they were merged.
            - **Profiles** save a whole queue plus merge options under a name: **Save profile**, and **Profile ▾** for **New profile…**, **Rename profile…**, **Restore profile backup** (every save backs up the one before) and **Delete profile**. **Share a mod list** holds **Export patch…** / **Import patch…**: a patch is the profile as it runs here (its data mods by their Nexus or IMM Database source, bundled when they have none; the UE4SS mods on in your game; their settings, unless you untick **Include UE4SS mod settings**). Importing one recreates the profile, offers the mods you lack as one download, writes the patch's settings into its UE4SS mods and offers to turn them on; a copy at another version is kept and listed in the warnings. A mod bundled in the patch replaces your copy of the same name only after backing it up (right-click › **Restore latest backup** brings yours back). A patch carries only plain settings files for UE4SS mods: scripts, and Lua settings that run code, are left out and named after the import. The Server page's Mods tab has **Export patch for players**, **Apply a patch to this server** and **Compare a patch** for a dedicated server.
            - **Find the broken mod** (**More ▾** here, or the Library's **Check ▾** and a mod's right-click menu):something broken in game, like you can't drink from a canteen, and you don't know which mod does it? Type what's broken (or pick the game row) and click **Find suspects**: mods that change it are listed with the reason, and tested first. If the health check already knows a problem in one, click **Fix…** first: that's often the answer. **Start** backs up your saves (characters and account, restorable from Saves, plus a zip of your worlds), then installs part of your mods. Start the game, check, close it, and say **Still broken**, **Works now** or **Can't tell**. Each round halves what's left, so 40 mods take about 7 rounds; a mod that needs another mod always goes in with it. At the end it names the mod (or the two that only break together): open its fix window, turn it off, or open its Nexus page. Then **Put my mods back** puts all your mods back in the game. Your queue isn't changed during a search, and if you close Starlink in the middle, it offers to put your mods back the next time it starts. Only data mods are tested; UE4SS (Lua) mods and merge options stay as they are. Test on a new world if you can: a world loaded while some mods are out can lose their items.
            - **Merge options** apply game-wide changes after your mods, even with nothing queued:
            - **Player boosts**: Speed Boost, Player Boost, XP Boost (L1 +200%, L2 +500%, L3 +1000%).
            - **Crafting & economy**: Craft Cost (fewer ingredients; Free = creative mode; trades with the vendors, like vestiges, crops, fish or Biomass for Ren and trophies and the food shop, always keep their normal price),**Stacks multiplier** (higher = bigger stacks), **Slots multiplier** (higher = more backpack and container slots), **Speed Crafting** and **Faster Taming** (0% = unchanged, higher = faster, 50% = half the time).
            - **Other**: **Lighter Items** (0% = normal weight, higher = lighter, 50% = half, 100% = weightless), Unlimited Ammo, Disable Temperatures, Remove Level Cap, and two marked **Beta** (built from the game's files, not tried in the game yet): **Free Item Repair** (repairing tools, weapons and armour costs no items; you still repair at the same bench, and turrets, enemy turrets and deployables still break as normal) and **No Weather** (no random storms, rain, snow or wind: every prospect uses the game's own clear-sky forecast; missions built around a storm, like the Glacier perma-storm expedition and the Ice Mammoth hunt and arena, and the tutorial rain keep theirs; a world you already started may keep the weather it had planned for a while).
            - The red bar above the queue counts what the check found ("3 conflicts · 1 clash between mods · 2 mods with problems"); **Review** opens **Check the merge**. A conflict means two mods (or a mod and an option) change the same value. The **Conflicts** tab shows them, grouped by which mods collide ("Homestead Expanded ↔ laanp-BuildTools · 14"). Pick the line, and the bottom shows the game's value, each mod's and the result side by side, lists lined up entry by entry (green: the game doesn't have it, red: left out). Choose a winner per line in its dropdown or with **Make it win** on a mod's column, or a whole group with **… wins all**. **Default** is the mod lower in the queue, except lists that can take every mod's additions.
            - **Save picks** keeps your winners with the profile, by mod name, so they still apply after you reorder the queue, add mods or restart; the next Merge & Install uses them.
            - What happened to the last install ("Installed…", "Install failed…") shows under the **Install** button.
            - **Advanced actions**: **Compare** (**Compare to installed**, **Compare paks…**) and **The merged pak in the game** (**Copy to game** installs the built pak without rebuilding; **Remove from game** removes it; **Restore previous install** puts back the pak that was there before; your Library is untouched).
            """),

        new("Merge conflicts", """
            # Merge conflicts

            When two or more mods in your queue change the **same** field, the default is simple: whichever mod is later in the queue wins. Most of the time that's exactly what you want.

            If you want more control, click **Check the merge** (or **Review** on the red bar) and open **Conflicts**:

            - It shows every field two or more queued mods actually disagree on (fields they all happen to agree on aren't shown — there's nothing to pick between).
            - For each one, pick a specific mod's value from the dropdown, or leave it on **Default** to keep the usual last-mod-wins behavior.
            - **Save picks** keeps them with the profile, and every later build uses them, also after the queue changes.
            """),

        new("After a game update", """
            # After a game update

            When Steam updates Icarus, the **PENDING** bar at the bottom of the **Library** says so ("Icarus updated (build …)") and a pop-up tells you once. Starlink reads the build number Steam keeps for the game, so it knows even if you haven't played yet.

            Click **Update everything…** on that bar. It does, in order:

            - **Saves your setup as it is now**, so you can roll back: what's ticked and in what order, merge options and conflict picks, the merged pak in the game, and which UE4SS mods are on. The newest 3 of these are kept (in Backups\LastKnownGood).
            - **Notes the game files mods replace**, so Starlink can tell what the update changed in them.
            - **Refreshes game data**, the same as the button in Settings.
            - **Checks every mod** against the new game and **makes only the proven fixes** (old copies of game data, links to rows the game no longer has). Each mod is backed up first.
            - **Checks for newer versions** of your mods on Nexus and the IMM Database.
            - **Builds the merge and installs it.** Not while Icarus is running: then it builds it and tells you to close the game and click **Copy to game** on Merge & Install.

            If a step doesn't work, it stops there and says why. The steps before it stay done.

            At the end, **"Icarus updated — here's what changed"** shows folding sections with a count on each: **High risk** and **Needs a look** open, **Fixed for you** folded.

            - **High risk**: mods that replace a game file the update changed. When the mod still has the game's code from before the update, the pill says **out of date** and the reason is named ("the game changed GetCurrentProspectLocation in this update; this mod still has its own older copy"). Starlink only says this with proof: it needs to have seen the game's files from before the update, so the first update after you start using Starlink can't show it yet. **Keep off until updated** unticks the mod and installs the merge without it. Tick it again once its author updates it.
            - **Needs a look**: things Starlink wasn't sure enough to change by itself, and mods with a newer version out (**Update**).
            - **Fixed for you**: each mod Starlink fixed, and how many proven fixes. **Open fix window** shows them.

            **Roll back to before the update** puts your mod setup back as it was: the merged pak, the ticks, order, options and picks, and which UE4SS mods are on. What's installed now is backed up first. The game itself can't be rolled back, only your mod setup, and fixes made to mods stay (right-click a mod › **Restore latest backup** undoes one).

            You can open the review again any time: **Check ▾ › What the last game update changed…** in the Library. **Dismiss** on the bar hides it until the next update, without changing anything.
            """),

        new("Weekly Changes", """
            # Weekly Changes

            What actually changed in the game's own data the last time it updated — a real, row-by-row diff, not a changelog someone wrote by hand.

            Every time you click **Refresh game data** (Settings), this app keeps a record of what it just extracted. Run it once before a game update and again after, and this page compares the two: which files changed, which items/fields were added, removed, or edited, old value beside new. Genuinely useful for figuring out why a mod that used to work suddenly doesn't (see "if a game update breaks your mod" under "Making your own mods").

            Pick a file on the left to see its changes on the right. **History** picks an earlier refresh (about a month is kept). **Refresh game data** reads the game again and compares. If you see "No changes tracked yet," you only have one extraction on record; run **Refresh game data** again after the next game update to get a real comparison.
            """),

        new("UE4SS mods", """
            # UE4SS mods

            UE4SS is Icarus's Lua scripting framework, separate from pak/EXMOD mods and from Merge & Install.

            - UE4SS mods are rows in **Library**'s right-hand list. Tick or untick their **ON** box, then **Apply** in its CHANGES WAITING bar: ticked mods move into the game's Mods folder, unticked ones move back out (backed up first). Nothing changes until you Apply. **Apply**, and deleting a mod that's in the game, need Icarus closed; if a file is still in use, nothing is moved and the file is named. Deleting any UE4SS mod keeps a copy in Starlink's Backups folder.
            - Ticked means UE4SS really starts the mod. UE4SS starts a mod only when `mods.txt` has a `1` for it or its folder has an `enabled.txt` (the `enabled.txt` wins even over a `0`). A mod whose folder is in the game but that UE4SS skips shows **Off in UE4SS** (hover it to see why); tick it and Apply, and Starlink adds its `enabled.txt`. A mod Starlink puts in the game gets one too when it needs it.
            - A download that is both a Lua mod and paks becomes one mod: its paks in the list as a mod, its Lua half as a linked UE4SS row. Deleting one offers to delete both.
            - **Edit config…** (right-click a UE4SS row) shows the mod's settings in groups, with plain names and the real name small beside each one. On/off settings are switches; numbers with a range show it; keys have a box for the key and Ctrl, Shift and Alt ticks. **Not the default** marks a setting the mod ships differently; **Reset to default** puts one back, **Reset all to default** puts them all back. **Server decides** means in multiplayer the host's or server's copy is used. Your edits stay when you switch file or copy, and closing asks before throwing them away. Nothing is written until you **Save**, and the old file is backed up first. If the mod (or another editor) changes the file while the window is open, Save writes only the settings you changed here into the file as it is now; in the text view Save refuses instead, so copy your text, click **Discard changes** and make the edit again. Empty lists such as `NoGuests = {}` can be filled in (keep the { } and put names in quotes). A mod update keeps your settings and adds the new version's new ones.
            - **Import UE4SS mod…** (Import menu) adds one from a zip, unticked.
            - Starlink ships with UE4SS. If you tick a UE4SS mod but the loader isn't in the game, a **UE4SS MISSING** bar says so, with **Install UE4SS** right there.
            - **Settings › UE4SS** installs, updates or reinstalls the loader (the bundled copy, or GitHub's latest if newer), **Install from zip…** installs a UE4SS release you downloaded yourself, and **Uninstall UE4SS** removes it. Each asks first and backs up, since it writes into `Binaries\Win64`; your mods are moved aside before an uninstall.
            """),

        new("Downloads", """
            # Downloads

            Everything downloaded through this app, from Nexus or the IMM Database, has its own page: **Downloads**.

            - A download installs the moment it finishes. One download is one mod: its paks go to Library, a Lua part to its UE4SS row.
            - Installed downloads are hidden; tick **Show installed** to see them. **Reinstall** puts back a mod you removed; **Discard** deletes the downloaded file (the installed mod stays).
            - **Needs** shows what the mod's own README says it requires, and **Get 'X'** fetches a missing one.
            - When you have one file of a Nexus mod installed and Nexus lists a newer version of that same file (Nexus keeps each file's update chain), the mod shows an update, and downloading it starts on that newer file in the file list (pick another file to switch variants). The older copy is replaced, not piled up.
            - A UE4SS mod in a subfolder of the zip (`Mods\MyMod\Scripts`) is put in the game under the mod's own folder name. A pak or EXMOD download that also carries a Lua folder becomes two linked rows.
            - A ".pak" that isn't a real pak (a web page or a broken download) is refused, with the reason.
            - If a download's file was deleted from Pending_Downloads, **Reinstall** says so: download it again, or **Discard** the row.
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
            - Anything that couldn't be found is listed by name rather than silently dropped — get those from the **Database** page's **IMM Database** tab, then run the migration again.

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

        new("Database", """
            # Database

            Everywhere to find mods, in two tabs. **Library** holds the mods you have; this page is for getting more.

            **IMM Database** is the community catalog: search, and **Download & extract**. The folding **Filters** section holds Author, Category, **Updates only**, **Downloaded only** / **Not downloaded only**, **Game week** and **Hide older weeks**; its header sums up what's set. The **Installed** and **Works with** columns show your copy's version and the game week the mod was made for. **Find in Database** (right-click a Library mod) opens this tab already searched. **Readme** only opens web links.

            **New mods.** Each tab says how many mods are new since your last visit, like "IMM Database (5 new)". New mods have a **New** tag; mods with a newer version than you last saw have an **Updated** tag. Tick **New only** to see just those. The tags clear when you close Starlink after looking at that tab, or straight away with **Mark all seen**. The very first time, everything counts as seen.

            **Nexus** is a native list of Icarus mods on Nexus, driven by the API key you save once in **Settings** — there's no embedded web browser and no second sign-in. **All** shows the whole catalog, most-endorsed first (works even without a key; the ‹ › buttons at the bottom go to the next or last page); **Trending / Latest / Updated** are Nexus's own curated lists (need a signed-in key); the search box switches to live results, covering every Icarus mod on Nexus.

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

            - Every **Save player data** and every **Restore** takes a backup of the slot's player-data files automatically before touching anything. Backups are zips in this app's own `Cache\save_backups` — never inside the game's folders.
            - **Restore** additionally writes a `pre_restore` zip of the slot as it currently is, so even restoring the wrong backup is undoable.
            - Saving and restoring are **refused while Icarus is running** — the game holds these files and rewrites them on exit, so an edit made mid-session would be lost.
            - A manual **Backup now** before experimenting is still the smart move.
            - Each backup says what it is: before a save, before a restore, or made with Backup now. Restore won't use a zip that has no Profile.json in it.
            - If a number box (XP, a currency, an appearance value, a bestiary count, a mount level) isn't a plain whole number, Save stops and names the box. Digits only, no commas.

            **What's editable today:** character name and XP (raw XP is the honest control — the game derives level from it); profile currencies (Ren, Exotics, Red, Stabilized, Biomass, Uranium, Licences, Respec points); character talents and account-wide Workshop research, each searchable with a rank +/− and a "Show unlearned" toggle; character/account/binary unlock flags, as searchable chips; account-wide Bestiary encounter points (with a Set max per creature); account-wide Accolade completion; the account-wide Items bank (view real item names and remove one — full item creation/stat editing isn't offered, since a malformed item is easy to create by hand and hard to notice); raw Cosmetic hash values per character (there's no known way to map these to option names, so it's a raw editor, not a picker — still useful for copying an exact look between your own characters); and your tamed **Mounts** — name, level, lineage, look, genetics and talents, or **Delete** one (the species is shown but can't be changed: its talents and stats live in the mount's own game data, so changing only the label would leave a mismatched mount). Everything the editor doesn't understand — item internals (durability, alterations, upgrades), Loadouts (equipped gear sets), raw Bestiary/Accolade progress counters, a mount's stomach and saddle state, timestamps — is preserved exactly as-is when saving. The Overview shows character cards and an account snapshot; click a card to edit it. **Duplicate** and **Delete** are under the character list on the Characters tab (Delete asks first, and the change is written by Save like every other edit, backup first). Items, mounts and creatures show the game's own icons.

            **In the game's order:** talents list the talent trees first, then blueprints by tier (Tier 1 Character crafting, Tier 2 Crafting Bench, Tier 3 Machining Bench, Tier 4 Fabricator, Tier 5 Manufacturer), then Solo. Workshop research, missions (by map) and the bestiary (by biome) follow the game's own order too. Click a heading to fold it. On the Bestiary tab, **Complete all** on a biome's heading fills every creature listed under it.

            **Worlds**: each saved world in the slot, with its custom world settings, the same switches the game shows when you create a world (Arachnophobia Mode, Mission Structure Cleanup, Mount Unclaim Requires Ownership, Spawn Great Hunt Creatures Post Campaign, and any the game adds later). Turn one on or off and **Save world settings**: the world is backed up first (to `Cache\save_backups\Worlds`), only its settings list changes, and saving is refused while Icarus runs. Settings the game only allows when a world is made, and the world's Difficulty and Hardcore, are shown but can't be changed. A number setting it can't use (letters, or past the game's range) goes back to the value that will be saved.
            """),

        new("Server", """
            # Server

            **Server** has two tabs.

            **Local server**: run an Icarus dedicated server on this PC.

            - **Install new server…** downloads one with SteamCMD (**Install SteamCMD**, or **Use my SteamCMD…**); **Add existing server…** adopts one you already have. Each server is its own profile.
            - **Start**, **Stop**, **Restart**, **Check for update**, **Update**.
            - Tiles show players, world, uptime, memory, build and last backup. **Overview**: **Automation** (restart if it crashes, a daily restart, update on restart, update automatically, a daily backup, start with Starlink, start minimized; its header lists what's on) and the folding **Windows firewall** section (its header says whether every port is open). Drag the gap next to the server list to widen it.
            - **Log** (follow, hide noise, filter; players online and events beside it, with a drag handle between). **Settings** in folding sections: **Server** (names, max players, passwords), **Branch & ports**, **World**, then **Lobby** and **Advanced** (bind IP, Saved folder suffix, log file name, the command line) folded; **Save settings** stays under the list.
            - **Create a new world on start**: pick the world from the game's own list (open worlds, outposts, missions), a difficulty, Hardcore, and a world name (an outpost needs one). Set it back to **Don't create one** after the first start, or every restart makes a fresh world. Without extracted game data it's a text box taking the server's own "[ProspectType] [Difficulty 1-4] [Hardcore True/False] [SaveName]".
            - **Mods**: **Install my merged pak** puts your Merge & Install pak on the server; **Install UE4SS from my game**, then **Copy to server** on each mod (or **Make the server match my game** for all). **ON** turns a mod on or off on the server only; **Match settings…** picks which copy's settings both use; **Remove** takes it off; **Refresh mods** reads it all again. The server must be stopped, and while it's starting, stopping or updating (by hand or by automation), mod changes, restores and list saves wait until it's done. ArcFission mods (WirelessPower…) need ArcFission Library and the mods on the server too, the same versions as in your game: their rows are already in the merged pak, and their scripts run on the server. The server's ArcFission Library writes its own data pak at each rebuild; **Install my merged pak** moves its older copies into the server's backups.
            - **Worlds & backups** (**Back up now**, **Restore selected**), **World settings** (each world's custom settings, like the Saves page's Worlds tab; the server must be stopped to save, and the world is backed up first) and **Admins & bans** (Steam64 ids, one per line). Bans made in game with BanPlayer are kept: if a list changed on disk since the page showed it, an untouched list takes the new file and an edited one asks before replacing it. If ServerSettings.ini can't be read, nothing is saved over it.

            **Remote (FTP)**: a server hosted elsewhere. Save sites (the password is kept in Windows Credential Manager; **Server folders** holds the Mods and Win64 paths, only needed when your host lays the server out differently), **Connect**, browse, upload and download, and the **Icarus server sync** buttons: **Install merged pak**, **Sync UE4SS loader**, **Sync UE4SS mods**. If an upload breaks off, the mods that went up whole are still turned on; delete the half-uploaded mod's folder on the host and sync again.
            """),

        new("Settings & Diagnostics", """
            # Settings & Diagnostics

            Settings is split into tabs. Beyond **Game & tools** (covered in Getting started) and **Migrate from IMM**:

            - **Nexus** — sign in with your own Personal API Key (a plain account-wide token from Nexus's own Account Settings page — not tied to this or any other app), and optionally register the nxm:// download handler (the folding **Download handler** section; its header says whether it's set up).
            - **UE4SS** — **Install / Update to / Reinstall** the loader (the dev build that ships with Starlink, or a newer one **Check for update** found on GitHub; GitHub is only asked when you press it), **Install from zip…** for a UE4SS release you downloaded yourself, and **Uninstall UE4SS** (your own mods are moved aside first; only the framework's files are removed).
            - **UE4SS config** — edit UE4SS-settings.ini for your game or one of your servers, one folding section per part of the file, each setting with a plain name and its real name small beside it. **Save** changes only the settings you edited (backed up first), and **Also save these changes to** copies them to your other targets.
            - **Maintenance** — **Check for updates** looks at this app's own GitHub releases; **Update now** downloads and installs the new version automatically (it closes the app, applies the update, and relaunches itself — nothing of yours in `Extracted_Mods`/`Profiles`/`Cache`/your settings is ever touched), or **Open release page** if you'd rather grab it yourself. **Advanced log** records every detail while you chase a bug (it switches back to Basic after 24 hours); **Open log folder** shows the logs. **Clean up backups** keeps the newest 2 backups of each mod, the merged pak and each server. Also **Performance tracking** (off by default; logs how long Rebuild/Install/catalog-refresh actually take to a separate `app.perf` log file) and **Export diagnostics zip…**, which bundles your logs — including any crash reports and your app/OS version info — plus a sanitized copy of your settings (nothing sensitive — real keys and passwords live in Windows Credential Manager, never in a settings file) into one zip to attach to a bug report.
            - **Appearance** — **Size of text and controls** makes every Starlink window bigger or smaller (90% to 150%): bigger is easier to read on a big or 4K screen, smaller fits more on a small one. It changes straight away. **Show pop-up messages** turns the small messages under the bell on or off. Three built-in themes (Icarus/Dark/Light) and your own Custom one; the **Theme** button at the bottom of the menu goes to the next one. Make the Custom one under the folding **Custom skin** section: edit any color by hex value, **Copy Icarus/Dark/Light** to start from a built-in theme's own colors instead of from scratch, and **Open skin file**/**Reload skin file** if you'd rather hand-edit the JSON file directly and pull your changes back in.
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

            - The **Editor** page (left menu) is the easy way: pick an item, change its numbers, save. See its own Help topic.
            - In the mod editor, **More ▾ › Copy a game row into this mod…** is the raw way: it copies any row of the game's data into your mod with all its current values, to change just the fields you want. **Change a game item (Editor page) →** takes you to the easy way.
            - The mod editor's **Fields** view shows each row in plain words: the list on the left is grouped by part (Armour, Recipes…) with in-game names and how many values differ from the game; a number is one box with what lower and higher do; game text (names, descriptions) is plain text; a stats list is one row per stat, with ✕, **Put back** and **Add a stat**. An orange border marks a value that differs from the game's. Fields are grouped (names, numbers, on/off, links, stats, lists); **Only changed fields** hides the ones still at the game's value; **↺** puts the game's value back and **✕** takes a field out of the mod. **Add a field** lists the fields the game's row has, by plain name, and adds one with the game's value. Lists still show as JSON. Under each field a short tip says what it does in the game; a stat also shows the line players read in game ("+N kg Weight Capacity").
            - A mod's stats list replaces the game's whole list, so a game stat the mod leaves out is gone in game; the Fields view marks those in red with **Put back**.
            - **Open in Editor** shows the selected row's item on the Editor page, with all its other parts. Rarer tools (Duplicate, Take out of this mod, Open original file, Insert file, Merge another mod, Open folder) are under **More ▾**.
            - **Ctrl+S** saves. Fields you left identical to the game are dropped from the file, so it only carries real changes. Save won't save while a box shows a value that isn't valid (a red message under it): fix it or press ↺ first.
            - **Search game data…** finds a row by name and every place the game refers to it (which recipes use Wood, which items grant a stat).
            - Select two or more rows to set one field on all of them at once. **⧉ Pop out** puts a view in its own window.
            - **Not in this mod yet** lists the game rows of the tables your mod changes that it doesn't have yet (new items after a game update, say), optionally only rows that set a field (for a stack mod: items that stack). Tick some and **Add ticked rows**; when your mod makes one change to that field on its other rows (×10), the new rows can get it too.

            **New items: New item from a game item…**

            The way new-item mods are built: start from the game item closest to what you want, and give the new item its own copies of only what you want to change.

            - **Start from**: pick the item. Its model, sounds and behaviour carry over.
            - **Name it**: the name players see, and a description (blank keeps the original's); the item id (its row name in every table) follows the name until you change it.
            - **What it gets its own copy of**: each table the item links to (name and icon, armour stats, what eating it does…). Ticked = its own copy, renamed and re-linked, safe to change. Unticked = it shares the game's row, and changing that row would change the original too. Food and medicine can also get **their own buff**.
            - **How players get it**: just the item; a **crafting recipe** at a bench you pick (starting from the original's ingredients, unlocked with the original's blueprint or known from the start); or an **orbital workshop purchase**, in an existing tab or a new tab of your mod's own, with research and replication costs in credits (research is paid once, replication per copy; lower is cheaper, 0 is free).
            - Everything lands in the mod editor as normal rows to fine-tune, and Undo takes it all back out.

            **Scale rules**

            The **Scale rules** view changes one field across a whole table, like every item's Weight ×0.5.

            - **Starter rules**: one click adds a ready rule (stack sizes, weight, crafting time, storage slots…); change its number, then Apply.
            - **Suggested from this mod**: when your mod already changes one field the same way on many rows (every crate's slots ×2), Starlink offers it as a rule. It writes the same values the mod has now, and after a game update it also changes the new rows.

            - Each rule has an on/off tick, a name, a **Table** and a **Field** (pick from the list; `Name.*` means every stat in it, `Name[].Field` that field in every list entry), and a value.
            - **Multiply by**: below 1 lowers the value (0.5 = half), above 1 raises it (2 = double), 1 leaves it as is, 0 makes it zero. **Set to**: every matching row gets exactly that number.
            - **Only rows whose name contains** limits a rule (several words, comma separated, match any). **Also rows using the table's default value** is off for stack sizes, so tools and weapons don't start stacking.
            - **Preview** / **Preview all** show what a rule would change without changing the mod. **Apply rules** writes the rows from your current game data (undoable); Save also applies a rule you changed but didn't apply.
            - Rules only touch the game's own rows: items your mod adds itself are left as you made them.
            - After a game update, **Refresh game data** (Settings) rebuilds every rule mod from the new data by itself (a mod open in the mod editor waits for its own Apply).
            - A Quality of life mod builds its rows the first time it opens. Its rules include ones you can turn on, among them **Cave sickness chance set to 5%** (25% in the game).

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

            **Check mods against game data** (Library › Check) compares every mod with what the game defines now. A mod made for an older game version often carries a copy of that version's data next to the author's changes, and that copy puts old data back: tags the game added since go missing, fields it added get deleted. A flagged mod gets a warning badge; click it for the **fix window**:

            - **Old copies of game data**: sure fixes. The game's current value stays. A hand-made mod lists only the fields its author changed, so a shorter list or a deleted field there is the author's choice (an emptied fuel list is how "torches need no fuel" works) and is never called old; only a prebuilt pak, or a table a mod copied wholesale, can carry old copies that way.
            - **Already the game's value**: changes that do nothing; taking them out is tidying up.
            - **Probably old**: other changes on rows the mod copied wholesale. The suggestion is the game's value.
            - **Lists both changed**: the mod added to a list and the game has entries the mod lacks. **Merge** keeps both.
            - **Rows the game renamed**: offered when it's certain. A mod's own new rows (a new item's rows, anything the mod links to) are never renamed, and a check never changes a mod by itself.
            - **Game files it can't find**: the mod's own cooked files (meshes, materials, blueprints) use game files where the game doesn't have them any more, so that part of the mod is broken (a missing look or sound, or the game stopping when it loads something built on it). Where the game only moved the file (same name and contents, another folder), the sure fix points the mod to the new place and changes nothing else; anything else is only listed, for the mod's author to update.
            - **Parts the game took out**: the game still has the file, but no longer has parts the mod uses (a component of a game blueprint). Only listed; **Leave them all as they are** stops them being listed.
            - **Old game rows**: whole rows a prebuilt pak still carries from an older game version, which the game has since removed (ISL knows every row the game has had since Week 9, from the public history of the game's data, plus each Refresh game data you run; two of your mods carrying identical rows counts as evidence too). Nothing uses them, so they do nothing; removing them is only tidying, and is offered, never done by itself.
            - **Tables the game doesn't have**: the mod names a table the game has no file for, so the merge skips all its rows. Usually the folder part of the name is missing; when the game has that table in one place, moving the rows there is a sure fix.
            - **Fields the game doesn't have**: a typo, or a field a game update renamed or removed, so the change does nothing. When only the case differs, matching the game's spelling is a sure fix. A close name is offered as a suggestion; taking the field out changes nothing in game.
            - **Values of the wrong kind**: text where the game has a number, say; the game may ignore it. Only listed.
            - **Text under another row's key**: a row copied without giving its name or description its own text key, so the game can show one text in place of the other. **Give it its own key** keeps the words.

            Every one shows the mod's value next to the game's, with the suggested fix already chosen; change any, or set a whole kind at once. **Apply** backs the mod up first (**Restore latest backup** undoes it). The author's own changes are never listed or touched.

            Your choices are remembered per mod, so you only decide once: a value you keep isn't asked about again (until the mod or the game changes it), and any other choice is made by itself from then on, by **Fix sure problems in every mod** and after every refresh of a prebuilt pak. **Forget my earlier choices** in the fix window starts afresh.

            **Fix sure problems in every mod** (Check ▾) makes only the sure fixes in every flagged mod at once. Prebuilt paks get the sure fixes by themselves each time they're converted, including after every **Refresh game data**. **Weekly Changes** shows what the game itself changed.
            """),

        new("Keyboard shortcuts", """
            # Keyboard shortcuts

            Anywhere in the main window:

            - **Ctrl+1** … **Ctrl+9** — go to the menu's pages in order (Database, Downloads, Library, Editor, Merge & Install, Saves, Server, Weekly Changes, Settings); **Ctrl+0** — Help
            - **Ctrl+F** — jump to the page's search or filter box

            The **Theme** button at the bottom of the menu changes the colours: each click goes to the next theme (Icarus, Dark, Light, Custom). **Play**, **Mods**, **UE4SS**, **Game** and **Server** at the top right start the game or open those folders.

            Inside the mod editor window:

            - **Ctrl+S** — save
            - **Ctrl+F** — focus the item filter box
            - **F3** — jump to the next item in the (possibly filtered) list
            - **Ctrl+D** — duplicate the selected item's fields under a new name
            - **Delete** — take the selected row(s) out of the mod (in the item list; Ctrl+Z puts them back)
            - **Ctrl+Z** — undo the last action (adding rows, a mass edit, a wizard's new item, applying rules); **Ctrl+Y** — redo

            On the Editor page, **Ctrl+S** saves. In the Library list: **Space** turns the selected mod on or off, **Delete** deletes it, **Enter** opens its details, **Esc** clears the selection.

            Select two or more items (Ctrl/Shift-click) to reveal a mass-edit bar — set one field across every selected item at once.
            """),

        new("Safety & backups", """
            # Safety & backups

            A few things this app is deliberately careful about:

            - **Install** (the one action that writes into your real game folder) always asks for confirmation, and always backs up whatever pak is already there first (last 3 kept).
            - The **UE4SS loader install/update/uninstall**, the **nxm:// protocol registration**, and **Update now** (this app updating itself) all work the same way — each touches something more sensitive than a normal mod folder, so each asks first too.
            - Every Rebuild independently re-reads the pak it just built and confirms every file it staged for packing is actually retrievable from it — not just trusting how many files it handed to UnrealPak.exe. If UnrealPak itself ever silently dropped something, this is what would catch it, and it shows up under **Build warnings and notes** on Merge & Install rather than as a quietly incomplete pak.
            - Profiles are backed up on **Save profile**, **Suggest order** and **Clear** (the automatic save of each tick isn't) — **Profile ▾ › Restore profile backup** in Merge & Install brings the last one back.
            - Your library is just the **Extracted_Mods** folder plus a small per-mod notes/pin sidecar — nothing about it is locked into a database only this app can read. If you ever stop using this app, your mods are still just files.
            - If the app ever hits an unexpected error — on the UI thread, during background startup, or inside an async action like Rebuild/Install/FTP/downloads/save-editing — it writes a crash report (the full exception, your app/OS version, and what you'd been doing right before it) to the **Logs** folder automatically, instead of the app previously either hard-crashing with nothing recorded or silently freezing with no trace at all.
            - If something goes wrong, **Export diagnostics zip…** (Settings) is the fastest way to get logs into a bug report.
            - Backups add up over time. **Settings › Maintenance › Clean up backups** shows how much space they take and keeps only the newest 2 of each mod, the merged pak and each server. The newest backup of anything is never deleted.
            """),
    ];

    [ObservableProperty]
    private HelpTopic? _selectedTopic;

    /// <summary>The search box over the topic list: every word must appear in a topic's title or text.</summary>
    [ObservableProperty]
    private string _searchText = "";

    /// <summary>The topics the search keeps, title matches first; every topic while the box is empty.</summary>
    [ObservableProperty]
    private IReadOnlyList<HelpTopic> _visibleTopics;

    public bool HasNoMatches => VisibleTopics.Count == 0;

    public HelpViewModel()
    {
        _visibleTopics = Topics;
        _selectedTopic = Topics[0];
    }

    partial void OnSearchTextChanged(string value)
    {
        VisibleTopics = Filter(Topics, value);
        OnPropertyChanged(nameof(HasNoMatches));

        // Keep the open topic while it still matches, so typing doesn't jump around.
        if (SelectedTopic is null || !VisibleTopics.Contains(SelectedTopic))
        {
            SelectedTopic = VisibleTopics.FirstOrDefault();
        }
    }

    public static IReadOnlyList<HelpTopic> Filter(IReadOnlyList<HelpTopic> topics, string? search)
    {
        var words = (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return topics;
        }

        bool InTitle(HelpTopic t) => words.All(w => t.Title.Contains(w, StringComparison.OrdinalIgnoreCase));
        return
        [
            .. topics.Where(InTitle),
            .. topics.Where(t => !InTitle(t) && words.All(w => t.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || t.Markdown.Contains(w, StringComparison.OrdinalIgnoreCase))),
        ];
    }
}
