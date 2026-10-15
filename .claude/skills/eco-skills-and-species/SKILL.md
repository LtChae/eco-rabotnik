---
name: eco-skills-and-species
description: Add or modify Eco progression and ecology content in server C# - professions and specialty skills (Skill, SkillBook/SkillScroll, research recipes, leveling strategies, star costs), talents (Talent/TalentGroup and the new Bonus system), starting player defaults, plants and crops (PlantSpecies + layer settings + plant block + seed), trees (TreeSpecies), and animals/fish (AnimalSpecies).
---

# Eco skills, talents, plants, trees and animals

All of these are server C# in `namespace Eco.Mods.TechTree` (skills, talents, animals) or `Eco.Mods.Organisms` (plants, trees). Copy the nearest vanilla file. `eco-api-lookup/vanilla/vanilla-types.tsv` maps every class to its file.

## Skills (`__core__\AutoGen\Tech\*.cs`)

Structure: a **profession** (`[Tag("Profession")]`, Tier 1, e.g. `CarpenterSkill`) owns **specialties** (`[RequiresSkill(typeof(CarpenterSkill), 0)]`, `[Tag("Carpenter Specialty")]`, `[Tag("Specialty")]`, `[Tag("Teachable")]`, e.g. `CarpentrySkill`).

```csharp
[Serialized]
[LocDisplayName("Widgetry")]
[LocDescription("Making widgets. Levels up by crafting related recipes.")]
[Ecopedia("Professions", "Carpenter", createAsSubPage: true)]
[RequiresSkill(typeof(CarpenterSkill), 0), Tag("Carpenter Specialty"), Tier(2)]
[Tag("Specialty")]
[Tag("Teachable")]
public partial class WidgetrySkill : Skill
{
    public static MultiplicativeStrategy MultiplicativeStrategy = new MultiplicativeStrategy(new float[] { 1, 1 - 0.2f, 1 - 0.25f, 1 - 0.3f, 1 - 0.35f, 1 - 0.4f, 1 - 0.45f, 1 - 0.5f });
    public override MultiplicativeStrategy MultiStrategy => MultiplicativeStrategy;   // per-level cost multipliers (index = level)
    public static AdditiveStrategy AdditiveStrategy = new AdditiveStrategy(new float[] { 0, 0.5f, 0.55f, 0.6f, 0.65f, 0.7f, 0.75f, 0.8f });
    public override AdditiveStrategy AddStrategy => AdditiveStrategy;
    public override int MaxLevel => 7;
    public override int Tier => 2;
    public override int SpecialtyCost => 2;
    public override void OnLevelUp(User user) =>
        user.Skillset.AddExperience(typeof(SelfImprovementSkill), 20, Localizer.DoStr("for leveling up another specialization."));
}

[Serialized, Weight(1000), LocDisplayName("Widgetry Skill Book"), Ecopedia("Items", "Skill Books", createAsSubPage: true)]
public partial class WidgetrySkillBook : SkillBook<WidgetrySkill, WidgetrySkillScroll> { }

[Serialized, Weight(100), LocDisplayName("Widgetry Skill Scroll")]
public partial class WidgetrySkillScroll : SkillScroll<WidgetrySkill, WidgetrySkillBook> { }
```

Then add a `WidgetrySkillBookRecipe : RecipeFamily` at `ResearchTableObject` that outputs `CraftingElement<WidgetrySkillBook>()` (copy `Carpentry.cs`). Recipes "level up" a skill when their ingredients use `typeof(WidgetrySkill)` and they're gated with `[RequiresSkill(typeof(WidgetrySkill), n)]`.

- Skill-book recipes are swapped at startup by difficulty variants (`__core__\Systems\DifficultyBasedRecipeVariants.cs`). To change vanilla book costs, edit the variants, not just the recipe.
- Star-cost formula hooks: `Skill.CalculateStarsNeededForSpecialty`, `TalentGroup.CalculateStarCost`, `ModuleTags.CalculateStarCost`. See the comments in `__core__\Skills\SkillMods.cs`. Per-skill cost: `public partial class MechanicsSkill { public override int SpecialtyCost => 2; }`.
- Values scaled by skill use `SkillModifiedValue(start, Strategy, typeof(Skill), typeof(Beneficiary), LocString desc, DynamicValueType)`, combined with `MultiDynamicValue` and `TalentModifiedValue`. See `__core__\Player\PlayerDefaults.cs` and the tools in `AutoGen\Tool`.

