---
name: eco-items-recipes
description: Write or modify Eco items and recipes in server C#. Covers plain items, parts, fuel, food, tools, clothing, seeds, block items, RecipeFamily/Recipe/IngredientElement/CraftingElement, tag ingredients, garbage byproducts, labor/time/XP values, and changing vanilla recipes safely via partial ModsPreInitialize hooks or .override.cs files.
---

# Eco items and recipes

Put all files in `Mods/UserCode/<ModName>/` with `namespace Eco.Mods.TechTree`, which is where every vanilla item, recipe, skill and table lives. **Copy the closest vanilla file first:** `eco-api-lookup/vanilla/vanilla-types.tsv` maps class → base type → file under `__core__`, and `vanilla-recipes.tsv` lists every recipe with its table, skill, ingredients and outputs.

## Usings that cover almost every content file

```csharp
namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using Eco.Core.Items;
    using Eco.Gameplay.Blocks;
    using Eco.Gameplay.Components;
    using Eco.Gameplay.DynamicValues;
    using Eco.Gameplay.Items;
    using Eco.Gameplay.Items.Recipes;   // Recipe, RecipeFamily, IngredientElement, CraftingElement
    using Eco.Gameplay.Garbage;         // GarbageOutput
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Skills;
    using Eco.Gameplay.Systems.TextLinks;
    using Eco.Shared.Localization;      // Localizer, LocString
    using Eco.Shared.Serialization;     // [Serialized]
    using Eco.Shared.Utils;
    using Eco.Shared.Time;              // TimeUtil (food shelf life)
    // ... classes ...
}
```

## Item classes

| Base class | Use for | Vanilla example |
|---|---|---|
| `Item` | Generic material (add `[Fuel(joules)]` + `[Tag("Burnable Fuel")]` for fuel) | `AutoGen\Item\Charcoal.cs` |
| `PartItem` | Repairable machine parts (gears, etc.) | `AutoGen\Item\IronGear.cs` |
| `FoodItem` | Edible items | `AutoGen\Food\Bread.cs` |
| `ToolItem` / `AxeItem`, `PickaxeItem`, `ShovelItem`, `HammerItem`, `HoeItem`, `MacheteItem`, `SickleItem`, `BowItem`... | Tools (behaviour lives in `__core__\Tools\*Item.cs`) | `AutoGen\Tool\SteelAxe.cs` |
| `ClothingItem` | Wearables (`Slot => AvatarAppearanceSlots.Head` etc.) | `AutoGen\Clothing\FarmerHat.cs` |
| `SeedItem` (+ hidden `SeedPackItem`) | Plantable seeds; `SpeciesName` must match a PlantSpecies | `AutoGen\Seed\CornSeed.cs` |
| `FertilizerItem` | Fertilizer | `AutoGen\Fertilizer\*` |
| `BlockItem<TBlock>` | Placeable blocks | `AutoGen\Block\Brick.cs` |
| `WorldObjectItem<TObject>` | Placeable objects → see **eco-world-objects** | `AutoGen\WorldObject\*` |
| `SkillBook<,>` / `SkillScroll<,>` | Skill books → see **eco-skills-and-species** | `AutoGen\Tech\*` |

### Item attributes (class-level)

```csharp
[Serialized]                                   // REQUIRED on every item/object/block class (save system)
[LocDisplayName("Iron Gear")]                  // display name (localized)
[LocDescription("A toothed machine part.")]    // tooltip text (no DisplayDescription override on items)
[Weight(500)]                                  // grams
[MaxStackSize(20)]                             // optional; default stack size otherwise
[Tag("Gear")]                                  // tags drive tag ingredients, filters, fuel, laws
[Ecopedia("Items", "Products", createAsSubPage: true)]  // Ecopedia placement (optional)
[SalvageCost(typeof(IronScrap), 0.5f)]         // what the item breaks down into (optional)
[Tier(3)]                                      // optional tier
[StartsDiscovered]                             // known at world start (optional)
[Category("Hidden")]                           // hide from players (admin/debug items)
[Fuel(20000)]                                  // burn energy in joules (with Tag "Burnable Fuel")
```

