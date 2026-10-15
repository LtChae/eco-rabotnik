---
name: eco-contracts
description: How Eco contracts (contract boards, Contract, ContractClause*, ContractManager, escrow payment, item trackers) work in server C#, verified by decompiling v0.14.1.1, what in-game testing proved (connectionless users can accept, haul and get paid; the source is locked at accept time), and how Rabotnik's contract hauling job (RabotnikContracts.cs) is designed, with the plan for harvest contracts. Use when reading, creating, accepting, completing or reacting to contracts from code.
---

# Eco contracts: how they work programmatically

Everything here was read from the decompiled `Eco.Gameplay.dll` (v0.14.1.1). To refresh it, see `eco-api-lookup` Â§4 and decompile `Eco.Gameplay.Economy.Contracts.*`, `Eco.Gameplay.Economy.ContractBoardComponent`, `Eco.Gameplay.Utils.{Picker,PickerObject,PickerProperty,PickerTracker,ItemTracker}`. Items marked **(unverified)** haven't been tested in game.

## 1. The moving parts

| Type | Namespace | Role |
|---|---|---|
| `ContractBoardObject` | `Eco.Mods.TechTree` (`__core__\Objects\ContractBoardObject.cs`) | Placeable board; `[RequireComponent(typeof(ContractBoardComponent))]`, auth set public on create. |
| `ContractBoardComponent` | `Eco.Gameplay.Economy` | Holds `ContainedWorkables` (contracts **and** work parties). Max **18** posts per board, accept range **8 m**. `GetContractsOnBoard`, `AddExistingContract`, `RemoveContract`, `DebugAddContract(user, name)`. |
| `Contract` | `Eco.Gameplay.Economy.Contracts` | `Workable` â†’ `SimpleProposable`. Client (poster), Contractor (acceptor), `Clauses`, `Hours`, `EndTime`, `AutoAccept`, `Repetitions`, `BaseContractID`, `State`. Registered in `Registrars.Get<Contract>()`. |
| `ContractClause` (abstract) | same | One requirement or reward. Hooks: `Valid`, `CanJoin`, `CanSucceed`, `TryPostJob`, `TryJoin`, `TrySucceed`, `TryFail`, `ShouldOverrideAuth(GameAction)`, `ActionPerformed(GameAction)`, `Pickers`. |
| `ContractManager` | same | `Singleton<ContractManager>.Obj`, also `EconomyManager.Contracts`. **Game-action listener** (`IGameActionAware`) and **auth override** (`ICanOverrideAuth`) for every active contract. `Contracts`, `ContractsWithClient(u)`, `ContractsWithContractor(u)`, `CreateClause(Type, client)`, `RepeatContract`. |
| `Picker` / `PickerObject` / `PickerTracker` / `PickerProperty` | `Eco.Gameplay.Utils` | A clause's target: a container (`PickerObject.PickedObj`) or a deed (`PickerProperty.Deed`). `PickerTracker` adds an `ItemTracker` that counts items in or out. |
| `ItemTracker` | `Eco.Gameplay.Utils` | `TrackingStacks` (original), `RemainingStacks` (still to do), `TrackAction` (`InventoryActionType.Put`/`Take`), `TargetObject`. `AnyLeft` means not done. |
| Game actions | `Eco.Gameplay.GameActions` | `PostedContract`, `JoinedContract` (laws can prevent these), `CompletedContract`, `FailedContract` (`[CannotBePrevented]`). All are `WorkableAction` (`Citizen`, `Client`, `Currency`, `CurrencyAmount`). |
| Events | `Contract` statics | `Contract.ContractCompletedEvent` (`ThreadSafeAction<User, Contract>`), `Contract.ContractPropertyChangedEvent`; per instance `OnStateChanged`, `OnClauseChange`. |

## 2. Lifecycle (`ProposableState`)

```
Draft --PostJob(client)--> Proposed (on board) --AcceptJob(contractor)--> Active (off board)
   \--DeleteContract--> Removed       |                                   |-- JobFinished --> Removed (CompletedSuccessfully = true)
                                       \--DeleteContract--> Removed        \-- MarkJobFailed (after EndTime) --> Failed
```

