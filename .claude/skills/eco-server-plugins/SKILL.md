---
name: eco-server-plugins
description: Write Eco server-side logic in C# - IModKitPlugin plugins (initialize/shutdown/config/threaded), IModInit static init hooks, chat commands ([ChatCommandHandler]/[ChatCommand]/[ChatSubCommand]), server events (user login/logout, chat messages), game-action listeners (IGameActionAware/ActionUtil), messaging players, logging, persistent JSON config in Configs/*.eco, and extending the web REST API.
---

# Eco server plugins, commands and events

Server logic can live in `Mods/UserCode/<ModName>/*.cs`, which the server compiles. It can also go in a precompiled DLL under `Mods/`, which needs the .NET 10 SDK (see the build section). Both forms are discovered by reflection at startup: no registration file, no manifest.

## Plugin lifecycle (`Eco.Core.Plugins.Interfaces`)

| Interface | Members | Use |
|---|---|---|
| `IModKitPlugin : IServerPlugin` | `string GetStatus()`, `string GetCategory()` | **Marker that makes the server instantiate the class.** Also override `ToString()` to set the name shown in the server UI. |
| `IInitializablePlugin` | `void Initialize(TimedTask timer)` | startup: subscribe to events, load config |
| `IShutdownablePlugin` | `Task ShutdownAsync()` | unsubscribe, flush |
| `IConfigurablePlugin` | `IPluginConfig PluginConfig { get; }`, `ThreadSafeAction<object,string> ParamChanged { get; set; }`, `object GetEditObject()`, `void OnEditObjectChanged(object o, string param)` | admin-editable settings saved as `Configs/<name>.eco` |
| `IThreadedPlugin` | `void Run()` | owns a background thread |
| `IWorkerPlugin` | `Task DoWork(CancellationToken)` | called in a loop; you add your own delay (`Task.Delay`) |
| `IModInit` (not a plugin) | `static void Initialize()`, `static void PostInitialize()`, `static ModRegistration Register()` | **static** hooks on mod-assembly init; vanilla uses it for tags, housing values, recipe variants and migrations |

### Configurable plugin template (current API, adapted from EcoModKit `Examples/ModkitPlugin`)

```csharp
namespace MyMod
{
    using System.Threading.Tasks;
    using Eco.Core.Plugins;
    using Eco.Core.Plugins.Interfaces;
    using Eco.Core.Utils;
    using Eco.Gameplay.Players;
    using Eco.Shared.Localization;
    using Eco.Shared.Utils;

    [Localized]
    public class WelcomeConfig : Singleton<WelcomeConfig>
    {
        [LocDescription("Title of the welcome popup.")] public string Title { get; set; } = "Welcome!";
        [LocDescription("Body of the welcome popup.")]  public string Body  { get; set; } = "Have fun.";
    }

    public class WelcomePlugin : IModKitPlugin, IInitializablePlugin, IShutdownablePlugin, IConfigurablePlugin
    {
        PluginConfig<WelcomeConfig> config;
        public IPluginConfig PluginConfig => this.config;
        public ThreadSafeAction<object, string> ParamChanged { get; set; } = new();
        public object GetEditObject() => this.config.Config;
        public void OnEditObjectChanged(object o, string param) => this.SaveConfig();   // ConfigurablePluginExtensions

        public string GetStatus()   => "Ready";
        public string GetCategory() => Localizer.DoStr("Mods");
        public override string ToString() => Localizer.DoStr("Welcome Popup");

        public void Initialize(TimedTask timer)
        {
            this.config = new PluginConfig<WelcomeConfig>("WelcomePopup");   // -> Configs/WelcomePopup.eco
            UserManager.OnUserLoggedIn.Add(this.OnLogin);
        }
        public Task ShutdownAsync() { UserManager.OnUserLoggedIn.Remove(this.OnLogin); return Task.CompletedTask; }

        void OnLogin(User user) => user.Player?.OpenInfoPanel(this.config.Config.Title, this.config.Config.Body, "Welcome");
    }
}
```

## Chat commands

