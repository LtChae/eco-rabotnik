// Rabotnik - the Logbook's other two tabs. Eco shows each autogen component as its own tab, so the worker's information is
// split across three: Worker (RabotnikComponent), Home & Money and Activity (here). RabotnikComponent.Refresh
// computes the text and pushes it into these with Show.
// Display text is plain string: the client can't build a UI for LocString properties.
namespace Eco.Mods.TechTree
{
    using System.ComponentModel;
    using Eco.Core.Controller;
    using Eco.Core.Items;
    using Eco.Gameplay.Economy;
    using Eco.Gameplay.Economy.Transfer;
    using Eco.Gameplay.Objects;
    using Eco.Gameplay.Players;
    using Eco.Shared.Items;
    using Eco.Shared.Localization;
    using Eco.Shared.Networking;
    using Eco.Shared.Serialization;

    /// <summary>Citizenship, home, rent, money and income, with the owner's controls for funding the worker.
    /// (Housing XP is on the Worker tab, next to the food XP.)</summary>
    [Tag("Economy"), Category("Hidden"), NoIcon, Serialized, AutogenClass, LocDisplayName("Home & Money")]
    public class RabotnikHomeComponent : WorldObjectComponent
    {
        [SyncToView, Autogen, UITypeName("StringDisplay")] public string Residence { get; private set; } = "";
        [SyncToView, Autogen, UITypeName("StringDisplay")] public string Finances  { get; private set; } = "";
        [SyncToView, Autogen, UITypeName("StringDisplay")] public string Income    { get; private set; } = "";

        // Funding: the owner picks a currency and amount and presses "Fund Rabotnik".
        [SyncToView, Autogen, AutoRPC] public Currency FundCurrency { get => this.fundCurrency; set => this.fundCurrency = value; }
        [SyncToView, Autogen, AutoRPC, Serialized] public float FundAmount { get; set; } = 100;
        [Serialized] Currency fundCurrency;

        [RPC, Autogen]
        public void FundWorker(Player player)
        {
            var main = this.Parent?.GetComponent<RabotnikComponent>();
            var worker = main?.Worker;
            if (worker?.BankAccount == null) { player.ErrorLocStr("This Logbook has no Rabotnik yet."); return; }
            if (this.FundCurrency == null)  { player.ErrorLocStr("Choose a currency to fund the Rabotnik with."); return; }
            if (this.FundAmount <= 0)       { player.ErrorLocStr("Enter an amount above zero."); return; }

            var result = Transfers.TransferNow(player.User, new TransferData
            {
                Amount              = this.FundAmount,
                Currency            = this.FundCurrency,
                SourceAccount       = player.User.BankAccount,
                TargetAccount       = worker.BankAccount,
                Sender              = player.User,
                TransferType        = TransferType.DirectTransfer,
                TransferDescription = Localizer.Do($"Funding {worker.MarkedUpName}"),
            });
            if (result.Success) { RabotnikIncome.NoteFunding(main, this.FundCurrency, this.FundAmount); main.Refresh(); }
        }

        /// <summary>Only assigns what changed: each assignment pushes an update to open panels.</summary>
        public void Show(string residence, string finances, string income)
        {
            if (this.Residence != residence) this.Residence = residence;
            if (this.Finances != finances)   this.Finances = finances;
            if (this.Income != income)       this.Income = income;
        }
    }

    /// <summary>What the worker is doing now, its work record, and the recent event log.</summary>
    [Tag("Economy"), Category("Hidden"), NoIcon, Serialized, AutogenClass, LocDisplayName("Activity")]
    public class RabotnikActivityComponent : WorldObjectComponent
    {
        [SyncToView, Autogen, UITypeName("StringDisplay")] public string Now    { get; private set; } = "";
        [SyncToView, Autogen, UITypeName("StringDisplay")] public string Record { get; private set; } = "";
        [SyncToView, Autogen, UITypeName("StringDisplay")] public string Events { get; private set; } = "";

        public void Show(string now, string record, string events)
        {
            if (this.Now != now)       this.Now = now;
            if (this.Record != record) this.Record = record;
            if (this.Events != events) this.Events = events;
        }
    }
}
