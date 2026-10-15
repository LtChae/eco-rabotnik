// Rabotnik - citizenship and housing for workers.
// Everything goes through vanilla paths so laws, fees, taxes and rent apply (verified in game):
// - citizenship: ImmigrationPolicy check, then the settlement's DirectCitizenRoster
// - housing: a worker rents a Residence deed it is invited to, exactly like a player accepting an invitation.
//   Residency.BecomeResident needs a Player (confirm boxes), so we evaluate the fees ourselves and call the private
//   BecomeResidentInternal by reflection. Rent is then charged hourly by vanilla, and vanilla evicts on non-payment.
namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using Eco.Core.Utils;
    using Eco.Gameplay.Economy;
    using Eco.Gameplay.Economy.Jobs;
    using Eco.Gameplay.GameActions;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Property;
    using Eco.Gameplay.Settlements;
    using Eco.Gameplay.Systems.TextLinks;
    using Eco.Shared.Items;
    using Eco.Shared.Math;
    using Eco.Shared.Utils;

    public static class RabotnikHousing
    {
        static readonly MethodInfo BecomeResidentInternal = typeof(Residency).GetMethod("BecomeResidentInternal", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly MethodInfo TryRemoveResident = typeof(Residency).GetMethod("TryRemoveResident", BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(GameActionPack), typeof(User), typeof(Deed), typeof(ResidencyChangeReason) }, null);

        /// <summary>Moves the worker towards being a housed citizen of the Logbook's settlement. Returns a short status line,
        /// or "" once housed (the Home & Money tab shows the home).</summary>
        public static string Step(RabotnikComponent component, User worker)
        {
            var settlement = SettlementUtils.GetLowestLevelSettlementAtPos(component.Parent.Position.XZi());
            if (settlement == null) return "Waiting: the Logbook isn't inside a settlement.";

            if (worker.DirectCitizenship != settlement)
            {
                var join = JoinSettlement(worker, settlement);
                if (!join.Success) return $"Waiting to become a citizen of {settlement.UILink()}: {join.Message}";
            }

            if (Residency.GetResidencyDeed(worker) == null)
            {
                var rent = FindAndRentHome(component, worker, settlement);
                if (!rent.Success) return $"Homeless: {rent.Message}";
            }
            return "";
        }

        public static Result JoinSettlement(User worker, Settlement settlement)
        {
            if (worker.DirectCitizenship != null && worker.DirectCitizenship != settlement) LeaveSettlement(worker);
            var canJoin = settlement.ImmigrationPolicy?.CheckCanJoinAsCitizen(worker, true);
            if (canJoin != null && !canJoin.Success) return canJoin;
            settlement.Citizenship.DirectCitizenRoster.AddToRoster(null, worker, false, true);
            return settlement.Citizenship.HasDirectCitizen(worker) ? Result.Succeeded : Result.FailLocStr("the settlement didn't accept the worker.");
        }

        public static void LeaveSettlement(User worker)
        {
            var settlement = worker.DirectCitizenship;
            if (settlement == null) return;
            settlement.Citizenship.DirectCitizenRoster.Leave(worker, false);
        }

        /// <summary>Rents the Logbook's own deed if possible, otherwise the cheapest suitable residence in the settlement.</summary>
        static Result FindAndRentHome(RabotnikComponent component, User worker, Settlement settlement)
        {
            var LogbookDeed = PropertyManager.GetDeedWorldPos(component.Parent.Position.XZi());
            Result LogbookDeedProblem = null;
            if (LogbookDeed != null)
            {
                var rent = TryRent(worker, LogbookDeed);
                if (rent.Success) return rent;
                LogbookDeedProblem = rent;
            }

            var others = Singleton<PropertyManager>.Obj.Deeds.ToList()
                .Where(d => d != LogbookDeed && d.CachedInfluencingSettlement == settlement && IsCandidate(worker, d))
                .Select(d => (deed: d, fee: GetFee(worker, d)))
                .Where(x => CanAfford(worker, x.fee))
                .OrderBy(x => x.fee.Rent)
                .ToList();
            foreach (var (deed, _) in others)
                if (TryRent(worker, deed).Success) return Result.Succeeded;

            return LogbookDeedProblem ?? Result.FailLocStr("no residence in the settlement has invited this Rabotnik or is affordable.");
        }

        static bool IsCandidate(User worker, Deed deed) =>
            deed.DeedPropertyType == PropertyType.Residence
            && deed.Plots.Any()
            && deed.Residency.CanBecomeResident(worker)
            && deed.Rent.RecurringTransferDefinition?.PaymentType != WageType.OnlineHours;

        static Result TryRent(User worker, Deed deed)
        {
            var deedLink = deed.UILink();
            if (deed.DeedPropertyType != PropertyType.Residence) return Result.FailLocStr($"{deedLink} isn't a residence.");
            if (!deed.Residency.CanBecomeResident(worker))
                return Result.FailLocStr($"{deedLink} hasn't invited the worker. The deed owner can add {worker.UILink()} (or Everyone) to the deed's residency invitations.");
            // OnlineHours rent only counts online time, and a worker is never online, so it would live for free.
            if (deed.Rent.RecurringTransferDefinition?.PaymentType == WageType.OnlineHours)
                return Result.FailLocStr($"{deedLink} charges rent for online hours only; Rabotniks only rent where rent covers online and offline hours.");

            var fee = GetFee(worker, deed);
            if (!CanAfford(worker, fee))
                return Result.FailLocStr($"can't afford {deedLink} (move-in {fee.MoveInFee:0.##} {fee.MoveInFeeCurrency?.UILink().ToString()}, rent {fee.Rent:0.##}/h {fee.RentCurrency?.UILink().ToString()}; needs {RabotnikPlugin.Config.RentBudgetHours} hours of rent up front). Fund the Rabotnik.");

            if (BecomeResidentInternal == null) return Result.FailLocStr("this game version is not supported (Residency.BecomeResidentInternal not found).");
            return (Result)BecomeResidentInternal.Invoke(deed.Residency, new object[] { worker, ResidencyChangeReason.AcceptingInvite, fee });
        }

        /// <summary>Leaves the worker's residence voluntarily (stops rent).</summary>
        public static void LeaveHome(User worker)
        {
            var deed = Residency.GetResidencyDeed(worker);
            if (deed == null || TryRemoveResident == null) return;
            var pack = (GameActionPack)TryRemoveResident.Invoke(deed.Residency, new object[] { new GameActionPack(), worker, deed, ResidencyChangeReason.Voluntary });
            pack.TryPerform(worker);
        }

        /// <summary>How many hours of rent at the worker's current home its balance covers. Null if homeless or rent-free.</summary>
        public static float? HoursOfRentLeft(User worker)
        {
            var deed = Residency.GetResidencyDeed(worker);
            if (deed == null) return null;
            var fee = GetFee(worker, deed);
            if (fee.Rent <= 0 || fee.RentCurrency == null) return null;
            return (worker.BankAccount?.GetCurrencyHoldingVal(fee.RentCurrency) ?? 0f) / fee.Rent;
        }

        /// <summary>The worker's rent per hour and its currency. Null if homeless or rent-free.</summary>
        public static (Currency Currency, float PerHour)? CurrentRent(User worker)
        {
            var deed = Residency.GetResidencyDeed(worker);
            if (deed == null) return null;
            var fee = GetFee(worker, deed);
            if (fee.Rent <= 0 || fee.RentCurrency == null) return null;
            return (fee.RentCurrency, fee.Rent);
        }

        /// <summary>The worker's home, its owners and rent, for the status screen.</summary>
        public sealed class RentSummary
        {
            public string Home;
            /// <summary>The deed as a hoverable/clickable link.</summary>
            public string Link;
            public string Owners;
            public float Rent;
            public string Currency;
            /// <summary>The rent currency as a link.</summary>
            public string CurrencyLink;
            public float? HoursLeft;
        }

        /// <summary>Null if the worker is homeless.</summary>
        public static RentSummary RentInfo(User worker)
        {
            var deed = Residency.GetResidencyDeed(worker);
            if (deed == null) return null;
            var fee = GetFee(worker, deed);
            return new RentSummary
            {
                Home      = deed.Name,
                Link      = deed.UILink().ToString(),
                Owners   = deed.Owners == null ? null : string.Join(", ", deed.Owners.UserSet.Take(3).Select(u => u.UILink().ToString())),
                Rent      = fee.Rent,
                Currency  = fee.RentCurrency?.Name,
                CurrencyLink = fee.RentCurrency?.UILink().ToString(),
                HoursLeft = HoursOfRentLeft(worker),
            };
        }

        // Same evaluation RentForProperty.ValidateAndGetFeeInfo does, minus the confirm dialog.
        static RentForProperty.FeeInfo GetFee(User worker, Deed deed)
        {
            var rent = deed.Rent;
            var ctx = new RecurringTransferContext { Citizen = worker, ServiceAccount = rent.ReceivedRentAccount, UserAccount = worker.BankAccount, RentalDeed = deed };
            var moveInFee = rent.MoveInFee?.Value(ctx);
            var moveInCur = rent.MoveInFeeCurrency?.Value(ctx);
            var rentPerHour = rent.RecurringTransferDefinition?.AmountPerHour?.Value(ctx);
            var rentCur = rent.RecurringTransferDefinition?.Currency?.Value(ctx);
            return new RentForProperty.FeeInfo
            {
                MoveInFee         = moveInFee != null && moveInFee.Valid ? moveInFee.Val : 0f,
                MoveInFeeCurrency = moveInCur != null && moveInCur.Valid ? moveInCur.Val : null,
                Rent              = rentPerHour != null && rentPerHour.Valid ? rentPerHour.Val : 0f,
                RentCurrency      = rentCur != null && rentCur.Valid ? rentCur.Val : null,
                UserAccount       = worker.BankAccount,
            };
        }

        static bool CanAfford(User worker, RentForProperty.FeeInfo fee)
        {
            var needed = new Dictionary<Currency, float>();
            if (fee.MoveInFee > 0 && fee.MoveInFeeCurrency != null) needed[fee.MoveInFeeCurrency] = fee.MoveInFee;
            if (fee.Rent > 0 && fee.RentCurrency != null)
                needed[fee.RentCurrency] = needed.GetValueOrDefault(fee.RentCurrency) + fee.Rent * RabotnikPlugin.Config.RentBudgetHours;
            return needed.All(kv => (worker.BankAccount?.GetCurrencyHoldingVal(kv.Key) ?? 0f) >= kv.Value);
        }
    }
}
