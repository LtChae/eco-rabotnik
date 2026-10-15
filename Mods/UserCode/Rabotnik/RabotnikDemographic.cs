// Rabotnik - the "Rabotnik" demographic: a civics demographic whose only members are Rabotnik workers, so laws can target them.
// Membership is whitelist-only: the demographic has one condition nobody can pass, and workers are whitelisted into it.
// (A demographic with no conditions would contain every user.) Rabotniks also stay in vanilla Abandoned, so a law can
// combine "Rabotnik" and "Abandoned" to exclude Rabotniks from laws aimed at Abandoned players.
namespace Eco.Mods.TechTree
{
    using System;
    using System.Linq;
    using Eco.Core.Systems;
    using Eco.Gameplay.Civics.Demographics;
    using Eco.Gameplay.Civics.GameValues;
    using Eco.Gameplay.Civics.Misc;
    using Eco.Gameplay.Players;
    using Eco.Shared.Items;
    using Eco.Shared.Logging;

    public static class RabotnikDemographic
    {
        public const string DemographicName = "Rabotnik"; //noloc

        static bool backfilled;

        /// <summary>The Rabotnik demographic, created on first use. Null while civics isn't ready yet.</summary>
        public static Demographic GetOrCreate()
        {
            var registrar = Registrars.Get<Demographic>();
            if (registrar == null) return null;
            var demographic = registrar.GetByName(DemographicName) ?? registrar.Add<Demographic>(null, DemographicName);
            if (demographic == null) return null;

            // Idempotent: repeated every call because it's cheap and heals a half-made demographic.
            demographic.SetRequiresHost(false);
            if (demographic.Conditions.Count == 0)
                demographic.Conditions.Add(new Compare { Left = MakeGameValue.Number(0), Comparison = ComparisonType.NotEqualTo, Right = MakeGameValue.Number(0) });
            if (demographic.State != ProposableState.Active) demographic.SetProposedState(ProposableState.Active);
            return demographic;
        }

        /// <summary>Makes the worker a member of the Rabotnik demographic.</summary>
        public static void Apply(User worker)
        {
            var demographic = GetOrCreate();
            if (demographic == null || demographic.ContainsUser(worker)) return;
            demographic.SetWhitelist(worker, true);
        }

        /// <summary>Once per boot, adds every existing worker (including pooled ones) to the demographic. Retries until civics is ready.</summary>
        public static void Backfill()
        {
            if (backfilled) return;
            try
            {
                if (GetOrCreate() == null) return;
                foreach (var worker in UserManager.Users.Where(u => RabotnikService.IsWorkerName(u.Name)).ToList()) Apply(worker);
                backfilled = true;
            }
            catch (Exception e) { Log.WriteException(e); }
        }
    }
}