```csharp
namespace MyMod
{
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Systems.Messaging.Chat.Commands;

    [ChatCommandHandler]                                     // attribute, NOT the old IChatCommandHandler interface
    public static class MyCommands
    {
        [ChatCommand("Repeats a message back to you.", ChatAuthorizationLevel.Admin)]
        public static void Echo(User user, string message = "")
        {
            if (string.IsNullOrEmpty(message)) { user.MsgLocStr("Usage: /echo <message>"); return; }
            user.MsgLocStr(message);
        }

        [ChatCommand("My mod's commands.")]                  // parent; invoked as /mymod
        public static void MyMod(User user) { }

        [ChatSubCommand("MyMod", "Shows your position.", ChatAuthorizationLevel.User)]   // /mymod where
        public static void Where(User user) => user.MsgLocStr($"You are at {user.Position}");
    }
}
```

- Auth levels: `User`, `Moderator`, `Admin`, `DevTier`. Constructors: `ChatCommand(helpText, level)`, `ChatCommand(helpText, shortCut, level)`, `ChatSubCommand(parentCommand, helpText, [shortCut], level)`.
- The first parameter is the caller (`User` or `IChatClient`). Later parameters are parsed from the comma-separated arguments, and defaults are allowed.
- Real examples: `__core__\Commands\*.cs` (`AvatarCommands`, `SimCommands`, `TechTreeCommands`, `PollutionCommands`).

## Talking to players

| Call | Effect |
|---|---|
| `user.MsgLocStr("text")` / `user.Msg(LocString)` / `user.MsgLoc($"...")` | chat or notification message to one user (style defaults apply) |
| `player.InfoBoxLocStr("text")`, `player.OkBoxLocStr("text")`, `player.ErrorLocStr("text")` | popup, OK box, or red error |
| `player.OpenInfoPanel(title, body, category)` | info panel |
| `NotificationManager.ServerMessageToAll(LocString)` | broadcast (`Eco.Gameplay.Systems.Messaging.Notifications`) |
| `NotificationManager.ServerMessageToPlayer(LocString, User, ...)` | server message to one user |

`Localizer.DoStr("literal")` makes a LocString from a literal, and `Localizer.Do($"interpolated {x}")` from an interpolated string. Mark non-translatable strings with `//noloc`. `Player` is the online avatar (`user.Player` is null when offline), while `User` is the persistent account.

## Events

- `UserManager.OnUserLoggedIn`, `OnUserLoggedOut`, `NewUserJoinedEvent`: `ThreadSafeAction<User>`, subscribed with `.Add(handler)` and `.Remove(handler)`. `UserManager.OnlineUsers` is the live set.
- `ChatManager.MessageSent.Add(Action<ChatMessage>)`: every chat message (pattern: `__core__\ChatLog\ChatLogger.cs`).
- Game actions (things laws can react to) go through an `IGameActionAware` listener:

```csharp
public class GarbageWatcher : IModKitPlugin, IInitializablePlugin, IShutdownablePlugin, IGameActionAware
{
    public string GetStatus() => "Ready"; public string GetCategory() => "Mods";
    public void Initialize(TimedTask t) => ActionUtil.AddListener(this);
    public Task ShutdownAsync() { ActionUtil.RemoveListener(this); return Task.CompletedTask; }
    public void ActionPerformed(GameAction action)
    {
        if (action is DropOrPickupGarbage g) { /* inspect g, e.g. (action as IUserGameAction)?.Citizen */ }
    }
    // From ICanOverrideAuth (IGameActionAware inherits it). Current 3-arg signature; the official example's 1-arg form is stale.
    public LazyResult ShouldOverrideAuth(IAlias alias, IOwned property, GameAction action) => LazyResult.Succeeded; // don't change auth
    // Optional, default-implemented: public Result CanPerformAction(GameAction action) => ...  (veto actions)
}
```

Usings: `Eco.Gameplay.GameActions`, `Eco.Gameplay.Aliases` (IAlias), `Eco.Gameplay.Property` (IOwned), `Eco.Core.Utils` (LazyResult, Result). Check whether `LazyResult.Succeeded` is the right "no opinion" value against vanilla implementers such as `PlantActionAware`; I haven't verified it.

