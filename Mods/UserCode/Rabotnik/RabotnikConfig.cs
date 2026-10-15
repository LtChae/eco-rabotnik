// Rabotnik - server settings, saved as Configs/Rabotnik.eco and editable in the server UI.
namespace Eco.Mods.TechTree
{
    using Eco.Shared.Localization;

    [Localized]
    public class RabotnikConfig
    {
        [LocDescription("Seconds between Rabotnik updates.")]
        public int TickSeconds { get; set; } = 30;

        [LocDescription("Maximum number of Rabotniks updated per tick. Rabotniks are updated round-robin, so a large population spreads over several ticks.")]
        public int MaxRabotniksPerTick { get; set; } = 50;

        [LocDescription("Name prefix of old-style Rabotnik users ('<prefix> <number>'), still recognized so existing workers keep working. New Rabotniks get a random first name and a Ra... surname.")]
        public string RabotnikNamePrefix { get; set; } = "Rabotnik";

        [LocDescription("A Rabotnik only moves in if it can pay the move-in fee plus this many hours of rent. It also keeps this many hours of rent, plus enough to refill its stomach with the cheapest shop food, when it spends money on optional purchases such as tools.")]
        public float RentBudgetHours { get; set; } = 24;

        [LocDescription("Rabotniks eat from their Logbook's storage when their calories drop below this.")]
        public float EatBelowCalories { get; set; } = 2000;

        [LocDescription("When the Logbook has no food and the Rabotnik is hungry, it buys food from shops in its settlement, filling its calories with the most nutrition per currency spent.")]
        public bool BuyFoodFromShops { get; set; } = true;

        [LocDescription("Safety cap on shop trades per Rabotnik per tick (each trade buys as much of one food as fits).")]
        public int MaxFoodPurchasesPerTick { get; set; } = 10;

        [LocDescription("Before joining a work party, a Rabotnik estimates its pay per labor against the cheapest shop food (in the pay's currency) for the calories that labor burns, and skips the party if the pay doesn't cover it. Parties with no currency pay, or whose currency no shop sells food in, are skipped.")]
        public bool RequirePayCoversFood { get; set; } = true;

        [LocDescription("Pay must be at least this many times the estimated food cost of the labor for a Rabotnik to join a work party.")]
        public float MinPayToFoodRatio { get; set; } = 1;

        [LocDescription("Culture a Rabotnik's Logbook creates per point of the Rabotnik's (capped) XP rate. Kept small on purpose: Rabotniks are a secondary source next to players' art and architecture. It counts as a Cultural furnishing, so in a Cultural property it raises the settlement's influence radius.")]
        public float CulturePerXpRate { get; set; } = 0.05f;

        [LocDescription("The food part of a Rabotnik's XP rate counts toward culture up to this much. Food XP beyond it still speeds up skill XP but adds no culture.")]
        public float FoodXpCultureCap { get; set; } = 120;

        [LocDescription("The housing part of a Rabotnik's XP rate counts toward culture up to this much. Housing XP beyond it still speeds up skill XP but adds no culture.")]
        public float HousingXpCultureCap { get; set; } = 200;

        [LocDescription("A Rabotnik's culture is multiplied by 1 plus this times its reputation (positive only, on the same curve as player reputation in the settlement culture settings, 0 to 1). 0 ignores reputation.")]
        public float ReputationCultureBonus { get; set; } = 1;

        [LocDescription("Safety cap on labor actions per Rabotnik per tick. Rabotniks labor until they run out of calories or the order is done; each action adds up to 100 labor and burns calories like a player's labor click.")]
        public int MaxLaborActionsPerTick { get; set; } = 50;

        [LocDescription("Rabotniks take hauling contracts (Transport clauses with payment, Auto-Accept and no deposit) posted on contract boards in their settlement, when they did no work party labor that update. A Rabotnik finishes a contract it has accepted before doing work party labor. Turning this off stops new contracts; accepted ones are still finished.")]
        public bool HaulContracts { get; set; } = true;

