// Rabotnik - food, the worker's one skill, and skill level from quality of life.
// - Food: eaten from the Logbook's storage when calories run low, preferring variety (vanilla nutrition and taste
//   then drive SkillRate). Stomach.Eat throws at the end for a Player-less user, after the meal is applied (verified in game).
// - Skill: the first skill scroll stocked in the Logbook is consumed and becomes the worker's skill. One per worker.
// - Level: the skill's level is set from the worker's vanilla SkillRate (nutrition + housing) via config thresholds,
//   instead of the worker earning XP.
namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Eco.Gameplay.Components.Storage;
    using Eco.Gameplay.Items;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Skills;
    using Eco.Gameplay.Systems.TextLinks;

    public static class RabotnikNeeds
    {
        const int MaxMealsPerUpdate = 10;

        /// <summary>Eats from the Logbook's storage while calories are below the threshold. Returns meals eaten.</summary>
        public static int Eat(RabotnikComponent component, User worker)
        {
            var inventory = component.Parent.GetComponent<PublicStorageComponent>()?.Inventory;
            if (inventory == null) return 0;
            var meals = 0;
            while (meals < MaxMealsPerUpdate && worker.Stomach.Calories < RabotnikPlugin.Config.EatBelowCalories)
            {
                // Variety first: the food eaten least recently-often, then the most calories.
                var food = inventory.NonEmptyStacks.Select(s => s.Item).OfType<FoodItem>()
                    .Where(f => f.Calories > 0 && worker.Stomach.CanEat(f))
                    .GroupBy(f => f.GetType()).Select(g => g.First())
                    .OrderBy(f => worker.Stomach.Contents.Count(e => e.Food?.GetType() == f.GetType()))
                    .ThenByDescending(f => f.Calories)
                    .FirstOrDefault();
                if (food == null || !inventory.TryRemoveItem(food.GetType()).Success) break;
                EatOne(worker, food);
                meals++;
                component.LastMeal = food.UILink().ToString();
            }
            if (meals > 0)
            {
                worker.UserXP?.UpdateSkillRate();
                component.MealsEaten += meals;
                component.LogEvent(meals == 1 ? $"Ate {component.LastMeal}." : $"Ate {meals} meals, the last was {component.LastMeal}."); //noloc
            }
            return meals;
        }

        const int MaxSupplyEntries = 6;

        /// <summary>What the Logbook's storage holds for the worker: meals, their calories, skill scrolls, and those stacks
        /// as linked "qty item" entries (empty if there are none).</summary>
        public static (int meals, float calories, int scrolls, string contents) Supplies(RabotnikComponent component)
        {
            var inventory = component.Parent.GetComponent<PublicStorageComponent>()?.Inventory;
            if (inventory == null) return (0, 0, 0, "");
            int meals = 0, scrolls = 0; float calories = 0;
            var counts = new Dictionary<Type, (Item item, int qty)>();
            foreach (var stack in inventory.NonEmptyStacks)
            {
                if (stack.Item is FoodItem f && f.Calories > 0) { meals += stack.Quantity; calories += f.Calories * stack.Quantity; }
                else if (stack.Item is SkillScroll) scrolls += stack.Quantity;
                else continue;
                var type = stack.Item.GetType();
                counts[type] = (stack.Item, (counts.TryGetValue(type, out var c) ? c.qty : 0) + stack.Quantity);
            }
            var entries = counts.Values.OrderByDescending(x => x.qty).Select(x => $"{x.qty} {x.item.UILink()}").ToList(); //noloc
            var contents = string.Join(", ", entries.Take(MaxSupplyEntries)) + (entries.Count > MaxSupplyEntries ? $", +{entries.Count - MaxSupplyEntries} more" : ""); //noloc
            return (meals, calories, scrolls, contents);
        }

        /// <summary>The skill type for a base skill picked on the Logbook (e.g. LoggingSkill). Null for None.</summary>
        public static Type BaseSkillType(RabotnikBaseSkill baseSkill) =>
            baseSkill == RabotnikBaseSkill.None ? null : typeof(RabotnikNeeds).Assembly.GetType($"Eco.Mods.TechTree.{baseSkill}Skill");

        public static void EatOne(User worker, FoodItem food)
        {
            try { worker.Stomach.Eat(food, out _, force: true); }
            catch (ArgumentException e) when (e.Message.Contains("FoodEaten")) { } // client RPC after the meal is applied; no client
        }

        /// <summary>The worker's assigned skill. If it has none yet: the base skill picked on the Logbook, else a stocked skill scroll.</summary>
        public static Skill EnsureSkill(RabotnikComponent component, User worker)
        {
            var assigned = component.AssignedSkill(worker);
            if (assigned != null)
            {
                if (assigned.Level < 1) assigned.ForceSetLevel(worker, 1); // workers learned before this fix are stuck at level 0
                return assigned;
            }

            var baseType = BaseSkillType(component.BaseSkill);
            if (baseType != null) return Learn(component, worker, baseType);

            var inventory = component.Parent.GetComponent<PublicStorageComponent>()?.Inventory;
            var scroll = inventory?.NonEmptyStacks.Select(s => s.Item).OfType<SkillScroll>().FirstOrDefault();
            if (scroll == null) return null;
            // SkillScroll.TryLearnScroll moves the scroll with the worker's auth, which the Logbook's storage refuses,
            // so take the scroll out ourselves and learn the skill directly.
            if (!inventory.TryRemoveItem(scroll.GetType()).Success) return null;
            return Learn(component, worker, scroll.SkillType);
        }

        static Skill Learn(RabotnikComponent component, User worker, Type skillType)
        {
            var skill = worker.Skillset.HasSkill(skillType) ? worker.Skillset.GetSkill(skillType) : worker.Skillset.LearnSkill(skillType);
            // LearnSkill leaves the skill at level 0, but players start a learned skill at level 1 and recipes need that level;
            // a level 0 worker could never labor, so never earn the XP to reach 1.
            if (skill.Level < 1) skill.ForceSetLevel(worker, 1);
            component.AssignedSkillName = skillType.Name;
            return skill;
        }

        /// <summary>Keeps the worker's SkillRate fresh. Levels come from labor XP like a player's (WorkOrder pays it out when an
        /// iteration completes, scaled by the SkillRate), so nothing sets the level here.</summary>
        public static void RefreshSkillRate(User worker) => worker.UserXP?.UpdateSkillRate();
    }
}
