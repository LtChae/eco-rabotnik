This repo represents an attempt to make a mod for the game Eco. The mod is called Rabotnik (its workers are Rabotniks): server code in Mods/UserCode/Rabotnik, Unity client assets in Client/Assets/Rabotnik.

The docs:
https://docs.play.eco/index.html

Example mods:
https://github.com/StrangeLoopGames/EcoModKit/tree/main/Examples

## Project skills (.claude/skills/)

Eco modding knowledge is organized into skills. Start with `eco-modding` (architecture, local paths, dev loop, stale-API traps), then use:
- `eco-items-recipes`: items, food, tools, blocks, recipes, editing vanilla recipes
- `eco-world-objects`: placeable objects, crafting tables, components, custom UI, interactions
- `eco-skills-and-species`: skills, talents, plants, trees, animals
- `eco-server-plugins`: plugins, config, chat commands, events, game actions, logging
- `eco-contracts`: contract boards, clauses, escrow, item trackers, and the plan for Rabotnik contract labor (hauling, harvesting)
- `eco-unity-client`: ModKit Unity project, icons, prefabs, block sets, .unity3d bundles
- `rabotnik-ecopedia`: keep Rabotnik's in-game Ecopedia pages in sync whenever player- or admin-visible behaviour, config or commands change
- `eco-api-lookup`: offline API signature index + vanilla type/recipe catalog (verify every API here; the online docs and examples are often stale)

Vanilla source (the best reference): `E:\SteamLibrary\steamapps\common\Eco\Eco_Data\Server\Mods\__core__`