        [LocDescription("Most items a Rabotnik hauls per update for a contract, like a carry load. A large contract takes several updates.")]
        public int HaulItemsPerTick { get; set; } = 40;

        [LocDescription("Calories a Rabotnik burns per item hauled. Hauling stops when it can't afford the next item; it eats on the next update. Also used to judge whether a contract's pay covers the food (see RequirePayCoversFood).")]
        public float CaloriesPerItemHauled { get; set; } = 5;

        [LocDescription("Rabotniks take harvest contracts (a Harvest Field clause with payment, Auto-Accept and no deposit; crops, bushes and, see HarvestTrees, trees) posted on contract boards in their settlement, when they did no work party labor that update. The Rabotnik needs a Sickle or Scythe (an axe for trees) in its Logbook storage or inventory, and wears it down. Independent of HaulContracts. Turning this off stops new contracts; accepted ones are still finished.")]
        public bool HarvestContracts { get; set; } = true;

        [LocDescription("A Rabotnik that wants to take or keep working a harvest contract but has no working Sickle or Scythe sells its broken ones to shops in its settlement that buy them, then buys the best affordable working one with its own money. Turn off to make the owner supply tools.")]
        public bool BuyHarvestTools { get; set; } = true;

        [LocDescription("Most plants a Rabotnik harvests per update for a harvest contract. A large field takes several updates.")]
        public int HarvestPlantsPerTick { get; set; } = 20;

        [LocDescription("Calories a Rabotnik burns per plant harvested. Harvesting stops when it can't afford the next plant; it eats on the next update. Also used to judge whether a contract's pay covers the food (see RequirePayCoversFood).")]
        public float CaloriesPerPlantHarvested { get; set; } = 10;

        [LocDescription("Harvest contracts may include trees. The Rabotnik needs an axe (not a chainsaw) in its Logbook storage or inventory. Felling is simulated, not physical: swings and calories are worked out from the tree and the axe like a player's felling, slicing, stump removal and debris cleanup, then the tree is removed and its logs handed over, with no falling trunk or debris left behind. Fallen trees (already felled by a player) are not touched.")]
        public bool HarvestTrees { get; set; } = true;

        [LocDescription("Most axe swings a Rabotnik makes per update when felling and processing trees. A big tree takes several updates.")]
        public int ChopSwingsPerTick { get; set; } = 40;

        [LocDescription("Rabotniks leave trees that would yield more logs than this to players (old growth redwoods give 700 or more).")]
        public int MaxTreeLogs { get; set; } = 200;

        [LocDescription("Estimated tree debris blocks cleared per this many logs, scaled by the species' debris chance and capped at 20 like vanilla. Each block costs one swing, and gives the species' debris items (wood pulp, seeds) only if the contract's container expects them.")]
        public float DebrisLogsPerBlock { get; set; } = 6;

        [LocDescription("A Rabotnik only takes a contract if its estimated work time (updates needed times how often it gets an update) is at most this share of the contract's time limit. A contractor can't quit, so this leaves room for restocking delays.")]
        public float ContractTimeSafety { get; set; } = 0.5f;

        [LocDescription("Rabotniks give reputation to players who treat them well: generous contract pay (to the client, when it pays out), generous work party pay (to the party's creator, when the Rabotnik joins) and cheap shop food (to the shop's owner, when the Rabotnik buys). Master switch for all reputation settings below.")]
        public bool GiveReputation { get; set; } = true;

        [LocDescription("Rabotniks judge contract pay for reputation (see GiveReputation).")]
        public bool ReputationForContracts { get; set; } = true;

        [LocDescription("Rabotniks judge work party pay for reputation (see GiveReputation).")]
        public bool ReputationForWorkParties { get; set; } = true;

        [LocDescription("Rabotniks judge shop food prices for reputation (see GiveReputation).")]
        public bool ReputationForFood { get; set; } = true;

        [LocDescription("Contract or work party pay counts as generous when it is at least this many times the cheapest shop food cost for the calories the work burns (the same measure as MinPayToFoodRatio).")]
        public float GenerousPayRatio { get; set; } = 2;

