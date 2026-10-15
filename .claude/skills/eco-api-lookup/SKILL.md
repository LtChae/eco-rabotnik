---
name: eco-api-lookup
description: Look up exact Eco server API signatures (classes, methods, properties, constructors, attributes, enums) and find vanilla examples (item/recipe/object/skill class names, which table and skill a recipe uses, tags) offline. Use before writing or reviewing any Eco mod C#, because the online docs and official examples are often stale or unreachable.
---

# Eco API and vanilla content lookup

Everything here is local and greppable. Prefer it over memory, docs.play.eco (blocks bots) and GitHub examples (partly stale).

## 1. API signatures: `api/<Assembly>.api.tsv`

One line per public member of each ModKit reference assembly (v0.14.1.1-beta, .NET 10):

```
<docId>\t<C# declaration>\t// <summary, if documented>
```

- `docId` prefixes: `T:` type, `M:` method or ctor (`#ctor`), `P:` property, `F:` field or enum value, `E:` event.
- Files: `Eco.Gameplay` (items, recipes, objects, components, players, chat, economy, game actions: the big one), `Eco.Core` (plugins, config, utils, `Result`/`LazyResult`), `Eco.Shared` (localization, math, logging, networking attributes, `Singleton<T>`), `Eco.Simulation` (species, world layers), `Eco.World` (blocks), `Eco.Mods` (types compiled from `__core__`), `Eco.WebServer`, `Eco.Plugins`, `Eco.ModKit`, `Eco.WorldGenerator`, `Eco.Stats`.
- **Caveat:** declarations come from docfx metadata, which **drops default values for enum and struct parameters**. `NotificationStyle style` may really be `NotificationStyle style = NotificationStyle.Chat`. When in doubt, find a vanilla call site.

Search recipes (PowerShell; the Grep tool works too):

```powershell
$api = "E:\Code\eco_mod\.claude\skills\eco-api-lookup\api"
# All members of a type
Select-String -Path "$api\Eco.Gameplay.api.tsv" -Pattern '^[MPFE]:Eco\.Gameplay\.Items\.Recipes\.IngredientElement\.'
# Constructors of a type
Select-String -Path "$api\*.api.tsv" -Pattern '^M:[^\t]*\.CraftingElement`1\.#ctor'
# Where is a type defined (namespace for the using)
Select-String -Path "$api\*.api.tsv" -Pattern '^T:[^\t]*\.PublicStorageComponent\t'
# Everything deriving from X
Select-String -Path "$api\*.api.tsv" -Pattern '^T:[^\t]*\t[^\t]*: WorldObjectComponent\b'
```

Generic types use a backtick arity (``PluginConfig`1``), and nested types use `.`. Method docIds include full parameter types.

## 2. Vanilla content: `vanilla/`

Generated from `E:\SteamLibrary\steamapps\common\Eco\Eco_Data\Server\Mods\__core__`:

| File | Columns | Typical question |
|---|---|---|
| `vanilla-types.tsv` | `ClassName  BaseType  File` | "What's the class name for the carpentry table / iron bar / steel axe, and which file shows how it's built?" |
| `vanilla-recipes.tsv` | `RecipeFamily  Tables  Skill Level  Ingredients => Outputs  File` | "What crafts at the Kiln?", "What uses IronBarItem?", "What skill gates X?" |
| `vanilla-tags.txt` | `Tag  count` | "Is `Fabric` a real tag?" (only `[Tag("...")]` uses; also see `Systems\TagDefinitions.cs`) |

```powershell
$v = "E:\Code\eco_mod\.claude\skills\eco-api-lookup\vanilla"
Select-String -Path "$v\vanilla-recipes.tsv" -Pattern '\tKilnObject'          # recipes at a table
Select-String -Path "$v\vanilla-recipes.tsv" -Pattern 'IronBarItem.*=>'       # recipes consuming iron bars
Select-String -Path "$v\vanilla-types.tsv"   -Pattern '\tWorldObject\t'        # all world objects
Select-String -Path "$v\vanilla-types.tsv"   -Pattern '^\w+Skill\tSkill\t'     # all skills
```

Then **read the vanilla file** at `__core__\<File>`. It's the authoritative, compiling example for the current version.

## 3. Raw sources when the index isn't enough

- Vanilla C#: `E:\SteamLibrary\steamapps\common\Eco\Eco_Data\Server\Mods\__core__` (grep it directly for usage patterns and call sites).
- XML doc comments: the `ReferenceAssemblies\*.xml` files inside `E:\Code\eco_mod\EcoModKit_v0.14.1.1-beta.zip`.
- Server config templates (all settings and defaults): `...\Eco_Data\Server\Configs\*.eco.template`.

## 4. Reading the real implementation (decompiling)

The ModKit DLLs are **reference assemblies**, which are signatures only with no method bodies. The real code is packed inside the single-file `EcoServer.exe`. When behaviour matters (preconditions, Player-only prompts, distance checks), read the real code:

```powershell
$x = "$env:TEMP\ecobin"   # any scratch dir
# 1. Extract Eco*.dll from the bundle (tools/BundleExtract is a tiny .NET single-file bundle reader)
dotnet run --project E:\Code\eco_mod\tools\BundleExtract -- "E:\SteamLibrary\steamapps\common\Eco\Eco_Data\Server\EcoServer.exe" $x Eco
# 2. Decompile one type (ilspycmd is installed as a global dotnet tool)
ilspycmd "$x\Eco.Gameplay.dll" -t Eco.Gameplay.Property.Residency -r $x > Residency.cs
# List type names: ilspycmd "$x\Eco.Gameplay.dll" -l c | Select-String Residency
```

