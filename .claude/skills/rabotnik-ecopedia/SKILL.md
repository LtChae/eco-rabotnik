---
name: rabotnik-ecopedia
description: Keep Rabotnik's in-game Ecopedia pages (Mods/UserCode/Rabotnik/Ecopedia/Concepts/*.xml) in sync with the mod. Use after any change to Rabotnik behaviour a player or admin would notice, such as a new job, a changed rule, a new or renamed config setting or default, a chat command, a Logbook tab, button or storage rule, or a notice. Also use when asked to add, fix or review Rabotnik documentation. It has a source-to-page map, the writing rules, and a validation script.
---

# Rabotnik Ecopedia pages

The Ecopedia is Eco's in-game help. Rabotnik adds a **Rabotniks** category to the vanilla **Concepts** chapter. The pages are player documentation, so they must describe what the code **does now**. If you change behaviour, update the pages in the same task.

The general format (discovery, file naming, link syntax, the `[Ecopedia]` attribute) is in the `eco-modding` skill, section "Ecopedia pages". This skill covers Rabotnik's pages and how to maintain them.

## Files

All files are in `Mods/UserCode/Rabotnik/Ecopedia/Concepts/`. The server loads XML only from an immediate subfolder of `Ecopedia/`, so keep that structure.

| File | Page name (used in links) | Priority | Source of truth |
|---|---|---|---|
| `Rabotniks.xml` | category; `chapter="Concepts"` | 5 | n/a |
| `Rabotniks;Rabotniks Overview.xml` | Rabotniks Overview | -10 | `RabotnikService.cs` (assign, retire, pool), the money rule, and an index of all pages |
| `Rabotniks;Rabotnik Logbook.xml` | Rabotnik Logbook | -9 | `RabotnikLogbook.cs` (recipe, placement, storage), `RabotnikComponent.cs` and `RabotnikTabs.cs` (tabs, buttons, Fund), `RabotnikOwner.cs` (notices) |
| `Rabotniks;Rabotnik Housing.xml` | Rabotnik Housing | -8 | `RabotnikHousing.cs` |
| `Rabotniks;Rabotnik Food and Skills.xml` | Rabotnik Food and Skills | -7 | `RabotnikNeeds.cs`, `RabotnikShopping.cs` |
| `Rabotniks;Rabotnik Work Parties.xml` | Rabotnik Work Parties | -6 | `RabotnikWork.cs`, `RabotnikJobValue.cs` |
| `Rabotniks;Rabotnik Contracts.xml` | Rabotnik Contracts | -5 | `RabotnikContracts.cs`, `RabotnikContractCore.cs`, `RabotnikHarvest.cs`, `RabotnikTrees.cs`, `RabotnikTools.cs`, `RabotnikBudget.cs` |
| `Rabotniks;Rabotniks in Society.xml` | Rabotniks in Society | -4 | `RabotnikReputation.cs`, `RabotnikCulture.cs`, `RabotnikDemographic.cs`, `RabotnikVoting.cs` |
| `Rabotniks;Rabotnik Server Settings.xml` | Rabotnik Server Settings | 10 | `RabotnikConfig.cs` (every setting and its default), `RabotnikCommands.cs` |

`RabotnikConfig.cs` feeds every page: the player pages quote defaults (for example "2000 calories", "24 hours of rent", "over 200 logs"). When you change a default, search all the pages for the old number.

The Logbook's object, item and recipe carry `[Ecopedia("Rabotniks", "Rabotnik Logbook")]` in `RabotnikLogbook.cs`. This attaches them to the hand-written Logbook page, so hovering the Logbook links there. If you rename that page, change the attribute too, or the server creates a separate empty auto page.

## When code changes, update the pages

1. Find the pages affected, using the source column above. Also grep the pages for the feature's keywords and numbers: `Grep pattern="<word>" path="Mods/UserCode/Rabotnik/Ecopedia"`.
2. Read the changed code, not just the commit or plan, and write what it actually does. Check `[LocDescription]` texts in `RabotnikConfig.cs` for how the behaviour is described.
3. **New config setting:** add it to the matching group on Server Settings, with its default. If players would notice it, mention its behaviour on the player page, with "by default" when it can be switched off.
4. **New command:** add it to Server Settings under "Admin commands".
5. **New player-facing system** (for example a new job type): add a section to the closest page. Create a new page only if no page fits. In that case:
   - name it `Rabotniks;Rabotnik <Topic>.xml`, with a "Rabotnik" prefix because page names are global;
   - give it a priority between -4 and 9;
   - link it from the Overview's "More about Rabotniks" list.
