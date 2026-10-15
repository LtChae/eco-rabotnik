// Rabotnik - decides whether a work party's pay is worth the calories its labor burns.
// Estimate: pay per labor (the party's currency payments, spread over the party's total labor) must cover the
// cheapest shop food (price per calorie, in the pay's currency) for the calories one labor point burns.
// No currency pay, or no food sold in the pay's currency, counts as not worth it.
// The same ratio tells RabotnikReputation whether pay was generous.
namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Eco.Gameplay.Economy;
    using Eco.Gameplay.Economy.WorkParties;
    using Eco.Gameplay.Items;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Settlements;
    using Eco.Shared.Items;

    public static class RabotnikJobValue
    {
        // Cheapest price per calorie by settlement and currency, cached for a tick's worth of time so a round of workers shares one store scan.
        static readonly Dictionary<(Settlement, Currency), (DateTime At, float? Cost)> costCache = new();

        /// <summary>True if the order's pay covers the food cost of its labor. why explains the numbers if not.
        /// best is the best-paying currency's pay-to-food ratio and pay per calorie (Currency null if none could be measured),
        /// measured even when RequirePayCoversFood is off (RabotnikReputation uses it).</summary>
        public static bool IsWorthIt(User worker, Settlement settlement, WorkParty party, WorkOrder order, out string why,
            out (Currency Currency, float Ratio, float PayPerCalorie) best)
        {
            var config = RabotnikPlugin.Config;
            why = null;
            best = default;

            var labor = order.PossibleLaborContributeAmount(worker, 1f, false, out var calories).Val;
            if (labor <= 0 || calories <= 0) return true; // free labor: nothing to weigh
            var caloriesPerLabor = calories / labor;

            var totalLabor = party.Work.OfType<LaborWork>().Where(w => w.Order != null).Sum(w => w.Order.LaborFullCost);
            if (totalLabor <= 0) return true;
            var laborers = Math.Max(1, party.Laborers.Count());

            var anyPay = false;
            foreach (var pay in party.Payment.OfType<CurrencyPayment>().Where(p => p.Currency != null && p.Amount > 0))
            {
                anyPay = true;
                var costPerCalorie = FoodCostPerCalorie(settlement, pay.Currency);
                if (costPerCalorie == null) continue;
                // Evenly split pay is a share of the whole pool per laborer; by work percent it is the pool over all the labor.
                var share = pay.PayType == PayType.SplitEvenly ? pay.Amount / laborers : pay.Amount;
                var payPerCalorie = share / totalLabor / caloriesPerLabor;
                var ratio = costPerCalorie.Value <= 0 ? float.MaxValue : payPerCalorie / costPerCalorie.Value;
                if (ratio > best.Ratio) best = (pay.Currency, ratio, payPerCalorie);
            }
            if (!config.RequirePayCoversFood || best.Ratio >= config.MinPayToFoodRatio) return true;

            why = !anyPay ? "no currency pay"
                : best.Currency == null ? "no shop sells food in the pay's currency"
                : $"pay covers only {best.Ratio:P0} of the food cost (needs {config.MinPayToFoodRatio:P0})";
            return false;
        }

        /// <summary>True if a hauling contract's payment covers the food cost of the calories hauling its items burns. why explains the numbers if not.</summary>
        public static bool IsHaulWorthIt(Settlement settlement, Currency currency, float payment, int items, out string why)
        {
            return IsContractWorthIt(settlement, currency, payment, items * RabotnikPlugin.Config.CaloriesPerItemHauled, out why);
        }

        /// <summary>True if a contract's payment covers the food cost of the calories its work burns. why explains the numbers if not.</summary>
        public static bool IsContractWorthIt(Settlement settlement, Currency currency, float payment, float calories, out string why)
        {
            var config = RabotnikPlugin.Config;
            why = null;
            if (!config.RequirePayCoversFood) return true;
            if (calories <= 0) return true; // free work: nothing to weigh
            if (currency == null || payment <= 0) { why = "no currency pay"; return false; }
            var ratio = PayToFoodRatio(settlement, currency, payment, calories);
            if (ratio == null) { why = "no shop sells food in the pay's currency"; return false; }
            if (ratio >= config.MinPayToFoodRatio) return true;
            why = $"pay covers only {ratio:P0} of the food cost (needs {config.MinPayToFoodRatio:P0})";
            return false;
        }

        /// <summary>Payment over the cheapest food cost of the calories, or null if there's no pay, no calories, or no food sold in the currency.</summary>
        public static float? PayToFoodRatio(Settlement settlement, Currency currency, float payment, float calories)
        {
            if (settlement == null || currency == null || payment <= 0 || calories <= 0) return null;
            var costPerCalorie = FoodCostPerCalorie(settlement, currency);
            if (costPerCalorie == null) return null;
            if (costPerCalorie <= 0) return float.MaxValue; // free food: any pay covers it
            return payment / (calories * costPerCalorie.Value);
        }

        /// <summary>Cheapest currency per calorie for in-stock food in the settlement's shops that use this currency, or null if none.</summary>
        public static float? FoodCostPerCalorie(Settlement settlement, Currency currency)
        {
            var key = (settlement, currency);
            var ttl = TimeSpan.FromSeconds(Math.Max(5, RabotnikPlugin.Config.TickSeconds));
            if (costCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < ttl) return hit.Cost;

            float? cheapest = null;
            foreach (var store in RabotnikShopping.Stores(settlement).Where(s => s.Currency == currency))
                foreach (var offer in store.StoreData.SellOffers)
                {
                    if (offer.Buying || offer.IsTagOffer || offer.Price < 0 || offer.Stack?.Item is not FoodItem food || offer.Stack.Quantity <= 0 || food.Calories <= 0) continue;
                    var cost = offer.Price / food.Calories;
                    if (cheapest == null || cost < cheapest) cheapest = cost;
                }
            costCache[key] = (DateTime.UtcNow, cheapest);
            return cheapest;
        }
    }
}
