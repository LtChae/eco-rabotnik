// Rabotnik - the Rabotnik Logbook: the furniture piece a Rabotnik is tied to. Placing it assigns a worker; picking it up
// retires the worker. Its storage is the worker's supply depot (food and skill scrolls).
// Client model (rabotnik.unity3d): "Simple Book" by Ludus101 (CC BY 4.0, see CREDITS.md).
// It is a tabletop item (SurfaceTags.CanBeOnSurface), like vanilla lamps and pots. The worker's name is printed on the
// spine like a sign's text: a CustomTextComponent here, a Sign script and TextMeshPro on the prefab (LogbookBuilder).
namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using Eco.Core.Items;
    using Eco.Gameplay.Components;
    using Eco.Gameplay.Components.Auth;
    using Eco.Gameplay.Components.Storage;
    using Eco.Gameplay.Housing;
    using Eco.Gameplay.Housing.PropertyValues;
    using Eco.Gameplay.Items;
    using Eco.Gameplay.Items.Recipes;
    using Eco.Gameplay.Objects;
    using Eco.Gameplay.Occupancy;
    using Eco.Gameplay.Placement;
    using Eco.Gameplay.Property;
    using Eco.Gameplay.Skills;
    using Eco.Gameplay.Systems.TextLinks;
    using Eco.Shared.Items;
    using Eco.Shared.Localization;
    using Eco.Shared.Math;
    using Eco.Shared.Serialization;

    [Serialized]
    [RequireComponent(typeof(PropertyAuthComponent))]
    [RequireComponent(typeof(PublicStorageComponent))]
    [RequireComponent(typeof(OccupancyRequirementComponent))]
    [RequireComponent(typeof(RoomRequirementsComponent))]
    [RequireComponent(typeof(RabotnikComponent))]
    [RequireComponent(typeof(RabotnikHomeComponent))]
    [RequireComponent(typeof(RabotnikActivityComponent))]
    [RequireComponent(typeof(HousingComponent))]
    [RequireComponent(typeof(CustomTextComponent))] // the worker's name on the spine (RabotnikComponent.SetSpineText), shown like a sign's text
    [RequireRoomContainment] // must be inside a room; as a "Cultural" furnishing its value is the Rabotnik's culture (RabotnikCulture)
    [Tag("Usable")]
    [Ecopedia("Rabotniks", "Rabotnik Logbook")] // the hand-written page in Ecopedia/Concepts; the object, item and recipe all link to it
    public partial class RabotnikLogbookObject : WorldObject, IRepresentsItem, IHasDynamicHomeFurnishingValue, IMoveableWithinSettlement
    {
        // Like vanilla's PictureFrameObject: the dynamic value is what the "artwork" (the Rabotnik) is worth in culture, and
        // uniqueness (diminishing returns on repeats) is per Rabotnik, so every Rabotnik counts in full.
        float     IHasDynamicHomeFurnishingValue.DynamicFurnishingValue => this.GetComponent<RabotnikComponent>().Culture;
        LocString IHasDynamicHomeFurnishingValue.DynamicFurnishingTitle => this.CachedSettlementAtPos != null ? Localizer.Do($"Rabotnik Culture in {this.CachedSettlementAtPos.MarkedUpName}") : Localizer.DoStr("Rabotnik Culture");
        object    IHasDynamicHomeFurnishingValue.UniqueObject => this.GetComponent<RabotnikComponent>().Worker;
        LocString IHasDynamicHomeFurnishingValue.UniqueObjectName => Localizer.DoStr("Rabotnik");

        public virtual Type RepresentedItemType => typeof(RabotnikLogbookItem);
        // The component renames the object "Logbook of <worker>" once a worker is assigned (see RabotnikComponent.Refresh).
        public override LocString DisplayName => Localizer.DoStr("Rabotnik Logbook");
        public override TableTextureMode TableTexture => TableTextureMode.Wood;

        static RabotnikLogbookObject()
        {
            AddOccupancy<RabotnikLogbookObject>(new List<BlockOccupancy> { new BlockOccupancy(new Vector3i(0, 0, 0)) });
        }

        protected override void Initialize()
        {
            var storage = this.GetComponent<PublicStorageComponent>();
            storage.Initialize(8);
            storage.Storage.AddInvRestriction(new RabotnikSuppliesRestriction());
            this.GetComponent<HousingComponent>().HomeValue = RabotnikLogbookItem.homeValue;
            this.GetComponent<CustomTextComponent>().Initialize(40);
        }
    }

    [Serialized]
    [LocDisplayName("Rabotnik Logbook")]
    [LocDescription("Home of a Rabotnik. Place it in a house inside a settlement; a Rabotnik moves in and works for the settlement. Stock it with food and skill scrolls (and a sickle, scythe or axe for harvest contracts), and fund the Rabotnik so it can pay rent. Sits on tables. Model: \"Simple Book\" by Ludus101, CC BY 4.0.")]
    [Tag(nameof(SurfaceTags.CanBeOnSurface))] // lets it sit on tables, like vanilla lamps and pots
    [Weight(1000)]
    [Ecopedia("Rabotniks", "Rabotnik Logbook")]
    [Tag("Housing")]
    public partial class RabotnikLogbookItem : WorldObjectItem<RabotnikLogbookObject>
    {
        public override HomeFurnishingValue HomeValue => homeValue;
        // No base value: all of a Logbook's furnishing value is the Rabotnik's culture (the dynamic part).
        public static readonly HomeFurnishingValue homeValue = new HomeFurnishingValue()
        {
            ObjectName       = typeof(RabotnikLogbookObject).UILink(),
            Category         = HousingConfig.GetRoomCategory("Cultural"),
            BaseValue        = 0,
            TypeForRoomLimit = Localizer.DoStr("Cultural"),
            DiminishingMultiplierAcrossFullProperty = 0.5f,
        };

        protected override OccupancyContext GetOccupancyContext => new SideAttachedContext(0 | DirectionAxisFlags.Down, WorldObject.GetOccupancyInfo(this.WorldObjectType));
    }

    /// <summary>The Logbook's storage only takes what the worker uses: food, skill scrolls and harvesting tools (sickles, scythes and axes, used for harvest contracts).</summary>
    public class RabotnikSuppliesRestriction : InventoryRestriction
    {
        public override LocString Message => Localizer.DoStr("Only food, skill scrolls and harvest tools (sickles, scythes and axes) can be stored for the worker.");
        public override int MaxAccepted(Item item) => item is FoodItem || item is SkillScroll || RabotnikHarvest.IsHarvestTool(item) ? -1 : 0;
    }

    [RequiresSkill(typeof(CarpentrySkill), 1)]
    [Ecopedia("Rabotniks", "Rabotnik Logbook")]
    public partial class RabotnikLogbookRecipe : RecipeFamily
    {
        public RabotnikLogbookRecipe()
        {
            var recipe = new Recipe();
            recipe.Init(
                name: "RabotnikLogbook", //noloc
                displayName: Localizer.DoStr("Rabotnik Logbook"),
                ingredients: new List<IngredientElement>
                {
                    new IngredientElement(typeof(PlantFibersItem), 20, typeof(CarpentrySkill)),
                    new IngredientElement("WoodBoard", 10, typeof(CarpentrySkill)), //noloc
                },
                garbages: new List<GarbageOutput>(),
                items: new List<CraftingElement>
                {
                    new CraftingElement<RabotnikLogbookItem>()
                });
            this.Recipes = new List<Recipe> { recipe };
            this.ExperienceOnCraft = 1;
            this.LaborInCalories = CreateLaborInCaloriesValue(60, typeof(CarpentrySkill));
            this.CraftMinutes = CreateCraftTimeValue(beneficiary: typeof(RabotnikLogbookRecipe), start: 1, skillType: typeof(CarpentrySkill));
            this.Initialize(displayText: Localizer.DoStr("Rabotnik Logbook"), recipeType: typeof(RabotnikLogbookRecipe));
            CraftingComponent.AddRecipe(tableType: typeof(CarpentryTableObject), recipeFamily: this);
        }
    }
}
