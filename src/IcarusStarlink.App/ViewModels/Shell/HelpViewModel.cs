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

            **Your first mod in the game**

            1. Get a mod: **Database** finds one (Nexus or the IMM Database) and **Download** installs it into your **Library**; or **Library › Import ▾** takes a file you downloaded yourself.
            2. In **Library**, tick its **ON** box.
            3. A data mod (left list): click **Merge & Install →** on the CHANGES WAITING bar, then **Install**. A UE4SS mod (right list): click **Apply** on its bar.
            4. Start Icarus with **Play** (top right). That's it. To take a mod out, untick it and do step 3 again.

            Icarus must be closed for Install and Apply. Starlink backs up what it replaces, so **Restore previous install** (Merge & Install) can always put the last setup back.

            **Words you'll see**

            - **Data mod**: changes the game's data (stack sizes, recipes, new items). Most come as an **EXMOD** (a list of changed rows, the format Jimk72's Icarus Mod Manager made) or a **pak** (a packed game file). Starlink merges every ticked data mod into one pak for the game: that's **Merge & Install**.
            - **UE4SS mod**: a Lua script that runs inside the game (new behaviour, new menus). It needs the **UE4SS** loader, which Starlink installs for you.
            - **Game data**: Starlink's own copy of the game's tables (**Refresh game data** in Settings). Checks, the Editor and the merge all compare against it, so refresh it after a game update (**Update everything** does it for you).
            - **Your own mods**: made in Starlink (**New mod…**, the **Editor** page). Mods from Nexus or the IMM Database stay as their authors made them, so an update never overwrites your changes.
            """),

        new("Library", """
            # Library

            Your local collection of mods in two lists: **Data mods** on the left (EXMOD and pak mods, which Merge & Install builds into the game) and **UE4SS mods** on the right (Lua scripts, which **Apply** turns on or off). The source of truth is the **Extracted_Mods** folder itself, not a database inside the app.

            - The **ON** tick is how a mod goes into the game: ticked data mods go in on the next **Merge & Install**, ticked UE4SS mods on **Apply**. Select several rows (Ctrl/Shift-click) and tick one to tick them all.
            - Each list has a **CHANGES WAITING** bar at the bottom when you've changed something the game doesn't have yet (mods to put in, mods to take out, mods changed since the last install), with **Merge & Install →** (opens that page) or **Apply** (does it) right there. A row's status says the same: **Needs Merge & Install**, **Changed**, **Will be removed**, **In game**.
            - The chips filter both lists: **On**, **Off**, **Changes waiting**, **Updates** (a newer version online), **Problems** (what the mod check found, a mod replacing a high-risk game file, or a UE4SS mod that can't run) and **Favourites**. The search box finds a mod by name. Search with several words shows only mods that match every word (each word can be the start of a word in the name, author, notes or content).
            - **Import ▾**: **Import archive…** (a `.zip`/`.rar`/`.7z`/`.EXMODZ`; it looks inside and works out whether it's an EXMOD mod, a bare `.pak`, a UE4SS mod or a mix), **Import folder…** (an unpacked mod folder, recognised the same way: EXMOD, pak or UE4SS), **Import .pak…**, **Import UE4SS mod…** (lands unticked: tick it, then **Apply**). A prebuilt pak is converted into a real, editable EXMOD whenever UnrealPak.exe is set up and the game data is extracted.
            - If a download holds several alternatives of one mod (different sizes or presets), you're asked which one to install, since the author meant you to pick one. If Starlink can't work out how to install something, the **Install manually** window opens (see "When an install goes wrong").
            - Importing also checks how the mod was packed and warns in the Activity panel (the bell) about: leftover build files (a `.git` folder, editor session files), files outside the game's Content folder, data tables older than your game (missing rows the game has since added), and tables shipped without a single change.
            - **New mod…** creates a mod from a starting point (blank, Quality of life, a new item, or a classic IMM template; see "Making your own mods") and opens it in the mod editor.
            - **Check ▾**: **Check for updates**, **Update all**, **Check mods against game data** and **Fix sure problems in every mod** (see "Making your own mods"), **Check endorsements**, **What the last game update changed…** (see "After a game update"), **Make prebuilt paks editable…**. **Check mods against game data** only looks: fixes are made from a mod's warning badge or **Fix sure problems in every mod**. What the update, game-data and endorsement checks found is listed in the folding **Check results** section (**Clear** empties it). The **Check** menu greys an item out only while a check it can't run alongside is running, and a line above Check results names the checks that are running.
            - **Update all** updates every mod the last check found out of date, one at a time. Database mods update right away. Nexus and UE4SS mods run their own Update: a mod with several files asks which one, and cancelling stops Update all there. Without a Nexus key or Nexus Premium, it lists the Nexus and UE4SS mods that have updates instead. Replacing a UE4SS mod (Update, Reinstall, Activate) backs up the old copy to Backups\UE4SS first, whether it is on or off.
            - Right-click a mod: **Pinned**, **Favorite**, **Rename…**, **Export for sharing…** (your own mods; see "Making your own mods"), **Edit config…**, **Check against the game & fix…**, **Find the broken mod…**, **Convert to EXMOD…**, **Create mod backup** / **Restore latest backup**, **Reinstall original** (imports the mod again from the download Starlink kept, exactly as its author made it; fixes and edits since are dropped, after a backup), **See what changed vs previous version…**, **Find in Database**, **Search Nexus for this**, **Open on Nexus**, **Link to Nexus ID…**, **Endorse** / **Don't endorse (stop asking)**, **Delete**. Right-click a UE4SS row for **Open folder**, **Open UE4SS.log**, **Edit config…**, **Set minimum UE4SS version…**, the Nexus link items and **Delete**. **Del** on the UE4SS panel deletes the selected mod; with several selected it says to pick one.
            - **Create mod backup** / **Restore latest backup**: a backup is taken before each fix, refresh or update, and before a **Save** on the Editor page into one of your mods (each such save uses one of the mod's 5 kept backups), so **Restore latest backup** can undo it.
            - **Keep this version** (right-click a mod, data or UE4SS): stay on the version you have, for a server or a save. The **Update** badge goes away (a small **Kept** mark shows instead) and **Update all** leaves the mod alone. When a newer update than the one you skipped comes out, the badge comes back. Click **Keep this version** again to turn it off. **Versions…** (right-click a data mod) lists every copy Starlink kept of it, newest first, with why it was kept and its version: **Compare with now** shows what differs, **Put this version back** asks first and backs up the copy you have now, so you can switch back from the same list.
            - A download with a data part and a Lua part is two rows, one in each list, each marked ↔ with the other's name. Ticking the data row ticks its Lua part too (you can untick the Lua part on its own), and deleting either deletes both.
            - Double-click a mod for its details. **Notes** and the **Changes** tab's **In short** fold away. **In short** is a few lines like "D_Itemable · MaxStack ×10 on 1337 rows", worked out against your current game data, before the full list. A list a mod empties reads like "D_WorkshopItems · ResearchCost emptied on 12 rows"; a field a mod takes out reads like "D_Itemable · Weight taken out on 40 rows (the table's default applies)"; rows in a table the game doesn't have are counted on their own line, because the merge skips them. In the full list a taken-out field shows as "(taken out)".
            - Pin/Favorite/Notes are yours to organize with; none of them affect merging. ✎ means you changed it in the mod editor; the wrench means ISL's mod check fixed it (those fixes are made again after game updates); a faint "Made for an older game: between Week 222 and 4 Sep 2026" line under a mod's name is read from the game rows it carries (some were removed from the game after then); a **High risk** or **Medium risk** pill means the mod replaces game files with its own copies (hover it to see which): High is something the whole game builds on, like the player character or the base every building piece uses, so after a game update try without that mod first if things break; Medium is one blueprint or screen. It also names any other mod replacing the same file with a different copy, since only one can win; a warning badge means the mod doesn't fully fit the current game (click it to see what and fix it). Right-click › **Check against the game & fix…** does the same for one mod.
            - A greyed-out button or menu item still shows its tooltip, which says why it's greyed out.
            """),

        new("Editor", """
            # Editor

            Change the game's own items: make the default Envirosuit lighter, give it more oxygen slots, make Bread's buff last longer, a recipe cheaper. New mod… (Library) is the other half: it only ever makes new items, never changes the game's.

            **Your first change:** click an item on the left (or a starter like **Xigo Envirosuit**), change a number (Weight 100 → 50), type a name under **Save to › A new mod** and **Save**. Then **Turn on & go to Merge & Install** and **Install**. Nothing in the game changes until you install.

            - **Pick an item** on the left: every item in the game, with its icon. Search by name (envirosuit, bread, axe…); what the game says about an item is searched too, after the names. The list updates a moment after you stop typing, and so does **Search game data**.
            - **Narrow the list:** the category chips (Armour, Weapons, Consumables, Deployables…, as the game files its own icons) count what each would show. **Has** keeps items with a part you want to change (a recipe, a workshop price, durability, storage slots…), **Made at** the items crafted at one bench. With one of your mods under **Save to**, its items have an orange dot, and **Only the N items this mod changes** shows just those. **Clear filters** shows everything again.
            - **One value on many items at once:** tick items in the list (**Tick all shown** ticks what the search and filters show), then **Change N ticked…**; or pick a category or filter and click a **Quick pick** (Stack size, Weight, Durability, Spoil time, Buff length, Storage slots, Ingredients, Research cost), which ticks the listed items that have it. Choose **What** (the values the ticked items have; a recipe's ingredients are "every ingredient amount"), **How** (Multiply by, Set to, Add) and the **Number** (or ×0.5, ×1.5, ×2, ×3). Whole-number values are rounded, and a multiply or add never takes 1 or more below 1. The preview shows each item's value now and after: type over an item's new value to give just that item something else (a list takes one number per value, with commas), or untick it to leave it out. If some items use a row shared with others, choose their own copies (the default) or changing the shared rows. **Put into Your changes** fills it all in like typing would; **Undo last bulk change** takes it back out. Nothing is saved until **Save**.
            - **Change as many items as you like:** each keeps what you typed when you pick another (a ✎ in the list marks it). **Your changes** lists every change by item: click an item's name to show it again, its × throws away that item's changes, the × on a line undoes that one.
            - **Each card is one game row the item uses:** Name, icon, weight · Armour · Eating or using it · Its buff · Durability · Decay and spoiling · Tool damage · Recipe · Workshop price. Every number says what lower and higher do, and shows the game's value on the right; an orange border means yours differs. ↺ puts the game's value back. **Only changed values** hides the values still at the game's.
            - **Shared rows:** a card marked "shared by N items" is used by other items too (the basic Envirosuit's decay row is shared by 1,895 items). Choose **Give this item its own copy** (the default: only this item changes) or **Change it for all**.
            - **Stats:** armour, module and buff cards can **Add stat**: any stat the game knows, like cold resistance or max health. A stat the game is known to ignore on that card (tested in game) is refused, with the reason.
            - **On/off and choices:** a few on/off settings are tick boxes (Weather damages it, Must be outside, Needs shelter, Requires energy, Destroyed at zero durability, Take out only). What food **Spoils into**, a recipe's **Made at** bench and a bench's **Recipe list** are drop-downs of the game's rows by their in-game names (type to find one).
            - **Recipes:** ✕ beside an ingredient takes it out (↺ puts it back); **Add ingredient** searches the game's items. **Add bench** makes the recipe at another bench too, and ✕ beside a bench takes it out (the last one stays: a recipe needs a bench). To move a recipe, say from the Manufacturer to the Fabricator, add the Fabricator, then ✕ the Manufacturer, or just pick the Fabricator in its **Made at** drop-down. The recipe is saved with its whole new ingredient and bench lists.
            - **Storage:** a crate, bench or furnace has a **Storage** card per storage (Processor, Fuel…) with its **Slots**. Storages are often shared (40 benches share one): the usual choice applies, its own copy or change it for all.
            - Models, sounds and how an item is picked up or used are kept as the game's; changing those needs new art.
            - **DLC items:** an item from a paid DLC has a **DLC** card at the top with **Remove the DLC requirement**. Tick it and **Save**: your mod takes the DLC's lock off every row of that item, so you can use it without the DLC. Untick it and Save to put the lock back. **New item from a game item…** has the same tick for a copy. Your saves and Profile.json are never touched. The game's developer said: "Note we deliberately have not encrypted the files so that, if people want, they can easily award themselves the DLC."

            **Saving**

            - **Save to:** **A new mod** (give it a name), or one of **your own mods**: ones made in Starlink. Mods from Nexus or the IMM Database aren't listed, since an update would replace your changes and they aren't yours. **Open in Editor** from a mod you didn't make in Starlink keeps **Save to** as it was and says so: changes go into your own mods or a new mod. New mod names can't contain \ / : * ? " < > | or be a name Windows keeps for itself (like CON or NUL).
            - A value saved here for a field one of the mod's Scale rules sets stays: the rule skips that row from then on (as **Keep my value** does in the mod editor), also when you put the field back to the game's value. The row shows on the rule's card in Scale rules, and its ✕ hands the row back to the rule.
            - **Save** writes every item's changes into that one mod; **Discard** throws away all of them. Only the values you changed go into the mod. Putting a value back to the game's takes it out again.
            - After saving, **Turn on & go to Merge & Install** ticks the mod and takes you there (or tick it in **Library** yourself). Open it in the mod editor to see exactly what it holds.
            """),
        new("When an install goes wrong", """
            # When an install goes wrong

            Most downloads install by themselves. When one doesn't, Starlink says why and lets you do it by hand, instead of guessing.

            - **Install manually** opens only when an automatic install fails. The left side shows what's in the download; the right side, **Will be installed**, shows Starlink's best guesses. Leave out anything with ✕, or select a file or folder on the left and add it as a **Pak mod**, **EXMOD mod** or **UE4SS mod**. Rename a piece if you like, then **Install**. A UE4SS piece's name becomes its folder name, so it can't contain \ / : * ? " < > |, end with a dot, or be a name Windows keeps for itself. It's all or nothing: if one piece can't go in, the others are taken back out. **Cancel** changes nothing.
            - **Choose which one to install** appears when a download holds several alternatives of the same mod. Pick one; installing a different one later replaces it. Cancelling leaves the copy you already have exactly as it was.
            - Packing warnings (in the Activity panel after an import) don't stop the install. They tell you the mod may be outdated or carelessly packed, which is worth knowing when it misbehaves. A table "from an older game version" is the most important one: it replaces the whole table, so items the game added since are missing until the author updates it.
            - When Merge & Install's **Install** fails, the message says why: a full drive (with how much space to free), a game folder that can't be reached (check the Icarus Content folder in Settings), or a file in the mods folder that's in use.
            """),

        new("Merge & Install", """
            # Merge & Install

            Builds one merged pak from every ticked mod, then installs it into the game.

            - Tick mods' **ON** box in **Library** (select several rows with Ctrl or Shift and tick one to tick them all). Order matters: later mods win when two change the same thing, unless you pick otherwise in **Check the merge**. **More ▾ › Suggest order** proposes one. **Check the merge** looks at the queue before you build, in three tabs: **Conflicts** (values two mods change differently), **Between mods** (two mods overwriting the same file; the talent, blueprint and workshop trees as the whole queue leaves them: two nodes on top of each other, nodes that wait for each other so none can be unlocked, a node needing one from another tree; and a mod that needs another mod you haven't queued) and **Per mod** (the same check as the Library's warning badge; **Fix…** opens that mod's fix window). **Copy report** copies everything it found, each with a short code. None of it stops the merge.
            - **ArcFission mods** (WirelessPower, LumberYard…) come with ArcFission Library, which writes its own data pak (ZZ_ArcFission_Data_N_P.pak) into the mods folder when the game starts. Merge & Install reads the rows those mods add straight from the mods themselves (their ArcFission\rows.lua), so they're in the merge before the game has ever run with them, and, while ArcFission Library is on, installs the merge as zz_zz_ISL-Merged_9999_P.pak, which loads after the library's pak (without the library it's just ISL-Merged_P.pak; a server goes by its own library). You don't need to start the game first, or restart it after the library says so, for the mods' items to be there. The library's newest pak is left where it is (it would only write it again); its older copies are removed at Install. If ArcFission Library is uninstalled or turned off, no ArcFission rows go into the merge, and Install offers to back up and remove its leftover pak. When an ArcFission mod is added, removed or updated, Library's CHANGES WAITING bar asks for Merge & Install again.
            - **Install** rebuilds and installs in one click. It always asks first (it's what writes into your real `Content\Paks\mods`) and backs up the pak already there (the last 3 are kept; change it in **Settings › Maintenance › Backups to keep**). It reads **Update install** once something of this app's is installed. With nothing ticked and Starlink's merge in the game, **Update install** offers to take the merge out of the game (the same question, game check and backup as **Remove from game**).
            - While a build runs, **Cancel** next to the progress bar stops it. Nothing is installed, and the game keeps the mods it has. A build can't start while **Import patch** is still adding mods to your Library.
            - A mod whose Library folder is gone (deleted or renamed outside Starlink) is greyed in the list and left out of the build with a warning.
            - When two or more mods in the queue add new seeds, the build says so: the game numbers seeds by their place, so moving those mods in the queue renumbers their seeds, and crops already planted can change.
            - Your ticks, their order and the merge options save themselves into the profile in use ("Default" until you make others), and the last profile you used is picked again on the next start. If the game has mods that aren't ticked (Merge & Install would take them out), the CHANGES WAITING bar in Library offers **Tick the N mods in the game**, which ticks them again in the order they were merged.
            - **Profiles** save a whole queue plus merge options under a name: **Save profile**, and **Profile ▾** for **New profile…**, **Rename profile…**, **Restore profile backup** (every save backs up the one before) and **Delete profile**. A profile that can't be loaded (a broken or locked file) is never saved over: fix or delete its file, use **Restore profile backup**, or **Save profile** (which keeps a copy of the broken file). Renaming a profile takes its backups along; deleting one sets its backups aside, so a new profile with that name starts without them. **Share a mod list** holds **Export patch…** / **Import patch…**: a patch is the profile as it runs here (its data mods by their Nexus or IMM Database source, bundled when they have none; the UE4SS mods on in your game; their settings, unless you untick **Include UE4SS mod settings**). Importing one recreates the profile, offers the mods you lack as one download, writes the patch's settings into its UE4SS mods and offers to turn them on; a copy at another version is kept and listed in the warnings. A mod bundled in the patch replaces your copy only when it is the same mod (same name and author, or a copy an earlier patch import made), and backs yours up first. Your pin, favourite, notes and name stay. If you edited or fixed your copy, you're asked first, and No keeps yours. A different mod that uses the same folder name is left alone and the patch's copy goes in beside it. Right-click › **Restore latest backup** brings your files back. On **Export patch**, a mod you edited or fixed in Starlink goes in as your changed copy (it keeps its Nexus link), so friends build with your changes; the status line names these mods. A patch carries only plain settings files for UE4SS mods: scripts, and Lua settings that run code, are left out and named after the import. The Server page's Mods tab has **Export patch for players**, **Apply a patch to this server** and **Compare a patch** for a dedicated server.
            - **Find the broken mod** (**More ▾** here, or the Library's **Check ▾** and a mod's right-click menu): something broken in game, like you can't drink from a canteen, and you don't know which mod does it? Type what's broken (or pick the game row) and click **Find suspects**: mods that change it are listed with the reason, and tested first. If the health check already knows a problem in one, click **Fix…** first: that's often the answer. **Start** backs up your saves (characters and account, restorable from Saves, plus a zip of your worlds), then installs part of your mods. Start the game, check, close it, and say **Still broken**, **Works now** or **Can't tell**. Each round halves what's left, so 40 mods take about 7 rounds; a mod that needs another mod always goes in with it. At the end it names the mod (or the two that only break together): open its fix window, turn it off, or open its Nexus page. Then **Put my mods back** puts all your mods back in the game. Only the first round backs up the merged pak that's in your game (your real install); later rounds and Put my mods back only replace the search's own test paks, so they don't. That way **Restore previous install** still brings back your real install however many rounds the search took. Your queue isn't changed during a search, and if you close Starlink while only some of your mods are in the game, it asks first (**Close anyway** or **Cancel**), then offers to put your mods back the next time it starts. Only data mods are tested; UE4SS (Lua) mods and merge options stay as they are. Test on a new world if you can: a world loaded while some mods are out can lose their items. **Turn it off** and **Put my mods back** wait while Merge & Install is building. The save backups it takes show in the Saves page's backup list as "before Find the broken mod", and each slot keeps the newest 3 of them (Save and Restore never delete them, and they never delete your Backup now zips). It also keeps only the newest 3 world zips per save slot in Cache\save_backups\Worlds.
            - **Merge options** apply game-wide changes after your mods, even with nothing queued. A profile or patch can carry a Stacks or Slots value the pills can't show (like x2.5); the pills then show Off. Clicking the highlighted pill drops that value and uses the pill's own.
            - **Player boosts**: Speed Boost, Player Boost, XP Boost (L1 +200%, L2 +500%, L3 +1000%).
            - **Crafting & economy**: Craft Cost (fewer ingredients; Free = creative mode; trades with the vendors, like vestiges, crops, fish or Biomass for Ren and trophies and the food shop, always keep their normal price; 25%, 50% and the other scale options never make a real cost free, and leave a mod's free (0) ingredient free), **Stacks multiplier** (higher = bigger stacks), **Slots multiplier** (higher = more backpack and container slots), **Speed Crafting** and **Faster Taming** (0% = unchanged, higher = faster, 50% = half the time). If the game data lacks a table an option needs, the build says which, with the next step.
            - **Other**: **Lighter Items** (0% = normal weight, higher = lighter, 50% = half, 100% = weightless), Unlimited Ammo, Disable Temperatures, Remove Level Cap, and two marked **Beta** (built from the game's files, not tried in the game yet): **Free Item Repair** (repairing tools, weapons and armour costs no items; you still repair at the same bench, and turrets, enemy turrets and deployables still break as normal) and **No Weather** (no random storms, rain, snow or wind: every prospect uses the game's own clear-sky forecast; missions built around a storm, like the Glacier perma-storm expedition and the Ice Mammoth hunt and arena, and the tutorial rain keep theirs; a world you already started may keep the weather it had planned for a while).
            - The red bar above the queue counts what the check found ("3 conflicts · 1 clash between mods · 2 mods with problems"); **Review** opens **Check the merge**. A conflict means two mods (or a mod and an option) change the same value. The **Conflicts** tab shows them, grouped by which mods collide ("Homestead Expanded ↔ laanp-BuildTools · 14"). Pick the line, and the bottom shows the game's value, each mod's and the result side by side, lists lined up entry by entry (green: the game doesn't have it, red: left out). Choose a winner per line in its dropdown or with **Make it win** on a mod's column, or a whole group with **… wins all**. **Default** is the mod lower in the queue, except lists that can take every mod's additions.
            - **Save picks** keeps your winners with the profile, by mod name, so they still apply after you reorder the queue, add mods or restart; the next Merge & Install uses them.
            - What happened to the last install ("Installed…", "Install failed…") shows under the **Install** button.
            - **Advanced actions**: **Compare** (**Compare to installed**, **Compare paks…**) and **The merged pak in the game** (**Copy to game** installs the built pak without rebuilding; **Remove from game** removes it; **Restore previous install** puts back the pak that was there before; your Library is untouched). **Copy to game** refuses a pak built before an Icarus update or before the last **Refresh game data** (click **Install** to build it again). After a profile switch, its question says which profile the pak was built from.
            """),

        new("Merge conflicts", """
            # Merge conflicts

            When two or more mods in your queue change the **same** field, the default is simple: whichever mod is later in the queue wins. Most of the time that's exactly what you want.

            If you want more control, click **Check the merge** (or **Review** on the red bar) and open **Conflicts**:

            - It shows every field two or more queued mods actually disagree on (fields they all happen to agree on aren't shown — there's nothing to pick between).
            - For each one, pick a specific mod's value from the dropdown, or leave it on **Default** to keep the usual last-mod-wins behavior.
            - **Save picks** keeps them with the profile, and every later build uses them, also after the queue changes. If a build stops with "A pick in Check the merge no longer matches the queue", open **Check the merge**, pick again, then build.
            - **Between mods**: when two queued mods replace the same game file, it is listed once, by its path without the file ending (for example BP/Building/BP_Wall), and marked as the same only when both mods ship exactly the same parts.
            """),

        new("After a game update", """
            # After a game update

            When Steam updates Icarus, the **PENDING** bar at the bottom of the **Library** says so ("Icarus updated (build …)") and a pop-up tells you once. Starlink reads the build number Steam keeps for the game, so it knows even if you haven't played yet.

            Click **Update everything…** on that bar. It does, in order:

            - **Saves your setup as it is now**, so you can roll back: what's ticked and in what order, merge options and conflict picks, the merged pak in the game, and which UE4SS mods are on. The newest 3 of these are kept (in Backups\LastKnownGood).
            - **Notes the game files mods replace**, so Starlink can tell what the update changed in them.
            - **Refreshes game data**, the same as the button in Settings. When Refresh game data has already run for this version of Icarus, this step is skipped ("Game data already matches this version of Icarus.").
            - **Checks every mod** against the new game and **makes only the proven fixes** (old copies of game data, links to rows the game no longer has). Each mod is backed up first.
            - **Checks for newer versions** of your mods on Nexus and the IMM Database.
            - **Builds the merge and installs it.** If Icarus is running, the new merge is built but not installed, and the Library's "Icarus updated" bar stays until it is in the game: close Icarus, then click **Update everything** again.

            If a step doesn't work, it stops there and says why. The steps before it stay done.

            At the end, **"Icarus updated — here's what changed"** shows folding sections with a count on each: **High risk** and **Needs a look** open, **Fixed for you** folded.

            - **High risk**: mods that replace a game file the update changed. When the mod still has the game's code from before the update, the pill says **out of date** and the reason is named ("the game changed GetCurrentProspectLocation in this update; this mod still has its own older copy"). Starlink only says this with proof: it needs to have seen the game's files from before the update, so the first update after you start using Starlink can't show it yet. **Keep off until updated** unticks the mod and installs the merge without it; it says "Kept off" only once that merge is installed (or nothing else is ticked). If Icarus was running or the build failed, the button stays so you can try again. Tick the mod again once its author updates it.
            - **Needs a look**: things Starlink wasn't sure enough to change by itself, and mods with a newer version out (**Update**).
            - **Fixed for you**: each mod Starlink fixed, and how many proven fixes. **Open fix window** shows them.

            **Roll back to before the update** puts your mod setup back as it was: the merged pak, the ticks, order, options and picks, and which UE4SS mods are on. What's installed now is backed up first. The game itself can't be rolled back, only your mod setup, and fixes made to mods stay (right-click a mod › **Restore latest backup** undoes one).

            You can open the review again any time: **Check ▾ › What the last game update changed…** in the Library. **Dismiss** on the bar hides it until the next update, without changing anything.
            """),

        new("Weekly Changes", """
            # Weekly Changes

            What actually changed in the game's own data the last time it updated — a real, row-by-row diff, not a changelog someone wrote by hand.

            Every time you click **Refresh game data** (Settings), this app keeps a record of what it just extracted. Run it once before a game update and again after, and this page compares the two: which files changed, which items/fields were added, removed, or edited, old value beside new. A game update that changes only a table's Defaults (the values every row starts from) or its RowStruct is listed too, with one "(every row)" line for that table. Genuinely useful for figuring out why a mod that used to work suddenly doesn't (see "if a game update breaks your mod" under "Making your own mods").

            Pick a file on the left to see its changes on the right. **History** picks an earlier refresh (about a month is kept). **Refresh game data** reads the game again and compares. If you see "No changes tracked yet," you only have one extraction on record; run **Refresh game data** again after the next game update to get a real comparison.

            **Search** shows only the changes whose item, field or file name has what you type ("Canteen"). Tick **Only rows my mods change** to see just the changed rows that a mod in your Library also changes; the **Your mods** column names those mods, which may need an update. Each report in History also says which game build it came from (Steam's build number), when Starlink knew it.

            A refresh that finds nothing changed doesn't add a report. **Remove** (beside History) deletes the report shown, for one you don't need; it asks first, and the game's files and your mods aren't touched. Starlink also uses a report's old values to spot mods that are old copies, so once a report with changes is removed, that check won't know that update's values.
            """),

        new("Map", """
            # Map

            Every world the game has, drawn from the game's own files: where creatures spawn, which fish bite where, the biomes, and where dropships land.

            - **The map list** has the open world maps (Olympus, Styx, Prometheus, Elysium), then the outposts, then worlds the game has in its files but doesn't offer yet, tagged **In development** (ARK, for one). Under each name it says what the game uses it for: open world, how many missions, or outpost. An unfinished world shows whatever the game has; its picture may be dark or patchy, or missing.
            - **Creatures**, **Fishing** and **Biomes**: tick any mix to colour those zones on the map. **Drop sites** shows where dropships can land; zoom in to see each site's landing spots. **Grid** draws the in-game map grid (letters across, numbers down).
            - What's placed on the world, read from its level files: **Caves** (numbered by tier: 1 up to 3 cave worms, 2 for 4-6, 3 for 7 or more; more worms usually means a better cave; the box beside it shows only the higher tiers), hand-made **big caves** (orange; click one to see which biome each entrance is in: worked out from the biome map, the game doesn't say which caves connect), **Geysers** (oil and enzyme), **Dens & bosses** (Great Hunt dens and where world bosses can spawn), **Deep ore spots** and **Exotic spots** (spots they *can* appear: which ones get used, and the ore, is picked when a world is made), **Lakes & rivers**, **Fishing spots**, and **Missions** (pick a kind beside the box).
            - Click a cave for its cave worms, length, the creature zone it's in, and the ores caves of its kind can have. Any marker says its grid square. **Zoom to it** puts it in the middle, close up; **Copy** copies where it is ("Olympus H2: Tier 3 cave · 7-11 cave worms") to paste to friends. **Clear** (or Esc) unpicks it.
            - Mission spots go by their mission's name as the game shows it (POTSHOT, HABITAT, BLACK MARKET, Blacksand); spots many missions share say what they are ("Mission objective spots: overlook").
            - The **Key** under the side panel says what each marker shown means.
            - **Click** the map to see what's there in each ticked layer: the creatures that spawn (their levels and each one's share), the fish that bite (the zone's fish quality and the chance for each fish), and the biome. Hover to see it in short. **Scroll** zooms, **drag** moves, the buttons on the map zoom and show the whole map again; the eye (**Map only**) hides everything drawn over the map until you click it again. The map picture is the game's own (about 2 m of ground per pixel on the big maps), so very close up it gets soft: there's no finer picture in the game files.
            - **Find** lights up every zone with that creature or fish, and rings every place that matches ("tier 3", "geyser", "big cave", a mission's name), shown even if its box isn't ticked. **Enter** zooms to them. A grid square ("I7") goes straight there.
            - What you tick under Show, the cave tier and the mission kind are kept for next time.
            - A zone an installed mod changes says **Changed by your mods**, with the game's own list under it. UE4SS mods that change spawning while the game runs (MkHaters Wildlife, for one) aren't shown: the map is the game plus your installed data mods.

            Starlink draws the maps once and keeps one copy. **Reload** checks the game's files and draws again only the maps that changed, then says what changed ("Olympus: Conifer Hard: Desert Wolf added"). **Rebuild all maps** draws every one again, changed or not (several minutes: each big map's caves and spots take about a minute to read). The page tells you when the game's files changed since the maps were made. Tick **Redraw during Update everything** to have **Update everything** redraw the changed ones after a game update; off (the default), it's left to your Reload.

            While Reload or Rebuild all is drawing, **Stop** next to the status line stops after the map being drawn now; the maps drawn so far are kept, and Reload (or opening Map again) draws the rest. If Starlink closes while the maps are being made, opening Map again carries on. A map that can't be drawn keeps its last drawing. If the game folder in Settings has no Paks folder, Map says so and changes nothing. When the maps finish while you're on another page, the Activity panel says what changed and which maps couldn't be drawn.
            """),

        new("UE4SS mods", """
            # UE4SS mods

            UE4SS is Icarus's Lua scripting framework, separate from pak/EXMOD mods and from Merge & Install.

            - UE4SS mods are rows in **Library**'s right-hand list. Tick or untick their **ON** box, then **Apply** in its CHANGES WAITING bar: ticked mods move into the game's Mods folder, unticked ones move back out (backed up first). Nothing changes until you Apply. **Apply**, and deleting a mod that's in the game, need Icarus closed; if a file is still in use, nothing is moved and the file is named. Deleting any UE4SS mod keeps a copy in Starlink's Backups folder.
            - Ticked means UE4SS really starts the mod. UE4SS starts a mod only when `mods.txt` has a `1` for it or its folder has an `enabled.txt` (the `enabled.txt` wins even over a `0`). A mod whose folder is in the game but that UE4SS skips shows **Off in UE4SS** (hover it to see why); tick it and Apply, and Starlink adds its `enabled.txt`. A mod Starlink puts in the game gets one too when it needs it.
            - A ticked UE4SS row also says what UE4SS did with it the last time you played, read from UE4SS's own log: **Started**, **Didn't start** (on, but UE4SS skipped it: right-click › **Open UE4SS.log** to see why), or **Not run since** (you turned it on or changed it after the game last ran, or UE4SS was just installed, so play once and look again).
            - A download that is both a Lua mod and paks becomes one mod: its paks in the list as a mod, its Lua half as a linked UE4SS row. Deleting one offers to delete both.
            - **Edit config…** (right-click a UE4SS row) shows the mod's settings in groups, with plain names and the real name small beside each one. On/off settings are switches; numbers with a range show it; keys have a box for the key and Ctrl, Shift and Alt ticks. **Not the default** marks a setting the mod ships differently; **Reset to default** puts one back, **Reset all to default** puts them all back. **Server decides** means in multiplayer the host's or server's copy is used. Your edits stay when you switch file or copy, and closing asks before throwing them away. Nothing is written until you **Save**, and the old file is backed up first. If the mod (or another editor) changes the file while the window is open, Save writes only the settings you changed here into the file as it is now; in the text view Save refuses instead, so copy your text, click **Discard changes** and make the edit again. Empty lists such as `NoGuests = {}` can be filled in (keep the { } and put names in quotes). A mod update keeps your settings and adds the new version's new ones. Config files keep the encoding they were saved in (ANSI, UTF-8, with or without a BOM) when you save them, update a mod or apply a shared list.
            - UE4SS's own **shared** folder (the Lua library its mods use) shows a lock and "Part of UE4SS, always on". It isn't a mod, so it can't be turned off or deleted. UE4SS's own built-in mods (Keybinds, ConsoleEnablerMod, ActorDumperMod and the rest) are known by name, so the official 3.0.1 release, which has no mods.json, shows them as UE4SS's own and Uninstall doesn't move them to staging.
            - **Import UE4SS mod…** (Import menu) adds one from a zip, unticked.
            - Starlink ships with UE4SS. If you tick a UE4SS mod but the loader isn't in the game, a **UE4SS MISSING** bar says so, with **Install UE4SS** right there.
            - **Settings › UE4SS** installs, updates or reinstalls the loader (the bundled copy, or GitHub's latest if newer), **Install from zip…** installs a UE4SS release you downloaded yourself, and **Uninstall UE4SS** removes it. Each asks first and backs up, since it writes into `Binaries\Win64`; your mods are moved aside before an uninstall.
            """),

        new("Downloads", """
            # Downloads

            Everything downloaded through this app, from Nexus or the IMM Database, has its own page: **Downloads**.

            - A download installs the moment it finishes. One download is one mod: its paks go to Library, a Lua part to its UE4SS row.
            - While something downloads, a **Downloading** list at the top shows each one: how much has arrived, the size when it's known, and the speed. **Cancel** on a row stops that download only; **Cancel downloads** stops them all. Once the file is in, the row says Installing… until the mod is in your Library.
            - Installed downloads are hidden; tick **Show installed** to see them. **Reinstall** puts back a mod you removed; **Discard** deletes the downloaded file (the installed mod stays). If that mod is still installed, Starlink asks first: **Reinstall original** can't put it back as its author made it, and **Reinstall** won't work until you download it again. To discard several, select them with Ctrl or Shift and click **Discard selected**.
            - **Needs** shows what the mod's own README says it requires, and **Get 'X'** fetches a missing one.
            - When you have one file of a Nexus mod installed and Nexus lists a newer version of that same file (Nexus keeps each file's update chain), the mod shows an update, and downloading it starts on that newer file in the file list (pick another file to switch variants). The older copy is replaced, not piled up.
            - A UE4SS mod in a subfolder of the zip (`Mods\MyMod\Scripts`) is put in the game under the mod's own folder name. A pak or EXMOD download that also carries a Lua folder becomes two linked rows.
            - A ".pak" that isn't a real pak (a web page or a broken download) is refused, with the reason.
            - Starlink won't save a single download bigger than 4 GB: if the server says a file is bigger, it is refused before it starts; if it keeps sending past 4 GB, the download stops and no partial file is left.
            - If a download also contains UE4SS itself, Starlink adds only the mod's own folders. UE4SS is installed from Settings › UE4SS.
            - If a mod has programs or scripts in it (.exe, .bat, .ps1 and the like), Starlink warns you in the Activity panel. Icarus mods don't need them, and Starlink never runs them. Don't open one unless you trust where the mod came from.
            - A download that holds the same pak in two folders (versions to choose from) isn't installed automatically: the manual install opens so you can pick the folder you want.
            - A download that holds only a README, pictures or an installer isn't installed: Starlink says "Nothing in this download can be installed" and opens the manual install.
            - While Icarus is running, Starlink won't replace a UE4SS mod that is on in the game. Close the game, then click **Reinstall** or **Update** again.
            - Older downloads of a mod you've updated read **Older version** and are hidden with the installed ones (tick **Show installed** to see them). Installing one asks first, because it replaces the newer version.
            - With a free (not Premium) Nexus account, **Update** opens the file's page on Nexus: click **Mod Manager Download** there and Starlink installs it (Nexus lets only Premium accounts download from inside an app).
            - Mod authors: `IcarusStarlink.exe --import <zip or folder>` installs your build without asking when it is on this PC; a build on a network share or mapped network drive asks first.
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
            - Your merge list is rebuilt in **Merge & Install** in the same order, in its own profile **Classic IMM** (made the first time, used again after), so the profile you were using is left as it was. **Classic IMM** is picked for you, so you can click **Install** right away. The migration's status ends with "Merge list (profile 'Classic IMM'): …", and a mod that came over with a warning shows it on its line.
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

            **IMM Database** is the community catalog: search, and **Download & extract**. The folding **Filters** section holds Author, Category, **Updates only**, **Downloaded only** / **Not downloaded only**, **Game week** and **Hide older weeks**; its header sums up what's set. The **Installed** and **Works with** columns show your copy's version and the game week the mod was made for. **Find in Database** (right-click a Library mod) opens this tab already searched. **Readme** only opens web links. Only https download links are used: a plain http link to GitHub is switched to https, and any other http link gives no download (use the mod's page instead).

            **New mods.** Each tab says how many mods are new since your last visit, like "IMM Database (5 new)". New mods have a **New** tag; mods with a newer version than you last saw have an **Updated** tag. Tick **New only** to see just those. The tags clear when you close Starlink after looking at that tab, or straight away with **Mark all seen**. The very first time, everything counts as seen.

            **Nexus** is a native list of Icarus mods on Nexus, driven by the API key you save once in **Settings** — there's no embedded web browser and no second sign-in. **All** shows the whole catalog, most-endorsed first (works even without a key; the ‹ › buttons at the bottom go to the next or last page); **Trending / Latest / Updated** are Nexus's own curated lists (need a signed-in key); the search box switches to live results, covering every Icarus mod on Nexus.

            Because the list is ours rather than a web page, each mod shows what YOUR machine already knows about it: **In Library**, **Downloaded**, **Tracked**, and an **Update** badge — a precise comparison between the live Nexus version and your own Library copy's version, not a guess. Three toggles filter the list: **Hide ones you have**, **Updates only**, and **Tracked**. With the search box empty, **Tracked** shows every mod you track and **Updates only** shows every Library mod from Nexus that has a newer version there; both replace the list and have no pages. With search text, they only narrow the results.

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
            - If a number box (XP, a currency, an appearance value, a bestiary count, a mount level) isn't a plain whole number, it gets a red border and a short line under it as you type ("Whole number, digits only"), and Save stops and names the box. Digits only, no commas.
            - When a Save fails part way, Starlink puts back the files it had already written from the backup it took just before. Your edits stay on the page, so click **Save** to try again.
            - **Clean up** and the leftover fixes are turned off when a pak sits anywhere under Content\Paks other than Paks\mods (for example Paks\~mods or Paks\LogicMods), because Starlink can't tell those mods' rows from leftovers. A creature, accolade, currency or talent rank Starlink can't place (game data older than the game, or installed mods not read yet) gets a note and no fix: run **Refresh game data** in Settings to place it.
            - After a **Restore** (player data or a world), the restored files get the time of the restore, so Steam Cloud treats them as the newest copy and doesn't put its older one back.
            - **Worlds tab › Backups of this world** lists every backup of the selected world: the copy taken before each World settings save, before each world restore, and Find the broken mod's zip of all your worlds. Pick one and press **Restore selected** (Icarus closed; on the Server page, the server stopped). The world as it is now is backed up first, so a restore can be undone from the same list; from a zip of all worlds only the selected world is put back. Each world keeps as many as Settings › Backups to keep › Save backups; the oldest is always kept.

            **What's editable today:** character name and XP (the game works the level out from XP; the **Level** box sets the least XP that reaches the level you type); profile currencies (Ren, Exotics, Red, Stabilized, Biomass, Uranium, Licences, Respec points; one the game doesn't have can be removed); character talents, account-wide Workshop research, missions and a mount's talents, each as **Tree** (the game's own menu: click a node for a rank, right-click or − takes one off, Fit and a size slider, Undo changes per tree) or **List** (searchable, a rank +/−, a "Show unlearned" toggle); character/account/binary unlock flags, as searchable chips; account-wide Bestiary encounter points (with a Set max per creature); account-wide Accolade completion; the account-wide Items bank (view real item names and remove one — full item creation/stat editing isn't offered, since a malformed item is easy to create by hand and hard to notice); raw Cosmetic hash values per character (there's no known way to map these to option names, so it's a raw editor, not a picker — still useful for copying an exact look between your own characters); and your tamed **Mounts** — name, level, lineage, look, genetics and talents, or **Delete** one (the species is shown but can't be changed: its talents and stats live in the mount's own game data, so changing only the label would leave a mismatched mount). Everything the editor doesn't understand — item internals (durability, alterations, upgrades), Loadouts (equipped gear sets), raw Bestiary/Accolade progress counters, a mount's stomach and saddle state, timestamps — is preserved exactly as-is when saving. The Overview shows character cards and an account snapshot; click a card to edit it. **Duplicate** and **Delete** are under the character list on the Characters tab (Delete asks first, and the change is written by Save like every other edit, backup first). Items, mounts and creatures show the game's own icons.

            **In the game's order:** talents list the talent trees first, then blueprints by tier (Tier 1 Character crafting, Tier 2 Crafting Bench, Tier 3 Machining Bench, Tier 4 Fabricator, Tier 5 Manufacturer), then Solo. Workshop research, missions (by map) and the bestiary (by biome) follow the game's own order too. Click a heading to fold it. On the Bestiary tab, **Complete all** on a biome's heading fills every creature listed under it.

            **Worlds**: each saved world in the slot, with its custom world settings, the same switches the game shows when you create a world (Arachnophobia Mode, Mission Structure Cleanup, Mount Unclaim Requires Ownership, Spawn Great Hunt Creatures Post Campaign, and any the game adds later). Turn one on or off and **Save world settings**: the world is backed up first (to `Cache\save_backups\Worlds`), only its settings list changes, and saving is refused while Icarus runs. Settings the game only allows when a world is made, and the world's Difficulty and Hardcore, are shown but can't be changed. A number setting it can't use (letters, or past the game's range) goes back to the value that will be saved.
            """),

        new("Server", """
            # Server

            **Server** has two tabs.

            **Local server**: run an Icarus dedicated server on this PC.

            - **Install new server…** downloads one with SteamCMD (**Install SteamCMD**, or **Use my SteamCMD…**); **Add existing server…** adopts one you already have. Each server is its own profile.
            - **Start**, **Stop**, **Restart**, **Check for update**, **Update**. Check for update asks Steam (through SteamCMD) for the newest build on the server's branch; the line beside it says "up to date" or "update available (build …)".
            - **Start**: if another Icarus server is already running from the same install and no server in the list is set up for it (for example one your own .bat started), Starlink asks before starting a second one. **Add existing server…** picked while such a server runs offers to copy its Saved folder, log file and ports.
            - **Update** on a running server asks first, stops it, updates, then starts it again (also when the update fails, on the build it already had). **Verify files** (next to Update) has SteamCMD check every server file and download again any that is damaged or missing. It is slower than Update and doesn't touch worlds, settings or mods; a running server is stopped for it and started again. Routine updates don't do this full check; it still runs for a branch switch, for a server SteamCMD has no record of, and for the first update after one that failed.
            - While SteamCMD installs or updates a server, a box under the server's status card shows what it's doing and how far it got ("Updating 'My server' · downloading 40%"). **Cancel** stops it (run it again later; it carries on from what it already downloaded), **Hide** closes it when done, and **SteamCMD output** opens the full output. **Check for update** shows it only while Steam is asked, then it closes by itself: the answer is on the build line. One SteamCMD run happens at a time: an Update waits for another server's check to finish. If the hourly automatic check can't get an answer (5 minutes at most), Recent events says so and automation carries on.
            - **Now** (top of the Overview) says in one line what the server is doing: running (world, players, uptime), starting, updating, stopping, or how it last stopped (cleanly, by you, or a crash). **Coming up** lists what automation does next for each option that's on: the next daily restart, update check and daily backup, and how many crash restarts were used in the last 10 minutes. **Recent events** shows the newest eight (players joining and leaving, starts, stops, crashes, updates, backups); the Log tab has them all.
            - **Connect** (Overview) shows the address friends type into **Direct Connect** in game: this PC's address on your home network plus the server's game port (for example 192.168.1.20:17777), with **Copy**. When Settings › Advanced › **Bind IP** is set, the card shows that address instead: the server answers only there. For friends outside your home network, press **Show my public IP**: Starlink asks api.ipify.org for your internet address, only when you press it, and doesn't save it. They also need both UDP ports (game port, default 17777, and query port, default 27015) forwarded on your router to the home-network address shown; Starlink can't change router settings. Windows Firewall is handled in the **Windows firewall** section.
            - Tiles show players, world, uptime, memory, build and last backup. **Overview**: **Automation** (restart if it crashes, a daily restart, update on restart, update automatically, a daily backup, start with Starlink, start minimized; its header lists what's on) and the folding **Windows firewall** section (its header says whether every port is open). Drag the gap next to the server list to widen it.
            - **Log** (follow, hide noise, filter; players online and events beside it, with a drag handle between). **Settings** in folding sections: **Server** (names, max players, passwords), **Branch & ports**, **World**, then **Lobby** and **Advanced** (bind IP, Saved folder suffix, log file name, the command line) folded; **Save settings** stays under the list.
            - Each player online shows their Steam64 id (the 17-digit number the admin and ban lists use) with **Copy**, **Add to admins** and **Ban**. The last two put the id in the list on **Admins & bans**; click **Save lists** there while the server is stopped to make it count (bans typed in game meanwhile are kept; picking another server first drops the change). To ban someone right away, type /BanPlayer name in game chat.
            - While the server runs, its game settings (max players, passwords, world and lobby options) can't be saved: the server keeps its own and writes them back. Click **Restart**: it stops the server, saves them and starts it with them. The **Saved folder suffix** and **Log file name** change the same way: only while the server is stopped, or through **Restart**. Ports, branch and other launch options save at any time and apply at the next start. If a setting on the Settings tab is wrong, **Restart** says so and leaves the server running.
            - **Save**: if the new game or query port is one another server in the list already uses, Starlink asks first. Two worlds you never run at the same time may share ports.
            - **Create a new world on start**: pick the world from the game's own list (open worlds, outposts, missions), a difficulty, Hardcore, and a world name (an outpost needs one). Set it back to **Don't create one** after the first start, or every restart makes a fresh world. Without extracted game data it's a text box taking the server's own "[ProspectType] [Difficulty 1-4] [Hardcore True/False] [SaveName]".
            - **Mods**: **Install my merged pak** puts your Merge & Install pak on the server; **Install UE4SS from my game**, then **Copy to server** on each mod (or **Make the server match my game** for all). **ON** turns a mod on or off on the server only; **Match settings…** picks which copy's settings both use; **Remove** takes it off; **Refresh mods** reads it all again. These buttons are greyed out while the server (or another server on the same install) is running, starting, stopping or updating, by hand or by automation; hover a greyed button to see why. ArcFission mods (WirelessPower…) need ArcFission Library and the mods on the server too, the same versions as in your game: their rows are already in the merged pak, and their scripts run on the server. The server's ArcFission Library writes its own data pak at each rebuild; **Install my merged pak** moves its older copies into the server's backups. **Apply a patch to this server** asks before it puts the patch's own UE4SS mods (not your game's) on the server.
            - **Worlds & backups** (**Back up now**, **Restore selected**), **World settings** (each world's custom settings, like the Saves page's Worlds tab; the server must be stopped to save, and the world is backed up first) and **Admins & bans** (Steam64 ids, one per line). Bans made in game with BanPlayer are kept: if a list changed on disk since the page showed it, an untouched list takes the new file and an edited one asks before replacing it. If ServerSettings.ini can't be read, nothing is saved over it. If it was changed outside Starlink after the page read it (by hand, or by the server), Save and Start load the file first when you haven't changed anything here, and ask **Reload it** / **Keep mine** when you have. Restore, Save lists and World settings Save are greyed out while the server runs.

            **Remote (FTP)**: a server hosted elsewhere. Save sites (the password is kept in Windows Credential Manager; **Server folders** holds the Mods and Win64 paths, only needed when your host lays the server out differently), **Connect**, browse, upload and download, and the **Icarus server sync** buttons: **Install merged pak**, **Sync UE4SS loader**, **Sync UE4SS mods**.

            - New sites start as **Encrypted when the host supports it (recommended)**: TLS when the host has it, plain FTP when it doesn't. Saved sites keep their setting, and after connecting the status line says whether the connection is encrypted. A password typed in the site form is saved only once the login works.
            - **Upload** asks before it replaces a file of the same name and shows both sizes and dates (the server's copy isn't kept, so download it first if you may need it). Replacements go up under a temporary name, so a dropped connection leaves the server's file whole.
            - **Install merged pak** removes only pak files (.pak, .sig, .utoc, .ucas), ISL-Merged.txt and leftover .uploading files from the server's mods folder, and nothing at all when the Mods path doesn't end in a folder named mods. Stop the server on the host first: its pak files are locked while it runs.
            - **Sync UE4SS loader**: each file goes up under a temporary name; if a sync stops part way, run it again before you start the server. Your UE4SS-settings.ini goes up only when the server surely has none.
            - **Sync UE4SS mods**: if the server's mods.txt couldn't be updated, the message names the mods left off; the next sync turns on a mod that is on the server but that nothing there turns on. A 'Name : 0' line on the server stays as it is. If an upload breaks off, the mods that went up whole are still turned on; delete the half-uploaded mod's folder on the host and sync again.
            """),

        new("Settings & Diagnostics", """
            # Settings & Diagnostics

            Settings is split into tabs. Beyond **Game & tools** (covered in Getting started) and **Migrate from IMM** (see "Coming from classic IMM": the migrated merge list goes into its own profile, **Classic IMM**, so the profile you were using is left as it was):

            - **Nexus** — sign in with your own Personal API Key (a plain account-wide token from Nexus's own Account Settings page — not tied to this or any other app), and optionally register the nxm:// download handler (the folding **Download handler** section; its header says whether it's set up). If Nexus links point at a Starlink copy that was deleted or moved, Starlink takes them back (with **Take them back automatically** on) or warns you.
            - **UE4SS** — **Install / Update to / Reinstall** the loader (the dev build that ships with Starlink, or a newer one **Check for update** found on GitHub; GitHub is only asked when you press it), **Install from zip…** for a UE4SS release you downloaded yourself, and **Uninstall UE4SS** (your own mods are moved aside first; only the framework's files are removed).
            - **UE4SS config** — edit UE4SS-settings.ini for your game or one of your servers, one folding section per part of the file, each setting with a plain name and its real name small beside it. **Save** changes only the settings you edited (backed up first), and **Also save these changes to** copies them to your other targets. Settings a newer UE4SS build added that your UE4SS-settings.ini doesn't have yet are listed too, at their default and marked "not in your file yet"; one is added to your file only when you change it and save. Installing or updating UE4SS keeps your own settings file and puts the new build's own copy beside it as UE4SS-settings.default.ini (UE4SS never reads that one).
            - **Maintenance** — **Check for updates** looks for a newer Starlink. Stable updates come from Starlink's Nexus page when you're signed in to Nexus (Nexus tab), otherwise from GitHub; betas always come from GitHub. The line under the buttons says which. **Update now** opens a window that shows each step: getting the download, downloading, checking the file, unpacking, then closing Starlink to install the update and open it again. **Cancel** works until Starlink closes, and nothing is changed before then. With Nexus Premium the download comes straight from Nexus. With a free account the Nexus page opens on the right file: press **Slow download** there (this needs Starlink as your Nexus download handler: see Nexus above, or press **Make Starlink the download handler** in the update window when it shows), or click **Get it from GitHub instead**. When an update fails, the update window offers **Try again**. If getting the Nexus file fails a second time and the release is on GitHub, it also offers **Get it from GitHub instead**; Starlink never switches by itself, and the GitHub zip gets the same checksum check. A download is only installed when it matches the release's published checksum. Nothing of yours in `Extracted_Mods`/`Profiles`/`Cache`/your settings is touched. **Open release page** is there if you'd rather get it yourself. **Update channel**: Stable gets finished releases only; Beta also gets test versions (they may have bugs), and switching back to Stable offers the newest stable one. **Check for updates when Starlink starts** looks each time Starlink starts and asks before installing anything. It only decides whether Starlink looks for newer versions when it starts: after you update, the first start still shows What's new for the version you installed (it asks GitHub once for those notes). **Advanced log** records every detail while you chase a bug (it switches back to Basic after 24 hours); **Open log folder** shows the logs. **Backups to keep** sets how many backups each kind keeps: **Merged pak installs** (kept on every install, for Restore previous install; 3 at first), **Mod backups (each mod)** (what Clean up backups keeps of each mod and server; 2), **Save backups (each slot)** (automatic save backups; the first one of a slot is kept as well; 10) and **Server backups (each server)** (what a server you add starts with; each server's own Backups tab changes its count; 10). Higher keeps more history; lower saves disk space. The newest backup is always kept. **Clean up backups** deletes older backups down to those counts. Also **Performance tracking** (off by default; logs how long Install and catalog refresh actually take to a separate `app.perf` log file) and **Export diagnostics zip…**, which bundles your logs — including any crash reports and your app/OS version info — plus a sanitized copy of your settings (nothing sensitive — real keys and passwords live in Windows Credential Manager, never in a settings file) into one zip to attach to a bug report. The zip hides your Windows user name (your user folder shows as %USERPROFILE%). Starlink keeps the newest 20 crash reports and 14 days of timing logs.
            - **What's in the diagnostics zip for a bug report**: besides the logs and settings, the zip has three short files that answer what a mod author asks first. **mod-setup.txt** lists your Library mods (version, where they came from, Nexus number), the Merge queue in order, your Merge options, what is installed in the game's mods folder, your UE4SS build and which UE4SS mods are on, ArcFission, the game build and when you last refreshed game data. **last-game-crash.txt** has the game's last crash: its error, whether UE4SS was part of it, and the engine version. It never takes the crash dump or your account ids. **ue4ss-log-tail.txt** is the end of UE4SS.log. No keys, passwords or saves go in the zip.
            - **If settings won't load**: each time Starlink saves settings, it keeps the previous copy as settings.json.bak. If settings.json is broken or another program has it open, Starlink starts from that copy and says so in the Activity panel.
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
            - A mod's list replaces the game's whole list (stats, benches, tags…), so an entry the mod leaves out is gone in game: keep the game's entries and add yours. For a stats list the Fields view marks a missing game stat in red with **Put back**.
            - **Open in Editor** shows the selected row's item on the Editor page, with all its other parts. Rarer tools (Duplicate, Rename this row, Take out of this mod, Open original file, Insert file, Merge another mod, Open folder) are under **More ▾**.
            - **Rename this row… (F2)** gives a row this mod adds a new name and changes the links to it in this mod (links from other mods aren't changed). A copy of a game row can't be renamed, because the mod changes the game's row by its name: use Duplicate instead.
            - **Mod details** (top left of the mod editor) sets the mod's own name, author, version, description, picture link and readme link, ready for a Nexus upload. They're written into the mod when you Save, and Undo takes a change back. The mod's file name stays the same. If you renamed the mod in Library, Library keeps showing that name.
            - **Ctrl+S** saves. Fields you left identical to the game are dropped from the file, so it only carries real changes. Save won't save while a box shows a value that isn't valid (a red message under it): fix it or press ↺ first.
            - **Search game data…** finds a row by name and every place the game refers to it (which recipes use Wood, which items grant a stat).
            - Select two or more rows to set one field on all of them at once. **⧉ Pop out** puts a view in its own window.
            - **Not in this mod yet** lists the game rows of the tables your mod changes that it doesn't have yet (new items after a game update, say), optionally only rows that set a field (for a stack mod: items that stack). Tick some and **Add ticked rows**; when your mod makes one change to that field on its other rows (×10), the new rows can get it too.

            **New items: New item from a game item…**

            The way new-item mods are built: start from the game item closest to what you want, and give the new item its own copies of only what you want to change.

            - **Start from**: pick the item. Its model, sounds and behaviour carry over.
            - **Name it**: the name players see, and a description (blank keeps the original's); the item id (its row name in every table) follows the name until you change it.
            - **What it gets its own copy of**: each table the item links to (name and icon, armour stats, what eating it does…). Ticked = its own copy, renamed and re-linked, safe to change. Unticked = it shares the game's row, and changing that row would change the original too. Food and medicine can also get **their own buff**. With **Inventory** ticked, the new item gets its own copy of the storage rows (slots), so its slots can change without changing the game's item.
            - When the game item needs a DLC (Great Hunts, New Frontiers, Dangerous Horizons, Homestead and others), the wizard notes that your copy needs the same DLC as the original: anyone who can use the original in a prospect (for example through the host's DLC) can use the copy. You can copy and change it like any other item.
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
            - A field a rule sets shows "set by rule: …" in Fields. If you change it by hand, Apply would set it back: pick **Leave it to the rule**, or **Keep my value** (the rule then skips that row, and the row is listed on the rule's card; ✕ there hands it back to the rule). Before Apply (or Save after a rule change) replaces values you set by hand, it asks: Keep mine, Use the rule, or Cancel.
            - After a game update, **Refresh game data** (Settings) rebuilds every rule mod from the new data by itself (a mod open in the mod editor waits for its own Apply).
            - A Quality of life mod builds its rows the first time it opens. Its rules include ones you can turn on, among them **Cave sickness chance set to 5%** (25% in the game).

            **Sharing your mod**

            Right-click a mod you made in Starlink in the Library and pick **Export for sharing…** to save it as one .EXMODZ file. Give it to friends or post it on Nexus: they add it with **Import** in Starlink or in the classic mod manager. Save it outside the Library folder, such as in Documents. Only the mod goes in, not your notes or Library settings. Mods from Nexus or the IMM Database can't be exported this way, since they aren't yours. If your mod uses parts of someone else's mod, give them credit and check they allow it before you post it.

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

            - **Old copies of game data**: sure fixes. The game's current value stays. A hand-made mod lists only the fields its author changed, so a shorter list or a deleted field there is the author's choice (an emptied fuel list is how "torches need no fuel" works) and is never called old; only a prebuilt pak, or a table a mod copied wholesale, can carry old copies that way. An emptied list (for example a workshop item's cost list set to []) is the mod author's change, in prebuilt paks too, so "make everything free" mods work. In a hand-made mod whose table looks copied from an older game, shape-only findings and old values are listed with a suggested fix but never fixed automatically.
            - **Already the game's value**: changes that do nothing; taking them out is tidying up.
            - **Probably old**: other changes on rows the mod copied wholesale. The suggestion is the game's value.
            - **Lists both changed**: the mod added to a list and the game has entries the mod lacks. **Merge** keeps both.
            - **Rows the game renamed**: offered when it's certain. A rename is fixed automatically only when the game's history shows it happened in one update; otherwise it is only suggested. A mod's own new rows (a new item's rows, anything the mod links to) are never renamed, and a check never changes a mod by itself.
            - **Game files it can't find**: the mod's own cooked files (meshes, materials, blueprints) use game files where the game doesn't have them any more, so that part of the mod is broken (a missing look or sound, or the game stopping when it loads something built on it). Where the game only moved the file (same name and contents, another folder), the sure fix points the mod to the new place and changes nothing else; anything else is only listed, for the mod's author to update.
            - **Parts the game took out**: the game still has the file, but no longer has parts the mod uses (a component of a game blueprint). Only listed; **Leave them all as they are** stops them being listed.
            - **Old game rows**: whole rows a prebuilt pak still carries from an older game version, which the game has since removed (ISL knows every row the game has had since Week 9, from the public history of the game's data, plus each Refresh game data you run; two of your mods carrying identical rows counts as evidence too). Nothing uses them, so they do nothing; removing them is only tidying, and is offered, never done by itself.
            - **Tables the game doesn't have**: the mod names a table the game has no file for, so the merge skips all its rows. Usually the folder part of the name is missing; when the game has that table in one place, moving the rows there is a sure fix.
            - **Fields the game doesn't have**: a typo, or a field a game update renamed or removed, so the change does nothing. When only the case differs, matching the game's spelling is a sure fix. A close name is offered as a suggestion; taking the field out changes nothing in game.
            - **Values of the wrong kind**: text where the game has a number, say; the game may ignore it. Only listed.
            - **Text under another row's key**: a row copied without giving its name or description its own text key, so the game can show one text in place of the other. **Give it its own key** keeps the words.
            - **Probably misspelt row names**: a row name no version of the game ever had, a letter or two from one of its rows with the same fields. A misspelt name doesn't change the game's row, it adds a new row nothing uses. **Rename** it if the game's row is what was meant.
            - **New rows the game can't use**: rows added to a table the game has built into its code (D_CharacterGrowth). Tested in game: tames pointed at such a row showed level 0 and "-1" XP. When a dedicated server's log says the same at its start, that's quoted as proof. The mod editor won't add a new row there.
            - **Changes the game ignores**: tested in game, these do nothing where the mod puts them: an aura stat on a placed item (beds, heaters: only characters get auras from stats) and a spoil-rate stat on a bag or storage. Only listed; **Add stat** refuses them in both editors.
            - **New items nobody can get**: a new item with no item template, or one nothing in the mod hands out (no recipe, workshop item, reward or loot). Unless another mod does, players can't get it. Only listed.

            Every one shows the mod's value next to the game's, with the suggested fix already chosen; change any, or set a whole kind at once. **Apply** backs the mod up first (**Restore latest backup** undoes it). The author's own changes are never listed or touched.

            Your choices are remembered per mod, so you only decide once: a value you keep isn't asked about again (until the mod or the game changes it), and any other choice is made by itself from then on, by **Fix sure problems in every mod** and after every refresh of a prebuilt pak. **Forget my earlier choices** in the fix window starts afresh.

            **Fix sure problems in every mod** (Check ▾) makes only the sure fixes in every flagged mod at once. Prebuilt paks get the sure fixes by themselves each time they're converted, including after every **Refresh game data**. **Weekly Changes** shows what the game itself changed.
            """),

        new("Keyboard shortcuts", """
            # Keyboard shortcuts

            Anywhere in the main window:

            - **Ctrl+1** … **Ctrl+9** — go to the menu's pages in order (Database, Downloads, Library, Editor, Merge & Install, Saves, Server, Map, Weekly Changes); **Ctrl+0** — Help; **Ctrl+,** — Settings. With the menu folded to icons, hover an icon to see its page and shortcut
            - **Ctrl+F** — jump to the page's search or filter box

            The **Theme** button at the bottom of the menu changes the colours: each click goes to the next theme (Icarus, Dark, Light, Custom). **Play**, **Mods**, **UE4SS**, **Game** and **Server** at the top right start the game or open those folders (if a folder isn't there, like UE4SS before it's installed, the bell says so). After a click, Play says **Starting…** until Icarus opens (clicking again does nothing, so the game is never started twice; after 2 minutes with no game it says Play again). If Windows can't ask Steam to start the game, Play stays and the bell says why. While Icarus runs it shows a green dot and **Running**, and a click brings the game's window to the front.

            If you ticked or unticked mods and haven't done **Merge & Install** yet (or UE4SS changes aren't applied), **Play** asks first: go and put them in, play without them, or play and don't ask again (**Settings › Appearance › Messages** turns the question back on). If **Install** says Icarus is running, **Install when Icarus closes** waits for the game to close, then asks before it copies the pack you built.

            If Icarus crashes while Starlink is open, Starlink says so as soon as the game closes: the game's error, whether the crash went through UE4SS (then check your UE4SS mods first), and which UE4SS mods wrote errors to UE4SS.log that session. A graphics driver crash says so instead, since mods don't cause those. Pick **Find the broken mod**, **Copy crash report** (for a bug report; it never has your account ids) or **Open crash folder**. Each crash is told once, so it doesn't come back the next time you start Starlink.

            Inside the mod editor window:

            - **Ctrl+S** — save
            - **Ctrl+F** — focus the item filter box
            - **F3** — jump to the next item in the (possibly filtered) list
            - **Ctrl+D** — duplicate the selected item's fields under a new name
            - **Delete** — take the selected row(s) out of the mod (in the item list; Ctrl+Z puts them back)
            - **Ctrl+Z** — undo the last action (adding rows, a mass edit, a wizard's new item, applying rules); **Ctrl+Y** — redo

            On the Editor page, **Ctrl+S** saves. In the Library list: **Space** turns the selected mods on or off, **Delete** deletes the selected mod (or the one selected UE4SS mod), **Enter** opens its details, **F2** renames it, **Ctrl+A** selects every mod shown in that list, **Esc** clears the selection.

            In the mod editor, select two or more items (Ctrl/Shift-click) to reveal a mass-edit bar: set one field across every selected item at once. On the Editor page, tick items in the list instead and use **Change N ticked…**.
            """),

        new("Safety & backups", """
            # Safety & backups

            A few things this app is deliberately careful about:

            - **Install** (the one action that writes into your real game folder) always asks for confirmation, and always backs up whatever pak is already there first (last 3 kept, or what **Settings › Maintenance › Backups to keep** says). Find the broken mod backs up only before its first round, so your real install is never pushed out by the search's test paks.
            - The **UE4SS loader install/update/uninstall**, the **nxm:// protocol registration**, and **Update now** (this app updating itself) all work the same way — each touches something more sensitive than a normal mod folder, so each asks first too.
            - Every Install re-reads the pak it just built and confirms every file it staged for packing is actually retrievable from it — not just trusting how many files it handed to UnrealPak.exe. If UnrealPak itself ever silently dropped something, this is what would catch it, and it shows up under **Build warnings and notes** on Merge & Install rather than as a quietly incomplete pak.
            - Profiles are backed up on **Save profile**, **Suggest order** and **Clear** (the automatic save of each tick isn't) — **Profile ▾ › Restore profile backup** in Merge & Install brings the last one back.
            - Starlink won't unpack a pak if a file name in it would land outside its folder, or if it would unpack to more than 8 GB or more than your drive has free (keeping 1 GB spare). The message names the pak; don't use that pak and tell its author.
            - Your library is just the **Extracted_Mods** folder plus a small per-mod notes/pin sidecar — nothing about it is locked into a database only this app can read. If you ever stop using this app, your mods are still just files.
            - If the app ever hits an unexpected error — on the UI thread, during background startup, or inside an action like Install, FTP, downloads or save editing — it writes a crash report (the full exception, your app/OS version, and what you'd been doing right before it) to the **Logs** folder automatically.
            - If something goes wrong, **Export diagnostics zip…** (Settings) is the fastest way to get logs into a bug report.
            - Backups add up over time. **Settings › Maintenance › Clean up backups** shows how much space they take and keeps only the newest of each mod, the merged pak and each server (2 of each mod and server and 3 merged pak installs at first: change them under **Backups to keep** there). The newest backup of anything is never deleted.
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
