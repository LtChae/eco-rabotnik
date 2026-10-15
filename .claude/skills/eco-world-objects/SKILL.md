---
name: eco-world-objects
description: Build or modify placeable Eco world objects in server C#: WorldObject + WorldObjectItem pairs, crafting tables, storage, furniture/housing value, power generators/consumers, fuel, occupancy (block footprint), room requirements, and custom WorldObjectComponents with auto-generated UI ([Eco]/[Autogen]/[RPC]) and [Interaction] handlers.
---

# Eco world objects and components

A placeable thing is **three classes**, usually in one file (copy the closest file from `__core__\AutoGen\WorldObject\`):

1. `XObject : WorldObject` is the placed instance. Its behaviour comes from **components** declared with `[RequireComponent]` and configured in `Initialize()`.
2. `XItem : WorldObjectItem<XObject>` is the inventory item that places it.
3. `XRecipe : RecipeFamily` crafts the item (see **eco-items-recipes**).

The client renders it with a prefab named exactly **`XObject`** (tag `ModObject`), and the item icon uses a prefab named **`XItem`**. See **eco-unity-client**. To reuse a vanilla look, use a `ReuseVanillaPrefab` prefab (no new art).

## Skeleton: a crafting table

```csharp
namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using Eco.Core.Items;
    using Eco.Gameplay.Components;
    using Eco.Gameplay.Components.Auth;
    using Eco.Gameplay.Housing.PropertyValues;
    using Eco.Gameplay.Items;
    using Eco.Gameplay.Items.Recipes;
    using Eco.Gameplay.Minimap;
    using Eco.Gameplay.Modules;
    using Eco.Gameplay.Objects;
    using Eco.Gameplay.Occupancy;
    using Eco.Gameplay.Housing;
    using Eco.Gameplay.Property;
    using Eco.Gameplay.Systems.TextLinks;
    using Eco.Shared.Localization;
    using Eco.Shared.Math;
    using Eco.Shared.Serialization;
    using Eco.Shared.Items;

    [Serialized]
    [RequireComponent(typeof(OnOffComponent))]
    [RequireComponent(typeof(PropertyAuthComponent))]
    [RequireComponent(typeof(MinimapComponent))]
    [RequireComponent(typeof(LinkComponent))]               // pull ingredients from linked storage
    [RequireComponent(typeof(CraftingComponent))]           // makes it a crafting table
    [RequireComponent(typeof(HousingComponent))]
    [RequireComponent(typeof(OccupancyRequirementComponent))]
    [RequireComponent(typeof(PluginModulesComponent))]      // upgrade module slot
    [RequireComponent(typeof(ForSaleComponent))]
    [RequireComponent(typeof(RoomRequirementsComponent))]
    [RequireRoomContainment]                                 // must be inside a room
    [RequireRoomVolume(18)]
    [RequireRoomMaterialTier(0.8f)]
    [Tag("Usable")]
    public partial class WidgetBenchObject : WorldObject, IRepresentsItem
    {
        public virtual Type RepresentedItemType => typeof(WidgetBenchItem);
        public override LocString DisplayName => Localizer.DoStr("Widget Bench");
        public override TableTextureMode TableTexture => TableTextureMode.Wood;

        static WidgetBenchObject()   // footprint, relative to the placement block (x,y,z)
        {
            AddOccupancy<WidgetBenchObject>(new List<BlockOccupancy>
            {
                new BlockOccupancy(new Vector3i(0, 0, 0)), new BlockOccupancy(new Vector3i(0, 1, 0)),
                new BlockOccupancy(new Vector3i(1, 0, 0)), new BlockOccupancy(new Vector3i(1, 1, 0)),
            });
        }

        protected override void Initialize()
        {
            this.GetComponent<MinimapComponent>().SetCategory(Localizer.DoStr("Crafting"));
            this.GetComponent<HousingComponent>().HomeValue = WidgetBenchItem.homeValue;
        }
    }

    [Serialized]
    [LocDisplayName("Widget Bench")]
    [LocDescription("A bench for making widgets.")]
    [IconGroup("World Object Minimap")]
    [Weight(2000)]
    [AllowPluginModules(ItemTypes = new[] { typeof(BasicUpgradeItem), typeof(AdvancedUpgradeItem), typeof(ModernUpgradeItem) })]
    public partial class WidgetBenchItem : WorldObjectItem<WidgetBenchObject>
    {
        protected override OccupancyContext GetOccupancyContext =>
            new SideAttachedContext(0 | DirectionAxisFlags.Down, WorldObject.GetOccupancyInfo(this.WorldObjectType));
        public override HomeFurnishingValue HomeValue => homeValue;
        public static readonly HomeFurnishingValue homeValue = new HomeFurnishingValue()
        {
            ObjectName       = typeof(WidgetBenchObject).UILink(),
            Category         = HousingConfig.GetRoomCategory("Industrial"),
            TypeForRoomLimit = Localizer.DoStr(""),
        };
    }
}
```

Then register recipes for this table with `CraftingComponent.AddRecipe(tableType: typeof(WidgetBenchObject), recipeFamily: this)`.

### Occupancy

- Vanilla footprints live in `__core__\WorldObjectOccupancyAutoGen.cs`. Search there for a similarly sized object and copy its `AddOccupancy` list.
- For a new object, call `WorldObject.AddOccupancy<T>(List<BlockOccupancy>)` or `AddOccupancyList(typeof(T), params BlockOccupancy[])` in the object's **static constructor**. Single block: `AddOccupancyList(typeof(T), new BlockOccupancy(Vector3i.Zero, typeof(BuildingWorldObjectBlock)))`.
- Special ports (pipes, chimneys) use `BlockOccupancy(pos, typeof(...), rotation, BlockOccupancyType.WaterInputPort)` and similar. Copy from vanilla objects with pipes.
- The client prefab's `WorldObject` component has `size` / `occupancyOffset`, which should agree with the server footprint.

## Common components and how vanilla configures them

| Component | Purpose | Initialize call (in object `Initialize()`) | Vanilla example |
|---|---|---|---|
| `CraftingComponent` | crafting table | (none; recipes register themselves) | `CarpentryTable.cs` |
| `PublicStorageComponent` | chest | `var s = GetComponent<PublicStorageComponent>(); s.Initialize(16); s.Storage.AddInvRestriction(new NotCarriedRestriction());` | `StorageChest.cs` |
| `LinkComponent` | links to nearby storage | `GetComponent<LinkComponent>().Initialize(5)` (range) in `PostInitialize` | `Objects\StorageChestObject.cs` |
| `FuelSupplyComponent` + `FuelConsumptionComponent` | burns fuel | `.Initialize(2, new[]{"Burnable Fuel"})` / `.Initialize(10)` (watts) | `Campfire.cs` |
| `PowerGridComponent` + `PowerGeneratorComponent` | generator; also needs `[PowerGenerator(typeof(ElectricPower))]` | `.Initialize(30, new ElectricPower(), 7)` / `.Initialize(12000)` | `CombustionGenerator.cs` |
| `PowerGridComponent` + `PowerConsumptionComponent` | consumer | `.Initialize(10, new ElectricPower())` / `.Initialize(8000)` | `Objects\LaserObject.cs` |
| `HousingComponent` | home value | `.HomeValue = XItem.homeValue` | `HewnChair.cs` |
| `MountComponent` | seats | `.Initialize(1)` | `HewnChair.cs` |
| `MinimapComponent` | minimap icon | `.SetCategory(Localizer.DoStr("Crafting"))` | most |
| `OnOffComponent`, `PropertyAuthComponent`, `ForSaleComponent`, `OccupancyRequirementComponent`, `PaintableComponent`, `MustBeOwnedComponent`, `AttachmentComponent`, `SolidGroundComponent` | standard behaviours (on/off switch, claim auth, sellable, placement rules, paint) | none | various |
| `PartsComponent` | wear parts | `.Config(() => LocString.Empty, new PartInfo[]{ new() { TypeName = nameof(XItem), Quantity = 1 } })` | `CombustionGenerator.cs` |
| `LiquidProducerComponent` / `LiquidConverterComponent` / `AirPollutionComponent` | pipes, pollution | see example | `CombustionGenerator.cs` |

The full list of component types is in `eco-api-lookup` (grep `Component\t` in `Eco.Gameplay.api.tsv`).

Housing: `HomeFurnishingValue { ObjectName, Category = HousingConfig.GetRoomCategory(...), BaseValue, TypeForRoomLimit, DiminishingReturnMultiplier }`. Categories defined in `__core__\Systems\HousingValues.cs` are `Living Room`, `Seating`, `Cultural`, `Bedroom`, `Kitchen`, `Bathroom`, `Outdoor`, `Industrial`, `Decoration` and `Lighting`.

### Lifecycle overrides

- **WorldObject:** `Initialize()`, `PostInitialize()`, `OnCreatePreInitialize()`, `OnCreatePostInitialize()` (first placement only, e.g. `GetComponent<PropertyAuthComponent>().SetPublic()`), `Tick()`, `LateTick()`, `Destroy()`, `OnDestroy()`, `RoomUpdated()`, `Use(...)`.
- **WorldObjectComponent:** `Initialize()`, `PostInitialize()`, `OnCreate()`, `Tick()`, `Destroy()`, `OnPickup(player)`.
- AutoGen objects call `ModsPreInitialize()` / `ModsPostInitialize()` inside `Initialize()`. To tweak a vanilla object, implement them in a UserCode partial (e.g. change storage slots or fuel use).

## Custom component with auto-generated UI

Mark a component `[AutogenClass]`, and its properties and methods become UI in the object's panel. The canonical example is `__core__\Objects\TollObject.cs`, which is thoroughly commented.

```csharp
[Serialized, AutogenClass, LocDisplayName("Donation")]
[Tag("Economy"), Category("Hidden"), NoIcon]
public class DonationComponent : WorldObjectComponent
{
    [Eco, Serialized] public BankAccount TargetAccount { get; set; }                  // [Eco] = shortcut for SyncToView + Autogen + AutoRPC
    [SyncToView, Autogen, AutoRPC, Serialized] public float Amount { get; set; } = 1; // shown and editable
    [Serialized] double hiddenState;                                                  // persisted, not shown

