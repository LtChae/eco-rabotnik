// Rabotnik - workers take and finish harvest (Harvest Field) contracts posted in their settlement. Separate from hauling
// (RabotnikContracts) with its own feature flag; the accept/finish/notice plumbing is shared in RabotnikContractCore.
// Vanilla harvesting (PlantEntity.TryHarvest / CalculateHarvestResources) dereferences a Player, so a worker emits the same
// HarvestOrHunt game action itself (decompiled v0.14.1.1). The contract's ContractClauseHarvest then counts the plant,
// adds the harvested items to its container tracker, and its ShouldOverrideAuth lets the worker harvest the client's deed.
// The harvested items go to the worker's inventory, then are moved into the contract's container (a Put the tracker counts).
// Trees are felled by simulation (RabotnikTrees): swings and calories worked out from vanilla's numbers, then the tree is removed
// and its logs delivered, over as many updates as the swings need. Fallen trees and huge ones are left to players.
// The worker needs a Sickle or Scythe for plants and an axe for trees, kept in its Logbook storage (or its own inventory); they wear down.
// Not yet verified in game: the scripted HarvestOrHunt counting on the clause, the worker's inventory -> container move,
// and plants and trees destroyed without a Player.
namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using Eco.Core.Items;
    using Eco.Core.Utils;
    using Eco.Gameplay.Bonuses;
    using Eco.Gameplay.Components;
    using Eco.Gameplay.Components.Storage;
    using Eco.Gameplay.DynamicValues;
    using Eco.Gameplay.Economy;
    using Eco.Gameplay.Economy.Contracts;
    using Eco.Gameplay.GameActions;
    using Eco.Gameplay.Items;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Plants;
    using Eco.Gameplay.Property;
    using Eco.Gameplay.Settlements;
    using Eco.Gameplay.Systems.TextLinks;
    using Eco.Gameplay.Utils;
    using Eco.Mods.Organisms;
    using Eco.Shared.Items;
    using Eco.Shared.Localization;
    using Eco.Shared.Logging;
    using Eco.Shared.Math;
    using Eco.Shared.Time;
    using Eco.Shared.Utils;
    using Eco.Simulation;
    using Eco.Simulation.Agents;
    using Eco.Simulation.Types;

    public static class RabotnikHarvest
    {
        // Clauses a worker can fulfil: harvesting, plus payment and a reputation gate. A Transport clause (or anything else) is not supported with it.
        static readonly Type[] Allowed = { typeof(ContractClauseHarvest), typeof(ContractClausePayment), typeof(ContractClauseReputation) };

        // Plant.CalculateResourceYield is protected; reflection keeps vanilla's growth/yield scaling.
        static readonly MethodInfo YieldMethod = typeof(Plant).GetMethod("CalculateResourceYield",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Eco.Shared.Math.Range), typeof(float) }, null);

        /// <summary>The tools a worker harvests plants with: sickles and scythes.</summary>
        public static bool IsPlantTool(Item item) => item is SickleItem || item is ScytheItem;

        /// <summary>The tools a worker fells trees with: axes (a chainsaw is left out: it needs fuel or power).</summary>
        public static bool IsAxe(Item item) => item is AxeItem && item is not ChainsawItem;

        /// <summary>Every tool a worker harvests with.</summary>
        public static bool IsHarvestTool(Item item) => IsPlantTool(item) || IsAxe(item);

        /// <summary>Works the worker's accepted harvest contract, if any. Activity is "" when it has none.</summary>
        public static (string Activity, int Plants, bool Worked) StepActive(RabotnikComponent c, User worker, Settlement settlement)
        {
            if (!RabotnikContractCore.CanWork(worker, settlement)) return ("", 0, false);
            RabotnikContractCore.CheckEnded(c, worker);
            var contract = RabotnikContractCore.ActiveOf(worker, harvest: true);
            if (contract == null) return ("", 0, false);
            c.ContractId = contract.Id;
            return Work(c, worker, contract);
        }

        /// <summary>Takes a new harvest contract when harvesting is on, the worker has none, and it did no party labor this tick.</summary>
        public static (string Activity, int Plants, bool Worked) StepNew(RabotnikComponent c, User worker, Settlement settlement, bool partyLaborDone)
        {
            var config = RabotnikPlugin.Config;
            if (!config.HarvestContracts || partyLaborDone || !RabotnikContractCore.CanWork(worker, settlement)) return ("", 0, false);
            if (RabotnikContractCore.HasActive(worker)) return ("", 0, false);

            var posted = RabotnikContractCore.Posted(settlement, harvest: true);
            if (posted.Count == 0) return ("", 0, false);

            var problems = new List<string>();
            var eligible = new List<(Contract Contract, float PayPerPlant, bool Sickle, bool Axe, float Calories)>();
            foreach (var contract in posted)
            {
                if (Eligible(c, worker, settlement, contract, out var payPerPlant, out var needsSickle, out var needsAxe, out var calories, out var why)) eligible.Add((contract, payPerPlant, needsSickle, needsAxe, calories));
                else problems.Add($"{contract.UILink()}: {why}");
            }
            // Only worth shopping for a tool when there is a contract to use it on. Broken tools are sold first (RabotnikTools).
            // A contract whose tool can't be had is dropped, the others may still be possible.
            string sickleNote = null, axeNote = null;
            var haveSickle = eligible.Any(e => e.Sickle) ? RabotnikTools.EnsureHarvestTool(c, worker, settlement, out sickleNote, axe: false) != null : true;
            var haveAxe = eligible.Any(e => e.Axe) ? RabotnikTools.EnsureHarvestTool(c, worker, settlement, out axeNote, axe: true) != null : true;
            foreach (var e in eligible.Where(e => (e.Sickle && !haveSickle) || (e.Axe && !haveAxe)).ToList())
            {
                problems.Add($"{e.Contract.UILink()}: needs a working {(e.Sickle && !haveSickle ? $"sickle or scythe ({sickleNote})" : $"axe ({axeNote})")}; put one in the Logbook's storage");
                eligible.Remove(e);
            }
            foreach (var (contract, _, _, _, calories) in eligible.OrderByDescending(e => e.PayPerPlant))
            {
                var accepted = RabotnikContractCore.TryAccept(worker, contract);
                if (!accepted.Success) { problems.Add($"couldn't accept {contract.UILink()}: {accepted.Message}"); continue; }
                c.ContractId = contract.Id;
                c.ContractCalories = calories;
                c.LastContractNotice = null;
                RabotnikContractCore.Notice(c, $"accepted the contract {contract.UILink()} from {contract.ClientUser.UILink()}: harvest {Describe(contract)} for {RabotnikContractCore.PayText(contract)}.");
                return Work(c, worker, contract);
            }
            return ($"No harvest contract taken: {string.Join("; ", problems.Select(p => p.TrimEnd('.')))}.", 0, false);
        }

        static bool Eligible(RabotnikComponent c, User worker, Settlement settlement, Contract contract, out float payPerPlant, out bool needsSickle, out bool needsAxe, out float calories, out string why)
        {
            var config = RabotnikPlugin.Config;
            payPerPlant = 0;
            calories = 0;
            needsSickle = needsAxe = false;
            var bad = contract.Clauses.FirstOrDefault(x => !Allowed.Contains(x.GetType()));
            if (bad != null) { why = $"has a {bad.Name} clause (only Harvest and Payment)"; return false; }
            var harvests = contract.Clauses.OfType<ContractClauseHarvest>().ToList();
            if (harvests.Count == 0) { why = "not a harvest contract"; return false; }
            if (!contract.AutoAccept) { why = "needs the client's approval (Auto-Accept off)"; return false; }
            var pay = contract.Clauses.OfType<ContractClausePayment>().FirstOrDefault();
            if (pay != null && pay.Deposit > 0) { why = "asks a deposit"; return false; }
            if (contract.ClientUser == null || contract.ClientUser == worker) { why = "no other client"; return false; }

            var plants = 0;
            var swings = 0;
            var treeCalories = 0f;
            var axe = FindTool(c, worker, axe: true); // null: estimated with a stone axe
            foreach (var h in harvests)
            {
                if (h.Property?.Deed == null || h.Container.PickedObj == null) { why = "its field or container is missing"; return false; }
                var ripe = RipePlants(h);
                if (ripe.Count == 0) { why = "nothing ripe to harvest"; return false; }
                foreach (var tree in ripe.OfType<TreeEntity>())
                {
                    if (!config.HarvestTrees) { why = "includes trees (tree harvesting is turned off)"; return false; }
                    var refused = RabotnikTrees.Refuse(worker, tree);
                    if (refused != null) { why = $"includes {refused}"; return false; }
                    var plan = RabotnikTrees.MakePlan(worker, tree, axe);
                    swings += plan.Swings;
                    treeCalories += plan.Calories;
                    needsAxe = true;
                }
                plants += ripe.Count(p => p is not TreeEntity);
            }
            needsSickle = plants > 0;
            var units = plants + (int)Math.Round(treeCalories / Math.Max(1f, config.CaloriesPerPlantHarvested)); // for pay per unit only
            var ticks = Math.Ceiling((double)plants / Math.Max(1, config.HarvestPlantsPerTick)) + Math.Ceiling((double)swings / Math.Max(1, config.ChopSwingsPerTick));

            var estimate = ticks * RabotnikContractCore.SecondsPerUpdate();
            var allowed = config.ContractTimeSafety * TimeUtil.HoursToSeconds(contract.Hours);
            if (estimate > allowed) { why = $"needs about {estimate / 3600:0.#} h of harvesting, more than {config.ContractTimeSafety:P0} of its {contract.Hours:0.#} h"; return false; }

            var payment = pay?.Payment ?? 0f;
            var workCalories = plants * config.CaloriesPerPlantHarvested + treeCalories;
            if (!RabotnikJobValue.IsContractWorthIt(settlement, pay?.Currency, payment, workCalories, out why)) return false;
            payPerPlant = payment / Math.Max(1, units);
            calories = workCalories;
            why = null;
            return true;
        }

        /// <summary>Work, but an error becomes a status line instead of aborting the worker's whole update (which skipped the Logbook refresh).</summary>
        static (string Activity, int Plants, bool Worked) Work(RabotnikComponent c, User worker, Contract contract)
        {
            try { return WorkContract(c, worker, contract); }
            catch (Exception e)
            {
                Log.WriteException(e);
                return ($"Harvesting: error on {contract.UILink()}: {e.Message}", 0, false);
            }
        }

        /// <summary>Harvests up to the per-tick cap, delivers the crops to the contract's container, and finishes once every clause can succeed.</summary>
        static (string Activity, int Plants, bool Worked) WorkContract(RabotnikComponent c, User worker, Contract contract)
        {
            var config = RabotnikPlugin.Config;
            var plants = 0;
            var delivered = 0;
            var outOfCalories = false;
            var problems = new List<string>();
            var stuck = new List<string>();
            var tool = FindTool(c, worker);
            var axe = FindTool(c, worker, axe: true);
            var trees = 0;
            var swings = 0;

            foreach (var clause in contract.Clauses.OfType<ContractClauseHarvest>())
            {
                var dst = RabotnikContractCore.Storages(clause.Container).FirstOrDefault();
                if (dst == null) { problems.Add("the container is gone"); continue; }
                delivered += Deliver(worker, clause, dst, problems); // crops still carried from an earlier update

                var ripe = RipePlants(clause);
                var standing = new List<TreeEntity>();
                foreach (var tree in ripe.OfType<TreeEntity>())
                {
                    var refused = !config.HarvestTrees ? "tree harvesting is turned off" : RabotnikTrees.Refuse(worker, tree);
                    if (refused != null) stuck.Add($"trees only a player can fell and clear ({refused})");
                    else standing.Add(tree);
                }
                foreach (var tree in RabotnikTrees.InWorkOrder(worker, standing))
                {
                    if (swings >= config.ChopSwingsPerTick) break;
                    if (axe == null || axe.Durability <= 0)
                    {
                        axe = RabotnikTools.EnsureHarvestTool(c, worker, SettlementUtils.GetLowestLevelSettlementAtPos(c.Parent.Position.XZi()), out var axeNote, axe: true);
                        if (axe == null) { stuck.Add($"a working axe ({axeNote}; or put one in the Logbook's storage)"); break; }
                    }
                    var plan = RabotnikTrees.MakePlan(worker, tree, axe);
                    // The whole tree's logs go into the container at once, so check there's room before spending calories on it.
                    var logItem = Item.Get(tree.Species.ResourceItemType);
                    var fits = RoomFor(dst, logItem, worker);
                    if (fits < plan.Logs) { stuck.Add($"room in {clause.Container.PickedObj?.UILink()} for {plan.Logs} {logItem.UILink()} (it has room for {fits})"); break; }
                    var done = RabotnikTrees.SwingsDone(worker, tree);
                    while (done < plan.Swings && swings < config.ChopSwingsPerTick && axe.Durability > 0)
                    {
                        if (worker.Stomach.Calories < plan.SwingCalories) { outOfCalories = true; break; }
                        worker.Stomach.BurnCalories(plan.SwingCalories, true);
                        axe.Durability = Math.Max(0f, axe.Durability - 1f); // not UseDurability, see below
                        done++;
                        swings++;
                    }
                    RabotnikTrees.SaveProgress(worker, tree, done);
                    if (done < plan.Swings) break; // finish this tree next update (calories, swing cap or a worn axe)

                    var expected = new Func<Item, int>(item => RabotnikContractCore.Remaining(clause.Container.Tracker, item));
                    var treePosition = tree.Position.XYZi();
                    var result = RabotnikTrees.Fell(worker, plan, axe, expected, out var given, out var bonus);
                    if (!result.Success) { problems.Add($"couldn't fell {tree.Species.DisplayName}:{result.Message}"); break; }
                    RabotnikTrees.ClearProgress(worker);
                    worker.Stomach.BurnCalories(plan.HaulCalories, true);
                    trees++;
                    delivered += DeliverTree(worker, clause, dst, given, treePosition, problems);
                    StoreBonus(worker, dst, bonus, treePosition, problems);
                }
                foreach (var plant in ripe.OfType<PlantEntity>())
                {
                    if (plants >= config.HarvestPlantsPerTick) break;
                    if (tool == null || tool.Durability <= 0)
                    {
                        // Worn out (or never had one): sell the broken one and buy another if it can, else wait for the owner.
                        tool = RabotnikTools.EnsureHarvestTool(c, worker, SettlementUtils.GetLowestLevelSettlementAtPos(c.Parent.Position.XZi()), out var toolNote);
                        if (tool == null) { stuck.Add($"a working sickle or scythe ({toolNote}; or put one in the Logbook's storage)"); break; }
                    }
                    if (config.CaloriesPerPlantHarvested > 0 && worker.Stomach.Calories < config.CaloriesPerPlantHarvested) { outOfCalories = true; break; }

                    var result = HarvestPlant(worker, plant, tool);
                    if (!result.Success)
                    {
                        // Probably a full backpack: deliver what is carried and try this plant once more.
                        delivered += Deliver(worker, clause, dst, problems);
                        result = HarvestPlant(worker, plant, tool);
                    }
                    if (!result.Success) { problems.Add($"couldn't harvest {plant.Species.DisplayName}:{result.Message}"); break; }
                    plants++;
                    worker.Stomach.BurnCalories(config.CaloriesPerPlantHarvested, true);
                    // Not UseDurability: it evaluates DurabilityBurn for a null Player and throws (seen in game).
                    tool.Durability = Math.Max(0f, tool.Durability - 1f);
                }
                delivered += Deliver(worker, clause, dst, problems);
            }

            var completed = RabotnikContractCore.TryFinish(c, worker, contract, problems, "Harvesting");
            if (completed != null) return (completed, plants + trees, true);

            var left = contract.Clauses.OfType<ContractClauseHarvest>().Sum(h => RipePlants(h).Count);
            var progress = $"{left} ripe plants and trees left";
            var did = trees > 0 ? $"{plants} plants and {trees} trees" : $"{plants} plants";
            plants += trees;
            string status;
            if (stuck.Count > 0)
            {
                var needs = string.Join(", ", stuck.Distinct());
                status = $"Waiting: {contract.UILink()} needs {needs}.";
                RabotnikContractCore.NotifyWaiting(c, contract, status,
                    $"is waiting on the contract {contract.UILink()}: it needs {needs}. Meanwhile it does other work.",
                    stuck.Any(s => s.StartsWith("trees") || s.StartsWith("room")) ? $"can't finish your contract {contract.UILink()}: it needs {needs}." : null);
                if (plants > 0) status = $"Harvested {did} ({progress}), then w{status.Substring(1)}";
            }
            else if (outOfCalories) status = $"Harvesting: {did} for {contract.UILink()} ({progress}); out of calories until it eats.";
            else if (swings > 0 && trees == 0 && plants == 0) status = $"Harvesting: cutting trees for {contract.UILink()} ({progress}).";
            else status = $"Harvesting: {did} for {contract.UILink()} ({progress}).";
            if (problems.Count > 0) status += $" Problems: {string.Join("; ", problems)}.";
            return (status, plants, plants > 0 || delivered > 0 || swings > 0);
        }

        /// <summary>A working sickle or scythe (or, with axe, an axe) in the Logbook's storage or the worker's inventory, the sturdiest first.</summary>
        public static ToolItem FindTool(RabotnikComponent c, User worker, bool axe = false)
        {
            var inventories = new List<Inventory> { worker.Inventory };
            var logbook = c.Parent?.GetComponent<PublicStorageComponent>()?.Inventory;
            if (logbook != null) inventories.Add(logbook);
            return inventories.SelectMany(i => i.NonEmptyStacks).Select(s => s.Item).OfType<ToolItem>()
                .Where(t => (axe ? IsAxe(t) : IsPlantTool(t)) && t.Durability > 0)
                .OrderByDescending(t => t.Durability)
                .FirstOrDefault();
        }

        /// <summary>Ripe plants of the contract's crops on its deed (what ContractClauseHarvest still counts as unharvested).</summary>
        static List<Plant> RipePlants(ContractClauseHarvest clause)
        {
            if (clause.Property?.Deed == null) return new List<Plant>();
            var species = new HashSet<Species>(clause.GetCropSpecies());
            return PropertyUtils.TopBlocksOnProperty(clause.Property.Pos.XZ)
                .Select(p => EcoSim.PlantSim.GetPlant(p + Vector3i.Up)).NonNull()
                .Where(p => species.Contains(p.Species) && p.Ripe)
                .ToList();
        }

        /// <summary>PlantEntity.TryHarvest for a User: same checks, yield, action and after-effects, with the worker in place of the Player.</summary>
        static Result HarvestPlant(User worker, PlantEntity plant, ToolItem tool)
        {
            var species = plant.Species;
            var can = plant.CanHarvest(null, false); // doesn't touch the player
            if (!can.Success) return can;

            var yield = Yield(worker, plant);
            var destroy = species.PostHarvestingGrowth == 0f || species.ScythingKills || plant.ClientBasedGrowthPercent < species.PostHarvestingGrowth + 0.1f;
            using var pack = new GameActionPack();
            var changeSet = pack.GetOrCreateInventoryChangeSet(worker.Inventory, worker);
            foreach (var stack in yield) changeSet.AddItemsNonUnique(stack.Item.Type, stack.Quantity);
            pack.AddGameAction(new HarvestOrHunt
            {
                Citizen = worker,
                Species = species.GetType(),
                HarvestedStacks = yield,
                ActionLocation = plant.Position.XYZi(),
                AccessNeeded = AccessType.ConsumerAccess,
                ToolUsed = tool,
                DamagedOrDestroyed = destroy ? DamagedOrDestroyed.DestroyingOrganism : DamagedOrDestroyed.NotDestroyingOrganism,
                DestroyedByBlock = false,
            });
            pack.AddPostEffect(() => OnHarvested(worker, plant, destroy));
            try { return pack.TryPerform(worker); }
            catch (Exception e) { Log.WriteException(e); return Result.FailLoc($"{e.Message}"); }
        }

        /// <summary>PlantEntity.CalculateHarvestResources with the worker as the harvester.</summary>
        static List<ItemStack> Yield(User worker, PlantEntity plant)
        {
            var list = new List<ItemStack>();
            var species = plant.Species;
            if (!plant.Alive || species.ResourceList.Count == 0) return list;
            foreach (var resource in species.ResourceList)
            {
                if (!RandomUtil.Chance(resource.ResourceChance)) continue;
                var multiplier = 1f;
                if (plant.ClientBasedGrowthPercent >= species.ResourceBonusAtGrowth)
                    multiplier = ItemAttribute.Get<YieldAttribute>(resource.ResourceType)?.Yield.GetCurrentValue(worker) ?? 1f;
                var quantity = (int)YieldMethod.Invoke(plant, new object[] { resource.ResourceRange, multiplier });
                var item = Item.GetNonUniqueOrClone(resource.ResourceType);
                quantity = (int)BonusManager.ApplyBonuses(worker.MakeBonusContext(BonusAction.HarvestYield, item), quantity);
                if (quantity <= 0 || resource.ResourceType == null) continue;
                var stack = new ItemStack(item, quantity);
                if (worker.Talentset.OnPlantHarvest != null) stack = worker.Talentset.OnPlantHarvest(plant, stack, worker);
                list.Add(stack);
            }
            return list;
        }

        /// <summary>PlantEntity.OnHarvested with the worker as the harvester: XP, then destroy the plant or reset its growth.</summary>
        static void OnHarvested(User worker, PlantEntity plant, bool destroy)
        {
            var species = plant.Species;
            if (plant.SkillType != null) worker.Skillset.AddExperience(plant.SkillType, species.ExperiencePerHarvest, Localizer.Format("harvesting {0}", species.DisplayName));
            if (destroy)
            {
                EcoSim.PlantSim.DestroyPlant(plant, DeathType.Harvesting, true, worker);
                Eco.World.World.ForceUpdate(plant.Position.XYZi());
                plant.ProcessSoilUnderneeth();
            }
            else if (plant.GrowthPercent > species.PostHarvestingGrowth + 0.1f)
            {
                plant.GrowthPercent = species.PostHarvestingGrowth;
                plant.YieldPercent = plant.ClientBasedGrowthPercent;
                Eco.World.World.ForceUpdate(plant.Position.XYZi());
            }
        }

        /// <summary>Moves harvested items from the worker's inventory into the contract's container, as far as the container tracker still expects them. Returns how many moved.</summary>
        static int Deliver(User worker, ContractClauseHarvest clause, Inventory container, List<string> problems)
        {
            var moved = 0;
            foreach (var stack in worker.Inventory.NonEmptyStacks.ToList())
            {
                var item = stack.Item;
                var qty = Math.Min(stack.Quantity, RabotnikContractCore.Remaining(clause.Container.Tracker, item));
                if (qty <= 0) continue;
                var before = worker.Inventory.TotalNumberOfItems(item.Type);
                Result result;
                try { result = worker.Inventory.MoveItems(stack, container, qty, worker); }
                catch (Exception e) { Log.WriteException(e); result = Result.FailLoc($"{e.Message}"); }
                moved += before - worker.Inventory.TotalNumberOfItems(item.Type);
                if (!result.Success) { problems.Add($"couldn't deposit {item.UILink()} in {clause.Container.PickedObj?.UILink()}: {result.Message}"); break; }
            }
            return moved;
        }

        /// <summary>Stump wood pulp and seeds the contract doesn't ask for go into the container too, as far as there's room (they are the client's
        /// land's to keep, like a player's debris cleanup); the rest is left on the ground where the tree stood.</summary>
        static void StoreBonus(User worker, Inventory container, List<ItemStack> bonus, Vector3i treePosition, List<string> problems)
        {
            foreach (var stack in bonus)
            {
                var item = stack.Item;
                var stored = Math.Min(stack.Quantity, RoomFor(container, item, worker));
                if (stored > 0 && !container.TryAddItemsNonUnique(item.Type, stored, worker).Success) stored = 0;
                var rest = stack.Quantity - stored;
                var dropped = rest > 0 ? RabotnikTrees.DropOnGround(worker, item, rest, treePosition) : 0;
                if (dropped > 0) problems.Add($"left {dropped} {item.UILink()} on the ground where the tree stood");
            }
        }

        /// <summary>How many of an item the inventory can still take. Inventory.GetMaxAccepted is the limit for ONE stack (a stockpile holds 20 logs a stack, say),
        /// and Inventory.CanFit misreads its arguments, so add it up per stack: the room left in stacks of the item, plus a full stack for each empty one.</summary>
        static int RoomFor(Inventory inventory, Item item, User worker)
        {
            var perStack = inventory.GetMaxAcceptedVal(item, 0, worker);
            if (perStack <= 0) return 0;
            var room = 0;
            foreach (var stack in inventory.Stacks)
            {
                if (stack.Item == null || stack.Quantity <= 0) room += perStack;
                else if (stack.Item.Type == item.Type) room += Math.Max(0, perStack - stack.Quantity);
            }
            return room;
        }

        /// <summary>Gets a felled tree's items into the container by carrying them in loads: a stack at a time into the worker's inventory, then moved on
        /// (the same move the crops use, which the container tracker counts). Returns how many moved.</summary>
        static int DeliverTree(User worker, ContractClauseHarvest clause, Inventory container, List<ItemStack> items, Vector3i treePosition, List<string> problems)
        {
            var moved = 0;
            foreach (var stack in items)
            {
                var item = stack.Item;
                var before = worker.Inventory.TotalNumberOfItems(item.Type);
                var dropped = 0;
                var left = stack.Quantity;
                while (left > 0)
                {
                    var load = Math.Min(left, Math.Max(1, item.MaxStackSize));
                    var added = worker.Inventory.TryAddItemsNonUnique(item.Type, load, worker);
                    if (!added.Success)
                    {
                        moved += Deliver(worker, clause, container, problems); // empty the backpack and try once more
                        added = worker.Inventory.TryAddItemsNonUnique(item.Type, load, worker);
                    }
                    if (!added.Success) dropped += RabotnikTrees.DropOnGround(worker, item, left, treePosition); // no room even so
                    if (!added.Success) break;
                    left -= load;
                    moved += Deliver(worker, clause, container, problems);
                }
                // What the container wouldn't take stays in the backpack: leave it on the ground where the tree stood.
                var leftover = Math.Min(stack.Quantity, worker.Inventory.TotalNumberOfItems(item.Type) - before);
                if (leftover > 0 && worker.Inventory.TryRemoveItems(new ItemStack(item, leftover)).Success)
                {
                    var put = RabotnikTrees.DropOnGround(worker, item, leftover, treePosition);
                    dropped += put;
                    if (put < leftover) worker.Inventory.TryAddItemsNonUnique(item.Type, leftover - put, worker); // couldn't drop: keep it
                }
                if (dropped > 0) problems.Add($"container full: left {dropped} {item.UILink()} on the ground where the tree stood");
                if (stack.Quantity - moved - dropped > 0 && dropped == 0 && left > 0) problems.Add($"{left} {item.UILink()} lost");
            }
            return moved;
        }

        /// <summary>What a harvest contract asks for, before it's accepted.</summary>
        static string Describe(Contract contract) =>
            string.Join("; ", contract.Clauses.OfType<ContractClauseHarvest>().Select(h => $"{h.OnProperty} into {h.Container.PickedObj?.UILink()}"));
    }
}
