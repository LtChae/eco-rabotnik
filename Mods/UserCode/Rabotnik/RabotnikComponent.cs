// Rabotnik - the worker component on the Logbook. Holds the worker reference (saved with the world object) and shows
// the worker's state in the Logbook's UI, with a button for the owner to fund the worker.
namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Linq;
    using System.Text;
    using Eco.Core.Controller;
    using Eco.Core.Items;
    using Eco.Gameplay.Aliases;
    using Eco.Gameplay.Components;
    using Eco.Gameplay.Economy;
    using Eco.Gameplay.Economy.Transfer;
    using Eco.Gameplay.Objects;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Property;
    using Eco.Gameplay.Settlements;
    using Eco.Gameplay.Skills;
    using Eco.Gameplay.Items;
    using Eco.Gameplay.Systems.NewTooltip.TooltipLibraryFiles;
    using Eco.Gameplay.Systems.TextLinks;
    using Eco.Shared.Items;
    using Eco.Shared.Localization;
    using Eco.Shared.Logging;
    using Eco.Shared.Math;
    using Eco.Shared.Networking;
    using Eco.Shared.Serialization;
    using Eco.Simulation.Time;

    /// <summary>The specialties in vanilla PlayerDefaults.GetDefaultSkills(), which players get to choose from without a scroll.</summary>
    /// Enums used in serialized/UI members need [Serialized] (the server refuses to start without it), like vanilla's WageType.
    [Serialized, Localized]
    public enum RabotnikBaseSkill { None, Logging, Mining, Gathering, CampfireCooking, Hunting }

    // A component must say [HasIcon] or [NoIcon] (the server logs an error otherwise, from WorldObjectComponent's [DerivedMustDefineIcon]).
    [Tag("Economy"), Category("Hidden"), HasIcon(nameof(RabotnikMaskItem)), Serialized, AutogenClass, LocDisplayName("Rabotnik")]
    public class RabotnikComponent : WorldObjectComponent
    {
        /// <summary>The tab's icon: the Rabotnik mask, carried by the hidden RabotnikMaskItem (see RabotnikIcons.cs).</summary>
        public override string IconName => nameof(RabotnikMaskItem);

        /// <summary>The worker living at this Logbook. Null until the first update after placement.</summary>
        [Serialized] public User Worker { get; set; }

        /// <summary>The base skill the owner picked (the specialties every player can choose without a scroll).
        /// Used if the worker has no skill yet; otherwise a skill scroll stocked in the Logbook is used.</summary>
        // Not shown as a dropdown: the client builds enum dropdowns from client-side types ("Can't convert named type to
        // system type: RabotnikBaseSkillView" in Player.log), so a server-only enum has no options. Buttons set it instead.
        [Serialized] public RabotnikBaseSkill BaseSkill { get; set; }

        [RPC, Autogen] public void LearnLogging(Player player)         => this.PickBaseSkill(player, RabotnikBaseSkill.Logging);
        [RPC, Autogen] public void LearnMining(Player player)          => this.PickBaseSkill(player, RabotnikBaseSkill.Mining);
        [RPC, Autogen] public void LearnGathering(Player player)       => this.PickBaseSkill(player, RabotnikBaseSkill.Gathering);
        [RPC, Autogen] public void LearnCampfireCooking(Player player) => this.PickBaseSkill(player, RabotnikBaseSkill.CampfireCooking);
        [RPC, Autogen] public void LearnHunting(Player player)         => this.PickBaseSkill(player, RabotnikBaseSkill.Hunting);

        void PickBaseSkill(Player player, RabotnikBaseSkill baseSkill)
        {
            var worker = this.Worker;
            if (worker == null) { player.ErrorLocStr("This Logbook has no Rabotnik yet."); return; }
            var current = this.AssignedSkill(worker);
            if (current != null) { player.ErrorLocStr($"{worker.UILink()} already has its skill ({current.UILink()}). Rabotniks keep their first skill."); return; }
            this.BaseSkill = baseSkill;
            RabotnikService.Update(this);
            var baseType = RabotnikNeeds.BaseSkillType(baseSkill);
            var learned = (ILinkable)this.AssignedSkill(worker) ?? (baseType != null ? Item.Get(baseType) : null);
            player.InfoBoxLocStr($"{worker.UILink()} learned {learned?.UILink().ToString() ?? baseSkill.ToString()}.");
        }

        /// <summary>Placed by /Rabotnik perfspawn, so /Rabotnik perfclear can remove it.</summary>
        [Serialized] public bool SpawnedForPerfTest { get; set; }

        /// <summary>Class name of the worker's one skill (e.g. "LoggingSkill"), learned from a scroll. Null until then.</summary>
        [Serialized] public string AssignedSkillName { get; set; }

        public Skill AssignedSkill(User worker) =>
            this.AssignedSkillName == null ? null : worker?.Skillset?.Skills?.FirstOrDefault(s => s.GetType().Name == this.AssignedSkillName);

        /// <summary>What the worker is doing or waiting for, set by each update.</summary>
        public string Activity { get; set; } = "";

        /// <summary>What the worker's shopping did or is waiting for, set by each update. Empty when it didn't need to shop.</summary>
        public string ShoppingActivity { get; set; } = "";

        /// <summary>What the worker's labor is doing or waiting for, set by each update.</summary>
        public string WorkActivity { get; set; } = "";

        /// <summary>What the worker's contract hauling did or is waiting for, set by each update. Empty when it has no contract and took none.</summary>
        public string ContractActivity { get; set; } = "";

        /// <summary>The last contract shortfall reported, so each one is told to the client and owner once. Not saved: a restart repeats it once.</summary>
        public string LastContractNotice { get; set; }

        /// <summary>The worker's situation at the last update, for owner notices. Not saved: rebuilt after a restart.</summary>
        public RabotnikOwner.Snapshot LastSnapshot { get; set; }

        /// <summary>The culture this Rabotnik creates as of the last update (RabotnikCulture). Read by the Logbook as its furnishing value. Not saved: rebuilt each update.</summary>
        public float Culture { get; private set; }

        /// <summary>The Logbook's owner as of the last update; used if the owner can't be read while the Logbook is destroyed.</summary>
        public User LastOwner { get; set; }

        // Lifetime stats and the event log, saved with the world object. Reset when a new worker is assigned (ResetRecord).
        /// <summary>WorldTime.Day when the current worker was assigned.</summary>
        [Serialized] public double HiredDay { get; set; }
        [Serialized] public float LaborDone { get; set; }
        [Serialized] public int MealsEaten { get; set; }
        [Serialized] public int PartiesJoined { get; set; }
        [Serialized] public string LastMeal { get; set; }
        [Serialized] public int ItemsHauled { get; set; }
        [Serialized] public int PlantsHarvested { get; set; }
        [Serialized] public int ContractsCompleted { get; set; }
        /// <summary>Id of the contract the worker accepted, to notice when it ends without the worker finishing it. 0 when none.</summary>
        [Serialized] public int ContractId { get; set; }
        /// <summary>Estimated calories of the accepted contract's work, to judge its pay when it pays out (RabotnikReputation). 0 when unknown.</summary>
        [Serialized] public float ContractCalories { get; set; }
        /// <summary>Average pay per calorie of the worker's jobs, per currency as "Name=rate;...", to judge food prices (RabotnikReputation).</summary>
        [Serialized] public string WageRates { get; set; } = "";
        // Income tracking (RabotnikIncome): per-currency totals as "Name=amount;..." and the last ledger entry counted.
        [Serialized] public string EarnedTotals { get; set; } = "";
        [Serialized] public string SpentTotals { get; set; } = "";
        [Serialized] public double LastLedgerTime { get; set; }
        /// <summary>Owner funding not yet seen in the ledger, per currency name (so it isn't counted as income). Not saved.</summary>
        public Dictionary<string, float> PendingFunding { get; } = new();
        /// <summary>Last ledger balance per currency name, to confirm each entry's sign. Not saved: rebuilt from the ledger.</summary>
        public Dictionary<string, float> LastBalances { get; } = new();
        /// <summary>Newest first, one event per line. A plain string rather than a list, to keep the save format simple.</summary>
        [Serialized] public string EventLog { get; set; } = "";
        const int MaxLogLines = 12;

        /// <summary>Adds a line to the event log, stamped with the world day.</summary>
        public void LogEvent(string text)
        {
            var lines = (this.EventLog ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            lines.Insert(0, $"Day {(int)WorldTime.Day}: {text}"); //noloc
            this.EventLog = string.Join("\n", lines.Take(MaxLogLines));
        }

        /// <summary>Starts a fresh record for a newly assigned worker.</summary>
        public void ResetRecord()
        {
            this.HiredDay = WorldTime.Day;
            this.LaborDone = 0; this.MealsEaten = 0; this.PartiesJoined = 0; this.LastMeal = null; this.ItemsHauled = 0; this.PlantsHarvested = 0; this.ContractsCompleted = 0; this.ContractId = 0; this.ContractCalories = 0; this.WageRates = ""; this.EventLog = ""; this.EarnedTotals = ""; this.SpentTotals = "";
        }

        // Stored properties, not computed ones: the weaver adds change notification to the setter, which is what pushes
        // the new value to open panels. A computed getter plus this.Changed() didn't update the client.
        // Plain strings: the client can't build a UI for LocString properties ("Can't convert named type to system type: LocStringView").
        // This is the Worker tab (who it is, its skill, food and the XP from food and housing); Home & Money and Activity are
        // RabotnikHomeComponent / RabotnikActivityComponent (RabotnikTabs.cs).
        [SyncToView, Autogen, UITypeName("StringDisplay")] public string Status   { get; private set; } = "No Rabotnik yet; one is assigned on the next update.";
        [SyncToView, Autogen, UITypeName("StringDisplay")] public string Skills   { get; private set; } = "";
        [SyncToView, Autogen, UITypeName("StringDisplay")] public string Needs    { get; private set; } = "";
        [SyncToView, Autogen, UITypeName("StringDisplay")] public string Supplies { get; private set; } = "";
        [SyncToView, Autogen, UITypeName("StringDisplay")] public string Nutrition { get; private set; } = "";
        [SyncToView, Autogen, UITypeName("StringDisplay")] public string Housing  { get; private set; } = "";
        [SyncToView, Autogen, UITypeName("StringDisplay")] public string Credit  { get; private set; } = "Book model: \"Simple Book\" by Ludus101, CC BY 4.0. Icon: \"Android mask\" by Lorc (game-icons.net), CC BY 4.0"; //noloc
        public override void Initialize()
        {
            base.Initialize();
            RabotnikService.Register(this);
        }

        public override void OnCreate()
        {
            base.OnCreate();
            try { RabotnikService.Update(this); }
            catch (Exception e) { Log.WriteException(e); }
        }

        public override void Destroy()
        {
            var owner = this.Parent?.Owners?.OneUser() ?? this.LastOwner ?? this.Parent?.Creator;
            RabotnikService.Retire(this, owner);
            RabotnikService.Unregister(this);
            base.Destroy();
        }

        /// <summary>Prints the text on the Logbook's spine: the CustomTextComponent, shown by the prefab's Sign script (LogbookBuilder).
        /// Set directly rather than through SetText, which needs a Player. Players with access can edit it like a sign; each
        /// update puts the worker's name back.</summary>
        void SetSpineText(string text)
        {
            var data = this.Parent?.GetComponent<CustomTextComponent>()?.TextData;
            if (data == null || data.Text == text) return;
            data.Text = text;
            data.Changed(nameof(TextItemData.Text));
            this.Parent.SetDirty();
        }

        /// <summary>Rebuilds the text on all three tabs, names the logbook after its worker and prints the name on its spine.</summary>
        public void Refresh()
        {
            var w = this.Worker;
            if (w == null) { if (this.Status != NoWorkerText) this.Status = NoWorkerText; this.SetSpineText(""); return; }

            var logbookName = $"Logbook of {w.Name}"; //noloc
            if (this.Parent != null && this.Parent.Name != logbookName) this.Parent.SetName(logbookName);
            this.SetSpineText(w.Name);

            var settlement = SettlementUtils.GetLowestLevelSettlementAtPos(this.Parent.Position.XZi());
            var skill = this.AssignedSkill(w);
            var rate = w.UserXP?.SkillRate ?? 0f;

            // Culture, like artwork: pushed into the deed's room values when it changes, so the settlement's borders follow.
            var culture = RabotnikCulture.For(w, settlement);
            if (Math.Abs(culture - this.Culture) > 0.01f)
            {
                this.Culture = culture;
                if (this.Parent is RabotnikLogbookObject logbook) RabotnikCulture.RefreshRooms(logbook);
            }
            var rent = RabotnikHousing.RentInfo(w);
            var (meals, mealCalories, scrolls, stocked) = RabotnikNeeds.Supplies(this);
            var balances = CurrencyManager.Currencies
                .Select(c => (c, v: w.BankAccount?.GetCurrencyHoldingVal(c) ?? 0f))
                .Where(x => x.v > 0 && !float.IsInfinity(x.v))
                .Select(x => $"{x.v:0.##} {x.c.UILink()}")
                .ToList();
            var daysEmployed = Math.Max(0, (int)(WorldTime.Day - this.HiredDay));

            // --- Worker tab
            // The settlement is on the Home & Money tab ("Citizen of"), and Activity says when the Logbook isn't in one.
            var status = $"{w.UILink()}, employed {daysEmployed} day{(daysEmployed == 1 ? "" : "s")}"; //noloc
            string skills;
            if (skill == null)
            {
                var baseSkills = Enum.GetValues(typeof(RabotnikBaseSkill)).Cast<RabotnikBaseSkill>()
                    .Select(RabotnikNeeds.BaseSkillType).Where(t => t != null)
                    .Select(t => Item.Get(t)?.UILink().ToString()).Where(l => l != null);
                skills = $"Skill: none yet. Press a Learn button ({string.Join(", ", baseSkills)}), or stock a skill scroll."; //noloc
            }
            else
            {
                skills = $"Skill: {skill.UILink()}, level {skill.Level}\n" +
                         $"XP rate: {rate:0.0} (food {w.Stomach?.NutrientSkillRate():0.0}, {(w.ResidencyPropertyValue != null ? w.ResidencyPropertyValue.UILink(Localizer.NotLocalizedStr($"housing {w.ResidencyPropertyValue.Value:0.0}")).ToString() : "no housing")})\n" +
                         (skill.Level >= skill.MaxLevel ? "Top level" : $"Experience: {skill.Experience:0} / {skill.ExperienceToLevel:0} to next level") + //noloc
                         $"\nCulture created: {this.Culture:0.#} (food and housing XP, capped, x reputation; counts in a Cultural property)"; //noloc
            }
            var needs = $"Calories: {w.Stomach?.Calories:0} / {w.Stomach?.MaxCalories:0}\nLast meal: {this.LastMeal ?? "none yet"} ({this.MealsEaten} eaten)"; //noloc
            var supplies = $"In logbook: {meals} meal{(meals == 1 ? "" : "s")} ({mealCalories:0} cal), {scrolls} skill scroll{(scrolls == 1 ? "" : "s")}" + //noloc
                           (string.IsNullOrEmpty(stocked) ? "" : $"\n{stocked}"); //noloc
            // Vanilla's own stomach tooltip text (nutrient balance + what's in the stomach), rendered as plain text.
            string nutrition;
            try
            {
                // Each food is a UILink(), so hovering it should show that food's own tooltip (test: does StringDisplay render links?).
                var foods = w.Stomach?.Contents.Where(e => e.Food != null).Select(e => e.Food).ToList() ?? new List<FoodItem>();
                float Total(FoodItem f) => f.Nutrition.Carbs + f.Nutrition.Fat + f.Nutrition.Protein + f.Nutrition.Vitamins;
                var sum = foods.Sum(Total);
                var lines = foods.GroupBy(f => f.GetType())
                    .Select(g => (food: g.First(), n: g.Count(), share: sum > 0 ? g.Sum(Total) / sum : 0f))
                    .OrderByDescending(x => x.share)
                    .Select(x => $"{x.share:P0}  {x.food.UILink()} x{x.n}"); //noloc
                nutrition = w.Stomach == null ? "" :
                    "Food XP sources (hover a food)\n" + (foods.Count == 0 ? "Nothing eaten yet" : string.Join("\n", lines)) + //noloc
                    $"\n\n{w.Stomach.StomachNutritionTooltip()}"; //noloc
            }
            catch (Exception e) { Log.WriteException(e); nutrition = "Nutrition breakdown unavailable."; } //noloc

            // Housing XP: the residency value's link already reads "+X XP/day" and hovers to vanilla's formula; each room links to its own value.
            string housingText;
            try
            {
                var housing = w.ResidencyPropertyValue;
                var roomLines = housing?.Rooms?.Where(r => r.RoomValue != null)
                    .OrderByDescending(r => r.RoomValue.Value)
                    .Select(r => $"{r.RoomValue.Value:0.#}  {r.RoomValue.TitleLink}") ?? Enumerable.Empty<string>(); //noloc
                housingText = housing == null ? "Housing XP: no home" //noloc
                    : $"Housing XP: {housing.UILink()}\n" + (roomLines.Any() ? string.Join("\n", roomLines) : "No rooms counted"); //noloc
            }
            catch (Exception e) { Log.WriteException(e); housingText = "Housing breakdown unavailable."; } //noloc
            if (this.Nutrition != nutrition) this.Nutrition = nutrition;
            if (this.Housing != housingText) this.Housing = housingText;
            if (this.Status != status)     this.Status = status;
            if (this.Skills != skills)     this.Skills = skills;
            if (this.Needs != needs)       this.Needs = needs;
            if (this.Supplies != supplies) this.Supplies = supplies;

            // --- Home & Money tab
            var residence = $"Citizen of: {w.DirectCitizenship?.UILink().ToString() ?? "none"}\n"; //noloc
            if (rent == null) residence += "Home: none"; //noloc
            else
            {
                residence += $"Home: {rent.Link}" + (string.IsNullOrEmpty(rent.Owners) ? "" : $" (owner {rent.Owners})") + "\n"; //noloc
                residence += rent.Rent > 0
                    ? $"Rent: {rent.Rent:0.##} {rent.CurrencyLink ?? rent.Currency}/hour" + (rent.HoursLeft != null ? $", paid for {rent.HoursLeft:0.#} more hours" : "")
                    : "Rent: free"; //noloc
            }
            var finances = $"Money: {(balances.Any() ? string.Join(", ", balances) : "none")}" + //noloc
                           (w.BankAccount != null ? $"\nAccount: {w.BankAccount.UILink()}" : ""); //noloc
            var income = $"Earned: {RabotnikIncome.Format(this.EarnedTotals)} (excluding your funding)\nSpent: {RabotnikIncome.Format(this.SpentTotals)} (rent, food)"; //noloc
            this.Parent?.GetComponent<RabotnikHomeComponent>()?.Show(residence, finances, income);

            // --- Activity tab: only problems and current work (housing and work stay silent once settled)
            var now = string.Join("\n", new[] { this.Activity, this.ShoppingActivity, this.WorkActivity, this.ContractActivity }.Where(a => !string.IsNullOrEmpty(a))); //noloc
            if (now == "") now = "Idle."; //noloc
            var record = $"Labor contributed: {this.LaborDone:0}\nWork parties joined: {this.PartiesJoined}\nItems hauled: {this.ItemsHauled}\nPlants harvested: {this.PlantsHarvested}\nContracts completed: {this.ContractsCompleted}"; //noloc
            var events = string.IsNullOrEmpty(this.EventLog) ? "Nothing yet." : this.EventLog; //noloc
            this.Parent?.GetComponent<RabotnikActivityComponent>()?.Show(now, record, events);
        }

        const string NoWorkerText = "No Rabotnik yet; one is assigned on the next update."; //noloc
    }
}
