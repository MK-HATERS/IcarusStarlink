using IcarusStarlink.Core.Library;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// Builds a new mod's starting ExmodPackage from one of the real templates in ModTemplate — Blank
/// matches "New mod…"'s original behavior; the other five are the actual JSON from classic IMM's
/// own real template sources (Jimk72's own GitHub — Blank_Craftable_Item.EXMODZ/
/// Blank_Consumable_Item.EXMODZ, plus BuildingPiece/ElectricGenerator/WaterPump from that same
/// tool's own bundled NewItemOptions.json), not invented, with every occurrence of each template's
/// own placeholder identifier ("TempName") replaced by a sanitized version of whatever name the
/// user actually typed. A single literal text replace before parsing is enough — "TempName" never
/// appears as a real substring of anything else in any of these templates, and it's used
/// consistently for every RowName, cross-reference, and NSLOCTEXT key/value throughout, exactly
/// the way the templates' own author intended a mod author to search-and-replace it by hand.
/// </summary>
public static class ModTemplateContent
{
    public static ExmodPackage Create(ModTemplate template, string name, string author)
    {
        var fileName = name.Replace(' ', '_');

        if (template == ModTemplate.QualityOfLife)
        {
            return new ExmodPackage
            {
                Name = name,
                Author = author,
                Version = "1.0",
                Description = "Quality-of-life tweaks. Turn rules on or off and change their numbers in the editor's Scale rules view, then Apply.",
                FileName = fileName,
                Rows = [new ExmodFileRow { CurrentFile = "EndOfMod" }],
                ScaleRules = QualityOfLifeRules(),
            };
        }

        if (template == ModTemplate.MapIcon)
        {
            var mapIcon = ExmodJson.Parse(TemplateText(ModTemplate.MapIcon).Replace("TempName", SanitizeIdentifier(name)).Replace("TempLabel", name.Replace("\\", "").Replace("\"", "")));
            mapIcon.Name = name;
            mapIcon.Author = author;
            mapIcon.FileName = fileName;
            return mapIcon;
        }

        if (template == ModTemplate.Blank || ItemPresetFor(template) is not null)
        {
            return new ExmodPackage { Name = name, Author = author, Version = "1.0", Description = "", FileName = fileName, Rows = [] };
        }

        var rawTemplate = TemplateText(template);
        var identifier = SanitizeIdentifier(name);
        var package = ExmodJson.Parse(rawTemplate.Replace("TempName", identifier));
        package.Name = name;
        package.Author = author;
        package.FileName = fileName;
        return package;
    }