6. Run the checker, then deploy (see below).

## Writing rules

- **Audience:** players. Only the Server Settings page is for admins. Use second person ("Stock the Logbook with food"), keep it short, and don't use code identifiers on player pages. On the settings page, show setting names in `<b>` as they appear in `Configs/Rabotnik.eco`.
- **Defaults:** state behaviour that a config setting can change as the default ("by default", "over 200 logs, by default").
- **Unverified features:** if a feature isn't verified in game yet, describe the intended behaviour, but don't promise details the code comments flag as unverified.
- **Links:** write each Rabotnik page name or vanilla concept as a `[Page]` link the first time it appears in a section. This is the project's "use links" convention.
  - Vanilla page names are the last `;` part of the file names in `E:\SteamLibrary\steamapps\common\Eco\Eco_Data\Server\Mods\__core__\Ecopedia\*\`, for example `Work Parties`, `Contracts`, `Residency`, `Immigration Policy`, `Reputation`, `Culture`, `Influence`, `Demographics`, `Laws`, `Taxes`, `Elections`, `Experience`, `Nutrition`, `Specialties`.
  - Use `[Shown text|Page]` when the wording differs, for example `[Contract Board|Contract Board Component]`.
- **Never use square or curly brackets in prose.** The server treats any `[x]` or `{x}` as a link. Write placeholders in caps instead: `/rabotnik info NAME`.
- **No XML entities** (`&amp;`, `&lt;` and so on): the client gets the raw inner XML. Reword instead, for example "Home and Money" rather than the ampersand.
- **Icons:**
  - `RabotnikMaskItem` (the Rabotnik mask, shipped in `rabotnik.unity3d` and carried by a hidden item in `RabotnikIcons.cs`) is for the category, the Overview, Server Settings and any general Rabotnik page.
  - `RabotnikLogbookItem` is for the Logbook page.
  - Topic pages use a matching vanilla icon (for example `Residency`, `Work Party`, `BuildContract`).
  - A new icon has to be added to the bundle first (see `eco-unity-client`).
- **Markup:** `<b>` and `<i>`; lines starting with `- ` or `1. `; blank lines between paragraphs; `<section type="header">` for subheadings. See vanilla pages for banners, quotes and buttons.
- **Tab and button names:** match the UI. The tabs are "Rabotnik", "Home and Money" (shown in game as Home & Money) and "Activity". Autogen button labels come from the `[RPC, Autogen]` method names (`FundWorker`, `LearnLogging`…). The pages call them "Fund Worker" and "base skill buttons". Check the exact wording in game before quoting a label.

## Validate

```powershell
powershell -File .claude/skills/rabotnik-ecopedia/scripts/check-ecopedia.ps1
```

The script checks that:
- every file parses as XML,
- every file is in a folder the server loads,
- every link resolves to a vanilla or Rabotnik page,
- no page name duplicates another or collides with a vanilla page, and
- no file contains XML entities.

Fix everything it reports. If you touched `RabotnikLogbook.cs` (the `[Ecopedia]` attributes), also run `dotnet build tools/BuildCheck`.

## Deploy and verify

- **Deploy:** standing permission covers this. Copy the changed files into the game:

  ```powershell
  Copy-Item -Recurse -Force Mods\UserCode\Rabotnik\Ecopedia "E:\SteamLibrary\steamapps\common\Eco\Eco_Data\Server\Mods\UserCode\Rabotnik"
  ```

  To remove a page, delete it in the game folder too. Otherwise it stays loaded there.
- **Restart the server:** the Ecopedia is only built at startup.
- **Check the log** for:
  - `Can't find ecopedia link`
  - `Ecopedia page name collision`
  - `Ecopedia: failed to load`
- **In game:** open Ecopedia → Concepts → Rabotniks and check the page order, the formatting and the links you changed.
