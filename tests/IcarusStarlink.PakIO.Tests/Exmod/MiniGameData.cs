namespace IcarusStarlink.PakIO.Tests.Exmod;

/// <summary>
/// A few rows of each table a new item touches, copied in shape from the real game data: the basic
/// envirosuit (a workshop item), bread (a recipe with a blueprint unlock and a buff), and the
/// envirosuit workshop tab.
/// </summary>
internal static class MiniGameData
{
    public static void Write(string dataFolder)
    {
        void W(string relative, string json)
        {
            var path = Path.Combine(dataFolder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }

        W("Items/D_ItemsStatic.json", """
            {"RowStruct":"S","Defaults":{
              "Meshable":{"RowName":"None","DataTableName":"D_Meshable"},
              "Itemable":{"RowName":"None","DataTableName":"D_Itemable"},
              "Armour":{"RowName":"None","DataTableName":"D_Armour"},
              "Consumable":{"RowName":"None","DataTableName":"D_Consumable"},
              "Decayable":{"RowName":"None","DataTableName":"D_Decayable"}},
             "Rows":[
              {"Name":"Envirosuit","Meshable":{"RowName":"Mesh_Generic_Workshop"},"Itemable":{"RowName":"Item_Envirosuit"},"Armour":{"RowName":"IO_Undersuit"},"Decayable":{"RowName":"Decay_General"},
               "Manual_Tags":{"GameplayTags":[{"TagName":"Item.Envirosuit"}]}},
              {"Name":"Food_Bread","Meshable":{"RowName":"Mesh_Bread"},"Itemable":{"RowName":"Item_Bread"},"Consumable":{"RowName":"Food_Bread"},"Decayable":{"RowName":"Decay_General"}}
            ]}
            """);
        W("Items/D_ItemTemplate.json", """
            {"RowStruct":"S","Defaults":{},"Rows":[
              {"Name":"Envirosuit","ItemStaticData":{"RowName":"Envirosuit"}},
              {"Name":"Food_Bread","ItemStaticData":{"RowName":"Food_Bread"}}
            ]}
            """);
        W("Traits/D_Decayable.json", """{"RowStruct":"S","Defaults":{"DecayTime":0,"SpoilTime":0},"Rows":[{"Name":"Decay_General","DecayTime":300}]}""");
        W("Traits/D_Itemable.json", """
            {"RowStruct":"S","Defaults":{"Weight":0,"MaxStack":1},"Rows":[
              {"Name":"Item_Envirosuit","DisplayName":"NSLOCTEXT(\"D_Itemable\", \"Item_Envirosuit-DisplayName\", \"Xigo Envirosuit\")","Description":"NSLOCTEXT(\"D_Itemable\", \"Item_Envirosuit-Description\", \"A suit.\")","Weight":100},
              {"Name":"Item_Bread","DisplayName":"NSLOCTEXT(\"D_Itemable\", \"Item_Bread-DisplayName\", \"Bread\")","Weight":50}
            ]}
            """);
        W("Traits/D_Meshable.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Mesh_Generic_Workshop"},{"Name":"Mesh_Bread"},{"Name":"Firepit"}]}""");
        W("Traits/D_Armour.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"IO_Undersuit","ArmourStats":{"(Value=\"BaseOxygenSlots_+\")":1}}]}""");
        W("Traits/D_Consumable.json", """
            {"RowStruct":"S","Defaults":{"Modifier":{"Modifier":{"RowName":"None","DataTableName":"D_ModifierStates"},"ModifierLifetime":10}},"Rows":[
              {"Name":"Food_Bread","Modifier":{"Modifier":{"RowName":"Bread"},"ModifierLifetime":900}}
            ]}
            """);
        W("Modifiers/D_ModifierStates.json", """
            {"RowStruct":"S","Defaults":{},"Rows":[
              {"Name":"Bread","ModifierName":"NSLOCTEXT(\"D_ModifierStates\", \"Bread-ModifierName\", \"Bread\")","GrantedStats":{"(Value=\"BaseMaximumStamina_+\")":125}}
            ]}
            """);
        W("Crafting/D_ProcessorRecipes.json", """
            {"RowStruct":"S","Defaults":{},"Rows":[
              {"Name":"Bread","Requirement":{"RowName":"Bread_Blueprint","DataTableName":"D_Talents"},
               "RecipeSets":[{"RowName":"Kitchen_Stove","DataTableName":"D_RecipeSets"}],
               "Inputs":[{"Element":{"RowName":"Dough_Bread","DataTableName":"D_ItemsStatic"},"Count":1}],
               "Outputs":[{"Element":{"RowName":"Food_Bread","DataTableName":"D_ItemTemplate"},"Count":1}]}
            ]}
            """);
        W("Crafting/D_RecipeSets.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Character"},{"Name":"Crafting_Bench"},{"Name":"Kitchen_Stove"}]}""");
        W("MetaWorkshop/D_WorkshopItems.json", """
            {"RowStruct":"S","Defaults":{},"Rows":[
              {"Name":"Meta_Envirosuit_Basic","Item":{"RowName":"Envirosuit"},
               "ResearchCost":[{"Meta":{"RowName":"Credits","DataTableName":"D_MetaCurrency"},"Amount":50}],
               "ReplicationCost":[{"Meta":{"RowName":"Credits","DataTableName":"D_MetaCurrency"},"Amount":25}]}
            ]}
            """);
        W("Talents/D_Talents.json", """
            {"RowStruct":"S","Defaults":{},"Rows":[
              {"Name":"Bread_Blueprint","TalentTree":{"RowName":"Blueprint_T1"}},
              {"Name":"Workshop_Envirosuit_1","ExtraData":{"RowName":"Meta_Envirosuit2","DataTableName":"D_WorkshopItems"},"TalentTree":{"RowName":"Workshop_Envirosuits"},"Position":{"X":1000,"Y":1150}},
              {"Name":"Workshop_Envirosuit","ExtraData":{"RowName":"Meta_Envirosuit","DataTableName":"D_WorkshopItems"},"TalentTree":{"RowName":"Workshop_Envirosuits"},"Position":{"X":500,"Y":800}}
            ]}
            """);
        W("Talents/D_TalentTrees.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Workshop_Envirosuits","Archetype":{"RowName":"Workshop_Envirosuits"},"Icon":"/Game/Icon.Icon"},{"Name":"Blueprint_T1","Archetype":{"RowName":"Blueprints"}}]}""");
        W("Talents/D_TalentArchetypes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Workshop_Envirosuits","Model":{"RowName":"Workshop"},"DisplayName":"NSLOCTEXT(\"D_TalentArchetypes\", \"Workshop_Envirosuits-DisplayName\", \"Envirosuits\")"},{"Name":"Blueprints","Model":{"RowName":"Blueprint"}}]}""");
    }
}