- **Create.** The board RPC `AddContract(Player)` runs `Registrars.Get<Contract>().Add(player)` and then `SetupDefault(user)`, which adds a Transport clause plus a Payment clause of 1 in the user's currency. From code, use `board.DebugAddContract(user, name)` (it calls `SetupEmpty`) and add clauses with `ContractManager.Obj.CreateClause(typeof(X), client)`.
- **`Valid()`** requires a client, at least one clause, a name other than "Unnamed Contract", and no `ContractClauseCustom` when `AutoAccept`. Every clause's `Valid` must pass, and the client's accounts must hold every `NeededCurrencyToAccept`.
- **`PostJob(User)`** needs a board and the `Draft` state. It runs `PostedContract` through `TryPostJob` on every clause, then sets `Proposed` and broadcasts.
- **`AcceptJob(User)`** (see Â§4 for the connectionless problem):
  1. `CanAcceptInternal` checks, in order: within 8 m of the board; not already holding an Active copy with the same `BaseContractID`; at most **10** Active accepted contracts; `AuthorizedToAccept(user.Player)` (ConsumerAccess on the board object); and not `AutoAccept` with every clause already satisfied.
  2. `Valid()`.
  3. A `GameActionPack` with `JoinedContract` plus every clause's `TryJoin`. Payment moves the client's payment and the contractor's deposit into escrow. Trackers lock their original stacks in post-effects.
  4. `RepeatContract` clones a fresh `Proposed` copy back onto the board when `Repetitions != 1` (0 means infinite).
  5. `EndTime = now + Hours`, `Contractor = name`, state `Active`, `Picker.Accepted(...)` adds world markers for the contractor, the client is notified, `requiresHost = false`, and `RemoveFromBoard()` runs (HostObject becomes null).
- **`JobFinished(User)`** (public) leads to `FinishContract`. If `!AutoAccept && user != client`, it only tells the client and returns `user.OkBox(..., false)`, which is **null for a connectionless user**. Otherwise it runs `CompletedContract` plus `TrySucceed` on every clause. The base `TrySucceed` sets `EarlyResult = CanSucceed(...)`, so **all clauses must pass**. Payment then releases escrow to the contractor (contract tax applies). Finally it fires `ContractCompletedEvent`, sets `CompletedSuccessfully`, and moves to `Removed`.
- **Nothing auto-completes a contract.** Someone must call `JobFinished`: the contractor through the `MarkJobFinished` RPC, or the client through the `JobFinished` RPC.
- **Failure:** the client calls `MarkJobFailed` only after `EndTime`, or `MarkJobFailedForced(user)` from code. Payment and deposit both go to the **client**. The contractor has **no way to quit**.
- `DeleteContract` refuses while Active. `Hours` is clamped to 0.1â€“10000 (default 24). `AutoAccept` defaults to true.

## 3. How clauses track progress: game actions, not polling

Clauses advance **only** through game actions performed by the contractor:

1. Any `GameActionPack.TryPerform(user)` (extension in `GameActionManager`) goes through auth and laws, then notifies listeners, including `ContractManager.ActionPerformed`.
2. `ContractManager` looks up Active contracts **by `Citizen.Name` as contractor** and calls `contract.ActionPerformed(action)` â†’ `clause.ActionPerformed(action)`. If the action's `Citizen` is null, nothing is tracked.
3. **Auth.** During the same pack, `ContractManager.ShouldOverrideAuth` grants special access when an Active contract of that contractor says `ShouldOverrideAuth(action)`. That's how a contractor may take from or put into the client's chests or harvest the client's deed **without** being on the deed's auth list.

Inventory moves raise the right actions with no Player: `Inventory.TryMoveItems(...)`, `MoveItems(stack, target, qty, user)` and `MoveAsManyItemsAsPossible(...)` all end in `InventoryChangeSet.PerformWithActionManager()`. For every changed stack whose inventory implements `IContributesMoveGameAction` (`AuthorizationInventory`, the inventory type world-object storage uses), that builds `InventoryUtils.MakeGameAction(...)`: an `InventoryAction { ActionType = Put|Take, ItemUsed, ItemsMoved, Citizen = user, WorldObject = inventory.Owner.Object, TargetInventory }`. It then calls `TryPerform(user)`. A direct move from storage A to storage B yields a Take on A **and** a Put on B in one pack.

