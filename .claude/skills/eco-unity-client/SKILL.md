---
name: eco-unity-client
description: Build the client-side half of an Eco mod with the EcoModKit Unity project - item icons, world-object prefabs, block sets, emotes, food/tool visuals, reusing vanilla prefabs and fonts (ReuseVanillaPrefab/ReuseVanillaFont), and exporting a .unity3d AssetBundle that the server ships to clients. Use when a mod adds anything that needs new visuals.
---

# Eco client assets (EcoModKit / Unity)

Server classes and client assets are matched **by name**:

| Server class | Client asset name | Where it lives in the mod scene |
|---|---|---|
| `FooItem` (any item) | GameObject `FooItem` built from `ItemTemplate` (icon) | under the `Items` root |
| `FooObject` (WorldObject) | prefab `FooObject`, tag **`ModObject`**, with the `WorldObject` component | listed in `ModkitPrefabContainer.Prefabs` on the `Objects` root |
| `FooBlock` (Block) | a `Block` entry named `Foo` (see note) in a `BlockSet` asset | in `BlockSetContainer` on the `BlockSets` root |
| chat emoji | entry in `ChatEmoteSetOld.Emotes` | `Emoji` root |

Check how vanilla names blocks in block sets before assuming `Foo` vs `FooBlock`. This hasn't been verified against a working mod.

## Toolchain (verified from the ModKit zip)

- **Unity 6000.3.6f1** (Unity 6.3). Use exactly this editor version. It's installed at `C:\Program Files\Unity\Hub\Editor\6000.3.6f1\Editor\Unity.exe`. Rabotnik's bundle builds headless with `Unity.exe -batchmode -quit -projectPath Client -executeMethod LogbookBuilder.Build -logFile <log>`. Close any editor that has `Client` open first.
- Create a new 3D project. The built-in render pipeline is likely, since the ModKit shaders are classic `.shader`/`.cginc` (unverified). Then **Assets → Import Package → Custom Package →** `EcoModKit.unitypackage` (inside `E:\Code\eco_mod\EcoModKit_v0.14.1.1-beta.zip`). It also installs `Packages/com.strangeloopgames.eco-modkit-deps` (Unity Entities, Physics, Collections, Newtonsoft JSON, uGUI) and `com.strangeloopgames.eco-shared` (Eco.Shared.dll etc.).
- The package ships the ModKit scripts (`Assets/EcoModKit/Scripts`, `VoxelEngine`), `Prefabs/ItemTemplate.prefab`, `IconTemplate.prefab`, `FoodItemTemplate.prefab`, `DefaultItem.prefab`, animators for animals and fish, the curved-world shaders (`CurvedStandard`, ...), `TemplateScene.unity`, and the tags in `ProjectSettings/TagManager.asset` (`ModItem`, `ModObject`, `ModBlock`, `ModImage`, `ModBenefit`, ...).
- Docs inside the package: `Assets/EcoModKit/Docs/README.md` and `ReuseVanillaPrefab.md`. Older wiki: https://wiki.play.eco/en/Mod_Development

## Scene setup (one scene per mod bundle)

1. New empty scene, with everything deleted.
2. Root GameObject **`Objects`** + component `ModkitPrefabContainer`.
3. Root **`Items`** (plain parent).
4. Root **`Emoji`** + `ChatEmoteSetOld` (optional).
5. Root **`BlockSets`** + `BlockSetContainer` (optional).

## Item icon

1. Drag `Assets/EcoModKit/Prefabs/ItemTemplate` under `Items`, then **Prefab → Unpack Completely**.
2. Rename it to the exact server class name (`FooItem`).
3. Set the `Background` / `Foreground` sprites of the `ItemTemplate` component. Import textures as **Sprite (2D and UI)**.
4. Bundle-size hygiene: delete the `IconName` child (it references `mainfont`), and if you don't need it, clear the `Background` sprite, which points into the vanilla `UI_ICONS` atlas and would be bundled whole.

**Icons that aren't items** (a component tab, an Ecopedia `icon=`):
- **A server item must exist.** The client only registers an `ItemTemplate` icon when the server has an item with the same name. Tested in game on 2026-10-15: a template named `RabotnikMask`, with no item of that name, logged `Cannot find icon with name "RabotnikMask"`, and the Ecopedia listed it under "Missing following icons".
- **The fix:** add a hidden carrier item, `[Serialized, LocDisplayName(..), Category("Hidden"), Tag("NotInBrowser")] public partial class FooIconItem : Item { }`, and name the template `FooIconItem`. Rabotnik does this in `RabotnikIcons.cs` (`RabotnikMaskItem`), built by `Client/Assets/Rabotnik/Editor/LogbookBuilder.cs`.
- **Component tab:** `WorldObjectComponent.IconName` defaults to the class name, so override it to name the carrier item. The component must also carry `[HasIcon]` or `[NoIcon]`: with neither, the server logs `Component X has to explicitly define [HasIcon] or [NoIcon]`. Vanilla example: `CameraComponent` in `Tools\CameraItem.cs`.
- **A `[NoIcon]` tab still gets looked up:** the client logs `Cannot find icon with name "<ComponentClass>"`. It's harmless, but noisy.
- **Banners are a different mechanism:** Ecopedia banner images (`<section type="banner" image="..">`) are `Image` GameObjects tagged **`ModImage`** under an `Images` root. That's how the EcoModKit `EcopediaPage` example ships `EcoLogoSampleImage`. Whether `ModImage` sprites also work as icons hasn't been tested.
- **Debugging:** the client is IL2CPP (`GameAssembly.dll`), so it can't be decompiled. Read the client log instead: `%USERPROFILE%\AppData\LocalLow\Strange Loop Games\Eco\Player.log`. It also includes the local server's output. While the game runs it shows 0 bytes in Explorer, but you can open it with shared read access.
- **Source images:** give them a transparent background. An opaque square shows up as a box in the UI.