    [RPC, Autogen, OwnerHidden, GuestEditable]                                        // a button visible to non-owners
    public void Donate(Player player) { /* ... */ player.InfoBoxLocStr("Thanks!"); }

    public override void Tick() { /* runs every server tick; keep it cheap */ }
}
```

Attach it with `[RequireComponent(typeof(DonationComponent))]` on the object. Call `this.Changed(nameof(Prop))` after changing a synced value from code so clients update.

### Links and tooltips in `StringDisplay` panels (verified in game, Rabotnik logbook)

Convention in Eco: use a link instead of plain text wherever the thing is an object. A link is the only way to get a hover tooltip on text; `StringDisplay` has no tooltip attribute and Eco has no server-side chart/pie widget (the vanilla pie charts are client-only Unity UI).

- `using Eco.Gameplay.Systems.TextLinks;` then `x.UILink()` on anything `ILinkable`. The text renders as a link in a `StringDisplay`, hovering shows a tooltip with the details of whatever is linked (deed, bank account, currency and so on), and tooltips nested inside a tooltip also work. Hover is what's confirmed; what a click does hasn't been checked.
- Autogen display properties must be plain `string`, so call `.ToString()` on the `LocString`: `settlement?.UILink().ToString() ?? "none"`. Plain `$"{x.UILink()}"` interpolation into a string is also fine.
- Custom link text: `linkable.UILink(Localizer.NotLocalizedStr("housing 2.0"))`; for a type, `type.UILink(LocString content)`. Many links already include a value (`ResidencyPropertyValue.UILink()` reads "Residence: +1.97 XP/day"), so don't append the number again.
- Used and rendering: items and `FoodItem`, `User`, `Settlement`, `Skill`, `Deed`, `Currency`, `BankAccount`, `WorkParty`, `Recipe`, `ResidencyPropertyValue`, and `Room.RoomValue.TitleLink` (a ready-made link to a room's value). Not linkable: `Stomach`. Check `ILinkable` in `eco-api-lookup` before assuming.
- Vanilla tooltip text can be reused as plain panel text: the `[NewTooltip]` library methods are extension methods in `Eco.Gameplay.Systems.NewTooltip.TooltipLibraryFiles` returning `LocString`, e.g. `stomach.StomachNutritionTooltip()` (carbs/protein/fat/vitamins, bonuses and the multiplier, with nested hover links). It works on a bot user's `Stomach`. `StomachContentsTooltip()` duplicates a per-food list you'd build yourself.
- Keep logic on plain names, show links only at display time: if the code diffs, splits or `Replace`s on text (change detection, "strip the worker's name from the log"), compare plain names and store link strings next to them. A link tag in the text breaks string matching, and a link can contain characters you split on. Saved strings may hold link text (a "last meal" link survives restarts), but older saved plain text stays plain until it is rewritten.
- A food-XP breakdown is `Stomach.Contents` (`StomachEntry.Food`, a `FoodItem`) with `Nutrition.Carbs/Fat/Protein/Vitamins`; `Stomach.NutrientSkillRate()` is the food XP and `User.ResidencyPropertyValue` the housing XP.

## Interactions (click and key handlers)

On a component, item or tool, mark a method with `[Interaction(...)]`. The signature is `(Player player, InteractionTriggerInfo trigger, InteractionTarget target)`:

```csharp
[Interaction(InteractionTrigger.RightClick, "Pay Toll", priority: -1, authRequired: AccessType.ConsumerAccess)]
public void PayToll(Player player, InteractionTriggerInfo trigger, InteractionTarget target) { ... }

