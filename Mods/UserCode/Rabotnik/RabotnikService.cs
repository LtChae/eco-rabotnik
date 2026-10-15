// Rabotnik - worker registry, spawn/retire, and the per-tick update.
// Workers are connectionless Users made with TestUtils.MakeTestUser (verified in game: they persist across
// restarts with their skills, citizenship, residence and balances). The Logbook's RabotnikComponent holds the
// reference, so all worker state is saved with the world object.
namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Eco.Gameplay.Aliases;
    using Eco.Gameplay.Civics.Demographics;
    using Eco.Gameplay.Systems.Messaging.Notifications;
    using Eco.Shared.Services;
    using Eco.Gameplay.Economy;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Settlements;
    using Eco.Gameplay.Systems.TextLinks;
    using Eco.Shared.Math;
    using Eco.Gameplay.Utils;
    using Eco.Shared.Localization;
    using Eco.Shared.Logging;

    public static class RabotnikService
    {
        static readonly HashSet<RabotnikComponent> Registered = new();
        static readonly object SpawnLock = new();
        static int cursor;

        public static int Count { get { lock (Registered) return Registered.Count; } }
        /// <summary>Snapshot of all registered Logbook components.</summary>
        public static List<RabotnikComponent> All { get { lock (Registered) return Registered.ToList(); } }

        public static void Register(RabotnikComponent component) { lock (Registered) Registered.Add(component); }
        public static void Unregister(RabotnikComponent component) { lock (Registered) Registered.Remove(component); }

        /// <summary>Updates up to MaxRabotniksPerTick workers, continuing where the last tick stopped. Returns how many were updated.</summary>
        public static int Step(RabotnikConfig config)
        {
            RabotnikDemographic.Backfill();
            try { RabotnikVoting.Step(config); }
            catch (Exception e) { Log.WriteException(e); }
            var list = All;
            if (list.Count == 0) return 0;
            var count = Math.Min(Math.Max(1, config.MaxRabotniksPerTick), list.Count);
            for (var i = 0; i < count; i++)
            {
                var component = list[(cursor + i) % list.Count];
                try { Update(component); }
                catch (Exception e) { Log.WriteException(e); }
            }
            cursor = (cursor + count) % list.Count;
            return count;
        }

        public static void Update(RabotnikComponent component)
        {
            if (component.Parent == null || component.Parent.IsDestroyed) return;
            var worker = EnsureWorker(component);
            component.Activity = RabotnikHousing.Step(component, worker);
            var settlement = SettlementUtils.GetLowestLevelSettlementAtPos(component.Parent.Position.XZi());
            var ate = RabotnikNeeds.Eat(component, worker);
            component.ShoppingActivity = ate == 0 ? RabotnikShopping.Step(component, worker, settlement) : "";
            if (component.ShoppingActivity.StartsWith("Bought")) component.LogEvent(string.Join(",", component.ShoppingActivity.Split(new[] { ";" }, 3, StringSplitOptions.None).Take(2)) + "."); //noloc
            var skill = RabotnikNeeds.EnsureSkill(component, worker);
            RabotnikNeeds.RefreshSkillRate(worker);
            // An accepted contract comes first (it can't be quit and has a deadline); a new one is only taken when no party labor happened.
            // Hauling and harvesting are separate jobs (own feature flags); a worker holds one contract at a time, whichever kind.
            var (contractActivity, hauled) = RabotnikContracts.StepActive(component, worker, settlement);
            var harvested = 0;
            var harvestWorked = false;
            if (contractActivity == "") (contractActivity, harvested, harvestWorked) = RabotnikHarvest.StepActive(component, worker, settlement);
            string workActivity;
            float laborDone = 0;
            if (hauled > 0) workActivity = "Hauling for a contract."; //noloc
            else if (harvestWorked) workActivity = "Harvesting for a contract."; //noloc
            else
            {
                workActivity = RabotnikWork.Step(component, worker, skill, settlement, out laborDone);
                if (contractActivity == "")
                {
                    var (haulNew, hauledNew) = RabotnikContracts.StepNew(component, worker, settlement, laborDone > 0);
                    var (harvestNew, harvestedNew, harvestNewWorked) = ("", 0, false);
                    if (!RabotnikContractCore.HasActive(worker)) (harvestNew, harvestedNew, harvestNewWorked) = RabotnikHarvest.StepNew(component, worker, settlement, laborDone > 0);
                    contractActivity = string.Join("\n", new[] { haulNew, harvestNew }.Where(a => a != "")); //noloc
                    hauled += hauledNew;
                    harvested += harvestedNew;
                }
            }
            component.ContractActivity = contractActivity;
            component.ItemsHauled += hauled;
            component.PlantsHarvested += harvested;
            // Log why work didn't happen once per change, so a skipped party isn't silent. Generic states (no settlement, no parties) stay on the Activity tab only.
            if (workActivity != component.WorkActivity && workActivity.StartsWith("Not working:") && laborDone == 0 && workActivity.Contains(": ") && (workActivity.Contains("skipped") || workActivity.Contains("couldn't") || workActivity.Contains("no open") || workActivity.Contains(" needs ") || workActivity.Contains("can't be labored"))) //noloc
                component.LogEvent(workActivity.Replace("Not working: ", "")); //noloc
            component.WorkActivity = workActivity;
            component.LaborDone += laborDone;
            RabotnikIncome.Update(component, worker);
            component.LastOwner = component.Parent.Owners?.OneUser() ?? component.LastOwner;
            RabotnikOwner.Report(component, worker);
            component.Refresh();
        }

        /// <summary>Gives the Logbook a worker if it has none: a retired worker from the pool, or a new one.</summary>
        public static User EnsureWorker(RabotnikComponent component)
        {
            lock (SpawnLock)
            {
                if (component.Worker != null) return component.Worker;
                var config = RabotnikPlugin.Config;
                var worker = UserManager.Users.FirstOrDefault(IsRetiredWorker) ?? CreateWorker(config);

                BankAccountManager.Obj.TryCreateUserAccount(worker);
                worker.Position = component.Parent.Position;
                if (config.MarkRabotniksAbandoned) DemographicManager.Abandoned?.SetWhitelist(worker, true);
                RabotnikDemographic.Apply(worker);
                worker.MarkDirty();

                component.Worker = worker;
                component.ResetRecord();
                RabotnikIncome.Baseline(component, worker);
                component.LogEvent("Assigned to this logbook."); //noloc
                Log.WriteLine(Localizer.DoStr($"[Rabotnik] {worker.Name} assigned to {component.Parent.Name}.")); //noloc
                return worker;
            }
        }

        /// <summary>Releases the Logbook's worker back to the pool. Users are never deleted, and the worker keeps its money:
        /// once given to a worker it is the worker's, and only laws (taxes, fines) take it.</summary>
        public static void Retire(RabotnikComponent component, User owner)
        {
            lock (SpawnLock)
            {
                var worker = component.Worker;
                if (worker == null) return;
                component.Worker = null;
                component.LastSnapshot = null;
                try
                {
                    RabotnikHousing.LeaveHome(worker);
                    RabotnikHousing.LeaveSettlement(worker);
                }
                catch (Exception e) { Log.WriteException(e); }
                Log.WriteLine(Localizer.DoStr($"[Rabotnik] {worker.Name} retired from {component.Parent?.Name}.")); //noloc
                if (owner != null && RabotnikPlugin.Config.NotifyOwners)
                    NotificationManager.ServerMessageToPlayer(Localizer.NotLocalizedStr($"[Rabotnik] {worker.UILink()} moved out and left its settlement, taking its savings with it."),
                        owner, NotificationCategory.Notifications, NotificationStyle.Chat);
            }
        }

        // Workers are named "<first name> <surname>", e.g. "Kenji Rabo". Names are single words so IsWorkerName can split on spaces.
        static readonly string[] Surnames = { "Ra", "Rab", "Rabo", "Rabot", "Rabotni", "Rabotnik" }; //noloc
        static readonly string[] FirstNames = //noloc
        {
            "Ana", "Kenji", "Omar", "Ivan", "Amara", "Lars", "Priya", "Mateo", "Aiko", "Tariq",
            "Nia", "Sven", "Leila", "Diego", "Mei", "Kofi", "Olga", "Ravi", "Ines", "Yusuf",
            "Elena", "Hiro", "Zara", "Pablo", "Freya", "Malik", "Sakura", "Tomas", "Ayesha", "Bruno",
            "Lina", "Arjun", "Greta", "Kwame", "Sofia", "Jin", "Nadia", "Emil", "Fatima", "Luca",
            "Mira", "Chen", "Rosa", "Aleksei", "Yara", "Oscar", "Hana", "Ibrahim", "Ingrid", "Rafael",
            "Suki", "Dmitri", "Abebe", "Clara", "Farid", "Noor", "Pedro", "Anya", "Kai", "Zola",
        };
        static readonly Random Rng = new();

        static User CreateWorker(RabotnikConfig config)
        {
            string RandomName() => $"{FirstNames[Rng.Next(FirstNames.Length)]} {Surnames[Rng.Next(Surnames.Length)]}";
            for (var i = 0; i < 50; i++)
            {
                var name = RandomName();
                if (UserManager.FindUserByName(name) == null) return TestUtils.MakeTestUser(name);
            }
            var baseName = RandomName();
            for (var n = 2; ; n++)
            {
                var name = $"{baseName} {n}";
                if (UserManager.FindUserByName(name) == null) return TestUtils.MakeTestUser(name);
            }
        }

        /// <summary>A worker user (by name, never connected through Steam, offline) that no Logbook currently uses.</summary>
        static bool IsRetiredWorker(User user) =>
            IsWorkerName(user.Name)
            && string.IsNullOrEmpty(user.SteamId)
            && !user.IsOnline
            && All.All(c => c.Worker != user);

        // No Regex: the server's mod compiler doesn't reference System.Text.RegularExpressions.
        // Accepts "<first name> <surname>[ <number>]" from the name lists, and the old-style "<prefix> <number>" so existing workers are still recognized.
        public static bool IsWorkerName(string name)
        {
            if (name == null) return false;
            var parts = name.Split(' ');
            if ((parts.Length == 2 || parts.Length == 3) && FirstNames.Contains(parts[0], StringComparer.Ordinal) && Surnames.Contains(parts[1], StringComparer.Ordinal)
                && (parts.Length == 2 || (parts[2].Length > 0 && parts[2].All(char.IsDigit))))
                return true;
            var prefix = RabotnikPlugin.Config.RabotnikNamePrefix + " ";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length == prefix.Length) return false;
            return name.Substring(prefix.Length).All(char.IsDigit);
        }
    }
}