## World object prefab

1. Empty GameObject named `FooObject`, **Tag = `ModObject`**.
2. Add the `WorldObject` component. It auto-adds `WrapTarget` and `NetObjComponent`.
3. Add the mesh as a child, a collider, and `HighlightableObject`. Use the curved shaders (`CurvedStandard`) so it matches world curvature.
4. In the `WorldObject` component, set the occupancy `size` / `occupancyOffset` to match the server's `AddOccupancy` footprint. Use the UnityEvents (`OnEnabled`, `OnEnableOperating`, `OnEnableUsing`, custom `States` / `Events`) to animate it from server state.
5. Save it as a prefab, delete it from the scene, and add it to `Objects → ModkitPrefabContainer.Prefabs`.

Shortcut: select FBX/prefab assets and use **Eco Tools → Mod Kit → World Object Setup**. It creates `<CleanName>Object.prefab` with a box collider, a `HighlightableObject` and the chosen WorldObject subclass (e.g. `PlanterPot`).

## Reuse vanilla visuals (no art, tiny bundle)

- **Object:** empty `ModObject`-tagged prefab named after your server object + `ReuseVanillaPrefab` with `Source Name = CampfireObject` (any vanilla object name). The whole vanilla prefab is used.
- **Item/tool/clothing:** `ReuseVanillaPrefab` on the item root with `Source Name = IronHammerItem` (icon, tool and avatar visuals, and the UMA recipe for clothing). Put it on a `Tool` or `Avatar` child to borrow only that part.
- **Fonts:** clear the TMP font asset, add `ReuseVanillaFont` with `Font Name = mainfont SDF` (UI), `mainfont SDF Worldspace` (signs) or `NotoSansCJK SDF`.
- **Server-driven text (`Sign`):** a `CustomTextComponent` on the server plus the `Sign` script on the prefab root, with `SignText` set to a `TextMeshPro` child, shows synced text. Verified in game: the client **resets the `SignText` object's own transform**. With the position and rotation on the text itself, it came out lying flat and about 50 times too big. Put the position, rotation and scale on a parent object and keep the text's own transform at identity. Rabotnik's Logbook spine works with a parent scaled to 0.01, sizes in centimetres, auto-size between 1 and 26, and no Bold, which blurs at that size (see `LogbookBuilder.BuildSpineText`).
- **PlanterPot:** leave `Plant Blocks` / `Tree Blocks` empty. Plants are found by species name in all loaded block sets.
- Sources must be vanilla or from the same mod. A missing source logs an error naming the prefab.

## Block sets

**Project window → Create → VoxelEngine → BlockSet**, then add `Block` entries: `Name`, `Builder` (mesh rules), `Material`, `LODTexture`, `MinimapColor`, `Solid`, `IsWater`, `Layer = "Terrain"`, `Category`, `Tier`, `AudioCategory`. LOD0 meshes come from a `BlockMeshLodGroup`. Add the set to `BlockSetContainer`. Reference example: GitHub `Examples/Blockset`.

## Build and deploy

1. Have **only this scene open**, and save it.
2. **Eco Tools → Mod Kit → Build Current Bundle**, then choose `<modname>.unity3d`. The tool sets `stripUnusedMeshComponents = false`, deactivates root objects for export, and copies the built bundle to your path.
3. Copy the `.unity3d` anywhere under the server's `Mods/` (convention: next to the mod's code in `Mods/UserCode/<ModName>/`). The server streams it to clients. `Configs/ModKit.eco → LiveUpdateUnityFiles: true` hot-reloads bundles while iterating.
4. Bundle too big? Right-click the scene → **Select Dependencies** to find stray vanilla references.

Other GitHub examples: `CornOnTheCob` (food with FPV hand animations, `FoodItemTemplate` + `BiteableFoodObject`), `Flag`, `SignImages`, `CustomFonts`, `EcopediaPage`. The `Assets/Mods/ReuseVanillaSample` mentioned in the docs isn't inside the unitypackage. Treat every example's server C# as possibly stale (see eco-modding).
