# Changelog

## 0.1.0 (unreleased, not yet tested in-game)

First build of Groundskeeper: one app that replaces running Landscape Seam Fixer, Landscape Texture Fixer,
Road Mask Merger, Floating Object Fixer and PatchForeman separately.

- One shared library (`Groundskeeper.Seams`) instead of five diverging copies of `SeamFinder.Core`.
- Mod Organizer 2, and Vortex / plain install (the game's `Data` folder).
- Scan lists every mod that overwrites trusted terrain or water; nothing is reverted unless ticked.
- Ordered terrain winners and an ordered list of water mods to protect.
- Seam, texture-layer, road and floating-object passes merged into one plugin, split on the 255-master
  limit and ESL-flagged when it fits.
- A guard against "Two records with the same FormKey" write errors.
- Landscape Fixer's `LandscapeFixer.esp` is never read as a terrain source.
- Fix: the game masters (Skyrim.esm, the DLCs) no longer make every plugin a trusted "patch", which had
  stopped the earlier tools from restoring any landscape. Official and Creation Club plugins are always
  trusted.
- Tests: `TerrainEditCheck`, `TerrainPriorityCheck`, `PlacedChildDeduperCheck`.