    /// <summary>
    /// A template's JSON, before its placeholder is replaced: Templates/&lt;name&gt;.json, embedded in
    /// this assembly (each file's text is exactly what the C# constants held before they moved).
    /// Where each comes from:
    /// </summary>
    // Modeled on the Teleporters mod's own map icon (Nexus), with the game's radar widget and
    // waypoint icon. Nothing in the data tables shows a map icon by itself — a mod's blueprint or
    // Lua script looks it up by this row name.
    //
    // Verbatim from https://github.com/Jimk72/Icarus_Software/raw/main/Blank_Craftable_Item.EXMODZ
    // (its own "Extracted Mods/Blank_Craftable_Item.EXMOD" entry) — a Campfire-based deployable
    // reusing real vanilla mesh/icon/blueprint assets as placeholders, craftable at a workbench.
    //
    // Verbatim from https://github.com/Jimk72/Icarus_Software/raw/main/Blank_Consumable_Item.EXMODZ
    // (its own "Extracted Mods/Blank_Consumable_Item.EXMOD" entry) — a Meta/consumable item reusing
    // a real vanilla crate icon, craftable at a workbench.
    //
    // Rows verbatim from https://github.com/Jimk72/Icarus_Software/raw/main/NewItemOptions.json's
    // own "Building Piece" entry (its "Add Item to Mod... premade templates" feature) — a wood
    // wall reusing the real vanilla wood-kit mesh, with its own Blueprint/D_BuildingPieces icon
    // left as a "BP_TempName"/empty placeholder for the author's own real custom art (unlike the
    // Craftable/Consumable templates above, this one doesn't have an existing vanilla actor to
    // point at instead). The wrapping name/fileName/description aren't from that source —
    // NewItemOptions.json's own entries are bare item snippets meant for an already-open mod, not
    // whole standalone packages — Create() always overwrites them from the user's own input anyway.
    //
    // One deliberate correction to the source's own real content: its Crafting recipe Outputs
    // point at a D_ItemTemplate/"TempName" row that the real NewItemOptions.json entry never
    // creates — the other three templates (Craftable/Consumable/WaterPump) all create this row via
    // their own Items-D_ItemTemplate.json entry, so its absence here reads as a real gap in the
    // source rather than a deliberate omission. Added in the file, matching those three templates' own
    // proven-working shape exactly, so the crafted output actually resolves in-game.
    //
    // Rows verbatim from https://github.com/Jimk72/Icarus_Software/raw/main/NewItemOptions.json's
    // own "Electric Generator" entry — a wind-generator-style deployable producing energy, reusing
    // that real vanilla deployable actor's own DeployableActor/ItemMesh Blueprint paths pattern
    // (with "TempName" standing in for the author's own real custom asset name).
    //
    // Same deliberate correction as Building Piece's above: the real
    // source's Crafting recipe Outputs point at a D_ItemTemplate/"TempName" row it never creates.
    // Added in the file to match Craftable/Consumable/WaterPump's own proven-working shape.
    //
    // Rows verbatim from https://github.com/Jimk72/Icarus_Software/raw/main/NewItemOptions.json's
    // own "Water Pump" entry — a deployable water generator reusing the real vanilla machining-kit
    // mesh, wired up as a resource-network water source (same "TempName"-as-custom-Blueprint-
    // placeholder convention as Electric Generator/Building Piece above).
    internal static string TemplateText(ModTemplate template) => template switch
    {
        ModTemplate.MapIcon => ReadTemplate("MapIcon"),
        ModTemplate.CraftableOrDeployableItem => ReadTemplate("CraftableOrDeployableItem"),
        ModTemplate.ConsumableItem => ReadTemplate("ConsumableItem"),
        ModTemplate.BuildingPiece => ReadTemplate("BuildingPiece"),
        ModTemplate.ElectricGenerator => ReadTemplate("ElectricGenerator"),
        ModTemplate.WaterPump => ReadTemplate("WaterPump"),
        _ => throw new ArgumentOutOfRangeException(nameof(template), template, "Unknown mod template."),
    };

