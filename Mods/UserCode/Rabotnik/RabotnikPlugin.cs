// Rabotnik - plugin: owns the config and runs the worker tick loop.
namespace Eco.Mods.TechTree
{
    using System;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using Eco.Core.Plugins;
    using Eco.Core.Plugins.Interfaces;
    using Eco.Core.Utils;
    using Eco.Shared.Localization;
    using Eco.Shared.Logging;

    public class RabotnikPlugin : IModKitPlugin, IInitializablePlugin, IShutdownablePlugin, IConfigurablePlugin, IWorkerPlugin
    {
        PluginConfig<RabotnikConfig> config;

        /// <summary>Current settings. Falls back to defaults before the plugin has initialized.</summary>
        public static RabotnikConfig Config { get; private set; } = new RabotnikConfig();

        public IPluginConfig PluginConfig => this.config;
        public ThreadSafeAction<object, string> ParamChanged { get; set; } = new();
        public object GetEditObject() => this.config.Config;
        public void OnEditObjectChanged(object o, string param) => this.SaveConfig();

        string status = "Starting";
        public string GetStatus() => this.status;
        public string GetCategory() => Localizer.DoStr("Mods");
        public override string ToString() => Localizer.DoStr("Rabotnik");

        public void Initialize(TimedTask timer)
        {
            this.config = new PluginConfig<RabotnikConfig>("Rabotnik"); //noloc
            Config = this.config.Config;
            RabotnikVoting.Initialize();
            this.status = "Ready";
        }

        public Task ShutdownAsync() => Task.CompletedTask;

        public async Task DoWork(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, Config.TickSeconds)), token); }
                catch (TaskCanceledException) { return; }

                try
                {
                    var sw = Stopwatch.StartNew();
                    var updated = RabotnikService.Step(Config);
                    this.status = $"Ready. {RabotnikService.Count} Rabotniks; last tick updated {updated} in {sw.Elapsed.TotalMilliseconds:0.#} ms";
                }
                catch (Exception e) { Log.WriteException(e); }
            }
        }
    }
}
