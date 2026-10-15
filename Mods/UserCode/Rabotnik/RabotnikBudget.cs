// Rabotnik - how much money a worker is willing to spend on optional purchases (tools). One rule for all of them: keep enough
// to pay the rent for RentBudgetHours (the same setting as the move-in check) and enough to refill the stomach with the
// cheapest shop food. Food itself isn't held to this: eating comes first, and RabotnikShopping spends what it needs.
namespace Eco.Mods.TechTree
{
    using System;
    using Eco.Gameplay.Economy;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Settlements;

    public static class RabotnikBudget
    {
        /// <summary>Rent for RentBudgetHours at the worker's home, if the rent is in this currency.</summary>
        public static float RentReserve(User worker, Currency currency)
        {
            var rent = RabotnikHousing.CurrentRent(worker);
            if (rent == null || rent.Value.Currency != currency) return 0;
            return rent.Value.PerHour * RabotnikPlugin.Config.RentBudgetHours;
        }

        /// <summary>The cheapest shop food (in this currency) for a full stomach, less what the Logbook's storage already holds. 0 if no shop sells food in it.</summary>
        public static float FoodReserve(RabotnikComponent c, User worker, Settlement settlement, Currency currency)
        {
            var cost = RabotnikJobValue.FoodCostPerCalorie(settlement, currency);
            if (cost == null) return 0;
            var stocked = RabotnikNeeds.Supplies(c).calories;
            return Math.Max(0f, worker.Stomach.MaxCalories - stocked) * cost.Value;
        }

        public static float Reserve(RabotnikComponent c, User worker, Settlement settlement, Currency currency) =>
            RentReserve(worker, currency) + FoodReserve(c, worker, settlement, currency);

        /// <summary>What the worker may spend in this currency: its balance less the rent and food reserve.</summary>
        public static float Spendable(RabotnikComponent c, User worker, Settlement settlement, Currency currency)
        {
            if (currency == null || worker.BankAccount == null) return 0;
            return Math.Max(0f, worker.BankAccount.GetCurrencyHoldingVal(currency) - Reserve(c, worker, settlement, currency));
        }

        /// <summary>"has 12 Ruble, keeping 9.5 for rent and 6 for food", for status notes.</summary>
        public static string Explain(RabotnikComponent c, User worker, Settlement settlement, Currency currency)
        {
            var has = worker.BankAccount?.GetCurrencyHoldingVal(currency) ?? 0f;
            return $"has {has:0.##} {currency.Name}, keeping {RentReserve(worker, currency):0.##} for rent and {FoodReserve(c, worker, settlement, currency):0.##} for food"; //noloc
        }
    }
}