    /// <summary>The trailing newline an editor adds to a file isn't part of the template.</summary>
    private static string ReadTemplate(string name)
    {
        using var stream = typeof(ModTemplateContent).Assembly.GetManifestResourceStream($"Templates.{name}.json")
            ?? throw new InvalidOperationException($"The {name} template is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().TrimEnd('\r', '\n');
    }

    /// <summary>
    /// What the new-item wizard starts with for an item template — the game item to start from (the
    /// user can pick another), how it's obtained, and where. Each source is a real item of that kind
    /// and each tree a real workshop tab (checked against current game data).
    /// </summary>
    public static NewItemPreset? ItemPresetFor(ModTemplate template) => template switch
    {
        ModTemplate.Envirosuit => new NewItemPreset("Envirosuit", ObtainMethod.Workshop, WorkshopTree: "Workshop_Envirosuits"),
        ModTemplate.EnvirosuitModule => new NewItemPreset("Meta_Module_Movement", ObtainMethod.Workshop, WorkshopTree: "Workshop_Modules"),
        ModTemplate.ConsumableWithBuff => new NewItemPreset("Food_Bread", ObtainMethod.Recipe, CopyBuff: true),
        ModTemplate.WorkshopItem => new NewItemPreset(null, ObtainMethod.Workshop),
        _ => null,
    };

    /// <summary>
    /// The Quality of life template's rules. Every table and field path is one a real QoL mod
    /// changes (checked against Deyvid's All-in-One, Nexus 317, and current game data). The
    /// factors are gentler than that mod's; the less universal ones start off.
    /// </summary>
    public static List<ScaleRule> QualityOfLifeRules() =>
    [
        Qol("Stack sizes ×4", "Traits-D_Itemable.json", "MaxStack", 4, includeInherited: false),
        Qol("Item weight halved", "Traits-D_Itemable.json", "Weight", 0.5),
        Qol("Crafting time halved", "Crafting-D_ProcessorRecipes.json", "RequiredMillijoules", 0.5),
        Qol("Taming time halved", "AI-D_Tames.json", "TameDurationInSeconds", 0.5),
        Qol("Crop growth time halved", "Farming-D_FarmingGrowthStates.json", "TimeToNextState", 0.5),
        Qol("Food spoils half as fast", "Traits-D_Decayable.json", "SpoilTime", 2),
        Qol("Food buffs last twice as long", "Traits-D_Consumable.json", "Modifier.ModifierLifetime", 2),
        // Storage only — the same kinds of inventory the real mod doubles; not the quickbar, backpack, processors or fuel slots.
        Qol("Storage container slots ×2", "Inventory-D_InventoryInfo.json", "StartingSlots", 2,
            filter: "Storage, Container, Crate, Cupboard, IceBox, Refrigerator, Freeze, Trough, Pouch, Tackle, Silo, Rack, Sack, Bag, Cabinet, DropShip, ArmourStand, Prospect"),
        Qol("Backpack slots ×2", "Traits-D_Equippable.json", "GrantedStats.(Value=\"BaseBackpackSlots_+\")", 2),
        Qol("You get hungry 25% slower", "Stats-D_CharacterStartingStats.json", "StatsGranted.(Value=\"BaseFoodConsumptionPerHour_+\")", 0.75, enabled: false),
        Qol("You get thirsty 25% slower", "Stats-D_CharacterStartingStats.json", "StatsGranted.(Value=\"BaseWaterConsumptionPerHour_+\")", 0.75, enabled: false),
        Qol("Mining ore deposits faster", "World-D_OreDeposit.json", "MiningTimeSeconds", 0.5, enabled: false),
        Qol("Workshop replication costs -30%", "MetaWorkshop-D_WorkshopItems.json", "ReplicationCost[].Amount", 0.7, enabled: false),
        Qol("Workshop research costs -30%", "MetaWorkshop-D_WorkshopItems.json", "ResearchCost[].Amount", 0.7, enabled: false),
        Qol("Mission rewards ×1.5", "Factions-D_FactionMissions.json", "CurrencyRewarded[].Amount", 1.5, enabled: false),
        Qol("Armour stats ×1.25", "Traits-D_Armour.json", "ArmourStats.*", 1.25, enabled: false),
        // Cave sickness is the Pneumonia roll in D_AfflictionChance's Underground_Escalation row (25% in the game) — what NoCaveSickness changes.
        Qol("Cave sickness chance set to 5%", "Modifiers-D_AfflictionChance.json", "ChanceInPercent", 5, enabled: false, filter: "Underground_Escalation", set: true),
    ];

    private static ScaleRule Qol(string label, string file, string field, double value, bool enabled = true, bool includeInherited = true, string? filter = null, bool set = false) =>
        new()
        {
            Label = label, CurrentFile = file, Field = field, Value = value, Enabled = enabled, IncludeInherited = includeInherited, RowFilter = filter,
            Operation = set ? ScaleOperation.Set : ScaleOperation.Multiply,
        };

    /// <summary>RowNames/cross-references throughout both templates are plain identifiers with no
    /// spaces or punctuation — stripping anything that isn't a letter/digit/underscore keeps the
    /// substituted result matching that same real-world convention, even though this app's own
    /// EXMOD parser itself is more lenient (EnsurePlainIdentifier only rejects blank/control
    /// characters).</summary>
    private static string SanitizeIdentifier(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var sanitized = System.Text.RegularExpressions.Regex.Replace(new string(chars), "_{2,}", "_").Trim('_');
        return sanitized.Length > 0 ? sanitized : "NewItem";
    }
}