        [LocDescription("Shop food counts as cheap when the Rabotnik's wage (its average pay per calorie of work, in the shop's currency) buys at least this many times the food's calories. A Rabotnik with no wage in that currency yet uses the average of the settlement's other Rabotniks; with none, food isn't judged.")]
        public float GoodFoodWageRatio { get; set; } = 3;

        [LocDescription("Reputation a Rabotnik gives for generous pay or cheap food.")]
        public float ReputationPerGenerousAct { get; set; } = 1;

        [LocDescription("Rabotniks also give negative reputation for stingy pay (below StingyPayRatio) and overpriced food (below OverpricedFoodWageRatio). With the default job settings Rabotniks refuse pay that doesn't cover their food, so stingy pay only happens if RequirePayCoversFood is off or MinPayToFoodRatio is low.")]
        public bool NegativeReputation { get; set; } = false;

        [LocDescription("With NegativeReputation on, contract or work party pay below this many times the food cost of the work counts as stingy.")]
        public float StingyPayRatio { get; set; } = 0.5f;

        [LocDescription("With NegativeReputation on, shop food counts as overpriced when the Rabotnik's wage buys fewer than this many times its calories (below 1: a calorie of food costs more than a calorie of work earns).")]
        public float OverpricedFoodWageRatio { get; set; } = 1;

        [LocDescription("Reputation a Rabotnik takes away for stingy pay or overpriced food, with NegativeReputation on.")]
        public float ReputationPerStingyAct { get; set; } = 1;

        [LocDescription("World days before the same Rabotnik gives reputation to the same player for the same reason (contract, work party, food) again.")]
        public float ReputationCooldownDays { get; set; } = 1;

        [LocDescription("Most reputation (positive and negative counted together) all Rabotniks combined give one player per world day. 0 means no cap.")]
        public float MaxReputationPerPlayerPerDay { get; set; } = 5;

        [LocDescription("Rabotniks follow the game's own limits on giving reputation, like a player (by default 3 per target and 15 in total per day, and at most +30 / -10 to one target over time). Off ignores them; the cooldown and daily cap above still apply.")]
        public bool RespectVanillaReputationLimits { get; set; } = true;

        [LocDescription("Announce Rabotnik reputation like a player's: a server-wide message plus mail to the receiver. Off gives it silently (vanilla then also skips laws that would block the transfer).")]
        public bool AnnounceReputation { get; set; } = true;

        [LocDescription("Rabotniks may give reputation to their own Logbook's owners. Off by default, so owners can't farm reputation by paying their own workers.")]
        public bool ReputationFromOwnRabotniks { get; set; } = false;

        [LocDescription("Warn the Logbook's owner when the Rabotnik's money covers fewer than this many hours of rent. 0 turns the warning off.")]
        public float LowFundsWarningHours { get; set; } = 12;

        [LocDescription("Send the Logbook's owner chat messages when the Rabotnik's situation changes (home, citizenship, food, money, work).")]
        public bool NotifyOwners { get; set; } = true;

        [LocDescription("Put Rabotniks in the Abandoned demographic. Rabotniks never log in; this stops them generating land claims and stakes. It does not evict them or revoke citizenship.")]
        public bool MarkRabotniksAbandoned { get; set; } = true;

        [LocDescription("Rabotniks vote in elections they're eligible for, following the real players: once every non-abandoned non-Rabotnik eligible voter has voted, the Rabotnik votes are split across their ballots in proportion to each voter's reputation (negative counts as 0; if nobody has any, each voter counts the same). They re-follow when a player changes their vote. One summary message is posted instead of one per Rabotnik.")]
        public bool RabotniksVote { get; set; } = true;

        [LocDescription("If not every real player has voted this many minutes (world time) before an election expires, Rabotniks follow the real players who have voted. 0 turns this fallback off.")]
        public float VoteBeforeDeadlineMinutes { get; set; } = 60;
    }
}
