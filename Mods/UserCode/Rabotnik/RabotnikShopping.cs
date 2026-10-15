// Rabotnik - hungry workers with no food in their Logbook buy food from shops in their settlement.
// A worker fills its calorie bar, preferring the food with the most nutrition per currency spent (nutrition is what
// raises the vanilla SkillRate, so this is the cheapest XP), with a bonus for foods it hasn't eaten lately (variety).
// Trades go through StoreComponent.DoPerformTrade, which works for a User without a Player: the same path as the
// store UI (access, stock, price, tax and inventory moves are all vanilla). Bought food lands in the worker's own
// inventory and is eaten from there.
namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Eco.Core.Systems;
    using Eco.Gameplay.Components;
    using Eco.Gameplay.Components.Store;
    using Eco.Gameplay.Items;
    using Eco.Gameplay.Objects;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Settlements;
    using Eco.Gameplay.Systems.TextLinks;
    using Eco.Shared.Math;
    using Eco.Shared.Serialization;

    public static class RabotnikShopping
    {
        /// <summary>Score multiplier for a food the worker has none of in its stomach.</summary>
        const float VarietyBonus = 1.5f;

        sealed class Candidate
        {
            public StoreComponent Store;
            public TradeOffer Offer;
            public FoodItem Food;
            public int Quantity;
            public float Score;
        }

        /// <summary>Buys food if the worker is hungry. Returns a status line, or "" if it didn't need to shop.</summary>
        public static string Step(RabotnikComponent component, User worker, Settlement settlement)
        {
            var config = RabotnikPlugin.Config;
            if (!config.BuyFoodFromShops || worker.Stomach.Calories >= config.EatBelowCalories) return "";
            if (settlement == null) return "Hungry, but not in a settlement with shops.";
            if (worker.BankAccount == null) return "Hungry, but has no bank account.";

            var stores = Stores(settlement);
            if (stores.Count == 0) return "Hungry, but no open shop in the settlement.";

            var calories = worker.Stomach.Calories;
            var spent = new Dictionary<string, float>();
            var currencies = new Dictionary<string, Eco.Gameplay.Economy.Currency>();
            var bought = new List<string>();
            var eaten = 0;
            string stopped = null, rejected = null;
            var maxTrades = Math.Max(1, config.MaxFoodPurchasesPerTick);
            for (var trades = 0; ; trades++)
            {
                if (trades >= maxTrades) { stopped = $"hit the {maxTrades}-trade limit"; break; }
                var pick = Best(worker, stores);
                if (pick == null) { stopped = "no in-stock, affordable food fits in the remaining space"; break; }
                var count = Buy(worker, pick, out var error, out var ate);
                if (count == 0) { rejected = error; stores.Remove(pick.Store); if (stores.Count == 0) { stopped = "every shop rejected the trade"; break; } continue; } // a failed trade: stop asking this shop
                bought.Add($"{count} {pick.Food.UILink()}");
                RabotnikReputation.JudgeFood(component, worker, settlement, pick.Store, pick.Food, pick.Offer.Price);
                if (ate > 0) { eaten += ate; component.LastMeal = pick.Food.UILink().ToString(); }
                var currency = pick.Store.Currency?.Name ?? "?"; currencies[currency] = pick.Store.Currency;
                spent[currency] = spent.GetValueOrDefault(currency) + count * pick.Offer.Price;
            }
            worker.UserXP?.UpdateSkillRate();
            // Shop food is eaten on the spot, so count it like Logbook meals (the Worker tab's "last meal" and "eaten").
            if (eaten > 0) component.MealsEaten += eaten;

            var left = worker.Stomach.MaxCalories - worker.Stomach.Calories;
            var why = left > 1 && stopped != null ? $" Stopped with {left:0} calories of room: {stopped}." : "";
            if (rejected != null) why += $" A shop refused a trade: {rejected}";
            if (bought.Count == 0) return $"Hungry, but bought nothing.{why}".Replace("Stopped with", "Room for").Trim();
            var cost = string.Join(", ", spent.Select(kv => $"{kv.Value:0.##} {(currencies.TryGetValue(kv.Key, out var cur) && cur != null ? cur.UILink().ToString() : kv.Key)}"));
            return $"Bought {string.Join(", ", bought)} for {cost}; ate {eaten}; calories {calories:0} -> {worker.Stomach.Calories:0}.{why}";
        }

        /// <summary>Open stores with a currency, standing in the settlement.</summary>
        internal static List<StoreComponent> Stores(Settlement settlement)
        {
            var stores = new List<StoreComponent>();
            WorldObjectManager.ForEach(o =>
            {
                var s = o.GetComponent<StoreComponent>();
                if (s != null && o.Enabled && s.OnOff.On && s.Currency != null && s.BankAccount != null
                    && SettlementUtils.GetLowestLevelSettlementAtPos(o.Position.XZi()) == settlement)
                    stores.Add(s);
            });
            return stores;
        }

        /// <summary>The in-stock, affordable food that fits in the stomach with the most nutrition per currency.</summary>
        static Candidate Best(User worker, List<StoreComponent> stores)
        {
            var stomach = worker.Stomach;
            var room = stomach.MaxCalories - stomach.Calories;
            Candidate best = null;
            foreach (var store in stores)
            {
                var balance = worker.BankAccount.GetCurrencyHoldingVal(store.Currency);
                foreach (var offer in store.StoreData.SellOffers)
                {
                    if (offer.Buying || offer.IsTagOffer || offer.Price < 0 || offer.Stack?.Item is not FoodItem food || offer.Stack.Quantity <= 0) continue;
                    if (food.Calories <= 0 || food.Calories > room || !stomach.CanEat(food)) continue;

                    var affordable = offer.Price <= 0 ? int.MaxValue : (int)(balance / offer.Price);
                    var quantity = Math.Min(Math.Min(offer.Stack.Quantity, (int)(room / food.Calories)), affordable);
                    if (quantity <= 0) continue;

                    var score = food.Nutrition.NutrientTotal() / Math.Max(offer.Price, 0.0001f);
                    if (!stomach.Contents.Any(e => e.Food?.GetType() == food.GetType())) score *= VarietyBonus;
                    if (best == null || score > best.Score)
                        best = new Candidate { Store = store, Offer = offer, Food = food, Quantity = quantity, Score = score };
                }
            }
            return best;
        }

        /// <summary>Buys the candidate's quantity and eats it. Returns how many were bought (0 if the trade failed).</summary>
        static int Buy(User worker, Candidate pick, out string error, out int ate)
        {
            error = null;
            ate = 0;
            var type = pick.Food.GetType();
            var before = worker.Inventory.TotalNumberOfItems(type);

            var line = BSONObject.New;
            line["offer"] = pick.Offer.GetOrCreateID();
            line["amount"] = pick.Quantity;
            var buying = BSONArray.New;
            buying.Add(line);
            var trade = BSONObject.New;
            trade["itemsToBuy"] = buying;
            trade["itemsToSell"] = BSONArray.New;

            try
            {
                var result = pick.Store.DoPerformTrade(worker, trade, worker.BankAccount);
                if (!result.Success) { error = result.Message; return 0; }
            }
            catch (ArgumentException e) when (e.Message.Contains("RPC")) { } // client notice after the trade is applied; no client
            var got = worker.Inventory.TotalNumberOfItems(type) - before;

            for (var i = 0; i < got; i++)
            {
                var item = Item.Create(type) as FoodItem ?? pick.Food;
                if (!worker.Stomach.CanEat(item) || !worker.Inventory.TryRemoveItem(type).Success) break;
                RabotnikNeeds.EatOne(worker, item);
                ate++;
            }
            return got;
        }
    }
}