The GameAction types live in `Eco.Gameplay.GameActions`. Examples: `PlantSeeds`, `ChopStump`, `BlockAddRemove`, `ChatSent`, `TradeAction`, `TransferMoney`, `PayTax`, `PolluteAir`, `DropOrPickupGarbage`, `ClaimOrUnclaimProperty`, `GainSpecialty`, `FirstLogin`, `WorldObjectInteractAction`, `ToolInteractAction`, `Vote`, `StartElection`. Full list: grep `^T:Eco\.Gameplay\.GameActions\.` in `eco-api-lookup/api/Eco.Gameplay.api.tsv`. The official example uses `DropGarbage`, which has since been renamed `DropOrPickupGarbage`.

## Giving reputation from code

From decompiling v0.14.1.1; it compiles but hasn't been tested in game yet. Rabotnik's version is in `RabotnikReputation.cs`.

```csharp
ReputationManager.Obj.GiveRep(IGivesReputation source, IHasReputation target, float amount, string note = null,
    bool ignoreLimit = false, ReputationSource src = ReputationSource.TransferredManually, bool sendNotice = true, Vector3i? actionLocation = null)
// returns ValResult<float>; .Val is the amount actually applied after clamping
```

- **Usings:** `Eco.Gameplay.Economy.Reputation` and `Eco.Shared.Items` (`ReputationSource`). `User` is both a giver and a receiver.
- **Negative reputation:** pass a negative amount.
- **No Player needed.** A connectionless `TestUtils.MakeTestUser` user can give; the giver's popups (`InfoBoxLoc`) just do nothing. Don't use `Player.GiveReputationTo` or `OpenGiveRepUI`, which need a client.
- **Limits per giver** (`Configs/Reputation.eco`): 3 per target per day and 15 per day in total (both counted as absolute values, so negative uses them up too), and a lifetime net of +30 / −10 per target. They reset once per game day. `ignoreLimit: true` skips all of them.
- **`sendNotice: true`** broadcasts to all players and mails the receiver. **`sendNotice: false`** is silent, but because of how the code is written (`!result & sendNotice`) it also ignores law vetoes on `ReputationTransfer` and the target's `CanGiveReputation`.
- **Work-party reputation pay** comes from `ReputationPayment.PayUser`: `GiveRep(party.Creator, laborer, amount, ..., ReputationSource.TransferredUsingWorkParty)`.
- **Admin commands for testing:** `/culture rep` gives reputation, and `/culture reprep` refills a user's daily allowance.

## Logging

`Eco.Shared.Logging.Log`: `Log.WriteLine(Localizer.DoStr("..."))`, `Log.WriteLineLoc($"...")`, `Log.WriteWarningLineLocStr("...")`, `Log.WriteErrorLineLocStr("...")`, `Log.WriteException(ex)`. For a dedicated log file, use `NLogManager.GetLogWriter("MyMod/Name").Write(msg)` (pattern: `ChatLogger.cs`).

## Web API extension

The Eco web server (default port 3001) hosts ASP.NET Core. A public `Controller` in a mod assembly gets picked up:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
[AllowAnonymous, Route("api/v1/helloworld")]
public class HelloWorldController : Controller { [HttpGet("")] public string Get() => "Hello World!"; }
```

(This comes from `Examples/ExtendRestApi`. It may need a DLL build to reference ASP.NET Core; untested here.)

## Building a precompiled DLL mod (when source mods aren't enough)

Needs the **.NET 10 SDK**, which isn't installed on this machine (only runtimes 6 and 7). The csproj targets `net10.0` and references the ModKit `ReferenceAssemblies\*.dll` (extract them from `EcoModKit_v0.14.1.1-beta.zip`) with `<Private>false</Private>`. Put the output DLL anywhere under the server's `Mods/`. The official example csproj still says `net5.0`, which is stale. Prefer UserCode source mods unless you need ASP.NET, NuGet packages, or closed source.