Re-extract after every game update. Don't commit decompiled code.

### Verified behaviour (v0.14.1.1, from decompiling)
- **Deed rent:** `Deed.IsRentable` means `Residency.Invitations.InvitationList.Any()`. A deed is "rentable" only when the owner has invited users, titles or demographics, not merely when rent is set. `Residency.CanBecomeResident(user)` means not already a resident, and either the owner or invited (`ContainsExactAlias`).
- **Moving in from code:** `RentForProperty.ValidateAndGetFeeInfo(user)` does `await user.Player?.ConfirmBoxLoc(...)` whenever rent or a fee is set. For a connectionless `User` that awaits null and throws. Instead, evaluate `MoveInFee`, `MoveInFeeCurrency` and `RecurringTransferDefinition.AmountPerHour/Currency` with a `RecurringTransferContext`, then invoke the private `Residency.BecomeResidentInternal(User, ResidencyChangeReason, FeeInfo)` by reflection. That is the "accept invite" path: residency game action, move-in fee, recurring rent, notifications. `DebugForceResidency` skips rent and laws. Rent (`RecurringTransfer.Pay`) is charged every `CivicsPlugin` `RecurringTransferFrequencyInMinutes` (default 60) of world time, prorated by elapsed hours. If the deed's `RecurringTransferDefinition.PaymentType` is `WageType.OnlineHours`, only the customer's online seconds count, so a never-online user pays nothing. The default is `OnlineAndOfflineHours`.
- **Citizenship:** add the user via `settlement.Citizenship.DirectCitizenRoster.AddToRoster(adder, user, sendNotice, forceAdd)`. The roster's `MemberAddedEvent` sets `User.DirectCitizenship` and refreshes the citizen cache and demographic. Writing `User.DirectCitizenship` directly doesn't update the roster. Gate it with `settlement.ImmigrationPolicy?.CheckCanJoinAsCitizen(user, true)`. `HasCitizen(user)` refreshes the cache; `Citizens` doesn't.
- **Work parties:** `WorkParty.Join(User, Laborer)` fails if `user.Position` is more than 8 m from the board (`((IHostedObject)party).HostObject.Object`). `WorkOrder.TryToContributeLabor(User, mult)` has no distance check. It adds `min(100*mult, AllowedLabor)` labor, burns the user's calories, and checks skills, talents, `ExhaustionMonitor` and laws (`LaborWorkOrderAction`).
- **`Stomach.Eat(food, out msg)`** applies everything (Contents, Calories, taste and cravings, nutrients, eaten events) and then calls `Owner.Player.RPC(...)` with no null check. `RPC` is an extension method, so for a connectionless user it throws `ArgumentException("No network object provided to call RPC 'FoodEaten' on")` after the meal is recorded. Catch that and verify via `Stomach.Contents`. Before that, `CanEat` silently returns false (empty message) when `Calories + food.Calories > MaxCalories`; pass `force: true` or check `CanEat` first. The same applies to any vanilla `x.Player.RPC(...)` call reached with a null Player. Offline users still burn calories over time.
- **Activity:** `User.IsActive` and `IsAbandoned` are memberships in the special demographics `DemographicManager.Active` and `Abandoned`, computed from `User.OnlineTimeLog` (`HoursPlayed`), not `ActiveTimeLog`. Defaults in `CivicsPlugin.Config`: Active is at least 0.5 h online in 1.5 days; Abandoned is a citizen older than 24 h with too few hours in 7 days. Being Abandoned only reduces stakes and claims generated, excludes the user from specialty counts and annexation, and marks deeds they *own* abandoned. It doesn't evict anyone or revoke citizenship. Admins can whitelist a user into it (`CivicsCommands`).
- **Contracts:** `Contract.AcceptJob(user)` needs a Player (a NullReferenceException for connectionless users), and harvesting plants (`PlantEntity.TryHarvest`) does too. Moving items with `Inventory.MoveItems(..., user)` and `JobFinished(user)` work without one. See the `eco-contracts` skill for the Player-free accept path and the rest.
- **`TestUtils.MakeTestUser`** gives Survivalist and Self Improvement at level 1, a bank account, and a personal currency with infinite balance. The user isn't marked abandoned right away.

## Regenerating after a game or ModKit update

Both scripts are in `scripts/`. Extract the new ModKit zip somewhere temporary first (its `ReferenceAssemblies\obj\xdoc\cache\obj\*` holds the docfx YAML the API index is built from).

```powershell
$k = "E:\Code\eco_mod\.claude\skills\eco-api-lookup"
Expand-Archive "E:\Code\eco_mod\EcoModKit_<version>.zip" -DestinationPath "$env:TEMP\ecomodkit" -Force
powershell -NoProfile -ExecutionPolicy Bypass -File "$k\scripts\build-api-index.ps1" -RefDir "$env:TEMP\ecomodkit\ReferenceAssemblies" -OutDir "$k\api"
powershell -NoProfile -ExecutionPolicy Bypass -File "$k\scripts\build-vanilla-index.ps1" -CoreDir "E:\SteamLibrary\steamapps\common\Eco\Eco_Data\Server\Mods\__core__" -OutDir "$k\vanilla"
```

Takes about 20 s plus 10 s. Update the version numbers mentioned in the `eco-modding` skill afterwards.