### Clause catalog (relevant ones)

| Clause | Valid / CanJoin | Progress (`ActionPerformed`) | `CanSucceed` | Auth it grants the contractor |
|---|---|---|---|---|
| `ContractClauseTransport` (`MoveSource` Take tracker, `MoveDestination` Put tracker) | Both containers owned by the client, different, source not empty, destination can hold the items | On join, source locks **its contents at accept time**, and destination expects the same. Each Take/Put on those objects reduces `RemainingStacks`. | Both trackers have nothing left (`FailedNoMessage` if never locked) | Open/Query on both; Take/Put of tracked item types, **at most the remaining quantity per action** |
| `ContractClauseHarvest` (`Property` deed, `Items` crop picker, `Container` Put tracker) | Crop items selected; client owns the container; `CanJoin` recalculates and needs at least one **ripe** plant of those species on the deed | A `HarvestOrHunt` on that deed for a tracked species lowers the per-species count and **adds `HarvestedStacks` to the container's remaining**. Later Puts into the container reduce it. | No ripe tracked plants left on the deed (fallen trees need logs cleared) **and** all harvested items deposited | `HarvestOrHunt` and `InteractAction` on that deed for tracked species; Put into the container |
| `ContractClauseItemsInContainer` | The client owns the container; items listed | Puts of the listed items | Tracker empty | Put into the container (Take only from the player's own inventory) |
| `ContractClausePayment` | Client account has `Payment - Escrow`; `CanJoin` needs `ContractorBankAccount` set, accessible, and holding the `Deposit` | n/a | n/a | n/a. `OnOpen(user)` (`IOpenHandler`) sets `ContractorBankAccount = user.BankAccount` for non-clients. |
| `ContractClauseReputation` | Contractor reputation â‰¥ `MinRep` | n/a | n/a | n/a |
| `ContractClauseCustom` | Free text; forbids `AutoAccept` | none | always true, but the client must approve | none |
| Others | `BuildRoad`, `BuildRoom`, `Blocks`, `MowLawn`, `PlowField`, `Removal`, `Permissions`, `Loan`, `Bond`, `Nested` | (needs block placing, digging or finance) | | |

`HarvestOrHunt` is produced by vanilla `PlantEntity.TryHarvest(Player, picked, pack, changeSet, tool)` with `Citizen = player.User`. That method and `CalculateHarvestResources(Player)` **dereference `player`**, so a null Player throws. `AtomicActions.HarvestPlant/DestroyPlant` route through them with `context.Player`.

## 4. Connectionless users (Rabotnik workers) vs. contracts

| Call | Works with `user.Player == null`? |
|---|---|
| `contract.AcceptJob(worker)` | **No.** `CanAcceptInternal` calls `AuthorizedToAccept(user.Player)`, which does `player.User`: a NullReferenceException when the contract has a board, or a false "not authorized" when it doesn't. |
| `clause.TryJoin`, `JoinedContract`, escrow transfers | Yes. Pure user, account and game-action code. |
| `Inventory.TryMoveItems(..., worker)` / `MoveItems(stack, dst, qty, worker)` | Yes, **verified in game 2026-10-05**. It raises Take/Put `InventoryAction`s with `Citizen = worker`. The contract's auth override lets the worker into the client's stockpile and chest, and both trackers count every move. No distance check was hit. |
| `PlantEntity.TryHarvest` / `AtomicActions.HarvestPlant` | **No.** They dereference Player. |
| `contract.JobFinished(worker)` | Yes, **verified in game 2026-10-05**: it pays out on success. On failure it returns **null** (from `user.OkBox`), so treat null as failure. |
| `user.OkBox/OkBoxLoc` | Safe no-ops (`user.Player?.â€¦`). |

## 5. Rabotnik contract labor (built: hauling)

Code: [RabotnikContracts.cs](../../../Mods/UserCode/Rabotnik/RabotnikContracts.cs), called from `RabotnikService.Update`. Read the code for exact behaviour. This section records the *why* and what testing proved.

### 5.1 Design decisions (user-approved 2026-10-05)

- **Clause whitelist:** Transport, Payment (no deposit) and Reputation only, with at least one Transport. The contract must have `AutoAccept` on, because a worker can't wait for human approval. Everything else needs a player: Custom, Build*, Blocks, Plow/Mow, Removal, Permissions, Loan/Bond, Nested and (for now) Harvest.
- **No deposits:** a failed contract hands the deposit to the client, and money given to a worker is the worker's (a project rule).
- **Priority:** the accepted contract first (`StepActive`), because it can't be quit and has a deadline. Then work-party labor (`RabotnikWork.Step`). A new contract (`StepNew`) only in an update where no party labor happened. A worker skips party work in any update where it hauled.
- **One active contract per worker** (vanilla allows 10), to keep priority simple.
- **Pacing:** at most `HaulItemsPerTick` items per update (default 40), `CaloriesPerItemHauled` each (default 5, burned with `Stomach.BurnCalories(â€¦, true)` after each move). Hauling stops when the worker can't afford the next item, and it eats on the next update. Distance isn't modelled.
- **Choosing:** contracts that are `Proposed` and on a board in the worker's settlement (by the board's position), eligible, best payment per item first.
  - **Time:** it skips one whose estimated hauling time is over `ContractTimeSafety` (0.5) of its `Hours`. The estimate is updates needed Ã— `TickSeconds Ã— ceil(workers / MaxRabotniksPerTick)`, and assumes world time runs at real time **(unverified)**.
  - **Pay:** `RabotnikJobValue.IsHaulWorthIt` applies the party rule, so pay must cover the food cost of the calories (needs a shop selling food in the pay currency, gated by `RequirePayCoversFood`).