### Starting kit and skills

`__core__\Player\PlayerDefaults.cs` is a static, non-partial class (`GetDefaultInventory`, `GetDefaultCampsiteInventory`, `GetDefaultSkills`, `GetSkillsForcedToLevelUp`, `GetDefaultDynamicValues`). You can only change it with `UserCode\Player\PlayerDefaults.override.cs`.

## Talents (`__core__\AutoGen\Benefit\*.cs` + hand-written `__core__\Benefits\*.cs`)

- **AutoGen pattern:** a base talent `XTalent : Talent { Base => true; TalentType => typeof(CraftingTalent); }`, a `TalentGroup` per skill (`Talents = new[]{...}`, `OwningSkill = typeof(...)`, `Level = 6`), and concrete talents (`Base => false`, `TalentGroupType`, `Value`). Behaviour lives in hand-written partials (`OnLearned`, `OnUnLearned`, `Active(object, User)`). Example: `LavishWorkspace.cs`.
- **New Bonus system (in migration):** `this.Bonuses.Add(new Bonus { Causes = { new CraftBonusCause { Action = BonusAction.ResourceCost, SkillTypes = ... } }, Effects = { new BonusEffectMultiplicative { Value = 0.9f, LowerIsBetter = true } } })`. See `__core__\Benefits\SampleTalents.cs` (test with `/admin givetalent <Name>`). The API is flagged as temporary, so expect churn.

## Plants (`__core__\AutoGen\Plant\*.cs` + `AutoGen\Seed\*.cs`)

A crop is four pieces, all named after the species:
1. `PlantLayerSettingsX : PlantLayerSettings` in `namespace Eco.Mods.WorldLayers`: the population world layer.
2. `X : PlantEntity` with a nested `XSpecies : PlantSpecies` that sets growth, `ResourceList` (harvest drops), `BlockType`, climate ranges, `ResourceConstraints` (Nitrogen, Phosphorus, Potassium, SoilMoisture), `CapacityConstraints`, and `GenerationDefinitions` (worldgen biome and clusters).
3. `XBlock : InteractablePlantBlock` with `[Reapable]` / `[Clearable]` / tags.
4. `XSeedItem : SeedItem` (its `SpeciesName` must equal the species `DisplayName`), a hidden `XSeedPackItem`, and a seed recipe.

Example: `AutoGen\Plant\Corn.cs`. To tweak a vanilla species, implement `partial void ModsPostInitialize()` on `XSpecies`. New species also need **client block-set visuals** (eco-unity-client). Worldgen placement comes from the species' own `GenerationDefinitions` (`StartBiomes`, cluster sizes), not from `WorldGenerator.eco`.

## Trees (`__core__\Organisms\Tree\*.cs` + `AutoGen\Plant\<Tree>.cs`)

`X : TreeEntity` with `XSpecies : TreeSpecies`. Hand-written `SetDefaultProperties()` covers health, branching definitions, debris type and resources, trunk resources, scale and density. New tree species need a client tree prefab with matching branch names (`Branch0..N`).

## Animals and fish (`__core__\AutoGen\Animal\*.cs`)

`X : AnimalEntity` with ctor `(Animal parent, Vector3 pos, bool corpse, bool sick, int herdId)` and a nested `XSpecies : AnimalSpecies`. The species sets `Name`, `DisplayName`, `FoodSources` (plant types), `CalorieValue`, `ResourceList` (carcass item), `Health`, damage, herd and skittishness settings, population growth, and `PathRegionType`. Example: `Deer.cs`. Butchering is a recipe (`AutoGen\Recipe\Butcher*.cs`) turning the carcass item into meat and hides. New animals need a client prefab using the ModKit `Base Animals Animator.controller` / `Base Fish Animator.controller`.

## Gotchas

- Ecology (plants, animals) runs in the world simulation. Species added to an **existing** world don't spawn there by worldgen; they appear only via seeds or admin commands, or in a new world.
- Tags like `"Carpenter Specialty"` are listed as `TagDefinition`s in `Systems\TagDefinitions.cs`, which control filter, store and Ecopedia visibility and plural names. Give a new profession's specialty tag a definition too, via `TagDefinition.Register(new TagDefinition("X Specialty") { AutoHighlight = false, ShowInEcopedia = false })` in an `IModInit.Initialize()`. Mirror vanilla; I haven't verified what happens without one.