The class name *is* the identity: `IronGearItem` matches the client icon prefab named `IronGearItem` (see **eco-unity-client**). Renaming a class on a live server breaks saves.

### Minimal item

```csharp
[Serialized]
[LocDisplayName("Copper Coil")]
[LocDescription("Wire wound around a core.")]
[Weight(200)]
[Tag("Product")]
public partial class CopperCoilItem : Item { }
```

### Food

```csharp
[Serialized, LocDisplayName("Corn Chowder"), LocDescription("Thick and warming."), Weight(500)]
[Tag("Food")]
public partial class CornChowderItem : FoodItem
{
    public override LocString DisplayNamePlural => Localizer.DoStr("Corn Chowder");
    public override float Calories             => 900;
    public override Nutrients Nutrition        => new Nutrients { Carbs = 12, Fat = 6, Protein = 4, Vitamins = 8 };
    public override float BaseShelfLife        => (float)TimeUtil.HoursToSeconds(72);  // required (abstract)
}
```

## Recipes (current API)

A `RecipeFamily` holds one or more `Recipe` variants plus shared labor, time and XP. Its constructor builds the recipe and registers it with one or more tables.

```csharp
[RequiresSkill(typeof(MechanicsSkill), 1)]          // skill + level needed to craft (omit = anyone)
[RequiresModule(typeof(BlacksmithTableObject))]     // optional: needs an upgrade module/table nearby
public partial class CopperCoilRecipe : RecipeFamily
{
    public CopperCoilRecipe()
    {
        var recipe = new Recipe();
        recipe.Init(
            name: "CopperCoil",                                  //noloc  (unique id)
            displayName: Localizer.DoStr("Copper Coil"),
            ingredients: new List<IngredientElement>
            {
                new IngredientElement(typeof(CopperWiringItem), 4, typeof(MechanicsSkill)), // scales down with skill/talents
                new IngredientElement("Wood", 2, true),                                     // tag ingredient, static amount
            },
            garbages: new List<GarbageOutput>                    // optional pollution/waste byproducts
            {
                // new GarbageOutput(typeof(CeramicScrap), 1.5f),
            },
            items: new List<CraftingElement>
            {
                new CraftingElement<CopperCoilItem>(2),          // first output = the "main" product
                // new CraftingElement<SlagItem>(typeof(SmeltingSkill), 2),  // skill-scaled byproduct
            });
        this.Recipes = new List<Recipe> { recipe };
        this.ExperienceOnCraft = 1;
        this.LaborInCalories = CreateLaborInCaloriesValue(60, typeof(MechanicsSkill));
        this.CraftMinutes   = CreateCraftTimeValue(beneficiary: typeof(CopperCoilRecipe), start: 1.5f, skillType: typeof(MechanicsSkill));

        this.ModsPreInitialize();
        this.Initialize(displayText: Localizer.DoStr("Copper Coil"), recipeType: typeof(CopperCoilRecipe));
        this.ModsPostInitialize();

        CraftingComponent.AddRecipe(tableType: typeof(MachinistTableObject), recipeFamily: this);
    }
    partial void ModsPreInitialize();
    partial void ModsPostInitialize();
}
```

Constructor overloads (verified against the API):
- `IngredientElement(Type itemType | string tag | ItemRepresentation, float count = 1, bool staticIngredient = false)`: static means not reduced by skill.
- `IngredientElement(Type itemType | string tag, float start, Type skill, Type talent = null)`: the amount scales with `skill` and the talent's resource reduction.
- `CraftingElement<T>(float count = 1)`, `CraftingElement<T>(float count, bool refundsIngredient)`, `CraftingElement<T>(Type skill, float start, Type talent = null)`.
- `CreateLaborInCaloriesValue(float start)` (static) or `(float start, Type skillType)`.
- `CreateCraftTimeValue(float start)` or `(Type beneficiary, float start, Type skillType, params Type[] talents)`.
- `CraftingComponent.AddRecipe(Type tableType, RecipeFamily recipeFamily)`. Call it once per table.

