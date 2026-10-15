---
name: eco-modding
description: Start here for any work on an Eco (Strange Loop Games) mod. Covers how Eco mods are built and loaded (server C# compiled from Mods/__core__ + Mods/UserCode, precompiled DLLs, Unity .unity3d client bundles), the local install paths and versions on this machine, the dev/deploy/test loop, and the known traps (stale official examples). Routes to the specialised eco-* skills.
---

# Eco modding: overview and workflow

Eco mods have up to two halves:

| Half | What it is | Needed for |
|---|---|---|
| **Server (C#)** | Classes compiled into the server: items, recipes, world objects, skills, plugins, chat commands, etc. | Every mod. Gameplay lives entirely on the server. |
| **Client (Unity)** | A `.unity3d` AssetBundle built with the ModKit Unity project, holding prefabs, icons, meshes and block sets. Server classes match client prefabs **by name**. | Only for new visuals (new item icons, new object/block meshes). Server-only changes (recipe tweaks, plugins, commands) don't need it. |

The server sends `.unity3d` bundles under `Mods/` to connecting clients automatically, so players never install anything by hand.

## This machine (verified 2026-10-01)

| Thing | Path / value |
|---|---|
| Game client (Steam) | `E:\SteamLibrary\steamapps\common\Eco` |
| Embedded server | `E:\SteamLibrary\steamapps\common\Eco\Eco_Data\Server` (`EcoServer.exe`, `Configs\`, `Mods\`, `Storage\`) |
| Vanilla C# source | `...\Eco_Data\Server\Mods\__core__` (~2,600 files; **the** reference for how to write anything) |
| User mods folder | `...\Eco_Data\Server\Mods\UserCode` (empty except its README) |
| ModKit package | `E:\Code\eco_mod\EcoModKit_v0.14.1.1-beta.zip`: `EcoModKit.unitypackage`, `ReferenceAssemblies\*.dll + *.xml`, `ProjectVersion.txt` |
| Unity version for ModKit | **6000.3.6f1** (Unity 6.3) |
| Server runtime | **.NET 10** (`.NETCoreApp,Version=v10.0` in every reference DLL) |
| Local .NET | **SDK 10.0.401** (installed 2026-10-01) |
| Server logs | `...\Eco_Data\Server\Logs\log_<yyMMddHHmmss>.log` (one per server start). Chat-command exceptions show here with full stack traces; in game you only see "Failed to execute command". |
| Client log | `%USERPROFILE%\AppData\LocalLow\Strange Loop Games\Eco\Player.log` (previous run: `Player-prev.log`). Check it when UI misbehaves without a server error (e.g. empty dropdowns, missing panels). |
| Local compile check | `dotnet build tools/BuildCheck` compiles `Mods/UserCode/Rabotnik/**/*.cs` with **the same references as the server's mod compiler** (see below). Run it before every deploy. Add more mod folders to its `<Compile Include>` as needed. |
| git | not installed |

No dedicated-server Steam app is installed. Test with the embedded server, either by starting a single-player world or by running `EcoServer.exe` directly.

## How the server loads mods (from Eco.ModKit `RoslynCompiler`)

1. At startup the server collects **every `.cs` under `Mods/__core__` and `Mods/UserCode`** and compiles them with Roslyn into one assembly, then runs Fody weavers (property-changed injection for `[SyncToView]`, `[Eco]` and similar).
2. Because it's **one compilation**, UserCode can add members to vanilla `partial` classes, implement their `partial void ModsPreInitialize()/ModsPostInitialize()` hooks, and reference any vanilla type directly.
3. **Override files.** `UserCode/<same relative path>/<Name>.override.cs` *replaces* `__core__/<path>/<Name>.cs` entirely. Example: `__core__\AutoGen\Item\IronGear.cs` → `UserCode\AutoGen\Item\IronGear.override.cs`.
4. **Precompiled DLLs** can sit anywhere under `Mods/`. They're loaded as plugins and can't use partial classes of vanilla types.
5. **Compile errors in any UserCode file fail the whole mods assembly**, so the server can't start. Keep mods small and test often.
   - **Restricted references.** Decompiled from `RoslynCompiler` in v0.14.1.1, mods compile only against:
     - **System:** `netstandard`, `System.Collections`, `.Concurrent`, `.Immutable`, `System.ComponentModel.Primitives`, `System.IO.FileSystem`, `System.Linq`, `System.ObjectModel`, `System.Private.CoreLib`, `System.Runtime`, `System.Runtime.Extensions`, `System.Numerics.Vectors`.
     - **Game:** `Eco.Core/Gameplay/ModKit/Shared/Simulation/World/Stats/WorldGenerator`, `LiteDB`, `Priority Queue`, `PropertyChanged`, `NetFabric.Hyperlinq.Abstractions`.
   - So **no `System.Text.RegularExpressions`**, and no `System.Net.*`, `System.Text.Json`, `Eco.Plugins` or `Eco.WebServer`. A "The name 'X' does not exist" error here crashes server start. `tools/BuildCheck` mirrors this list, so it catches these locally.
6. `Configs/ModKit.eco` has `PreserveGeneratedModsAssembly` (keeps the compiled assembly for debugging), `LiveUpdateUnityFiles` (hot-reloads bundles) and `SubscribedMods` (mod.io IDs the server auto-downloads).

Never edit `__core__`: game updates wipe it. Use partials, `ModsPreInitialize`, or `.override.cs`.

## Choosing an approach

| Goal | Approach | Skill |
|---|---|---|
| Tweak a vanilla recipe/item/object/skill number | `partial class` + `partial void ModsPreInitialize()` in UserCode | eco-items-recipes / eco-world-objects |
| Totally rewrite a vanilla file | `.override.cs` (fragile across game updates) | eco-items-recipes |
| New item / food / tool / block / recipe | New classes in UserCode (+ icon in a Unity bundle) | eco-items-recipes, eco-unity-client |
| New placeable object / crafting table / custom UI component | WorldObject + WorldObjectItem + components (+ prefab) | eco-world-objects, eco-unity-client |
| New skill, talent, plant, tree, animal | Skill / Talent / PlantSpecies / AnimalSpecies classes | eco-skills-and-species |
| Server logic: events, chat commands, config, laws/game actions, web API | `IModKitPlugin`, `[ChatCommandHandler]`, `IGameActionAware` | eco-server-plugins |
| Read, create, accept or complete contracts from code (clauses, escrow, trackers) | `Contract`, `ContractClause*`, `ContractManager` | eco-contracts |
| Look up a type/method signature, or find the vanilla example of something | API + vanilla indexes | eco-api-lookup |
| In-game help pages (Ecopedia) | XML files in an `Ecopedia/<Chapter>/` folder, plus `[Ecopedia]` on types | this skill, "Ecopedia pages" below |

## Recommended repo layout and dev loop

The repo isn't a git repo yet. Suggested layout (adapt per mod):

```
eco_mod/
  Mods/UserCode/<ModName>/*.cs          # server source, mirrors the server's Mods/UserCode
  Mods/UserCode/<ModName>/<modname>.unity3d   # built client bundle (if any)
  Client/                                # Unity 6000.3.6f1 project with EcoModKit.unitypackage imported (if needed)
```

Loop:
1. Write or modify `.cs` under `Mods/UserCode/<ModName>/`. Use namespace `Eco.Mods.TechTree` for content mods so vanilla types resolve and partials match.
2. **Compile-check locally:** `dotnet build tools/BuildCheck`. This catches API and typo errors without starting the server. It checks only compilation, not runtime behaviour.
3. **Deploy** by copying `Mods/UserCode/<ModName>` → `E:\SteamLibrary\steamapps\common\Eco\Eco_Data\Server\Mods\UserCode\<ModName>`. Ask before writing into the game folder the first time.
4. Start the server (single-player world, or `EcoServer.exe`). Watch the console/log for Roslyn errors, which carry file and line numbers.
5. In game, as admin: `/give <ItemName>` to spawn items. Use the item's display name or class name; the Toll example in core says `/give TollItem`.
6. Iterate. Server-side C# needs a server restart. Unity bundles can hot-reload with `LiveUpdateUnityFiles`.

**Distribution:** mod.io is the official host. Server admins drop files into `Mods/` (source into `Mods/UserCode/`), and clients get bundles automatically.

## Ecopedia pages (decompiled v0.14.1.1: `EcopediaManager.Build`, `EcopediaLocTools`, `EcopediaSectionBase`)

Rabotnik's pages are in `Mods/UserCode/Rabotnik/Ecopedia/Concepts/`. Vanilla's are in `__core__\Ecopedia\`, and they're the best format reference.

- **Discovery:** at startup the server finds every directory named `Ecopedia` anywhere under `Mods/` (recursively). It loads `*.xml` from that directory's **immediate subdirectories only**: XML directly in `Ecopedia/`, or two levels down, is ignored. The subfolder name doesn't matter. Pages are built once at startup, so changes need a server restart.
- **File names:** the file name is `Category.xml`, `Category;Page.xml` or `Category;Page;Subpage.xml`. A chapter file is `<ecopediachapter priority=".."/>`. A category file is `<ecopedia icon=".." chapter="Concepts" priority=".."><summary>..</summary></ecopedia>`. A mod can add a category to a vanilla chapter by naming it in `chapter=`. Pages and categories sort by `priority`, lowest first.
- **Page names are global.** Links resolve by the bare page name, so a name that collides with a vanilla page (for example "Contracts") logs a collision error. Prefix mod page names.
- **Page contents:** `<summary>` is the list blurb. `<section>` holds body text (`<b>`, `<i>`, `- ` bullets, blank-line paragraphs). The other section types are `type="header"`, `type="banner" image=".."`, `type="quote"` and `type="button" url=".."`. `icon=` takes a sprite or item icon name (for example `RabotnikLogbookItem`, `Residency`, `Work Party`).
- **Links:** `[Page]` or `[Shown text|Page]`. The regex is `[\[{](.+?)[\]}]`, so **any square or curly brackets in prose are parsed as links**. Don't use them, and avoid XML entities (`&amp;`, `&lt;`) too: the section's raw inner XML is sent to the client, and no vanilla page uses entities. An unknown link logs "Can't find ecopedia link" and renders as plain text.
- **Linking a type to a page:** put `[Ecopedia("Category", "Page")]` on the object, item and recipe classes. If a page with that name already exists (a hand-written XML page), the type is attached to it, so its tooltip links there. Otherwise the server creates the page. With `createAsSubPage: true`, the type gets its own subpage under that page (vanilla's lamps and beds work this way).
- **Localization:** vanilla's `loc="N"` attributes key into `Ecopedia/LocalizedStrings/<lang>.xlf`. A mod can omit them.

## Known traps: the official examples are partly stale

The EcoModKit GitHub `Examples/` (and many community guides) predate current APIs. **Verify every API against `eco-api-lookup` before using it.** Confirmed drift:

| Old (examples/guides) | Current (v0.14.1.1 / .NET 10) |
|---|---|
| `class X : IChatCommandHandler` | `[ChatCommandHandler] public static class X` (attribute; interface no longer exists) |
| `ChatManager.ServerMessageToPlayer(...)` | `user.MsgLocStr("...")`, `user.Msg(LocString)`, `player.InfoBoxLocStr`, `NotificationManager.ServerMessageToPlayer(...)` |
| `new Recipe(name, display, IngredientElement[], CraftingElement[])` | `var r = new Recipe(); r.Init(name:, displayName:, ingredients: List, garbages: List, items: List)`, or the 5-arg ctor with `GarbageOutput[]` |
| `CraftingComponent.AddRecipe(typeof(T), recipe: this)` | `AddRecipe(tableType: typeof(T), recipeFamily: this)` |
| `public override int ShelfLife => 86000;` on FoodItem | `public override float BaseShelfLife => (float)TimeUtil.HoursToSeconds(96);` |
| `public override LocString DisplayDescription` on items | `[LocDescription("...")]` attribute on the item class |
| csproj `net5.0` | `net10.0` |

## Other references

- Official API docs: https://docs.play.eco (bot-blocked for WebFetch; use the local index instead)
- Wiki: https://wiki.play.eco/en/Modding (also bot-blocked)
- Examples: https://github.com/StrangeLoopGames/EcoModKit/tree/main/Examples (raw files fetch fine via raw.githubusercontent.com)
- Core design notes live as comments in `__core__`: `Objects\TollObject.cs` (custom components, interactions, auto-generated UI), `Skills\SkillMods.cs` (cost hooks), `UserCode\README.md` (override rules).
