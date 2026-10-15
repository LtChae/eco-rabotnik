// Rabotnik - a Logbook acts like artwork: it is a "Cultural" furnishing whose value is the Rabotnik's culture, computed like
// a picture's (the creator's reputation counts, reputation from the settlement's own citizens is discounted). The room value
// feeds the deed's CulturalPropertyValue, which vanilla turns into the settlement's influence radius (borders).
// Vanilla's PictureFrameObject does the same through IHasDynamicHomeFurnishingValue.
namespace Eco.Mods.TechTree
{
    using System;
    using Eco.Gameplay.Culture;
    using Eco.Gameplay.Economy.Reputation;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Rooms;
    using Eco.Gameplay.Settlements;
    using Eco.Shared.IoC;
    using Eco.Shared.Utils;

    public static class RabotnikCulture
    {
        /// <summary>The culture a Rabotnik creates: its XP rate (food capped at FoodXpCultureCap, housing at HousingXpCultureCap), scaled up by its reputation (1x with none, up to 1 + ReputationCultureBonus).
        /// Zero outside a settlement, like vanilla artwork.</summary>
        public static float For(User worker, Settlement settlement)
        {
            if (worker == null || settlement == null) return 0f;
            var config = RabotnikPlugin.Config;
            // The XP rate is food plus housing (as the Worker tab shows); each counts for culture only up to its own ceiling.
            var food = Math.Min(Math.Max(0f, worker.Stomach?.NutrientSkillRate() ?? 0f), config.FoodXpCultureCap);
            var housing = Math.Min(Math.Max(0f, worker.ResidencyPropertyValue?.Value ?? 0f), config.HousingXpCultureCap);
            var rate = food + housing;

            // Reputation counts like an artist's: positive only, with the settlement's own citizens discounted.
            var rep = ReputationManager.Obj.CalculateWeightedPositiveRep((IHasReputation)worker,
                (giver, given) => giver is User u && settlement.Citizenship.HasCitizen(u) ? given * CultureConfig.SameSettlementReputationMutliplier : given);
            // Vanilla's player reputation -> culture curve, as a 0..1 fraction of its limit, so the server's tuning of it still applies.
            var formula = SettlementsPlugin.Obj.Config.PlayerReputationToCultureFormula;
            var repFraction = formula.OutputLimit > 0 ? Math.Clamp(LimitMapper.Map(rep, formula) / formula.OutputLimit, 0f, 1f) : 0f;

            return rate * config.CulturePerXpRate * (1f + repFraction * config.ReputationCultureBonus);
        }

        /// <summary>Re-evaluates the room values of the deed the Logbook stands on, so a changed culture reaches the deed and the borders.</summary>
        public static void RefreshRooms(RabotnikLogbookObject logbook)
        {
            var deed = logbook.GetDeed();
            if (deed != null) Singleton<RoomData>.Obj.UpdateRoomValues(deed.Rooms);
        }
    }
}
