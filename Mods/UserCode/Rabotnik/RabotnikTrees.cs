// Rabotnik - simulated tree felling for harvest contracts. A worker can't fell a tree physically (no falling trunk, no physics
// client to place trunk pieces, no debris blocks), so it works the tree out from vanilla's own numbers (TreeObject.cs, AxeItem.cs):
//   logs     = (ResourceRange.Diff * growth + ResourceRange.Min), picked up in pieces of at most 5 base logs, each getting the log's
//              Yield and the HarvestYield bonuses
//   swings   = fell ceil(TreeHealth / damage) + slice (pieces of >= 5 logs, one cut between each) + stump ceil(TreeHealth / damage)
//              + one per debris block (estimate: logs / DebrisLogsPerBlock, scaled by the species' debris chance, capped at 20)
//   calories = swings * the axe's CaloriesBurn for the worker, plus CaloriesPerItemHauled per log
// then removes the tree with the same DestroyPlant call vanilla uses for saplings. Stump and debris items (wood pulp, seeds) are
// only given if the contract's container still expects them; they would otherwise clutter the worker's inventory.
// Not yet verified in game: the ChopTree/HarvestOrHunt pack with a worker, and ContractClauseHarvest's tree recount.
namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Eco.Core.Utils;
    using Eco.Gameplay.Bonuses;
    using Eco.Gameplay.GameActions;
    using Eco.Gameplay.Items;
    using Eco.Gameplay.Players;
    using Eco.Mods.Organisms;
    using Eco.Shared.Items;
    using Eco.Shared.Localization;
    using Eco.Shared.Logging;
    using Eco.Shared.Math;
    using Eco.Shared.Utils;
    using Eco.World.Blocks;
    using Eco.Simulation;
    using Eco.Simulation.Agents;

    public static class RabotnikTrees
    {
        const int MaxPickupLogs = 5;       // TreeEntity.MaxTrunkPickupSize
        const int MaxDebris = 20;          // TreeEntity.MaxTreeDebris
        const float DefaultDamage = 1f;    // stone axe, used to estimate before the worker owns an axe
        const float DefaultSwingCalories = 20f;

        // Progress of the tree a worker is cutting, by user id. Not saved: a restart costs the swings already made.
        static readonly Dictionary<int, (Vector3i Position, int Swings)> progress = new();
        static readonly object progressLock = new();

        /// <summary>What felling and processing one tree takes and gives, for one worker and axe (a null axe estimates with a stone axe).</summary>
        public sealed class Plan
        {
            public TreeEntity Tree;
            public int Logs;
            public float Damage;
            public float SwingCalories;
            public int FellSwings, SliceSwings, StumpSwings, DebrisBlocks;
            public int Swings => FellSwings + SliceSwings + StumpSwings + DebrisBlocks;
            public float HaulCalories;
            public float Calories => Swings * SwingCalories + HaulCalories;
        }

        /// <summary>Standing trees a worker may fell (not saplings, not fallen).</summary>
        public static bool IsStanding(Plant plant) => plant is TreeEntity tree && !tree.Dead && !tree.Fallen && tree.Ripe;

        /// <summary>Why the worker leaves this tree to players, or null if it may fell it.</summary>
        public static string Refuse(User worker, TreeEntity tree)
        {
            if (tree.Fallen) return "fallen trees only a player can clear";
            var logs = Logs(worker, tree);
            return logs > RabotnikPlugin.Config.MaxTreeLogs ? $"{tree.Species.DisplayName} giving {logs} logs (limit {RabotnikPlugin.Config.MaxTreeLogs})" : null;
        }

        public static Plan MakePlan(User worker, TreeEntity tree, ToolItem axe)
        {
            var config = RabotnikPlugin.Config;
            var species = tree.Species;
            var logs = Logs(worker, tree);
            var damage = DefaultDamage;
            var calories = DefaultSwingCalories;
            if (axe != null)
            {
                var baseDamage = axe.Damage.GetCurrentValue(worker.DynamicValueContext) + axe.PerkDamage.GetCurrentValue(worker.DynamicValueContext);
                damage = BonusManager.ApplyBonuses(new BonusContext { Action = BonusAction.UseTool, Source = worker, Tool = axe }, baseDamage);
                calories = axe.CaloriesBurn.GetCurrentValue(worker.DynamicValueContext);
            }
            damage = Math.Max(damage, 0.01f);
            var treeSwings = (int)Math.Ceiling(species.TreeHealth / damage);
            var debris = config.DebrisLogsPerBlock > 0 ? (int)Math.Round(Math.Min(MaxDebris, logs / config.DebrisLogsPerBlock) * species.ChanceToSpawnDebris) : 0;
            return new Plan
            {
                Tree = tree,
                Logs = logs,
                Damage = damage,
                SwingCalories = Math.Max(0f, calories),
                FellSwings = treeSwings,
                SliceSwings = Math.Max(0, (int)Math.Ceiling(logs / (double)MaxPickupLogs) - 1),
                StumpSwings = treeSwings,
                DebrisBlocks = debris,
                HaulCalories = logs * config.CaloriesPerItemHauled,
            };
        }

        /// <summary>The log count of the whole tree, with the log's Yield and bonuses applied to each pickup like vanilla's CalculateTrunkResources.</summary>
        static int Logs(User worker, TreeEntity tree)
        {
            var species = tree.Species;
            var range = species.ResourceRange;
            var total = Math.Max((int)Math.Round(range.Diff * tree.GrowthPercent + range.Min, MidpointRounding.AwayFromZero), 1);
            var item = Item.Get(species.ResourceItemType);
            var pickups = (int)Math.Ceiling(total / (double)MaxPickupLogs);
            var bonus = item.Yield?.GetCurrentValueInt(worker.DynamicValueContext, null) ?? 0;
            var logs = 0;
            for (var i = 0; i < pickups; i++)
            {
                var count = total / pickups + (i < total % pickups ? 1 : 0) + bonus;
                logs += (int)BonusManager.ApplyBonuses(worker.MakeBonusContext(BonusAction.HarvestYield, item), count);
            }
            return logs;
        }

        /// <summary>The swings already made on this tree (0 if the worker was cutting another).</summary>
        public static int SwingsDone(User worker, TreeEntity tree)
        {
            lock (progressLock)
                return progress.TryGetValue(worker.Id, out var p) && p.Position == tree.Position.XYZi() ? p.Swings : 0;
        }

        public static void SaveProgress(User worker, TreeEntity tree, int swings)
        {
            lock (progressLock) progress[worker.Id] = (tree.Position.XYZi(), swings);
        }

        public static void ClearProgress(User worker)
        {
            lock (progressLock) progress.Remove(worker.Id);
        }

        /// <summary>Puts the tree in progress first, so a half-cut tree is finished before another is started.</summary>
        public static IEnumerable<TreeEntity> InWorkOrder(User worker, IEnumerable<TreeEntity> trees) =>
            trees.OrderByDescending(t => SwingsDone(worker, t) > 0);

        /// <summary>Leaves items on the ground at a spot, like a player dropping them. Block items (logs) are stacked as the pickup blocks players
        /// can place and collect (up to 4 per block, a few high, in the nearest free cells around the spot); anything else goes through the user's
        /// trash drop (a garbage bag), which drops at the worker's position, so the worker is moved to the spot for it. Returns how many were dropped.</summary>
        public static int DropOnGround(User worker, Item item, int quantity, Vector3i at)
        {
            if (quantity <= 0) return 0;
            if (item is BlockItem blockItem && blockItem.BlockTypes?.Length > 0) return StackBlocks(blockItem, quantity, at);
            return DropToTrash(worker, item, quantity, at);
        }

        static int StackBlocks(BlockItem item, int quantity, Vector3i at)
        {
            const int Radius = 5, MaxHeight = 3;
            var types = item.BlockTypes;
            var left = quantity;
            foreach (var column in new WorldPosition3i(at).SpiralOutXZIter(Radius))
            {
                if (left <= 0) break;
                var top = (Vector3i)column;
                // The surface of this column near the spot: an empty cell on a solid one, highest first.
                Vector3i? surface = null;
                for (var dy = 3; dy >= -3 && surface == null; dy--)
                {
                    var p = top + Vector3i.Up * dy;
                    if (Eco.World.World.GetBlock(p) is EmptyBlock && Eco.World.World.GetBlock(p + Vector3i.Down).Is<Solid>()) surface = p;
                }
                if (surface == null) continue;
                for (var h = 0; h < MaxHeight && left > 0; h++)
                {
                    var p = surface.Value + Vector3i.Up * h;
                    if (Eco.World.World.GetBlock(p) is not EmptyBlock) break;
                    var n = Math.Min(types.Length, left);
                    Eco.World.World.SetBlock(types[n - 1], p);
                    left -= n;
                }
            }
            return quantity - left;
        }

        static int DropToTrash(User worker, Item item, int quantity, Vector3i at)
        {
            var dropped = 0;
            var trash = worker.Inventory.Trash;
            var before = worker.Position;
            worker.Position = new System.Numerics.Vector3(at.x, at.y, at.z);
            try
            {
                for (var left = quantity; left > 0;)
                {
                    var load = Math.Min(left, Math.Max(1, item.MaxStackSize));
                    if (!trash.TryAddItemsNonUnique(item.Type, load, worker).Success) break;
                    trash.DropItemToWaste(worker).GetAwaiter().GetResult();
                    var stuck = trash.NonEmptyStacks.Sum(s => s.Quantity);
                    dropped += load - stuck;
                    left -= load;
                    if (stuck > 0) { trash.MoveAsManyItemsAsPossible(worker.Inventory, worker); break; } // no room to drop here
                }
            }
            finally { worker.Position = before; }
            return dropped;
        }

        /// <summary>Performs the felling: the vanilla ChopTree and HarvestOrHunt actions for laws and the contract clause (which then expects the
        /// logs, and any stump/debris items the container wants, in its container), then the tree is removed. The caller moves the returned items
        /// to the container (a whole tree doesn't fit the worker's inventory at once). expected(item) is how many of an item the contract still wants.</summary>
        public static Result Fell(User worker, Plan plan, ToolItem axe, Func<Item, int> expected, out List<ItemStack> given, out List<ItemStack> bonus)
        {
            var tree = plan.Tree;
            var species = tree.Species;
            given = new List<ItemStack>();
            bonus = new List<ItemStack>();
            var log = Item.GetNonUniqueOrClone(species.ResourceItemType);
            if (plan.Logs > 0) given.Add(new ItemStack(log, plan.Logs));

            // Stump wood pulp and debris items (seeds): what the contract expects counts toward it; the rest is a bonus the caller stores or drops.
            var extras = new Dictionary<Type, int>();
            void Add(Dictionary<Type, Eco.Shared.Math.Range> resources, int times)
            {
                if (resources == null) return;
                for (var i = 0; i < times; i++)
                    foreach (var (type, range) in resources) extras[type] = extras.GetValueOrDefault(type) + range.RandIntInc;
            }
            Add(species.TrunkResources, 1);
            Add(species.DebrisResources, plan.DebrisBlocks);
            bonus = new List<ItemStack>();
            foreach (var (type, quantity) in extras)
            {
                var item = Item.GetNonUniqueOrClone(type);
                var qty = Math.Min(quantity, expected(item));
                if (qty > 0) given.Add(new ItemStack(item, qty));
                if (quantity - qty > 0) bonus.Add(new ItemStack(item, quantity - qty));
            }

            using var pack = new GameActionPack();
            var location = tree.Position.XYZi();
            pack.AddGameAction(new ChopTree
            {
                Citizen = worker,
                Species = species.GetType(),
                ChopperUserID = worker.Id,
                ActionLocation = location,
                AccessNeeded = AccessType.FullAccess,
                Felled = true,
                GrowthPercent = tree.GrowthPercent * 100,
                ToolUsed = axe,
            });
            pack.AddGameAction(new HarvestOrHunt
            {
                Citizen = worker,
                Species = species.GetType(),
                HarvestedStacks = given,
                ActionLocation = location,
                AccessNeeded = AccessType.ConsumerAccess,
                ToolUsed = axe,
                DamagedOrDestroyed = DamagedOrDestroyed.DestroyingOrganism,
                DestroyedByBlock = false,
            });
            var experience = 0.1f * plan.Damage * (plan.FellSwings + 0.1f * plan.DebrisBlocks);
            pack.AddPostEffect(() =>
            {
                worker.Skillset.AddExperience(typeof(LoggingSkill), experience, Localizer.Format("felling {0}", species.DisplayName));
                EcoSim.PlantSim.DestroyPlant(tree, DeathType.Logging, true, worker);
                Eco.World.World.ForceUpdate(location);
            });
            try { return pack.TryPerform(worker); }
            catch (Exception e) { Log.WriteException(e); return Result.FailLoc($"{e.Message}"); }
        }
    }
}
