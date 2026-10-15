// Rabotnik - workers take and finish hauling (Transport) contracts posted in their settlement.
// The accept/finish/notice plumbing shared with harvest contracts is in RabotnikContractCore (verified behaviour is noted there).
// Verified in game (contract spike, 2026-10-05; see the eco-contracts skill):
// - Moving items source -> destination with the worker as the acting user raises Take/Put InventoryActions; the contract's
//   auth override lets the worker into the client's containers and its trackers count the moves.
// - The source is locked to its contents at accept time: items the client uses up afterwards stall the contract until restocked,
//   and a contractor can't quit, so the worker reports the shortfall and does party work meanwhile.
namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Eco.Core.Utils;
    using Eco.Gameplay.Economy;
    using Eco.Gameplay.Economy.Contracts;
    using Eco.Gameplay.Items;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Settlements;
    using Eco.Gameplay.Systems.TextLinks;
    using Eco.Gameplay.Utils;
    using Eco.Shared.Localization;
    using Eco.Shared.Logging;
    using Eco.Shared.Time;
    using Eco.Shared.Utils;

    public static class RabotnikContracts
    {
        // Clauses a worker can fulfil: hauling, plus payment and a reputation gate. Anything else needs a player.
        static readonly Type[] Allowed = { typeof(ContractClauseTransport), typeof(ContractClausePayment), typeof(ContractClauseReputation) };

        /// <summary>Works the worker's accepted hauling contract, if any. Activity is "" when it has none; ItemsMoved is what it hauled this tick.</summary>
        public static (string Activity, int ItemsMoved) StepActive(RabotnikComponent c, User worker, Settlement settlement)
        {
            if (!RabotnikContractCore.CanWork(worker, settlement)) return ("", 0);
            RabotnikContractCore.CheckEnded(c, worker);
            var contract = RabotnikContractCore.ActiveOf(worker, harvest: false);
            if (contract == null) return ("", 0);
            c.ContractId = contract.Id;
            return Work(c, worker, contract);
        }

        /// <summary>Takes a new hauling contract when hauling is on, the worker has none, and it did no party labor this tick.</summary>
        public static (string Activity, int ItemsMoved) StepNew(RabotnikComponent c, User worker, Settlement settlement, bool partyLaborDone)
        {
            var config = RabotnikPlugin.Config;
            if (!config.HaulContracts || partyLaborDone || !RabotnikContractCore.CanWork(worker, settlement)) return ("", 0);
            if (RabotnikContractCore.HasActive(worker)) return ("", 0);

            var posted = RabotnikContractCore.Posted(settlement, harvest: false);
            if (posted.Count == 0) return ("", 0);

            // One reason per contract passed over, so the owner sees why each was skipped.
            var problems = new List<string>();
            var eligible = new List<(Contract Contract, float PayPerItem, float Calories)>();
            foreach (var contract in posted)
            {
                if (Eligible(worker, settlement, contract, out var payPerItem, out var calories, out var why)) eligible.Add((contract, payPerItem, calories));
                else problems.Add($"{contract.UILink()}: {why}");
            }
            foreach (var (contract, _, calories) in eligible.OrderByDescending(e => e.PayPerItem))
            {
                var accepted = RabotnikContractCore.TryAccept(worker, contract);
                if (!accepted.Success) { problems.Add($"couldn't accept {contract.UILink()}: {accepted.Message}"); continue; }
                c.ContractId = contract.Id;
                c.ContractCalories = calories;
                c.LastContractNotice = null;
                RabotnikContractCore.Notice(c, $"accepted the contract {contract.UILink()} from {contract.ClientUser.UILink()}: haul {Describe(contract)} for {RabotnikContractCore.PayText(contract)}.");
                return Work(c, worker, contract);
            }
            return ($"No contract taken: {string.Join("; ", problems.Select(p => p.TrimEnd('.')))}.", 0);
        }

        static bool Eligible(User worker, Settlement settlement, Contract contract, out float payPerItem, out float calories, out string why)
        {
            var config = RabotnikPlugin.Config;
            payPerItem = 0;
            calories = 0;
            var bad = contract.Clauses.FirstOrDefault(x => !Allowed.Contains(x.GetType()));
            if (bad != null) { why = $"has a {bad.Name} clause (only Transport and Payment)"; return false; }
            var transports = contract.Clauses.OfType<ContractClauseTransport>().ToList();
            if (transports.Count == 0) { why = "not a hauling contract"; return false; }
            if (!contract.AutoAccept) { why = "needs the client's approval (Auto-Accept off)"; return false; }
            var pay = contract.Clauses.OfType<ContractClausePayment>().FirstOrDefault();
            if (pay != null && pay.Deposit > 0) { why = "asks a deposit"; return false; }
            if (contract.ClientUser == null || contract.ClientUser == worker) { why = "no other client"; return false; }

            var items = 0;
            foreach (var t in transports)
            {
                if (t.MoveSource.PickedObj == null || t.MoveDestination.PickedObj == null) { why = "a container is missing"; return false; }
                var stacks = t.MoveSource.ItemStacks?.ToList() ?? new List<ItemStack>();
                var count = stacks.Sum(s => s.Quantity);
                if (count == 0) { why = $"{t.MoveSource.PickedObj.UILink()} is empty"; return false; }
                var fits = t.MoveDestination.CheckAnyCanContain(stacks);
                if (!fits.Success) { why = fits.Message.ToString(); return false; }
                items += count;
            }

            // Time: ticks needed times how often this worker gets an update (round-robin over all workers).
            var estimate = Math.Ceiling((double)items / Math.Max(1, config.HaulItemsPerTick)) * RabotnikContractCore.SecondsPerUpdate();
            var allowed = config.ContractTimeSafety * TimeUtil.HoursToSeconds(contract.Hours);
            if (estimate > allowed) { why = $"needs about {estimate / 3600:0.#} h of hauling, more than {config.ContractTimeSafety:P0} of its {contract.Hours:0.#} h"; return false; }

            var payment = pay?.Payment ?? 0f;
            if (!RabotnikJobValue.IsHaulWorthIt(settlement, pay?.Currency, payment, items, out why)) return false;
            payPerItem = payment / items;
            calories = items * config.CaloriesPerItemHauled;
            why = null;
            return true;
        }

        /// <summary>Hauls up to the per-tick cap, reports shortfalls, and finishes the contract once every clause can succeed.</summary>
        static (string Activity, int ItemsMoved) Work(RabotnikComponent c, User worker, Contract contract)
        {
            var config = RabotnikPlugin.Config;
            var moved = 0;
            var outOfCalories = false;
            var problems = new List<string>();
            var shortages = new List<string>();
            foreach (var clause in contract.Clauses.OfType<ContractClauseTransport>())
            {
                var src = RabotnikContractCore.Storages(clause.MoveSource);
                var dst = RabotnikContractCore.Storages(clause.MoveDestination).FirstOrDefault();
                if (src.Count == 0 || dst == null) { problems.Add("a container is gone"); continue; }
                foreach (var inv in src)
                    foreach (var stack in inv.NonEmptyStacks.ToList())
                    {
                        if (moved >= config.HaulItemsPerTick || outOfCalories) break;
                        var item = stack.Item;
                        var wanted = Math.Min(RabotnikContractCore.Remaining(clause.MoveSource.Tracker, item), RabotnikContractCore.Remaining(clause.MoveDestination.Tracker, item));
                        if (wanted <= 0) continue;
                        var affordable = config.CaloriesPerItemHauled <= 0 ? int.MaxValue : (int)(worker.Stomach.Calories / config.CaloriesPerItemHauled);
                        if (affordable <= 0) { outOfCalories = true; break; }
                        var qty = Math.Min(Math.Min(stack.Quantity, wanted), Math.Min(config.HaulItemsPerTick - moved, affordable));
                        var before = RabotnikContractCore.Count(src, item);
                        Result result;
                        try { result = inv.MoveItems(stack, dst, qty, worker); }
                        catch (Exception e) { Log.WriteException(e); result = Result.FailLoc($"{e.Message}"); }
                        var done = before - RabotnikContractCore.Count(src, item);
                        if (done > 0)
                        {
                            moved += done;
                            worker.Stomach.BurnCalories(done * config.CaloriesPerItemHauled, true);
                        }
                        if (!result.Success) { problems.Add($"couldn't move {item.UILink()} to {clause.MoveDestination.PickedObj?.UILink()}: {result.Message}"); break; }
                    }

                // Items locked at accept that the source no longer has: only the client can fix this.
                foreach (var need in clause.MoveSource.Tracker.RemainingStacks?.Where(s => s.Quantity > 0 && s.StackObject != null).ToList() ?? new List<IStack>())
                {
                    var have = src.SelectMany(i => i.NonEmptyStacks).Where(s => need.StackObject.RepresentsItemType(s.Item.Type)).Sum(s => s.Quantity);
                    if (have < need.Quantity) shortages.Add($"{need.Quantity - have} {need.StackObject.UILink()} in {clause.MoveSource.PickedObj?.UILink()}");
                }
            }

            var completed = RabotnikContractCore.TryFinish(c, worker, contract, problems, "Hauling");
            if (completed != null) return (completed, moved);

            var progress = Progress(contract);
            string status;
            if (shortages.Count > 0)
            {
                status = $"Waiting: {contract.UILink()} needs {string.Join(", ", shortages)} restocked.";
                RabotnikContractCore.NotifyWaiting(c, contract, status,
                    $"is waiting on the contract {contract.UILink()}: it needs {string.Join(", ", shortages)} restocked. Meanwhile it does other work.",
                    $"can't finish your contract {contract.UILink()}: {string.Join(", ", shortages)} that were there when it accepted are missing. Put them back so it can finish.");
                if (moved > 0) status = $"Hauled {moved} items ({progress}), then w{status.Substring(1)}";
            }
            else if (outOfCalories) status = $"Hauling: {moved} items for {contract.UILink()} ({progress}); out of calories until it eats.";
            else status = $"Hauling: {moved} items for {contract.UILink()} ({progress}).";
            if (problems.Count > 0) status += $" Problems: {string.Join("; ", problems)}.";
            return (status, moved);
        }

        /// <summary>"done/total items" over the contract's Transport clauses (put side: what reached the destination).</summary>
        static string Progress(Contract contract)
        {
            var trackers = contract.Clauses.OfType<ContractClauseTransport>().Select(t => t.MoveDestination.Tracker).ToList();
            var total = trackers.Sum(t => t.TrackingStacks?.Sum(s => s.Quantity) ?? 0);
            var left = trackers.Sum(t => t.RemainingStacks?.Where(s => s.Quantity > 0).Sum(s => s.Quantity) ?? 0);
            return $"{total - left}/{total} delivered";
        }

        /// <summary>What a contract asks to haul, before it's accepted (the source's current contents).</summary>
        static string Describe(Contract contract)
        {
            var stacks = contract.Clauses.OfType<ContractClauseTransport>().SelectMany(t => t.MoveSource.Tracker.TrackingStacks?.Where(s => s.Quantity > 0) ?? Enumerable.Empty<IStack>()).ToList();
            var total = stacks.Sum(s => s.Quantity);
            var from = contract.Clauses.OfType<ContractClauseTransport>().Select(t => t.MoveSource.PickedObj?.UILink().ToString()).FirstOrDefault();
            var to = contract.Clauses.OfType<ContractClauseTransport>().Select(t => t.MoveDestination.PickedObj?.UILink().ToString()).FirstOrDefault();
            return $"{total} items from {from} to {to}";
        }
    }
}
