// Rabotnik - workers supply labor to work parties in their settlement.
// Verified in game: WorkParty.Join(User, Laborer) needs the user within 8 m of the party's board, so the
// worker is moved there first; WorkOrder.TryToContributeLabor(User) has no distance check, burns the worker's calories,
// and applies skills, talents, exhaustion and laws. Payment to laborers is the party's own (vanilla) payment.
// A worker can't leave a party after contributing (vanilla), so it stays until the party completes.
namespace Eco.Mods.TechTree
{
    using System.Collections.Generic;
    using System.Linq;
    using Eco.Core.Systems;
    using Eco.Gameplay.Economy.WorkParties;
    using Eco.Gameplay.Items;
    using Eco.Gameplay.Placement;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Property;
    using Eco.Gameplay.Settlements;
    using Eco.Gameplay.Skills;
    using Eco.Gameplay.Systems.TextLinks;
    using Eco.Shared.Items;

    public static class RabotnikWork
    {
        /// <summary>Joins a suitable work party if needed and contributes labor. Returns a status line; laborDone is the labor contributed this step.
        /// Returns "" while the worker isn't a housed citizen: the housing line already says why.</summary>
        public static string Step(RabotnikComponent c, User worker, Skill skill, Settlement settlement, out float laborDone)
        {
            laborDone = 0;
            if (settlement == null || worker.DirectCitizenship != settlement || Residency.GetResidencyDeed(worker) == null) return "";
            if (skill == null) return "Not working: no skill yet.";

            // Parties the worker is already in come first, so it finishes what it started.
            var parties = Registrars.All<WorkParty>()
                .Where(p => p.State == ProposableState.Active && ((ISettlementAssociated)p).Settlement == settlement)
                .OrderByDescending(p => p.ContainsUser(worker))
                .ToList();
            if (parties.Count == 0) return "Not working: no active work party in the settlement.";

            // One reason per party passed over, so the owner sees why each was skipped, not just the last.
            var problems = new List<string>();
            foreach (var party in parties)
            {
                var order = FindWork(party, worker, skill, out var noWork);
                if (order == null) { problems.Add($"{party.UILink()}: {noWork}"); continue; }

                if (!party.ContainsUser(worker))
                {
                    // Only judge parties not yet joined: a worker can't leave once it has contributed.
                    if (!RabotnikJobValue.IsWorthIt(worker, settlement, party, order, out var why, out var pay)) { problems.Add($"skipped {party.UILink()}: {why}"); continue; }
                    var slot = party.Laborers.FirstOrDefault(l => l.Citizen == null);
                    if (slot == null) { problems.Add($"{party.UILink()} has no open laborer slot"); continue; }
                    var board = ((IHostedObject)party).HostObject.Object;
                    if (board != null) { worker.Position = board.Position; worker.MarkDirty(); }
                    var join = party.Join(worker, slot);
                    if (!join.Success) { problems.Add($"couldn't join {party.UILink()}: {join.Message}"); continue; }
                    // The party's pay is escrowed while it runs, and a worker joins a party once, so judge the pay now.
                    RabotnikReputation.JudgeWorkParty(c, worker, party, pay.Currency, pay.Ratio, pay.PayPerCalorie);
                }

                // Labor as long as the worker has the calories for it (vanilla refuses once it can't afford the next
                // action), the order still needs labor, and the safety cap allows. The worker eats on the next update.
                var before = order.LaborPerformed;
                Eco.Core.Utils.Result result = null;
                for (var i = 0; i < RabotnikPlugin.Config.MaxLaborActionsPerTick; i++)
                {
                    var performed = order.LaborPerformed;
                    result = order.TryToContributeLabor(worker);
                    if (!result.Success || order.LaborPerformed <= performed) break;
                }
                var done = order.LaborPerformed - before;
                laborDone = done;
                if (done > 0) return $"Working: {done:0} labor on {order.Recipe.UILink()} for {party.UILink()} ({order.LaborPerformed:0}/{order.LaborFullCost:0}).";
                problems.Add($"couldn't labor on {order.Recipe.UILink()} for {party.UILink()}: {result?.Message}");
            }
            return $"Not working: {string.Join("; ", problems.Select(p => p.TrimEnd('.')))}.";
        }

        /// <summary>A work order in the party that still needs labor the worker can do with its one skill.</summary>
        static WorkOrder FindWork(WorkParty party, User worker, Skill skill, out string why)
        {
            var reasons = new List<string>();
            foreach (var o in party.Work.OfType<LaborWork>().Select(lw => lw.Order).Where(o => o != null))
            {
                if (!UsesOnly(o.Recipe.RequiredSkills, skill, worker))
                {
                    var needs = string.Join("/", o.Recipe.RequiredSkills.Select(r => $"{r.SkillItem?.UILink().ToString() ?? r.SkillType.Name} {r.Level}")); //noloc
                    reasons.Add($"{o.Recipe.UILink()} needs {needs}");
                    continue;
                }
                var possible = o.PossibleLaborContributeAmount(worker, 1f, true, out var calories);
                if (possible.Val > 0) { why = null; return o; }
                reasons.Add($"{o.Recipe.UILink()} can't be labored now ({(string.IsNullOrEmpty(possible.Message.ToString()) ? $"possible labor {possible.Val:0.##}, needs {calories:0} calories, has {worker.Stomach.Calories:0}" : possible.Message.ToString())})"); //noloc
            }
            why = reasons.Count == 0 ? "no labor orders" : string.Join(", ", reasons); //noloc
            return null;
        }

        // The worker only supplies labor with its own skill (or for recipes that need no skill).
        static bool UsesOnly(IEnumerable<RequiresSkillAttribute> required, Skill skill, User worker) =>
            required == null || required.All(r => r.SkillType == skill.GetType() && r.IsMet(worker));
    }
}
