// Rabotnik - passive voting: Rabotniks follow the real players' votes, weighted by reputation.
// Once every non-abandoned real (non-Rabotnik) eligible voter has voted, or the election is close to expiring, the
// Rabotnik votes are split across the real ballots in proportion to each voter's reputation (largest remainder), and
// re-split when a real vote changes. Vanilla Election.Vote posts one global message per voter, so Rabotnik votes are
// written to Election.UserVotes directly after the Vote game action passes (laws still apply), with one summary message.
// Election.Vote fires UserVotedEvent before it recalculates results, so the last real vote and the Rabotnik votes are
// counted together and the election can't end early on real votes alone.
namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Eco.Core.Systems;
    using Eco.Gameplay.Civics;
    using Eco.Gameplay.Civics.Elections;
    using Eco.Gameplay.GameActions;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Systems.Messaging.Notifications;
    using Eco.Gameplay.Systems.TextLinks;
    using Eco.Shared.Items;
    using Eco.Shared.Localization;
    using Eco.Shared.Logging;
    using Eco.Shared.Services;
    using Eco.Shared.Utils;
    using VoteAction = Eco.Gameplay.GameActions.Vote;

    public static class RabotnikVoting
    {
        static readonly object SyncLock = new();
        static bool subscribed;

        public static void Initialize()
        {
            if (subscribed) return;
            subscribed = true;
            Election.UserVotedEvent.Add(OnUserVoted);
        }

        /// <summary>Re-checks every running election: catches restarts, players going abandoned, withdrawn candidates and the deadline fallback.</summary>
        public static void Step(RabotnikConfig config)
        {
            if (!config.RabotniksVote) return;
            foreach (var election in Singleton<ElectionManager>.Obj.CurrentElections(null).ToList())
            {
                try
                {
                    var nearDeadline = config.VoteBeforeDeadlineMinutes > 0 && election.TimeLeft <= config.VoteBeforeDeadlineMinutes * 60 + config.TickSeconds;
                    if (Sync(election, nearDeadline)) election.ForceUpdate();
                }
                catch (Exception e) { Log.WriteException(e); }
            }
        }

        static void OnUserVoted(UserRunoffVote vote)
        {
            try
            {
                if (!RabotnikPlugin.Config.RabotniksVote) return;
                var voter = vote.Voter;
                if (voter == null || IsRabotnik(voter)) return;
                // The event doesn't say which election; syncing every one the voter voted in is harmless (Sync is idempotent).
                // No ForceUpdate here: Election.Vote recalculates right after this event.
                foreach (var election in Singleton<ElectionManager>.Obj.CurrentElections(null).Where(e => e.UserVotes.ContainsKey(voter)).ToList())
                    Sync(election, nearDeadline: false);
            }
            catch (Exception e) { Log.WriteException(e); }
        }

        static bool IsRabotnik(User user) => RabotnikDemographic.GetOrCreate()?.ContainsUser(user) ?? RabotnikService.IsWorkerName(user.Name);

        /// <summary>Brings the Rabotnik votes in line with the real ballots. Returns true if any Rabotnik vote was cast or changed.</summary>
        static bool Sync(Election election, bool nearDeadline)
        {
            lock (SyncLock)
            {
                if (election.State != ProposableState.Active || election.Finished || election.Process?.Voters == null) return false;
                var demographic = RabotnikDemographic.GetOrCreate();
                if (demographic == null) return false;

                var settlement = election.Settlement;
                var eligible = election.Process.Voters.UserSet.Where(u => u != null && (settlement == null || settlement.Citizenship.HasCitizen(u))).Distinct().ToList();
                // Only workers that hold a Logbook vote; pooled (retired) ones sit out.
                var active = RabotnikService.All.Select(c => c.Worker).Where(w => w != null).ToHashSet();
                var rabotniks = eligible.Where(u => demographic.ContainsUser(u) && active.Contains(u)).OrderBy(u => u.Id).ToList();
                if (rabotniks.Count == 0) return false;
                var realPending = eligible.Where(u => !demographic.ContainsUser(u) && !u.IsAbandoned).ToList();

                var ballots = election.UserVotes.Where(kv => kv.Key != null && !demographic.ContainsUser(kv.Key) && kv.Value.RankedVotes != null && kv.Value.RankedVotes.Length > 0).ToList();
                if (ballots.Count == 0) return false;
                if (!nearDeadline && !realPending.All(election.Voted)) return false;

                // Group identical rankings; weight = non-negative reputation (everyone 1 if nobody has any).
                var useRep = ballots.Sum(kv => Math.Max(0f, kv.Key.Reputation)) > 0;
                var groups = ballots
                    .GroupBy(kv => Key(kv.Value.RankedVotes))
                    .Select(g => (Key: g.Key, Ranked: g.First().Value.RankedVotes, Weight: g.Sum(kv => useRep ? (double)Math.Max(0f, kv.Key.Reputation) : 1.0)))
                    .OrderByDescending(g => g.Weight).ThenBy(g => g.Key, StringComparer.Ordinal)
                    .ToList();
                var quota = Apportion(groups.Select(g => g.Weight).ToList(), rabotniks.Count);
                var remaining = new Dictionary<string, int>();
                for (var i = 0; i < groups.Count; i++) remaining[groups[i].Key] = quota[i];

                // Keep Rabotniks already on a group with quota left; re-vote only the rest, so laws on voting don't fire needlessly.
                var toAssign = new List<User>();
                foreach (var r in rabotniks)
                {
                    if (election.UserVotes.TryGetValue(r, out var current) && current.RankedVotes != null
                        && remaining.TryGetValue(Key(current.RankedVotes), out var left) && left > 0)
                        remaining[Key(current.RankedVotes)] = left - 1;
                    else toAssign.Add(r);
                }

                var changed = 0;
                foreach (var r in toAssign)
                {
                    var group = groups.FirstOrDefault(g => remaining[g.Key] > 0);
                    if (group.Key == null) break;
                    remaining[group.Key]--;
                    if (Cast(election, r, group.Ranked)) changed++;
                }
                if (changed == 0) return false;

                Announce(election, rabotniks);
                return true;
            }
        }

        static string Key(ElectionChoiceID[] ranked) => string.Join(",", ranked.Select(c => c.Id)); //noloc

        /// <summary>Largest-remainder split of seats by weight. Ties go to the earlier (heavier) entry.</summary>
        static int[] Apportion(List<double> weights, int seats)
        {
            var total = weights.Sum();
            var exact = weights.Select(w => total > 0 ? seats * w / total : 0).ToList();
            var result = exact.Select(x => (int)Math.Floor(x)).ToArray();
            var left = seats - result.Sum();
            foreach (var i in Enumerable.Range(0, weights.Count).OrderByDescending(i => exact[i] - result[i]).ThenBy(i => i).Take(Math.Max(0, left)))
                result[i]++;
            return result;
        }

        /// <summary>Runs the Vote game action (so laws apply) and records the vote without vanilla's per-voter global message.</summary>
        static bool Cast(Election election, User rabotnik, ElectionChoiceID[] ranked)
        {
            var hadVote = election.UserVotes.TryGetValue(rabotnik, out var old) && old.RankedVotes != null && old.RankedVotes.Length > 0;
            var action = new VoteAction
            {
                Citizen = rabotnik,
                Election = election,
                ElectionProcess = election.Process,
                VotedForUser = election.GetChoiceUser(ranked[0]),
                VoteType = hadVote ? VoteType.ChangedPreviousVote : VoteType.FirstVoteInElection,
            };
            if (!action.TryPerform(null)) return false;
            election.UserVotes.Remove(rabotnik);
            election.UserVotes.Add(rabotnik, new UserRunoffVote(rabotnik, ranked.ToArray()));
            return true;
        }

        static void Announce(Election election, List<User> rabotniks)
        {
            var tally = rabotniks
                .Where(r => election.UserVotes.TryGetValue(r, out var v) && v.RankedVotes != null && v.RankedVotes.Length > 0)
                .GroupBy(r => election.UserVotes[r].RankedVotes[0])
                .Select(g => (Choice: g.Key, Count: g.Count()))
                .OrderByDescending(t => t.Count)
                .ToList();
            var parts = string.Join(", ", tally.Select(t => $"{t.Count} for {election.GetChoiceName(t.Choice)}")); //noloc
            NotificationManager.ServerMessageToAll(
                Localizer.NotLocalizedStr($"{election.UILink()}: {tally.Sum(t => t.Count)} Rabotniks voted, following the citizens' votes weighted by reputation: {parts}."),
                NotificationCategory.Government, NotificationStyle.Info);
        }
    }
}
