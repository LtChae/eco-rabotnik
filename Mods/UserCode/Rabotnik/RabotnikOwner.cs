// Rabotnik - chat notices to the Logbook's owner when the worker's situation changes.
// The owner never takes the worker's money back: once given, it's the worker's, and only laws (taxes, fines) take it.
// Notices compare a snapshot taken after each update with the previous one, so each change is reported once. The first
// snapshot after a restart (or placement) is only recorded, so restarts don't repeat old news.
namespace Eco.Mods.TechTree
{
    using System.Collections.Generic;
    using System.Linq;
    using Eco.Core.Systems;
    using Eco.Gameplay.Components.Storage;
    using Eco.Gameplay.Economy.WorkParties;
    using Eco.Gameplay.Items;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Property;
    using Eco.Gameplay.Systems.Messaging.Notifications;
    using Eco.Gameplay.Systems.TextLinks;
    using Eco.Shared.Items;
    using Eco.Shared.Localization;
    using Eco.Shared.Services;

    public static class RabotnikOwner
    {
        /// <summary>The parts of a worker's situation the owner hears about when they change.</summary>
        public sealed class Snapshot
        {
            // Names are what changes are detected on; the Link strings are what the notices show.
            public string Citizenship, CitizenshipLink;
            public string Home, HomeLink;
            public string Skill, SkillLink;
            public bool OutOfFood;
            public bool LowFunds;
            public HashSet<string> Parties = new();
        }

        public static Snapshot Take(RabotnikComponent component, User worker)
        {
            var config = RabotnikPlugin.Config;
            var food = component.Parent.GetComponent<PublicStorageComponent>()?.Inventory?.NonEmptyStacks
                .Any(s => s.Item is FoodItem f && f.Calories > 0) ?? false;
            var hoursLeft = RabotnikHousing.HoursOfRentLeft(worker);
            return new Snapshot
            {
                Citizenship = worker.DirectCitizenship?.Name,
                CitizenshipLink = worker.DirectCitizenship?.UILink().ToString(),
                Home        = Residency.GetResidencyDeed(worker)?.Name,
                HomeLink    = Residency.GetResidencyDeed(worker)?.UILink().ToString(),
                Skill       = component.AssignedSkill(worker)?.DisplayName.ToString(),
                SkillLink   = component.AssignedSkill(worker)?.UILink().ToString(),
                OutOfFood   = !food && worker.Stomach.Calories < config.EatBelowCalories,
                LowFunds    = hoursLeft != null && hoursLeft < config.LowFundsWarningHours,
                Parties     = Registrars.All<WorkParty>()
                    .Where(p => p.State == ProposableState.Active && p.ContainsUser(worker))
                    .Select(p => p.Name).ToHashSet(),
            };
        }

        /// <summary>Records the worker's situation and tells the owner what changed since the last update.</summary>
        public static void Report(RabotnikComponent component, User worker)
        {
            var now = Take(component, worker);
            var before = component.LastSnapshot;
            component.LastSnapshot = now;
            if (before == null) return;

            var name = worker.UILink().ToString(); // a link; stripped from the log entry below
            var notices = new List<string>();

            if (now.Citizenship != before.Citizenship)
                notices.Add(now.Citizenship != null ? $"{name} became a citizen of {now.CitizenshipLink}."
                                                    : $"{name} is no longer a citizen of {before.CitizenshipLink}. {component.Activity}");
            if (now.Home != before.Home)
                notices.Add(now.Home != null ? $"{name} moved into {now.HomeLink}."
                                             : $"{name} lost its home at {before.HomeLink} (evicted or rent unpaid) and won't work until it has one. {component.Activity}");
            if (now.Skill != before.Skill && now.Skill != null)
                notices.Add($"{name} learned {now.SkillLink}.");
            if (now.OutOfFood && !before.OutOfFood)
                notices.Add($"{name} is out of food. Stock its Logbook with food, or its skill level will fall and it will stop laboring when its calories run out.");
            if (now.LowFunds && !before.LowFunds)
                notices.Add($"{name} has money for fewer than {RabotnikPlugin.Config.LowFundsWarningHours:0} hours of rent at {now.HomeLink}. Fund it from the Logbook to avoid eviction.");
            foreach (var party in now.Parties.Except(before.Parties))
            {
                notices.Add($"{name} joined the work party {Registrars.All<WorkParty>().FirstOrDefault(p => p.Name == party)?.UILink().ToString() ?? party}.");
                component.PartiesJoined++;
            }

            foreach (var notice in notices)
            {
                // The log is on the worker's own screen, so drop its name and the activity line the notice appends.
                var entry = notice.Replace($"{name} ", "");
                if (!string.IsNullOrEmpty(component.Activity)) entry = entry.Replace(component.Activity, "");
                component.LogEvent(entry.Trim());
                if (RabotnikPlugin.Config.NotifyOwners) Tell(component, notice);
            }
        }

        /// <summary>Sends a chat message to the Logbook's owners.</summary>
        public static void Tell(RabotnikComponent component, string text)
        {
            var owners = component.Parent?.Owners;
            if (owners == null) return;
            NotificationManager.ServerMessageToAlias(Localizer.NotLocalizedStr($"[Rabotnik] {text}"), owners, NotificationCategory.Notifications, NotificationStyle.Chat, forceTemporary: false);
        }

    }
}
