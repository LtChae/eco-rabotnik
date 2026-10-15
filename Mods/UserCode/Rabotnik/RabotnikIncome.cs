// Rabotnik - what the worker has earned and spent, read from its bank account ledger.
// The ledger doesn't say where money came from, so "earned" is every deposit except what the owner funded through the
// Logbook, and "spent" is every withdrawal (rent, food). Both are totalled per currency and saved on the component.
namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Eco.Gameplay.Economy;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Systems.TextLinks;

    public static class RabotnikIncome
    {
        /// <summary>Starts counting from now: ledger entries that already exist (an earlier job, or the owner's first funding) aren't income.</summary>
        public static void Baseline(RabotnikComponent component, User worker)
        {
            component.LastLedgerTime = Entries(worker).Select(t => t.Time).DefaultIfEmpty(0).Max();
            component.EarnedTotals = "";
            component.SpentTotals = "";
            component.PendingFunding.Clear();
            component.LastBalances.Clear();
        }

        /// <summary>The owner funded the worker through the Logbook: that deposit isn't income.</summary>
        public static void NoteFunding(RabotnikComponent component, Currency currency, float amount)
        {
            var key = currency.Name;
            component.PendingFunding[key] = component.PendingFunding.GetValueOrDefault(key) + amount;
        }

        /// <summary>Tallies ledger entries since the last update.</summary>
        public static void Update(RabotnikComponent component, User worker)
        {
            var last = component.LastLedgerTime;
            foreach (var t in Entries(worker))
            {
                var currency = t.Currency;
                if (currency == null) continue;
                var key = currency.Name;

                // Amount is meant to be signed from this account's point of view; the entry's running balance confirms it.
                var delta = t.Amount;
                if (component.LastBalances.TryGetValue(key, out var previous))
                {
                    var plus = Math.Abs(previous + t.Amount - t.NewBalance);
                    var minus = Math.Abs(previous - t.Amount - t.NewBalance);
                    if (minus < plus) delta = -t.Amount;
                }
                component.LastBalances[key] = t.NewBalance;

                if (t.Time <= component.LastLedgerTime) continue; // seen already; only here to keep the running balance
                last = Math.Max(last, t.Time);
                if (delta > 0)
                {
                    var pending = component.PendingFunding.GetValueOrDefault(key);
                    var funded = Math.Min(pending, delta);
                    if (funded > 0) component.PendingFunding[key] = pending - funded;
                    if (delta - funded > 0) component.EarnedTotals = Add(component.EarnedTotals, key, delta - funded);
                }
                else if (delta < 0) component.SpentTotals = Add(component.SpentTotals, key, -delta);
            }
            component.LastLedgerTime = last;
        }

        /// <summary>"12.5 Dollar, 3 Credit" for a totals string, or "none". Currencies are links when they still exist.</summary>
        public static string Format(string totals)
        {
            var parts = Parse(totals).Where(kv => kv.Value > 0)
                .Select(kv => $"{kv.Value:0.##} {CurrencyManager.Currencies.FirstOrDefault(c => c.Name == kv.Key)?.UILink().ToString() ?? kv.Key}").ToList();
            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }

        // Oldest first, so the running balance can be followed.
        static IEnumerable<Transaction> Entries(User worker) =>
            worker.BankAccount?.Ledger?.Transactions?.Snapshot?.OrderBy(t => t.Time) ?? Enumerable.Empty<Transaction>();

        // "Name=amount;Name=amount", saved as one string to keep the save format simple.
        static Dictionary<string, float> Parse(string totals)
        {
            var result = new Dictionary<string, float>();
            foreach (var part in (totals ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var i = part.LastIndexOf('=');
                if (i > 0 && float.TryParse(part.Substring(i + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                    result[part.Substring(0, i)] = v;
            }
            return result;
        }

        static string Add(string totals, string currency, float amount)
        {
            var map = Parse(totals);
            map[currency] = map.GetValueOrDefault(currency) + amount;
            return string.Join(";", map.Select(kv => $"{kv.Key.Replace(';', ' ').Replace('=', ' ')}={kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        }
    }
}
