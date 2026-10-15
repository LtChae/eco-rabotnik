// Rabotnik - a worker that needs a harvesting tool and has no working one sells its broken tools and buys a replacement from
// the settlement's shops. Like a player using a tool repair service: broken tools are sold to shops that buy them (for the
// share of a new tool's price the shop's owner offers), repaired ones are bought back. Selling goes first, so the money
// counts toward the purchase. Trades go through StoreComponent.DoPerformTrade, the same path as RabotnikShopping (access,
// stock, price, tax and inventory moves are vanilla). Sold items must be in the worker's own inventory, so a broken tool in
// the Logbook's storage is moved there first (plain inventory moves with no user, like eating from the Logbook).
// Money: a purchase may only use what RabotnikBudget leaves spendable (rent and a full stomach are kept back).
// Barter shops (no currency) are skipped: a barter trade needs the worker to hand over goods, and it owns too few personal effects for that to be viable.
namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Eco.Core.Systems;
    using Eco.Core.Utils;
    using Eco.Gameplay.Components;
    using Eco.Gameplay.Components.Storage;
    using Eco.Gameplay.Components.Store;
    using Eco.Gameplay.Economy;
    using Eco.Gameplay.Items;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Settlements;
    using Eco.Gameplay.Systems.TextLinks;
    using Eco.Shared.Serialization;

    public static class RabotnikTools
    {
        // Shop searches are throttled per worker; the last note is repeated meanwhile so the status doesn't flicker.
        static readonly Dictionary<(RabotnikComponent, bool), (DateTime At, string Note)> lastAttempt = new();
        static readonly object attemptLock = new();

        /// <summary>A working harvest tool for the worker (an axe if axe, else a sickle or scythe): one it already has, or one bought after selling its broken tools.
        /// Null if it can't get one; note says why (or what it did). Respects the BuyHarvestTools setting.</summary>
        public static ToolItem EnsureHarvestTool(RabotnikComponent c, User worker, Settlement settlement, out string note, bool axe = false)
        {
            note = null;
            var tool = RabotnikHarvest.FindTool(c, worker, axe);
            if (tool != null) return tool;

            var config = RabotnikPlugin.Config;
            if (!config.BuyHarvestTools) { note = "buying tools is turned off"; return null; }
            var ttl = TimeSpan.FromSeconds(Math.Max(30, config.TickSeconds * 3));
            lock (attemptLock)
                if (lastAttempt.TryGetValue((c, axe), out var last) && DateTime.UtcNow - last.At < ttl) { note = last.Note; return null; }

            var actions = new List<string>();
            string failure = null;
            try { failure = Replace(c, worker, settlement, actions, axe); }
            catch (Exception e) { Eco.Shared.Logging.Log.WriteException(e); failure = e.Message; }

            tool = RabotnikHarvest.FindTool(c, worker, axe);
            note = tool != null ? string.Join("; ", actions) : failure ?? "no shop has one";
            lock (attemptLock) lastAttempt[(c, axe)] = (DateTime.UtcNow, tool != null ? null : note);
            return tool;
        }

        /// <summary>Sells broken harvest tools, then buys the best working one of the kind the worker can pay for. Returns why it couldn't get one, or null.</summary>
        static string Replace(RabotnikComponent c, User worker, Settlement settlement, List<string> actions, bool axe)
        {
            if (settlement == null) return "not in a settlement with shops";
            if (worker.BankAccount == null) return "no bank account";
            var stores = RabotnikShopping.Stores(settlement).Where(s => s.Currency != null).ToList();
            if (stores.Count == 0) return "no open shop with a currency in the settlement (barter shops are skipped)";

            GatherBroken(c, worker);
            SellBroken(c, worker, stores, actions);

            var best = BestOffer(c, worker, settlement, stores, axe, out var why);
            if (best == null) return why;
            var item = best.Offer.Stack.Item;
            var before = worker.Inventory.TotalNumberOfItems(item.Type);
            var currency = best.Store.Currency;
            var paid = currency.UILink(best.Offer.Price).ToString();
            var result = Trade(best.Store, worker, new[] { (best.Offer, 1) }, new List<(TradeOffer, int)>());
            if (!result.Success || worker.Inventory.TotalNumberOfItems(item.Type) <= before)
                return $"a shop refused the trade for {item.UILink()}: {(result.Success ? "nothing arrived" : result.Message.ToString())}";
            actions.Add($"bought {item.UILink()} for {paid}");
            RabotnikContractCore.Notice(c, $"bought {item.UILink()} for {paid} to keep harvesting.");
            return null;
        }

        static bool IsBroken(Item item) => RabotnikHarvest.IsHarvestTool(item) && item is ToolItem t && t.Durability <= 0;

        /// <summary>Moves broken harvest tools out of the Logbook's storage into the worker's inventory, where a sale can take them.</summary>
        static void GatherBroken(RabotnikComponent c, User worker)
        {
            var logbook = c.Parent?.GetComponent<PublicStorageComponent>()?.Inventory;
            if (logbook == null) return;
            foreach (var stack in logbook.NonEmptyStacks.Where(s => IsBroken(s.Item)).ToList())
            {
                var item = stack.Item;
                if (!logbook.TryRemoveItems(new ItemStack(item, 1)).Success) continue;
                if (!worker.Inventory.TryAddItem(item).Success) logbook.TryAddItem(item);
            }
        }

        /// <summary>Sells each broken harvest tool to the currency shop that pays the most for it.</summary>
        static void SellBroken(RabotnikComponent c, User worker, List<StoreComponent> stores, List<string> actions)
        {
            foreach (var stack in worker.Inventory.NonEmptyStacks.Where(s => IsBroken(s.Item)).ToList())
            {
                var item = stack.Item;
                var offer = stores.SelectMany(s => s.AllOffers.Where(o => o.Buying && !o.IsTagOffer && o.Price > 0 && o.Stack?.Item != null
                                                                          && Wants(o, item) && s.Balance >= o.Price)
                                                  .Select(o => (Store: s, Offer: o)))
                    .OrderByDescending(x => x.Offer.Price)
                    .Cast<(StoreComponent Store, TradeOffer Offer)?>()
                    .FirstOrDefault();
                if (offer == null) continue;

                var before = worker.Inventory.TotalNumberOfItems(item.Type);
                var result = Trade(offer.Value.Store, worker, new List<(TradeOffer, int)>(), new[] { (offer.Value.Offer, 1) });
                if (result.Success && worker.Inventory.TotalNumberOfItems(item.Type) < before)
                {
                    var got = offer.Value.Store.Currency.UILink(offer.Value.Offer.Price);
                    actions.Add($"sold broken {item.UILink()} for {got}");
                    RabotnikContractCore.Notice(c, $"sold a broken {item.UILink()} for {got}.");
                }
            }
        }

        /// <summary>Does this buy offer want the item (type, durability range and other qualifiers) and still have room for more?</summary>
        static bool Wants(TradeOffer offer, Item item) =>
            offer.MatchesItem(item) && offer.MeetsDurabilityRequirements(item) && offer.MeetsSpecialRequirements(item) && (!offer.ShouldLimit || offer.MaxNumWanted >= 1);

        /// <summary>The working harvest tool for sale with the most durability per price that the worker can pay for within its spendable money. why says what blocked it if none.</summary>
        static Candidate BestOffer(RabotnikComponent c, User worker, Settlement settlement, List<StoreComponent> stores, bool axe, out string why)
        {
            Candidate best = null;
            string tooDear = null;
            foreach (var store in stores)
            {
                foreach (var offer in store.AllOffers)
                {
                    if (offer.Buying || offer.IsTagOffer || offer.Price < 0 || offer.Stack == null || offer.Stack.Quantity <= 0) continue;
                    if (offer.Stack.Item is not ToolItem tool || !(axe ? RabotnikHarvest.IsAxe(tool) : RabotnikHarvest.IsPlantTool(tool)) || tool.Durability <= 0) continue;
                    if (offer.Price > RabotnikBudget.Spendable(c, worker, settlement, store.Currency))
                    {
                        tooDear ??= $"{tool.UILink()} costs {store.Currency.UILink(offer.Price)} but the worker {RabotnikBudget.Explain(c, worker, settlement, store.Currency)}";
                        continue;
                    }
                    var score = tool.Durability / Math.Max(offer.Price, 0.01f);
                    if (best == null || score > best.Score) best = new Candidate { Store = store, Offer = offer, Score = score };
                }
            }
            why = best != null ? null : tooDear ?? (axe ? "no shop sells a working axe" : "no shop sells a working sickle or scythe");
            return best;
        }

        sealed class Candidate
        {
            public StoreComponent Store;
            public TradeOffer Offer;
            public float Score;
        }

        /// <summary>One trade with a store: the offers the worker buys (the store's sell offers) and sells (the store's buy offers), each with a quantity.</summary>
        static Result Trade(StoreComponent store, User worker, IEnumerable<(TradeOffer Offer, int Quantity)> buying, IEnumerable<(TradeOffer Offer, int Quantity)> selling)
        {
            BSONArray Lines(IEnumerable<(TradeOffer Offer, int Quantity)> offers)
            {
                var array = BSONArray.New;
                foreach (var (offer, quantity) in offers)
                {
                    var line = BSONObject.New;
                    line["offer"] = offer.GetOrCreateID();
                    line["amount"] = quantity;
                    array.Add(line);
                }
                return array;
            }
            var trade = BSONObject.New;
            trade["itemsToBuy"] = Lines(buying);
            trade["itemsToSell"] = Lines(selling);
            try { return store.DoPerformTrade(worker, trade, worker.BankAccount); }
            catch (ArgumentException e) when (e.Message.Contains("RPC")) { return Result.Succeeded; } // client notice after the trade is applied; no client
        }
    }
}

