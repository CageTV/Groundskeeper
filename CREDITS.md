# Credits

## Groundskeeper

Written by **Julio005 (GitHub: CageTV)**.

The Groundskeeper mountain-and-seam mark (`assets/GroundKeeperICO.png`) is by Julio005; the app icon and header logo are recolored versions of it.

## The tools it grew from

Groundskeeper merges the code and the ideas of five earlier tools by the same author. Each one still
exists as its own release:

| Tool | Where | What it contributed |
|---|---|---|
| Landscape Seam Fixer | [Nexus 190620](https://www.nexusmods.com/skyrimspecialedition/mods/190620) · [GitHub](https://github.com/CageTV/landscape-seam-fixer) | The seam and water restore engine and the trust model |
| Landscape Texture Fixer | [Nexus 192212](https://www.nexusmods.com/skyrimspecialedition/mods/192212) · [GitHub](https://github.com/CageTV/LandscapeTextureFixes) | Texture-layer boundary detection and blend hints |
| Road Mask Merger | [Nexus 191502](https://www.nexusmods.com/skyrimspecialedition/mods/191502) · [GitHub](https://github.com/CageTV/Road-Mask-Merger) | Road terrain reconciliation |
| Floating Object Fixer | [Nexus 191761](https://www.nexusmods.com/skyrimspecialedition/mods/191761) · [GitHub](https://github.com/CageTV/floating-reference-fixer) | Re-grounding floating and sunken references |
| PatchForeman | [Nexus 192362](https://www.nexusmods.com/skyrimspecialedition/mods/192362) · [GitHub](https://github.com/CageTV/PatchForeman) | The field-aware merge, ESL flagging and automatic master-limit split |

Thanks to everyone who reported bugs and tested those tools on Nexus, in the comments and on the bug
tabs. Several changes in Groundskeeper come from those reports: the heightmap decoding fix, leaving a
tool's own earlier output out of the scan on a re-run, and a guard against the "Two records with the
same FormKey" write error (the exact cause of the reported case is still unconfirmed).

## Landscape Fixer, by Cl3mus33

[Landscape Fixer](https://www.nexusmods.com/skyrimspecialedition/mods/198142) (the replacement for
Snow Fixer and AutoBlend) is by **Cl3mus33**, who also made Snow Fixer and AutoBlend. Groundskeeper
**does not contain any of its code**. It is designed so the two run side by side: Landscape Fixer's
plugin is never used as a terrain source. Groundskeeper's goal of working cleanly next to it, and the
idea of handling the snow, blend and landscape mess in one pass instead of many, owe a lot to that
work. Please support the original.

## ACMOS Road Generator, by DoubleYouC

The road pass finds roads by reading the road overlay images of **ACMOS Road Generator** by **DoubleYouC**
([Nexus mod 79205](https://www.nexusmods.com/skyrimspecialedition/mods/79205),
[GitHub](https://github.com/DoubleYouC/ACMOS-Road-Generator)). Those images were made to paint roads onto
terrain LOD, and Groundskeeper (like Road Mask Merger before it) reuses them as a map of where the roads are.
The images are licensed **CC BY-NC-SA** by their author, so Groundskeeper does **not** include them:
download ACMOS Road Generator yourself and point the "Road mask folder" box at it. Please support the
original author.

## Libraries

- **Mutagen** by Noggog and the Mutagen-Modding contributors, for reading and writing plugins.
  GPL-3.0. <https://github.com/Mutagen-Modding/Mutagen>
- **System.Drawing.Common** (Microsoft), for reading road mask images. MIT.
- **niflysharp** (C# bindings, by SteveTownsend) of **nifly** (by ousnius), for reading NIF files and their
  Havok collision in the floating-object pass. GPL-3.0.
  <https://github.com/SteveTownsend/niflyswig> · <https://github.com/ousnius/nifly>
- **PyNifly** by BadDogSkyrim. Not used by Groundskeeper, but its output was the reference the in-process
  collision reader was checked against (269 real meshes). <https://github.com/BadDogSkyrim/PyNifly>

## Terrain mods this was built and tested against

Groundskeeper was exercised against a large Mod Organizer 2 load order that includes Northern Roads
and its patch family, Unofficial Skyrim Special Edition Patch, Landscape and Water Fixes, CS Water Mod
and many landscape overhauls. These are the authors' own work, and Groundskeeper only reads them. It
never edits them.
