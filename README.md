# Groundskeeper

**Version 0.1.0 (first build, not yet tested in-game).** See [CHANGELOG.md](CHANGELOG.md).

One app for the landscape problems in a big Skyrim Special Edition / Anniversary Edition load order.
Groundskeeper scans your load order, shows you **which mods overwrite which terrain and water**, and
writes **one** patch plugin that repairs only what *you* choose. It replaces running Landscape Seam
Fixer, Landscape Texture Fixer, Road Mask Merger, Floating Object Fixer and PatchForeman one after
another, and it is built to work **alongside Landscape Fixer by Cl3mus33** (see below).

> **Read this first.** Groundskeeper has not been loaded in a game yet. Everything below was checked
> on a 445-plugin load order and on small synthetic plugins, in preview mode, and nothing has been
> played. Review the log, test on a copy of your profile, and keep your saves backed up.

## Why this exists

Every landscape tool runs into the same question: when two mods edit the same cell, whose terrain
should win? The old tools answered it with a built-in trust list and then silently restored the
"trusted" terrain. Groundskeeper makes the answer visible and puts you in charge:

1. **Scan.** It lists every mod that overwrites terrain or water that a trusted plugin (the game,
   the DLCs, Creation Club, known patches) authored, with how many cells, how many height units,
   and what it would be reverted to.
2. **Decide.** Nothing is reverted unless you tick it. Nothing ticked means no terrain or water
   changes at all.
3. **Choose winners.** Put mods in an ordered list and the highest one wins wherever they conflict,
   instead of whichever loads last.
4. **Generate.** One plugin, `LandscapeTerrain.esp` by default, split automatically if a plugin
   would pass the 255-master limit and flagged ESL when it fits.

## What it does

| Pass | What it does |
|---|---|
| **Seams** | Restores terrain (height, normals, vertex colour) and water that a later mod overwrote, only for the mods you ticked. Skips cells that have their own water plane or where placed objects would move, and reports them. |
| **Texture layers** | Adds a small blend hint where neighbouring cells show a hard texture boundary. Optional; review these in game. |
| **Roads** | Reconciles a road mod's terrain with the rest. Runs only if you name a road source plugin. |
| **Floating objects** | Re-grounds floating or sunken placed references, checking each against the real Havok collision of nearby meshes, read in-process (no Python needed). Off by default, and it flags many references, so read the log. |
| **Terrain winners** | You set the order of conflicting mods; the highest wins. Written through the same safety checks as the seam pass. |
| **Water mods** | Protects the water mods you tick (CS Water Mod, Water for ENB, RealisticWaterTwo, Simplicity of Sea and anything else with "water" in its name). Wherever another plugin overwrote one, its water is restored. The list is in priority order: first wins. |
| **Merge** | Folds every pass into the single output plugin using the PatchForeman merge, including a guard that stops "Two records with the same FormKey" write errors. |

Official content (the game masters, Creation Club plugins) is always trusted and never offered for
revert.

## Works with Landscape Fixer (Cl3mus33)

