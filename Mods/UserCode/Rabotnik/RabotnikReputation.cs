// Rabotnik - workers give reputation to the players who treat them well (or, if turned on, badly).
// Three moments are judged: a contract paid out (to the client), a work party joined (to its creator), and food bought
// from a shop (to the shop's owner). Pay is measured like RabotnikJobValue does: against the cheapest shop food for the
// calories the work burns. Food prices are measured against the worker's own wage (pay per calorie of its recent jobs).
// ReputationManager.GiveRep needs no Player (verified by decompiling v0.14.1.1; vanilla's /culture vincent gives from a
// test user too), so a connectionless worker can give. Vanilla's per-giver limits apply unless turned off in the config.
// Cooldowns and the per-player daily cap aren't saved: a restart clears them, but vanilla's own daily limits still hold.
namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using Eco.Gameplay.Components.Store;
    using Eco.Gameplay.Economy;
    using Eco.Gameplay.Economy.Contracts;
    using Eco.Gameplay.Economy.Reputation;
    using Eco.Gameplay.Economy.WorkParties;
    using Eco.Gameplay.Items;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Settlements;
    using Eco.Gameplay.Systems.TextLinks;
    using Eco.Shared.Items;
    using Eco.Simulation.Time;

    public enum RabotnikRepReason { Contract, WorkParty, Food }

    public static class RabotnikReputation
    {
        /// <summary>Weight of the newest job in the worker's wage average.</summary>
        const float WageWeight = 0.3f;

        static readonly object sync = new();
        // (worker, target, reason) -> world day reputation was last given.
        static readonly Dictionary<(int, int, RabotnikRepReason), double> lastGiven = new();
        // (target, world day) -> reputation (absolute) all Rabotniks gave that player that day.
        static readonly Dictionary<(int, int), float> givenToday = new();

        /// <summary>Judges a paid contract. paid is what reached the worker; calories is the estimate made when it accepted.</summary>
        public static void JudgeContract(RabotnikComponent c, User worker, Contract contract, Currency currency, float paid, float calories)
        {
            if (currency == null || paid <= 0 || calories <= 0) return;
            RecordWage(c, currency, paid / calories);
            var ratio = RabotnikJobValue.PayToFoodRatio(worker.DirectCitizenship, currency, paid, calories);
            if (ratio == null) return;
            Judge(c, worker, contract.ClientUser, RabotnikRepReason.Contract, ratio.Value, contract.Name, contract.UILink().ToString());
        }

        /// <summary>Judges a work party the worker just joined. ratio and payPerCalorie are from RabotnikJobValue.IsWorthIt.</summary>
        public static void JudgeWorkParty(RabotnikComponent c, User worker, WorkParty party, Currency currency, float ratio, float payPerCalorie)
        {
            if (currency == null || ratio <= 0) return;
            RecordWage(c, currency, payPerCalorie);
            Judge(c, worker, party.Owner, RabotnikRepReason.WorkParty, ratio, party.Name, party.UILink().ToString());
        }

        /// <summary>Judges food just bought: its price per calorie against the worker's wage in the store's currency.</summary>
        public static void JudgeFood(RabotnikComponent c, User worker, Settlement settlement, StoreComponent store, FoodItem food, float price)
        {
            if (store?.Currency == null || food == null || food.Calories <= 0) return;
            var wage = WagePerCalorie(c, settlement, store.Currency);
            if (wage == null) return;
            var ratio = price <= 0 ? float.MaxValue : wage.Value / (price / food.Calories);
            var owner = store.Parent.Owners as User ?? store.Parent.Creator;
            Judge(c, worker, owner, RabotnikRepReason.Food, ratio, food.DisplayName.ToString(), food.UILink().ToString());
        }

        /// <summary>Gives reputation if the ratio is generous (or stingy, when negative reputation is on), within the switches, cooldown and caps.</summary>
        static void Judge(RabotnikComponent c, User worker, User target, RabotnikRepReason reason, float ratio, string whatName, string whatLink)
        {
            var config = RabotnikPlugin.Config;
            if (!config.GiveReputation || target == null || worker == null || target == worker) return;
            if (reason == RabotnikRepReason.Contract && !config.ReputationForContracts) return;
            if (reason == RabotnikRepReason.WorkParty && !config.ReputationForWorkParties) return;
            if (reason == RabotnikRepReason.Food && !config.ReputationForFood) return;
            if (RabotnikService.IsWorkerName(target.Name)) return;
            if (!config.ReputationFromOwnRabotniks && c.Parent?.Owners?.ContainsUser(target) == true) return;

            var food = reason == RabotnikRepReason.Food;
            float amount;
            if (ratio >= (food ? config.GoodFoodWageRatio : config.GenerousPayRatio)) amount = config.ReputationPerGenerousAct;
            else if (config.NegativeReputation && ratio < (food ? config.OverpricedFoodWageRatio : config.StingyPayRatio)) amount = -config.ReputationPerStingyAct;
            else return;
            if (amount == 0) return;

            var now = WorldTime.Day;
            var day = (int)now;
            var key = (worker.Id, target.Id, reason);
            lock (sync)
            {
                if (lastGiven.TryGetValue(key, out var last) && now - last < config.ReputationCooldownDays) return;
                if (config.MaxReputationPerPlayerPerDay > 0)
                {
                    var left = config.MaxReputationPerPlayerPerDay - givenToday.GetValueOrDefault((target.Id, day));
                    if (left <= 0) return;
                    amount = Math.Sign(amount) * Math.Min(Math.Abs(amount), left);
                }
            }

            var note = Note(reason, amount > 0, ratio, whatName);
            var result = ReputationManager.Obj.GiveRep(worker, target, amount, note, ignoreLimit: !config.RespectVanillaReputationLimits,
                ReputationSource.TransferredManually, sendNotice: config.AnnounceReputation);
            var given = result.Val;
            if (given == 0) return;

            lock (sync)
            {
                lastGiven[key] = now;
                foreach (var old in givenToday.Keys.Where(k => k.Item2 < day).ToList()) givenToday.Remove(old);
                givenToday[(target.Id, day)] = givenToday.GetValueOrDefault((target.Id, day)) + Math.Abs(given);
            }
            var signed = given > 0 ? $"+{given:0.##}" : $"{given:0.##}";
            c.LogEvent($"Gave {target.UILink()} {signed} reputation: {Why(reason, given > 0, ratio, whatLink)}.");
        }

        static string Note(RabotnikRepReason reason, bool good, float ratio, string what)
        {
            var why = Why(reason, good, ratio, what);
            return char.ToUpper(why[0]) + why.Substring(1) + ".";
        }

        // "generous pay for the contract X (2.4x the food cost)" and the like. what is a name or a link.
        static string Why(RabotnikRepReason reason, bool good, float ratio, string what)
        {
            var times = ratio >= float.MaxValue / 2 ? "free" : $"{ratio:0.#}x"; //noloc
            switch (reason)
            {
                case RabotnikRepReason.Contract: return good ? $"generous pay for the contract {what} ({times} the food cost)" : $"stingy pay for the contract {what} ({times} the food cost)";
                case RabotnikRepReason.WorkParty: return good ? $"generous pay at the work party {what} ({times} the food cost)" : $"stingy pay at the work party {what} ({times} the food cost)";
                default: return good ? $"sells {what} cheaply (my wage buys {times} its calories)" : $"overprices {what} (my wage buys only {times} its calories)";
            }
        }

        /// <summary>Folds a job's pay per calorie into the worker's average wage in that currency.</summary>
        public static void RecordWage(RabotnikComponent c, Currency currency, float payPerCalorie)
        {
            if (currency == null || payPerCalorie <= 0 || float.IsInfinity(payPerCalorie) || float.IsNaN(payPerCalorie)) return;
            var map = Parse(c.WageRates);
            map[currency.Name] = map.TryGetValue(currency.Name, out var old) ? (1 - WageWeight) * old + WageWeight * payPerCalorie : payPerCalorie;
            c.WageRates = string.Join(";", map.Select(kv => $"{kv.Key.Replace(';', ' ').Replace('=', ' ')}={kv.Value.ToString(CultureInfo.InvariantCulture)}"));
        }

        /// <summary>The worker's wage per calorie in this currency; else the average of the settlement's other Rabotniks; else null.</summary>
        public static float? WagePerCalorie(RabotnikComponent c, Settlement settlement, Currency currency)
        {
            if (Parse(c.WageRates).TryGetValue(currency.Name, out var own)) return own;
            var others = RabotnikService.All
                .Where(o => o != c && o.Worker != null && settlement != null && o.Worker.DirectCitizenship == settlement)
                .Select(o => Parse(o.WageRates).TryGetValue(currency.Name, out var w) ? w : (float?)null)
                .Where(w => w != null).ToList();
            return others.Count == 0 ? null : others.Average();
        }

        // "Name=rate;Name=rate", saved as one string like RabotnikIncome's totals.
        static Dictionary<string, float> Parse(string rates)
        {
            var result = new Dictionary<string, float>();
            foreach (var part in (rates ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var i = part.LastIndexOf('=');
                if (i > 0 && float.TryParse(part.Substring(i + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    result[part.Substring(0, i)] = v;
            }
            return result;
        }
    }
}
