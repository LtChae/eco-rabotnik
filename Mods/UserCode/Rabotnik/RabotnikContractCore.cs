// Rabotnik - plumbing shared by the contract jobs (hauling in RabotnikContracts, harvesting in RabotnikHarvest).
// Verified in game (contract spike, 2026-10-05; see the eco-contracts skill):
// - Contract.AcceptJob reads user.Player.User, so TryAccept replays its steps: vanilla's checks without the Player one,
//   the JoinedContract pack with every clause's TryJoin (escrow), then AcceptJob's bookkeeping (Contractor/EndTime have private setters).
// - JobFinished(worker) pays out once every clause can succeed; it returns null (not a failed Result) when it can't finish.
namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Eco.Core.Controller;
    using Eco.Core.Utils;
    using Eco.Gameplay.Auth;
    using Eco.Gameplay.Civics.Misc;
    using Eco.Gameplay.Components.Storage;
    using Eco.Gameplay.Economy;
    using Eco.Gameplay.Economy.Contracts;
    using Eco.Gameplay.GameActions;
    using Eco.Gameplay.Items;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Property;
    using Eco.Gameplay.Settlements;
    using Eco.Gameplay.Systems.Messaging.Notifications;
    using Eco.Gameplay.Systems.TextLinks;
    using Eco.Gameplay.Utils;
    using Eco.Shared.IoC;
    using Eco.Shared.Items;
    using Eco.Shared.Math;
    using Eco.Shared.Localization;
    using Eco.Shared.Logging;
    using Eco.Shared.Services;
    using Eco.Shared.Time;
    using Eco.Shared.Utils;
    using Eco.Simulation.Time;

    public static class RabotnikContractCore
    {
        /// <summary>Contracts with a Harvest clause belong to the harvest job; every other contract belongs to the hauling job.</summary>
        public static bool IsHarvest(Contract contract) => contract.Clauses.Any(x => x is ContractClauseHarvest);

        // Same guard as RabotnikWork: only a housed citizen of the settlement works.
        public static bool CanWork(User worker, Settlement settlement) =>
            settlement != null && worker.DirectCitizenship == settlement && Residency.GetResidencyDeed(worker) != null;

        /// <summary>The worker's accepted contract of one job kind, if any.</summary>
        public static Contract ActiveOf(User worker, bool harvest) =>
            worker.AcceptedContracts.FirstOrDefault(x => x.State == ProposableState.Active && IsHarvest(x) == harvest);

        public static bool HasActive(User worker) => worker.AcceptedContracts.Any(x => x.State == ProposableState.Active);

        /// <summary>Proposed contracts on boards in the settlement, of one job kind.</summary>
        public static List<Contract> Posted(Settlement settlement, bool harvest) =>
            EconomyManager.Contracts.Contracts
                .Where(x => x != null && x.State == ProposableState.Proposed && x.Board?.Parent != null && IsHarvest(x) == harvest
                            && SettlementUtils.GetLowestLevelSettlementAtPos(x.Board.Parent.Position.XZi()) == settlement)
                .ToList();

        /// <summary>Reports a contract the worker was on that ended without the worker finishing it (failed by the client, or completed by the client).</summary>
        public static void CheckEnded(RabotnikComponent c, User worker)
        {
            if (c.ContractId == 0) return;
            var contract = EconomyManager.Contracts.Contracts.FirstOrDefault(x => x != null && x.Id == c.ContractId);
            if (contract != null && contract.State == ProposableState.Active) return;
            c.ContractId = 0;
            c.LastContractNotice = null;
            var calories = c.ContractCalories;
            c.ContractCalories = 0;
            if (contract == null) return;
            if (contract.CompletedSuccessfully)
            {
                c.ContractsCompleted++;
                Notice(c, $"was paid for the contract {contract.UILink()}: {contract.ClientUser?.UILink()} marked it complete.");
                var pay = contract.Clauses.OfType<ContractClausePayment>().FirstOrDefault();
                RabotnikReputation.JudgeContract(c, worker, contract, pay?.Currency, pay?.Payment ?? 0f, calories);
            }
            else Notice(c, $"failed the contract {contract.UILink()}: {contract.ClientUser?.UILink()} marked it failed after its deadline. The payment went back to the client.");
        }

        /// <summary>Contract.AcceptJob without its Player-only check (see the header comment).</summary>
        public static Result TryAccept(User worker, Contract contract)
        {
            // Vanilla CanAcceptInternal, with the board auth checked on the User instead of the Player.
            var client = contract.ClientUser;
            var board = contract.Board.Parent;
            worker.Position = board.Position; worker.MarkDirty(); // the 8 m rule, as RabotnikWork does for parties
            if (worker.AcceptedContracts.Any(x => x.State == ProposableState.Active && x.BaseContractID == contract.BaseContractID)) return Result.FailLoc($"already holds a copy");
            if (worker.AcceptedContracts.Count(x => x.State == ProposableState.Active) >= 10) return Result.FailLoc($"has 10 active contracts");
            if (!ServiceHolder<IAuthManager>.Obj.IsAuthorized(board, worker, AccessType.ConsumerAccess, null, out _)) return Result.FailLoc($"not authorized on {board.UILink()}");
            if (contract.Clauses.All(x => x.CanSucceed(worker, client).Success)) return Result.FailLoc($"nothing left to do");

            // What Contract.Open does for a player: the payment clause takes the opener's account as the contractor account.
            foreach (var handler in contract.Clauses.OfType<IOpenHandler>()) handler.OnOpen(worker);
            var valid = contract.Valid();
            if (!valid.Success) return valid;

            // Contract.ConfigureAndTryPerform(new JoinedContract(), TryJoin), built from public parts. Laws on JoinedContract apply.
            var pay = contract.Clauses.OfType<ContractClausePayment>().FirstOrDefault();
            using var pack = new GameActionPack();
            pack.AddGameAction(new JoinedContract
            {
                Citizen = worker, Client = client, Currency = pay?.Currency, CurrencyAmount = pay?.Payment ?? 0f,
                ActionLocation = board.Position3i,
            });
            foreach (var clause in contract.Clauses)
            {
                clause.TryJoin(pack, worker, client);
                if (!pack.EarlyResult.Success) return pack.EarlyResult;
            }
            var performed = pack.TryPerform(worker);
            if (!performed.Success) return performed;

            // The rest of Contract.AcceptJob.
            EconomyManager.Contracts.RepeatContract(contract);
            SetPrivate(contract, nameof(Contract.EndTime), WorldTime.Seconds + TimeUtil.HoursToSeconds(contract.Hours));
            SetPrivate(contract, nameof(Contract.Contractor), worker.Name);
            contract.SetProposedState(ProposableState.Active);
            contract.Changed(nameof(Contract.EndTime));
            contract.Changed(nameof(Contract.Contractor));
            contract.Changed(nameof(Contract.ContractorDisplay));
            foreach (var picker in contract.Clauses.SelectMany(x => x.Pickers)) picker.Accepted(contract.UILink(false), worker);
            NotificationManager.ServerMessageToPlayer(Localizer.Do($"{worker.UILink()} accepted your contract: {contract.UILink()}\n{contract.ClauseDesc()}"), client, NotificationCategory.YourContracts, NotificationStyle.Chat);
            contract.SetRequiresHost(false);
            contract.RemoveFromBoard();
            Singleton<EconomyManager>.Obj.MarkDirty();
            return Result.Succeeded;
        }

        static void SetPrivate(Contract contract, string property, object value) =>
            typeof(Contract).GetProperty(property).SetValue(contract, value);

        /// <summary>Calls JobFinished once every clause can succeed. Returns the completion status line (and counts it), or null if not finished; problems gets the reason.</summary>
        public static string TryFinish(RabotnikComponent c, User worker, Contract contract, List<string> problems, string activityPrefix)
        {
            if (!contract.Clauses.All(x => x.CanSucceed(worker, contract.ClientUser).Success)) return null;
            var pay = contract.Clauses.OfType<ContractClausePayment>().FirstOrDefault();
            var balanceBefore = pay?.Currency == null ? 0f : worker.BankAccount.GetCurrencyHoldingVal(pay.Currency);
            Result finished;
            try { finished = contract.JobFinished(worker); }
            catch (Exception e) { Log.WriteException(e); finished = Result.FailLoc($"{e.Message}"); }
            if (finished != null && finished.Success)
            {
                var paid = pay?.Currency == null ? 0f : worker.BankAccount.GetCurrencyHoldingVal(pay.Currency) - balanceBefore;
                c.ContractsCompleted++;
                c.ContractId = 0;
                c.LastContractNotice = null;
                var paidText = pay?.Currency == null ? "" : $", paid {pay.Currency.UILink(paid)}";
                Notice(c, $"completed the contract {contract.UILink()} for {contract.ClientUser?.UILink()}{paidText}.");
                RabotnikReputation.JudgeContract(c, worker, contract, pay?.Currency, paid, c.ContractCalories);
                c.ContractCalories = 0;
                return $"{activityPrefix}: completed {contract.UILink()}{paidText}.";
            }
            problems.Add($"couldn't finish: {finished?.Message.ToString() ?? "not accepted yet"}");
            return null;
        }

        /// <summary>Tells the owner and the client once per distinct status that the worker is stuck. ownerText and clientText follow the worker's name / start a sentence.</summary>
        public static void NotifyWaiting(RabotnikComponent c, Contract contract, string status, string ownerText, string clientText)
        {
            if (status == c.LastContractNotice) return;
            c.LastContractNotice = status;
            Notice(c, ownerText);
            var client = contract.ClientUser;
            if (client != null && clientText != null)
                NotificationManager.ServerMessageToPlayer(Localizer.NotLocalizedStr($"[Rabotnik] {c.Worker?.UILink()} {clientText}"), client, NotificationCategory.YourContracts, NotificationStyle.Chat);
        }

        /// <summary>Logs the event on the Logbook and tells the owner. text starts with a verb; the worker's name is prepended for the owner.</summary>
        public static void Notice(RabotnikComponent c, string text)
        {
            c.LogEvent(char.ToUpper(text[0]) + text.Substring(1));
            if (RabotnikPlugin.Config.NotifyOwners) RabotnikOwner.Tell(c, $"{c.Worker?.UILink()} {text}");
        }

        public static List<Inventory> Storages(PickerObject picker) =>
            picker.PickedObj?.GetComponents<StorageComponent>().Where(s => !(s is FuelSupplyComponent)).Select(s => s.Inventory).ToList() ?? new List<Inventory>();

        public static int Remaining(ItemTracker tracker, Item item) =>
            tracker.RemainingStacks?.Where(s => s.Quantity > 0 && s.StackObject != null && s.StackObject.RepresentsItemType(item.Type)).Sum(s => s.Quantity) ?? 0;

        public static int Count(IEnumerable<Inventory> inventories, Item item) =>
            inventories.SelectMany(i => i.NonEmptyStacks).Where(s => s.Item.Type == item.Type).Sum(s => s.Quantity);

        public static string PayText(Contract contract)
        {
            var pay = contract.Clauses.OfType<ContractClausePayment>().FirstOrDefault();
            return pay?.Currency == null ? "no pay" : pay.Currency.UILink(pay.Payment).ToString();
        }

        /// <summary>Seconds between one worker's updates (round-robin over all workers), for time estimates.</summary>
        public static double SecondsPerUpdate()
        {
            var config = RabotnikPlugin.Config;
            return config.TickSeconds * Math.Ceiling((double)RabotnikService.Count / Math.Max(1, config.MaxRabotniksPerTick));
        }
    }
}