[Landscape Fixer](https://www.nexusmods.com/skyrimspecialedition/mods/198142) by Cl3mus33 replaces
Snow Fixer and AutoBlend. Groundskeeper does not include, copy or call any of its code. It is only
**compatible** with it:

- `LandscapeFixer.esp` is never read as a terrain source, so what Landscape Fixer generates never
  feeds into Groundskeeper's repairs, and the other way round.
- Run them in either order. Neither reads the other's output.
- Keep both plugins enabled. Groundskeeper's plugin goes after your landscape mods.

If you re-run Groundskeeper, its own earlier plugins are excluded from the scan automatically.

## Using it

There is no packaged release yet: build from source (see below) and run `Groundskeeper.exe`. It is a
.NET 10 app (the runtime is required).

1. **Pick your mod manager.**
   - **Mod Organizer 2:** pick the instance folder (the one with `ModOrganizer.ini`) and a profile.
     Started from MO2's executables list it is meant to find the instance by itself (untested).
   - **Vortex or a plain install:** it reads the game's `Data` folder and plugin list. Leave the
     game folder empty to find it from the registry, or pick it.
2. **Pick an output folder.** Use a dedicated folder, for example a "Groundskeeper Output" mod
   folder. Only this tool's own plugin names in it are ever replaced; nothing else is deleted.
3. Press **Scan for terrain conflicts**. Tick the mods whose terrain or water should be reverted,
   use **Make winner** on mods that should win, and tick the water mods to protect.
4. Press **Preview** first. It runs everything and writes nothing. Then **Generate**.
5. Enable `LandscapeTerrain.esp` (and any `_2`, `_3` files, adjacent in load order) after your
   landscape mods.

### Command line

```
GroundskeeperCli --mo2 <instance> <profile> --output=<folder> [options]
GroundskeeperCli --direct [--game=<install or Data folder>] --output=<folder> [options]

  --plan                     list the mods that overwrite trusted terrain or water, then exit
  --revert=<plugin>          revert that plugin's terrain and water (repeatable)
  --winner=<plugin>          terrain winner, first = highest priority (repeatable)
  --water-mod=<plugin>       protect that water mod, first = highest priority (repeatable)
  --no-seams  --no-textures  --floating  --floating-threshold=96  --floating-worldspace=Tamriel
  --road-plugin=<esp>  --road-masks=<folder>
  --trust-northern-roads  --match-neighbors
  --name=LandscapeTerrain.esp
  --write                    write the plugin (the default is a dry run)
```

## Safety

- Preview and the command line default to a **dry run**; only Generate / `--write` writes.
- It never edits an existing plugin. It writes a new one.
- Generate refuses an output folder that is, or looks like, the game's `Data` folder or install.
- It never changes your load order or your mod manager's files.
- Your own settings are kept in `%APPDATA%\Groundskeeper\settings.json`.

## Known limits

- **Not tested in-game.** Preview results are plans, not proof the cells look right.
- **Vortex and "launched from MO2" are untested.** Direct mode was only run on a vanilla game folder
  with Creation Club.
- **Floating objects** is off by default. It reads each mesh's real collision in-process (compressed
  meshes, boxes, convex shapes, lists and transforms; capsules and spheres are skipped on purpose), but it
  only moves references that are clearly floating and leaves deeply sunk ones alone as likely deliberate.
  On the author's list it flagged 986 references and would correct 114: review before trusting it.
- **Cells with their own water plane**, and cells where placed objects would move, are skipped for
  terrain reverts by design. They are listed in the log for a manual look.
- **Water protection can touch many cells.** On the author's list, protecting one water mod restored
  its water in 657 cells; check the log before generating.
- Skyrim SE / AE only. No Legendary Edition, no VR testing.

## A bug fixed on the way

While building Groundskeeper's tests it turned out that the shared trust logic in the earlier tools
counted **every plugin that lists `Skyrim.esm` as a master** as a trusted "patch of Skyrim.esm". Since
nearly every plugin does, the seam and texture tools found no untrusted overwriter and restored
nothing on landscape. Groundskeeper fixes this (the game masters can no longer make a plugin
trusted) and covers it with `tests/TerrainEditCheck`. The earlier standalone releases still have it.

## Building

```
dotnet build src/LandscapeHybrid.UI        # Groundskeeper.exe
dotnet build src/LandscapeHybrid.Cli       # GroundskeeperCli.exe
dotnet run --project tests/TerrainEditCheck
dotnet run --project tests/TerrainPriorityCheck
dotnet run --project tests/PlacedChildDeduperCheck
dotnet run --project tests/CollisionCheck -- <reference.json>   # optional: compares against reference collision output; needs game meshes
```

Needs the .NET 10 SDK. Libraries: Mutagen 0.54.4, System.Drawing.Common.

## Credits and licence

See [CREDITS.md](CREDITS.md) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Source files are
MIT ([LICENSE](LICENSE)); the combined program links Mutagen, which is GPL-3.0, so a built
Groundskeeper is distributed under the GPL-3.0.