- **Own owner's contracts are allowed.** A Logbook owner can hire their own workers through contracts. Only a contract whose client is the worker itself is skipped.
- **Opt-in:** the server config `HaulContracts` (default on). Turning it off stops new contracts but still finishes accepted ones.
- **Reporting:** a contract line on the Logbook Activity tab (`ContractActivity`), `ItemsHauled` and `ContractsCompleted` in the record, and an event log entry plus an owner chat notice on accept, complete, waiting and failed. Skip reasons appear in the status line ("No contract taken: â€¦"), as for parties.

### 5.2 Accepting without a Player (`TryAccept`)

Same pattern as residency (`Residency.BecomeResidentInternal` by reflection, see `eco-api-lookup` "Verified behaviour"). It replays `Contract.AcceptJob` and bypasses only the Player-dependent check:

1. Vanilla `CanAcceptInternal` checks, with board auth done on the User: `IAuthManager.IsAuthorized(board, worker, ConsumerAccess, null, out _)`. The worker is moved to the board first (8 m rule parity).
2. `IOpenHandler.OnOpen(worker)` on every clause. That's what sets the payment clause's `ContractorBankAccount`; without it `CanJoin` fails.
3. `contract.Valid()`, then a `GameActionPack` with a `JoinedContract` plus every clause's `TryJoin`, then `TryPerform(worker)`. This is the private `ConfigureAndTryPerform`, rebuilt from public parts: escrow is taken, trackers lock, and laws on `JoinedContract` apply.
4. AcceptJob's bookkeeping: `RepeatContract`; the **private** setters `EndTime` then `Contractor`, via `typeof(Contract).GetProperty(..).SetValue`; `SetProposedState(Active)`; `Changed(...)`; `Picker.Accepted`; the client notice; `SetRequiresHost(false)`; `RemoveFromBoard()`; `EconomyManager.MarkDirty()`.

The contract's auth overrides applied to the worker straight after this, even though `ContractManager.byContractor` is a registrar snapshot.

### 5.3 Hauling (`Work`)

