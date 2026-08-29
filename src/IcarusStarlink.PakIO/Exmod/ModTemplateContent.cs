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

        if (template == ModTemplate.Blank)
        {
            return new ExmodPackage { Name = name, Author = author, Version = "1.0", Description = "", FileName = fileName, Rows = [] };
        }

        var rawTemplate = template switch
        {
            ModTemplate.CraftableOrDeployableItem => CraftableOrDeployableItemTemplate,
            ModTemplate.ConsumableItem => ConsumableItemTemplate,
            ModTemplate.BuildingPiece => BuildingPieceTemplate,
            ModTemplate.ElectricGenerator => ElectricGeneratorTemplate,
            ModTemplate.WaterPump => WaterPumpTemplate,
            _ => throw new ArgumentOutOfRangeException(nameof(template), template, "Unknown mod template."),
        };

        var identifier = SanitizeIdentifier(name);
        var package = ExmodJson.Parse(rawTemplate.Replace("TempName", identifier));
        package.Name = name;
        package.Author = author;
        package.FileName = fileName;
        return package;
    }

    /// <summary>RowNames/cross-references throughout both templates are plain identifiers with no
    /// spaces or punctuation — stripping anything that isn't a letter/digit/underscore keeps the
    /// substituted result matching that same real-world convention, even though this app's own
    /// EXMOD parser itself is more lenient (EnsurePlainIdentifier only rejects blank/control
    /// characters).</summary>
    private static string SanitizeIdentifier(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var sanitized = new string(chars).Trim('_');
        return sanitized.Length > 0 ? sanitized : "NewItem";
    }

    // Verbatim from https://github.com/Jimk72/Icarus_Software/raw/main/Blank_Craftable_Item.EXMODZ
    // (its own "Extracted Mods/Blank_Craftable_Item.EXMOD" entry) — a Campfire-based deployable
    // reusing real vanilla mesh/icon/blueprint assets as placeholders, craftable at a workbench.
    private const string CraftableOrDeployableItemTemplate = """
        {
            "name": "Blank Craftable Item",
            "author": "Jimk72",
            "version": "1.0",
            "description": "",
            "fileName": "Blank_Craftable_Item",
            "readmeURL": "",
            "imageURL": "",
            "Level2": "True",
            "Rows": [
                {
                    "CurrentFile": "Crafting-D_ProcessorRecipes.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "Requirement": {
                                "RowName": "None",
                                "DataTableName": "D_Talents"
                            },
                            "RecipeSets": [
                                {
                                    "RowName": "Character",
                                    "DataTableName": "D_RecipeSets"
                                }
                            ],
                            "ResourceCostMultipliers": [],
                            "Inputs": [
                                {
                                    "Element": {
                                        "RowName": "Wood",
                                        "DataTableName": "D_ItemsStatic"
                                    },
                                    "Count": 0,
                                    "DynamicProperties": []
                                }
                            ],
                            "Outputs": [
                                {
                                    "Element": {
                                        "RowName": "TempName",
                                        "DataTableName": "D_ItemTemplate"
                                    },
                                    "Count": 1,
                                    "DynamicProperties": []
                                }
                            ],
                            "Audio": {
                                "RowName": "Default"
                            }
                        }
                    ]
                },
                {
                    "CurrentFile": "Deployables-D_DeployableSetup.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "DeployableBlueprint": "/Game/BP/Objects/World/Items/Deployables/Campfire/BP_Campfire.BP_Campfire_C",
                            "PreviewStaticMesh": "/Game/ASS/DEP/SM_DEP_Campfire.SM_DEP_Campfire",
                            "DeployedSound": "/Game/FMOD/Events/SFX/D_Buildables/SFX_BLD_STONE_SMALL.SFX_BLD_STONE_SMALL",
                            "SupportsCustomRotation": false,
                            "bCanAffectNavigation": false,
                            "MaxRestackingAmount": 1
                        }
                    ]
                },
                {
                    "CurrentFile": "Items-D_ItemsStatic.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "Meshable": {
                                "RowName": "Mesh_TempName"
                            },
                            "Itemable": {
                                "RowName": "Item_TempName"
                            },
                            "Interactable": {
                                "RowName": "Deployable_NoInteract"
                            },
                            "Focusable": {
                                "RowName": "Focusable_1H"
                            },
                            "Highlightable": {
                                "RowName": "Generic"
                            },
                            "Actionable": {
                                "RowName": "Deployable"
                            },
                            "Usable": {
                                "RowName": "Place"
                            },
                            "Deployable": {
                                "RowName": "TempName"
                            },
                            "Durable": {
                                "RowName": "Deployable_100"
                            },
                            "Floatable": {
                                "RowName": "Items"
                            },
                            "Decayable": {
                                "RowName": "Decay_Deployable"
                            },
                            "Weight": {
                                "RowName": "LightDeployable"
                            },
                            "Audio": {
                                "RowName": "Default"
                            },
                            "Generated_Tags": {
                                "GameplayTags": [
                                    {
                                        "TagName": "Traits.Meshable"
                                    },
                                    {
                                        "TagName": "Traits.Itemable"
                                    },
                                    {
                                        "TagName": "Traits.Interactable"
                                    },
                                    {
                                        "TagName": "Traits.Highlightable"
                                    },
                                    {
                                        "TagName": "Traits.Actionable"
                                    },
                                    {
                                        "TagName": "Traits.Usable"
                                    },
                                    {
                                        "TagName": "Traits.Deployable"
                                    },
                                    {
                                        "TagName": "Traits.Durable"
                                    },
                                    {
                                        "TagName": "Traits.Floatable"
                                    }
                                ],
                                "ParentTags": []
                            }
                        }
                    ]
                },
                {
                    "CurrentFile": "Items-D_ItemTemplate.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "ItemStaticData": {
                                "RowName": "TempName"
                            }
                        }
                    ]
                },
                {
                    "CurrentFile": "Traits-D_Deployable.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "Variants": [
                                {
                                    "RowName": "TempName",
                                    "DataTableName": "D_DeployableSetup"
                                }
                            ],
                            "EffectedByWeather": false
                        }
                    ]
                },
                {
                    "CurrentFile": "Traits-D_Itemable.json",
                    "File_Items": [
                        {
                            "Name": "Item_TempName",
                            "DisplayName": "NSLOCTEXT(\"D_Itemable\", \"Item_TempName-DisplayName\", \"TempName\")",
                            "Icon": "/Game/Assets/2DArt/UI/Items/Item_Icons/Deployables/ITEM_Campfire.ITEM_Campfire",
                            "Description": "NSLOCTEXT(\"D_Itemable\", \"Item_TempName-Description\", \"A short description of item.\")",
                            "FlavorText": "NSLOCTEXT(\"D_Itemable\", \"Item_TempName-FlavorText\", \"A more elaborate description of item.\")",
                            "Weight": 100
                        }
                    ]
                },
                {
                    "CurrentFile": "Traits-D_Meshable.json",
                    "File_Items": [
                        {
                            "Name": "Mesh_TempName",
                            "ItemMesh": "/Game/ASS/KIT/SM_KIT_Person.SM_KIT_Person",
                            "EquipHandMesh": "/Game/Assets/3DArt/Interactables/Items/DummyCube/SK_dummy_cube.SK_dummy_cube",
                            "DeployableActor": "/Game/BP/Objects/World/Items/Deployables/Campfire/BP_Campfire.BP_Campfire_C"
                        }
                    ]
                },
                {
                    "CurrentFile": "EndOfMod"
                }
            ]
        }
        """;

    // Verbatim from https://github.com/Jimk72/Icarus_Software/raw/main/Blank_Consumable_Item.EXMODZ
    // (its own "Extracted Mods/Blank_Consumable_Item.EXMOD" entry) — a Meta/consumable item reusing
    // a real vanilla crate icon, craftable at a workbench.
    private const string ConsumableItemTemplate = """
        {
            "name": "Blank Consumable Item",
            "author": "Jimk72",
            "version": "1.0",
            "description": "",
            "fileName": "Blank_Consumable_Item",
            "readmeURL": "",
            "imageURL": "",
            "Level2": "True",
            "Rows": [
                {
                    "CurrentFile": "Crafting-D_ProcessorRecipes.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "Requirement": {
                                "RowName": "Stone_Pickaxe"
                            },
                            "RequiredMillijoules": 1,
                            "RecipeSets": [
                                {
                                    "RowName": "Character",
                                    "DataTableName": "D_RecipeSets"
                                }
                            ],
                            "ResourceCostMultipliers": [],
                            "Inputs": [
                                {
                                    "Element": {
                                        "RowName": "Wood",
                                        "DataTableName": "D_ItemsStatic"
                                    },
                                    "Count": 0,
                                    "DynamicProperties": []
                                }
                            ],
                            "Outputs": [
                                {
                                    "Element": {
                                        "RowName": "TempName",
                                        "DataTableName": "D_ItemTemplate"
                                    },
                                    "Count": 1,
                                    "DynamicProperties": []
                                }
                            ],
                            "Audio": {
                                "RowName": "Default"
                            }
                        }
                    ]
                },
                {
                    "CurrentFile": "Items-D_ItemsStatic.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "Meshable": {
                                "RowName": "Mesh_TempName"
                            },
                            "Itemable": {
                                "RowName": "Item_TempName"
                            },
                            "Interactable": {
                                "RowName": "Item"
                            },
                            "Highlightable": {
                                "RowName": "Generic"
                            },
                            "Actionable": {
                                "RowName": "Generic_Consumable"
                            },
                            "Consumable": {
                                "RowName": "TempName"
                            },
                            "Usable": {
                                "RowName": "Consume_Stack_FoodWater"
                            },
                            "Floatable": {
                                "RowName": "LightItem"
                            },
                            "Decayable": {
                                "RowName": "Decay_MetaItem"
                            },
                            "Audio": {
                                "RowName": "Default"
                            },
                            "CraftingExperience": 1000,
                            "Manual_Tags": {
                                "GameplayTags": [
                                    {
                                        "TagName": "Item.Meta.Consumable"
                                    }
                                ]
                            },
                            "Generated_Tags": {
                                "GameplayTags": [
                                    {
                                        "TagName": "Item.Meta.Consumable"
                                    },
                                    {
                                        "TagName": "Traits.Meshable"
                                    },
                                    {
                                        "TagName": "Traits.Itemable"
                                    },
                                    {
                                        "TagName": "Traits.Interactable"
                                    },
                                    {
                                        "TagName": "Traits.Highlightable"
                                    },
                                    {
                                        "TagName": "Traits.Consumable"
                                    },
                                    {
                                        "TagName": "Traits.Usable"
                                    },
                                    {
                                        "TagName": "Traits.Floatable"
                                    }
                                ],
                                "ParentTags": []
                            }
                        }
                    ]
                },
                {
                    "CurrentFile": "Items-D_ItemTemplate.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "ItemStaticData": {
                                "RowName": "TempName"
                            }
                        }
                    ]
                },
                {
                    "CurrentFile": "Traits-D_Consumable.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "Modifier": {
                                "ModifierLifetime": 0
                            },
                            "Byproducts": [
                                {
                                    "RowName": "Carbon_Chest",
                                    "DataTableName": "D_ItemTemplate"
                                },
                                {
                                    "RowName": "Carbon_Arms",
                                    "DataTableName": "D_ItemTemplate"
                                },
                                {
                                    "RowName": "Carbon_Legs",
                                    "DataTableName": "D_ItemTemplate"
                                },
                                {
                                    "RowName": "Carbon_Feet",
                                    "DataTableName": "D_ItemTemplate"
                                },
                                {
                                    "RowName": "Carbon_Head",
                                    "DataTableName": "D_ItemTemplate"
                                },
                                {
                                    "RowName": "Compound_Bow",
                                    "DataTableName": "D_ItemTemplate"
                                },
                                {
                                    "RowName": "Meta_Knife",
                                    "DataTableName": "D_ItemTemplate"
                                },
                                {
                                    "RowName": "Meta_Pickaxe",
                                    "DataTableName": "D_ItemTemplate"
                                },
                                {
                                    "RowName": "Meta_Axe",
                                    "DataTableName": "D_ItemTemplate"
                                },
                                {
                                    "RowName": "Ammo_Rifle_Round_x100",
                                    "DataTableName": "D_ItemTemplate"
                                },
                                {
                                    "RowName": "Composite_Arrow_x100",
                                    "DataTableName": "D_ItemTemplate"
                                },
                                {
                                    "RowName": "Basic_Quiver",
                                    "DataTableName": "D_ItemTemplate"
                                },
                                {
                                    "RowName": "Meta_Canteen_Shengong",
                                    "DataTableName": "D_ItemTemplate"
                                },
                                {
                                    "RowName": "Oxygen_Tank",
                                    "DataTableName": "D_ItemTemplate"
                                },
                                {
                                    "RowName": "Meta_Module_Movement",
                                    "DataTableName": "D_ItemTemplate"
                                },
                                {
                                    "RowName": "Meta_Module_Inventory_Slots_2",
                                    "DataTableName": "D_ItemTemplate"
                                },
                                {
                                    "RowName": "Flashlight",
                                    "DataTableName": "D_ItemTemplate"
                                }
                            ]
                        }
                    ]
                },
                {
                    "CurrentFile": "Traits-D_Itemable.json",
                    "File_Items": [
                        {
                            "Name": "Item_TempName",
                            "DisplayName": "NSLOCTEXT(\"D_Itemable\", \"Item_TempName-DisplayName\", \"TempName\")",
                            "Icon": "/Game/Assets/2DArt/UI/Items/Item_Icons/Deployables/ITEM_Metal_Crate_Small.ITEM_Metal_Crate_Small",
                            "Description": "NSLOCTEXT(\"D_Itemable\", \"Item_TempName-Description\", \"Short Description.\")",
                            "FlavorText": "NSLOCTEXT(\"D_Itemable\", \"Item_TempName-FlavorText\", \"More details about Item!\")",
                            "Weight": 10,
                            "MaxStack": 100
                        }
                    ]
                },
                {
                    "CurrentFile": "EndOfMod"
                }
            ]
        }
        """;

    // Rows verbatim from https://github.com/Jimk72/Icarus_Software/raw/main/NewItemOptions.json's
    // own "Building Piece" entry (its "Add Item to Mod... premade templates" feature) — a wood
    // wall reusing the real vanilla wood-kit mesh, with its own Blueprint/D_BuildingPieces icon
    // left as a "BP_TempName"/empty placeholder for the author's own real custom art (unlike the
    // Craftable/Consumable templates above, this one doesn't have an existing vanilla actor to
    // point at instead). The wrapping name/fileName/description below aren't from that source —
    // NewItemOptions.json's own entries are bare item snippets meant for an already-open mod, not
    // whole standalone packages — Create() always overwrites them from the user's own input anyway.
    private const string BuildingPieceTemplate = """
        {
            "name": "TempName",
            "author": "",
            "version": "1.0",
            "description": "",
            "fileName": "TempName",
            "Rows": [
                {
                    "CurrentFile": "Crafting-D_ProcessorRecipes.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "RecipeSets": [
                                {
                                    "RowName": "Character",
                                    "DataTableName": "D_RecipeSets"
                                }
                            ],
                            "ResourceCostMultipliers": [
                                {
                                    "Value": "BaseWoodBuildingCraftingCost_+%"
                                }
                            ],
                            "Inputs": [
                                {
                                    "Element": {
                                        "RowName": "Fiber",
                                        "DataTableName": "D_ItemsStatic"
                                    },
                                    "Count": 0,
                                    "DynamicProperties": []
                                }
                            ],
                            "Outputs": [
                                {
                                    "Element": {
                                        "RowName": "TempName",
                                        "DataTableName": "D_ItemTemplate"
                                    },
                                    "Count": 1,
                                    "DynamicProperties": [],
                                    "Alterations": []
                                }
                            ],
                            "Audio": {
                                "RowName": "Default"
                            }
                        }
                    ]
                },
                {
                    "CurrentFile": "Items-D_ItemsStatic.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "Meshable": {
                                "RowName": "Mesh_Wood_Kit"
                            },
                            "Itemable": {
                                "RowName": "Item_TempName"
                            },
                            "Interactable": {
                                "RowName": "Item"
                            },
                            "Hitable": {
                                "RowName": "Building"
                            },
                            "Focusable": {
                                "RowName": "Focusable_1H"
                            },
                            "Highlightable": {
                                "RowName": "Generic"
                            },
                            "Actionable": {
                                "RowName": "Building"
                            },
                            "Buildable": {
                                "RowName": "TempName"
                            },
                            "Usable": {
                                "RowName": "Place"
                            },
                            "Durable": {
                                "RowName": "ReinforcedWood_Building"
                            },
                            "Decayable": {
                                "RowName": "Decay_General"
                            },
                            "Flammable": {
                                "RowName": "Flammable_Building_Wood"
                            },
                            "Audio": {
                                "RowName": "WoodDeployable"
                            },
                            "Manual_Tags": {
                                "GameplayTags": [
                                    {
                                        "TagName": "Building.Wood"
                                    },
                                    {
                                        "TagName": "Audio.Shelter"
                                    }
                                ]
                            },
                            "Generated_Tags": {
                                "GameplayTags": [
                                    {
                                        "TagName": "Building.Wood"
                                    },
                                    {
                                        "TagName": "Audio.Shelter"
                                    },
                                    {
                                        "TagName": "Traits.Meshable"
                                    },
                                    {
                                        "TagName": "Traits.Itemable"
                                    },
                                    {
                                        "TagName": "Traits.Interactable"
                                    },
                                    {
                                        "TagName": "Traits.Hitable"
                                    },
                                    {
                                        "TagName": "Traits.Highlightable"
                                    },
                                    {
                                        "TagName": "Traits.Actionable"
                                    },
                                    {
                                        "TagName": "Traits.Buildable"
                                    },
                                    {
                                        "TagName": "Traits.Usable"
                                    },
                                    {
                                        "TagName": "Traits.Durable"
                                    },
                                    {
                                        "TagName": "Traits.Flammable"
                                    }
                                ],
                                "ParentTags": []
                            }
                        }
                    ]
                },
                {
                    "CurrentFile": "Traits-D_Itemable.json",
                    "File_Items": [
                        {
                            "Name": "Item_TempName",
                            "DisplayName": "NSLOCTEXT(\"D_Itemable\", \"Item_TempName-DisplayName\", \"TempName\")",
                            "Icon": "/Game/Assets/2DArt/UI/Items/Item_Icons/Buildables/ITEM_TempName_0.ITEM_TempName_0",
                            "Description": "NSLOCTEXT(\"D_Itemable\", \"Item_TempName-Description\", \"A wooden TempName (Hold 'R' for variants).\")",
                            "FlavorText": "NSLOCTEXT(\"D_Itemable\", \"Item_TempName-FlavorText\", \"Wood structures withstand light storms without damage. Can be repaired with a repair hammer.\")",
                            "Weight": 500,
                            "MaxStack": 20
                        }
                    ]
                },
                {
                    "CurrentFile": "Traits-D_Buildable.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "Stability": {
                                "RowName": "Wood_General"
                            },
                            "Type": {
                                "RowName": "Wood"
                            },
                            "PieceType": "Wall",
                            "Variations": [
                                {
                                    "Requirement": {
                                        "RowName": "None",
                                        "DataTableName": "D_Talents"
                                    },
                                    "Piece": {
                                        "RowName": "TempName",
                                        "DataTableName": "D_BuildingPieces"
                                    }
                                }
                            ]
                        }
                    ]
                },
                {
                    "CurrentFile": "Building-D_BuildingPieces.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "Type": {
                                "RowName": "Wall_Solid"
                            },
                            "Icon": "",
                            "Blueprint": "/Game/BP/Building/Wood/BP_TempName.BP_TempName_C",
                            "Audio": {
                                "RowName": "Wood"
                            }
                        }
                    ]
                },
                {
                    "CurrentFile": "EndOfMod"
                }
            ]
        }
        """;

    // Rows verbatim from https://github.com/Jimk72/Icarus_Software/raw/main/NewItemOptions.json's
    // own "Electric Generator" entry — a wind-generator-style deployable producing energy, reusing
    // that real vanilla deployable actor's own DeployableActor/ItemMesh Blueprint paths pattern
    // (with "TempName" standing in for the author's own real custom asset name).
    private const string ElectricGeneratorTemplate = """
        {
            "name": "TempName",
            "author": "",
            "version": "1.0",
            "description": "",
            "fileName": "TempName",
            "Rows": [
                {
                    "CurrentFile": "Crafting-D_ProcessorRecipes.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "RequiredMillijoules": 100,
                            "RecipeSets": [
                                {
                                    "RowName": "Character",
                                    "DataTableName": "D_RecipeSets"
                                }
                            ],
                            "Inputs": [
                                {
                                    "Element": {
                                        "RowName": "Fiber",
                                        "DataTableName": "D_ItemsStatic"
                                    },
                                    "Count": 0,
                                    "DynamicProperties": []
                                }
                            ],
                            "Outputs": [
                                {
                                    "Element": {
                                        "RowName": "TempName",
                                        "DataTableName": "D_ItemTemplate"
                                    },
                                    "Count": 1,
                                    "DynamicProperties": [],
                                    "Alterations": []
                                }
                            ],
                            "Audio": {
                                "RowName": "MachiningBench"
                            }
                        }
                    ]
                },
                {
                    "CurrentFile": "Items-D_ItemsStatic.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "Meshable": {
                                "RowName": "Mesh_TempName"
                            },
                            "Itemable": {
                                "RowName": "Item_TempName"
                            },
                            "Interactable": {
                                "RowName": "Deployable_Power_Toggle_Only"
                            },
                            "Focusable": {
                                "RowName": "Focusable_1H"
                            },
                            "Highlightable": {
                                "RowName": "Generic"
                            },
                            "Actionable": {
                                "RowName": "Deployable"
                            },
                            "Usable": {
                                "RowName": "Place"
                            },
                            "Deployable": {
                                "RowName": "TempName"
                            },
                            "Durable": {
                                "RowName": "Deployable_2500"
                            },
                            "Decayable": {
                                "RowName": "Decay_10_Minutes"
                            },
                            "Resource": {
                                "RowName": "TempName"
                            },
                            "Manual_Tags": {
                                "GameplayTags": [
                                    {
                                        "TagName": "Item.ResourceGenerator.Energy"
                                    }
                                ]
                            },
                            "Generated_Tags": {
                                "GameplayTags": [
                                    {
                                        "TagName": "Item.ResourceGenerator.Energy"
                                    },
                                    {
                                        "TagName": "Traits.Meshable"
                                    },
                                    {
                                        "TagName": "Traits.Itemable"
                                    },
                                    {
                                        "TagName": "Traits.Interactable"
                                    },
                                    {
                                        "TagName": "Traits.Highlightable"
                                    },
                                    {
                                        "TagName": "Traits.Actionable"
                                    },
                                    {
                                        "TagName": "Traits.Usable"
                                    },
                                    {
                                        "TagName": "Traits.Deployable"
                                    },
                                    {
                                        "TagName": "Traits.Durable"
                                    }
                                ],
                                "ParentTags": []
                            }
                        }
                    ]
                },
                {
                    "CurrentFile": "Traits-D_Meshable.json",
                    "File_Items": [
                        {
                            "Name": "Mesh_TempName",
                            "ItemMesh": "/Game/ASS/DEP/SM_TempName.SM_TempName",
                            "EquipHandMesh": "/Game/Assets/3DArt/Interactables/Items/DummyCube/SK_dummy_cube.SK_dummy_cube",
                            "DeployableActor": "/Game/BP/Objects/World/Items/Deployables/Wind/BP_TempName.BP_TempName_C"
                        }
                    ]
                },
                {
                    "CurrentFile": "Traits-D_Itemable.json",
                    "File_Items": [
                        {
                            "Name": "Item_TempName",
                            "DisplayName": "NSLOCTEXT(\"D_Itemable\", \"Item_TempName-DisplayName\", \"TempName\")",
                            "Icon": "/Game/Assets/2DArt/UI/Items/Item_Icons/Deployables/ITEM_TempName.ITEM_TempName",
                            "Description": "NSLOCTEXT(\"D_Itemable\", \"Item_TempName-Description\", \"A device that will generate electricity for your work.\")",
                            "FlavorText": "NSLOCTEXT(\"D_Itemable\", \"Item_TempName-FlavorText\", \"Created in IMM\")",
                            "Weight": 1000,
                            "MaxStack": 20
                        }
                    ]
                },
                {
                    "CurrentFile": "Traits-D_Deployable.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "Variants": [
                                {
                                    "RowName": "TempName",
                                    "DataTableName": "D_DeployableSetup"
                                }
                            ],
                            "EffectedByWeather": false
                        }
                    ]
                },
                {
                    "CurrentFile": "Deployables-D_DeployableSetup.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "DeployableBlueprint": "/Game/BP/Objects/World/Items/Deployables/Wind/BP_TempName.BP_TempName_C",
                            "PreviewStaticMesh": "/Game/ASS/DEP/SM_TempName.SM_TempName",
                            "DeployedSound": "/Game/FMOD/Events/SFX/D_Buildables/SFX_BLD_METAL.SFX_BLD_METAL"
                        }
                    ]
                },
                {
                    "CurrentFile": "Traits-D_Resource.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "bHasEnergyConnection": true,
                            "EnergyFlow": {
                                "RowName": "TempName"
                            }
                        }
                    ]
                },
                {
                    "CurrentFile": "Traits-D_Energy.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "FlowType": "Produce",
                            "ResourceFlowRate": 1750
                        }
                    ]
                },
                {
                    "CurrentFile": "EndOfMod"
                }
            ]
        }
        """;

    // Rows verbatim from https://github.com/Jimk72/Icarus_Software/raw/main/NewItemOptions.json's
    // own "Water Pump" entry — a deployable water generator reusing the real vanilla machining-kit
    // mesh, wired up as a resource-network water source (same "TempName"-as-custom-Blueprint-
    // placeholder convention as Electric Generator/Building Piece above).
    private const string WaterPumpTemplate = """
        {
            "name": "TempName",
            "author": "",
            "version": "1.0",
            "description": "",
            "fileName": "TempName",
            "Rows": [
                {
                    "CurrentFile": "Crafting-D_ProcessorRecipes.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "RequiredMillijoules": 1,
                            "RecipeSets": [
                                {
                                    "RowName": "Character",
                                    "DataTableName": "D_RecipeSets"
                                }
                            ],
                            "Inputs": [
                                {
                                    "Element": {
                                        "RowName": "Fiber",
                                        "DataTableName": "D_ItemsStatic"
                                    },
                                    "Count": 0,
                                    "DynamicProperties": []
                                }
                            ],
                            "Outputs": [
                                {
                                    "Element": {
                                        "RowName": "TempName",
                                        "DataTableName": "D_ItemTemplate"
                                    },
                                    "Count": 1,
                                    "DynamicProperties": [],
                                    "Alterations": []
                                }
                            ],
                            "Audio": {
                                "RowName": "MachiningBench"
                            }
                        }
                    ]
                },
                {
                    "CurrentFile": "Items-D_ItemTemplate.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "ItemStaticData": {
                                "RowName": "TempName"
                            }
                        }
                    ]
                },
                {
                    "CurrentFile": "Items-D_ItemsStatic.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "Meshable": {
                                "RowName": "Mesh_TempName"
                            },
                            "Itemable": {
                                "RowName": "Item_TempName"
                            },
                            "Interactable": {
                                "RowName": "Deployable_Power_Toggle_Only"
                            },
                            "Focusable": {
                                "RowName": "Focusable_1H"
                            },
                            "Highlightable": {
                                "RowName": "Generic"
                            },
                            "Actionable": {
                                "RowName": "Deployable"
                            },
                            "Usable": {
                                "RowName": "Place"
                            },
                            "Deployable": {
                                "RowName": "TempName"
                            },
                            "Durable": {
                                "RowName": "Deployable_2000"
                            },
                            "Decayable": {
                                "RowName": "Decay_10_Minutes"
                            },
                            "Generator": {
                                "RowName": "TempName"
                            },
                            "Resource": {
                                "RowName": "TempName"
                            },
                            "Audio": {
                                "RowName": "MetalDeployable"
                            },
                            "Manual_Tags": {
                                "GameplayTags": [
                                    {
                                        "TagName": "FieldGuide.ResourceNetwork"
                                    },
                                    {
                                        "TagName": "item.ResourceGenerator.Water"
                                    }
                                ]
                            },
                            "Generated_Tags": {
                                "GameplayTags": [
                                    {
                                        "TagName": "FieldGuide.ResourceNetwork"
                                    },
                                    {
                                        "TagName": "item.ResourceGenerator.Water"
                                    },
                                    {
                                        "TagName": "Traits.Meshable"
                                    },
                                    {
                                        "TagName": "Traits.Itemable"
                                    },
                                    {
                                        "TagName": "Traits.Interactable"
                                    },
                                    {
                                        "TagName": "Traits.Highlightable"
                                    },
                                    {
                                        "TagName": "Traits.Actionable"
                                    },
                                    {
                                        "TagName": "Traits.Usable"
                                    },
                                    {
                                        "TagName": "Traits.Deployable"
                                    },
                                    {
                                        "TagName": "Traits.Durable"
                                    }
                                ],
                                "ParentTags": []
                            }
                        }
                    ]
                },
                {
                    "CurrentFile": "Traits-D_Meshable.json",
                    "File_Items": [
                        {
                            "Name": "Mesh_TempName",
                            "ItemMesh": "/Game/ASS/KIT/SM_KIT_Machining.SM_KIT_Machining",
                            "EquipHandMesh": "/Game/Assets/3DArt/Interactables/Items/DummyCube/SK_dummy_cube.SK_dummy_cube",
                            "DeployableActor": "/Game/BP/Objects/World/Items/Deployables/Pump/BP_TempName.BP_TempName_C"
                        }
                    ]
                },
                {
                    "CurrentFile": "Traits-D_Itemable.json",
                    "File_Items": [
                        {
                            "Name": "Item_TempName",
                            "DisplayName": "NSLOCTEXT(\"D_Itemable\", \"Item_TempName-DisplayName\", \"TempName\")",
                            "Icon": "/Game/Assets/2DArt/UI/Items/Item_Icons/Deployables/ITEM_TempName.ITEM_TempName",
                            "Description": "NSLOCTEXT(\"D_Itemable\", \"Item_TempName-Description\", \"A water pump useful for creating water networks.\")",
                            "Weight": 1000,
                            "MaxStack": 20
                        }
                    ]
                },
                {
                    "CurrentFile": "Traits-D_Deployable.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "Variants": [
                                {
                                    "RowName": "TempName",
                                    "DataTableName": "D_DeployableSetup"
                                }
                            ],
                            "EffectedByWeather": false
                        }
                    ]
                },
                {
                    "CurrentFile": "Deployables-D_DeployableSetup.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "DeployableBlueprint": "/Game/BP/Objects/World/Items/Deployables/Pump/BP_TempName.BP_TempName_C",
                            "PreviewStaticMesh": "/Game/ASS/DEP/SM_TempName.SM_TempName",
                            "DeployedSound": "/Game/FMOD/Events/SFX/D_Deployables/SFX_DEPLOY_WATER_ITEM.SFX_DEPLOY_WATER_ITEM",
                            "SnapToSurfaceNormal": false,
                            "WorldPlacementType": "WaterPlacement"
                        }
                    ]
                },
                {
                    "CurrentFile": "Traits-D_Generator.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "Resource": {
                                "Value": "Water"
                            },
                            "TransmutableResources": [
                                {
                                    "Value": "Energy"
                                }
                            ],
                            "RequiresManualActivation": false,
                            "OutOfFuelThresholdPercent": -1
                        }
                    ]
                },
                {
                    "CurrentFile": "Traits-D_Resource.json",
                    "File_Items": [
                        {
                            "Name": "TempName",
                            "bHasEnergyConnection": true,
                            "EnergyFlow": {
                                "RowName": "Auto_Shutoff_At_0"
                            },
                            "bHasWaterConnection": true
                        }
                    ]
                },
                {
                    "CurrentFile": "EndOfMod"
                }
            ]
        }
        """;
}