[Interaction(InteractionTrigger.LeftClick, "Open Without Toll", requiredEnvVars: new[] { nameof(HasFreeToll) })]
public void OpenFree(Player player, InteractionTriggerInfo trigger, InteractionTarget target) { ... }

[EnvVar] public bool HasFreeToll(Player player) => /* per-player condition gating the interaction */ true;
```

Triggers: `LeftClick`, `RightClick`, `InteractKey`. Other options: `modifier: InteractionModifier.Shift`, `tags: BlockTags.X` (for tools targeting blocks), `canHoldToTrigger`, `animationDriven`, `Flags = InteractionFlags.NoTargetRequired`, `interactionDistance`, `highlightColorHex`. Tool examples: `__core__\Tools\*.cs`.

## Gotchas

- **Don't expose a mod-defined enum in autogen UI.** The client builds enum dropdowns from its own compiled types, so a server-only enum renders as an empty dropdown. Player.log shows `Can't convert named type to system type: <Enum>View`. Use one `[RPC, Autogen]` button per choice instead, or a type the client already knows (e.g. `Currency`, `BankAccount`, `User`).
- An **enum** used in a `[Serialized]` or `[Eco]` member needs `[Serialized]` on the enum itself (vanilla also adds `[Localized]`, e.g. `WageType`). Without it the server refuses to start ("Enum type X missing [Serialized] attribute"). That's a runtime check, so `tools/BuildCheck` can't catch it.
- Every object, item and component class needs `[Serialized]`. Don't rename or remove serialized classes or fields on a live world.
- `IRepresentsItem.RepresentedItemType` links the object back to its item, for pickup and tooltips.
- `[RequireRoomContainment]`, `[RequireRoomVolume]` and `[RequireRoomMaterialTier]` need `RoomRequirementsComponent`.
- Without a client prefab, the object can exist on the server but renders as a missing or placeholder object.