- Inventories come from `PickedObj.GetComponents<StorageComponent>()` minus `FuelSupplyComponent`, the same as `PickerObject.Storages`.
- For each source stack: `qty = min(stack, remaining Take, remaining Put, tick cap left, calories allow)`, then `srcInv.MoveItems(stack, dstInv, qty, worker)`. Count what actually left the source rather than trusting `qty`. A move larger than the remaining quantity fails auth, because the override only covers what's still tracked.
- **Shortfall:** if the source holds less than `MoveSource.Tracker.RemainingStacks`, the status becomes "Waiting: â€¦ restocked". The client (`NotificationCategory.YourContracts`) and the owner are told once per distinct text (`LastContractNotice`, not saved). The worker does party work meanwhile.
- **Finish:** once every clause `CanSucceed`, call `contract.JobFinished(worker)`. Null or a failure means not yet. The payout is the balance difference in `worker.BankAccount`.
- **Ended elsewhere:** `ContractId` (saved) is checked each update. A contract that left Active without the worker finishing it is logged as failed (the client marked it after the deadline), or as paid (the client completed it).

### 5.4 Harvest clause (built 2026-10-05, not yet tested in game)

Code: [RabotnikHarvest.cs](../../../Mods/UserCode/Rabotnik/RabotnikHarvest.cs), with the plumbing shared with hauling in [RabotnikContractCore.cs](../../../Mods/UserCode/Rabotnik/RabotnikContractCore.cs) (`TryAccept`, `TryFinish`, `Notice`, `NotifyWaiting`). Decisions:
- **Separate job, own flag:** `HarvestContracts` (plus `HarvestPlantsPerTick`, `CaloriesPerPlantHarvested`), independent of `HaulContracts`. A contract with a Harvest clause belongs to the harvest job, every other contract to hauling. A Transport + Harvest mix is unsupported (harvest rejects the Transport clause, hauling never sees it). One active contract per worker overall.
- **Tool:** vanilla lets hands pick crops, but the worker needs a `SickleItem`/`ScytheItem` (the "Harvester" tag is too broad: bows, shovels, tractors). It lives in the **Logbook's storage** (`RabotnikSuppliesRestriction` now allows it) or the worker's inventory, and is used in place: `ToolItem.UseDurability(1, null, false)` is null-Player safe. Passed as `ToolUsed`, so vanilla's `!picked` path applies (`ScythingKills`). With none left the worker reports "Waiting" and does party work.
- **Broken/missing tool (RabotnikTools.cs, `BuyHarvestTools` flag):** when a harvest contract is eligible (or the tool wears out mid-contract) and no tool has `Durability > 0`, the worker first sells its broken sickles/scythes (moved from the Logbook storage into its inventory with user-less `TryRemoveItems`/`TryAddItem`; sold items must be in the worker's own inventory) to the best store *buy* offer (`offer.Buying`, `MatchesItem` + `MeetsDurabilityRequirements` + `MeetsSpecialRequirements`, store `Balance` >= price), then buys the sell offer with the most durability per price it can afford. Both are `StoreComponent.DoPerformTrade` with `itemsToSell` / `itemsToBuy` lines (`offer` id + `amount`), like RabotnikShopping. Throttled per worker. **Budget (RabotnikBudget.cs):** a currency purchase may only use balance minus the rent reserve (`RentBudgetHours` of rent, in the rent's currency) and a full-stomach food reserve (`MaxCalories` less the Logbook's stocked calories, at the cheapest shop food in that currency); food buying itself ignores it. **Barter shops** (`IsBarter` = `Currency == null`) are skipped: a barter trade must sell the shop goods worth at least the price, and a worker owns too few personal effects (`RabotnikShopping.Stores` already excludes them). Wear is applied by setting `Durability` directly: `ToolItem.UseDurability(.., null, ..)` threw a NullReferenceException in game (DurabilityBurn needs a user). Untested in game.
- **Yield:** `PlantEntity.CalculateHarvestResources` ported with the worker as the User (YieldAttribute, `BonusManager` HarvestYield, `Talentset.OnPlantHarvest` all take a User). `Plant.CalculateResourceYield` is protected, so it's called by reflection. `CanHarvest(null, false)` doesn't touch the player.
- **Trees skipped:** `Tree` isn't a `PlantEntity`; contracts whose ripe plants include one are rejected, and a tree that ripens mid-contract shows as "Waiting".
- **Delivery:** harvested items land in the worker's inventory (change set), then `worker.Inventory.MoveItems(stack, container, qty, worker)` Puts them into the container, capped by the tracker's remaining quantity.
- Clause facts from the decompile: `ActionPerformed` counts `HarvestOrHunt` per species on the deed and adds `HarvestedStacks` to the container tracker; `CanSucceed` = no ripe tracked plants left and `Container.Test()`; `CanJoin` recalculates and needs at least one ripe plant.

Original sketch (the code follows it, with the Player-free yield port above). Vanilla harvest needs a Player (Â§3), so emit an equivalent `HarvestOrHunt` yourself:

```csharp
var plant = EcoSim.PlantSim.GetPlant(pos) as PlantEntity;   // pos from PropertyUtils.TopBlocksOnProperty(deed pos XZ) + Up, Ripe, tracked species
var stacks = /* per SpeciesResource in plant.Species.ResourceList: roll ResourceChance, qty from ResourceRange scaled by growth */;
using var pack = new GameActionPack();
var cs = pack.GetOrCreateInventoryChangeSet(worker.Inventory, worker);
foreach (var s in stacks) cs.AddItemsNonUnique(s.Item.Type, s.Quantity);
pack.BurnCaloriesAndCheckExhaustion(worker, false, caloriesPerPlant);
pack.AddGameAction(new HarvestOrHunt { Citizen = worker, Species = plant.Species.GetType(), HarvestedStacks = stacks,
    ActionLocation = plant.Position.XYZi(), AccessNeeded = AccessType.ConsumerAccess, ToolUsed = Item.Get(typeof(HandsItem)),
    DamagedOrDestroyed = DamagedOrDestroyed.DestroyingOrganism });
pack.AddPostEffect(() => { EcoSim.PlantSim.DestroyPlant(plant, DeathType.Harvesting, true, worker); plant.ProcessSoilUnderneeth();
    worker.Skillset.AddExperience(/* plant skill */, plant.Species.ExperiencePerHarvest, Localizer.Format("harvesting {0}", plant.Species.UILink())); });
var r = pack.TryPerform(worker);   // auth via the contract's ShouldOverrideAuth; harvest laws apply
```

Then move the crops into `clause.Container.PickedObj`'s storage with `worker.Inventory.TryMoveItems(..., worker)`. That Put clears the container tracker. Finish as in Â§5.3.

Simplifications to accept or refine later:
- Yield ignores `YieldAttribute`, talents (`Talentset.OnPlantHarvest`) and `BonusManager`. All of those need a User, not a Player, so copy `CalculateHarvestResources` with `worker` substituted.
- Perennials (`PostHarvestingGrowth > 0`) reset growth instead of dying (see `PlantEntity.OnHarvested`).
- Handle crops only; skip `Tree` species.
- `ContractClauseHarvest.CanJoin` needs at least one ripe tracked plant at accept time.

### 5.5 What testing proved, and what's open

Proved in game on 2026-10-05 (the spike commands have since been removed):
1. **Accept:** the replayed accept path works. Escrow was taken, `Contractor` was set, the contract was in `AcceptedContracts` and off the board, `RepeatContract` posted the next copy, and the same-base check refused a second copy.
2. **Hauling:** direct source-to-destination `MoveItems` calls succeed under the contract's auth override, and both trackers count every move.
3. **Trap: the source is locked at accept.** The Transport source tracker locks the source's contents when the contract is accepted. Items the client uses or removes later (in the test, 4 Birch Logs consumed by crafting) block completion until they're restocked, and the contractor can't quit. That's why `Work` reports shortfalls.
4. **Finish:** `JobFinished(worker)` with every clause satisfied set the contract to `Removed` with `CompletedSuccessfully`, and paid the full 100 into `worker.BankAccount` (that settlement has no contract tax). With an unmet clause it returned **null** and left the contract Active.

Not yet verified:
- The built job loop as a whole: pacing, priority, skip reasons, the shortfall notice, and failure detection.
- Contract tax being withheld, and `ContractCompletedEvent` firing.
- Several workers and `RepeatContract` copies.
- The world-time assumption in the time estimate.
- Laws blocking `JoinedContract`; the refusal text should show in the status.
- A container or deed destroyed mid-contract: `PickerTracker.Test()` then *succeeds* ("clause is ignored").

