// Rabotnik - admin chat commands (/rabotnik ...).
namespace Eco.Mods.TechTree
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using Eco.Gameplay.Objects;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Systems.Messaging.Chat.Commands;
    using Eco.Shared.Logging;
    using Eco.Shared.Math;

    [ChatCommandHandler]
    public static class RabotnikCommands
    {
        [ChatCommand("Rabotnik mod commands.", ChatAuthorizationLevel.Admin)]
        public static void Rabotnik(User user) { }

        [ChatSubCommand("Rabotnik", "List all Rabotnik Logbooks and their Rabotniks.", ChatAuthorizationLevel.Admin)]
        public static void List(User user)
        {
            var all = RabotnikService.All.ToList();
            if (all.Count == 0) { user.MsgLocStr("No Rabotnik Logbooks placed."); return; }
            // Vector3.ToString() is "<x, y, z>", which chat strips as a markup tag, so format it ourselves.
            var lines = all.Select(c => $"- {c.Worker?.Name ?? "(no worker)"} at {Pos(c)}: {c.Activity} {c.WorkActivity} {c.ContractActivity}");
            user.MsgLocStr($"{all.Count} Rabotnik Logbooks:\n{string.Join("\n", lines)}");
        }

        static string Pos(RabotnikComponent c) => c.Parent == null ? "?" : $"{c.Parent.Position.X:0}, {c.Parent.Position.Y:0}, {c.Parent.Position.Z:0}";

        [ChatSubCommand("Rabotnik", "Update every Rabotnik now instead of waiting for the next tick.", ChatAuthorizationLevel.Admin)]
        public static void Tick(User user)
        {
            var updated = 0;
            foreach (var c in RabotnikService.All) { RabotnikService.Update(c); updated++; }
            user.MsgLocStr($"Updated {updated} workers.");
        }

        [ChatSubCommand("Rabotnik", "Time one update of every Rabotnik and report the cost per tick.", ChatAuthorizationLevel.Admin)]
        public static void Perf(User user)
        {
            var all = RabotnikService.All;
            if (all.Count == 0) { user.MsgLocStr("No Rabotnik Logbooks placed."); return; }
            var times = new List<(RabotnikComponent c, double ms)>();
            var total = Stopwatch.StartNew();
            foreach (var c in all)
            {
                var sw = Stopwatch.StartNew();
                try { RabotnikService.Update(c); } catch (Exception e) { Log.WriteException(e); }
                times.Add((c, sw.Elapsed.TotalMilliseconds));
            }
            total.Stop();
            var slowest = times.OrderByDescending(t => t.ms).First();
            var config = RabotnikPlugin.Config;
            var perTick = Math.Min(config.MaxRabotniksPerTick, all.Count);
            var avg = times.Average(t => t.ms);
            user.MsgLocStr($"Updated {all.Count} Rabotniks in {total.Elapsed.TotalMilliseconds:0.0} ms: average {avg:0.00} ms, slowest {slowest.ms:0.00} ms ({slowest.c.Worker?.Name}). " +
                           $"A tick updates {perTick} Rabotniks every {config.TickSeconds} s, about {avg * perTick:0} ms per tick.");
        }

        [ChatSubCommand("Rabotnik", "Perf test: place this many Rabotnik Logbooks in a grid next to you (no materials or room checks). Args: count", ChatAuthorizationLevel.Admin)]
        public static void PerfSpawn(User user, int count)
        {
            if (count <= 0 || count > 200) { user.MsgLocStr("Count must be between 1 and 200."); return; }
            var start = user.Position.Round() +new Vector3i(2, 0, 2);
            var side = (int)Math.Ceiling(Math.Sqrt(count));
            var placed = 0;
            for (var i = 0; i < count; i++)
            {
                var pos = start + new Vector3i((i % side) * 2, 0, (i / side) * 2);
                var obj = WorldObjectManager.ForceAdd(typeof(RabotnikLogbookObject), user, pos, Quaternion.Identity, false);
                var c = obj?.GetComponent<RabotnikComponent>();
                if (c == null) continue;
                c.SpawnedForPerfTest = true;
                placed++;
            }
            user.MsgLocStr($"Placed {placed} test Logbooks. Each gets a Rabotnik on its next update; run /rabotnik perf to time them and /rabotnik perfclear to remove them.");
        }

        [ChatSubCommand("Rabotnik", "Perf test: remove every Logbook placed by perfspawn (their Rabotniks retire to the pool).", ChatAuthorizationLevel.Admin)]
        public static void PerfClear(User user)
        {
            var spawned = RabotnikService.All.Where(c => c.SpawnedForPerfTest && c.Parent != null && !c.Parent.IsDestroyed).ToList();
            foreach (var c in spawned) c.Parent.Destroy();
            user.MsgLocStr($"Removed {spawned.Count} test Logbooks.");
        }

        [ChatSubCommand("Rabotnik", "Show a Rabotnik's status. Args: Rabotnik name (or part of it)", ChatAuthorizationLevel.Admin)]
        public static void Info(User user, string workerName)
        {
            var c = RabotnikService.All.FirstOrDefault(x => x.Worker != null && x.Worker.Name.Contains(workerName, System.StringComparison.OrdinalIgnoreCase));
            if (c == null) { user.MsgLocStr($"No Rabotnik matching '{workerName}'."); return; }
            c.Refresh();
            var home = c.Parent?.GetComponent<RabotnikHomeComponent>();
            var activity = c.Parent?.GetComponent<RabotnikActivityComponent>();
            var sections = new[] { c.Status, c.Skills, c.Needs, c.Supplies, home?.Residence, home?.Finances, home?.Income, activity?.Now, activity?.Record, activity?.Events };
            user.MsgLocStr(string.Join("\n\n", sections.Where(s => !string.IsNullOrEmpty(s))));
        }
    }
}