Common tag ingredients (see `vanilla/vanilla-tags.txt` and `__core__\Systems\TagDefinitions.cs`): `Wood`, `Rock`, `Lumber`, `HewnLog`, `WoodBoard`, `Fabric`, `NaturalFiber`, `CrushedRock`, `Basic Research`, `Advanced Research`, `Modern Research`, `Fat`, `Oil`, `Vegetable`, `Fruit`, `Grain`, `Greens`, `Raw Food`, `Burnable Fuel`. To add a new tag, register a `TagDefinition` in an `IModInit.Initialize()` (pattern: `Systems\TagDefinitions.cs`).

Find table class names with `Select-String '\t\w+Object\t' vanilla-recipes.tsv` or grep for `: WorldObject` in `vanilla-types.tsv`. Examples: `WorkbenchObject`, `CarpentryTableObject`, `MasonryTableObject`, `AnvilObject`, `BloomeryObject`, `KilnObject`, `CampfireObject`, `BakeryOvenObject`, `StoveObject`, `MachinistTableObject`, `ShaperObject`, `ResearchTableObject`, `FarmersTableObject`, `ButcheryTableObject`, `TailoringTableObject`, `ElectronicsAssemblyObject`.

## Modifying vanilla (preferred: partial hooks)

Every AutoGen RecipeFamily, WorldObject and species has `partial void ModsPreInitialize()` / `ModsPostInitialize()` hooks. Implement them in UserCode in a `partial class` with the **same name and namespace**:

```csharp
namespace Eco.Mods.TechTree
{
    using System.Collections.Generic;
    using Eco.Gameplay.Items;
    using Eco.Gameplay.Items.Recipes;

    public partial class IronGearRecipe
    {
        partial void ModsPreInitialize()   // runs before Initialize(): change ingredients, outputs, labor, time, xp
        {
            var ingredients = this.Recipes[0].Ingredients;
            ingredients.Clear();
            ingredients.Add(new IngredientElement(typeof(IronBarItem), 2, typeof(MechanicsSkill)));
            this.LaborInCalories = CreateLaborInCaloriesValue(100, typeof(MechanicsSkill));
            this.CraftMinutes = CreateCraftTimeValue(beneficiary: typeof(IronGearRecipe), start: 0.6f, skillType: typeof(MechanicsSkill));
        }
    }
}
```

- `ModsPreInitialize` is for recipe contents, XP, labor and time. `ModsPostInitialize` runs after init but before table registration, so use it for skill requirements.
- **A partial method can have only one implementation across all mods.** Two mods hooking the same recipe's `ModsPreInitialize` conflict at compile time.
- **Difficulty variants.** Skill-book recipes and endgame recipes (`ComputerLabRecipe`, `LaserRecipe`) have their ingredients swapped at startup by `RecipeVariant.Register/RegisterDefault` in `__core__\Systems\DifficultyBasedRecipeVariants.cs`, based on the difficulty config. Editing them only in `ModsPreInitialize` may get overwritten, so check that file first.
- **Adding a recipe to another table** doesn't need a hook. Write a new `RecipeFamily` (pattern: GitHub `Examples/AltRecipe`) registered to the other table.
- **Item properties** aren't hook-based. Attributes on a partial can't be changed, so overriding attributes like `[Weight]` or `[MaxStackSize]` needs a `.override.cs` replacement of the vanilla file.
- **Full replacement:** `UserCode/<path relative to __core__>/<Name>.override.cs`. It replaces the core file entirely, so copy the original and edit it. It must be re-synced whenever the game updates that file.

## Checklist for a new item

1. `[Serialized]`, `[LocDisplayName]`, `[LocDescription]`, `[Weight]` on the class. The class name ends in `Item`.
2. A RecipeFamily registered to at least one table, or another source (loot, harvest), or it's admin-only (`/give`).
3. An icon: either a Unity bundle with a prefab named exactly like the class (see **eco-unity-client**), or accept the placeholder icon while testing.
4. Deploy, restart the server, check for compile errors, then `/give` and craft it.
