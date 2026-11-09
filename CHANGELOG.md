# Changelog

## 0.1.0 (release candidate, not yet tested in-game)

First build of Groundskeeper: one app that replaces running Landscape Seam Fixer, Landscape Texture Fixer,
Road Mask Merger, Floating Object Fixer and PatchForeman separately.

- Problem map: **3D view** button opens the full-resolution terrain of a block of cells (3x3 to 7x7) around a mark as a 3D model (drag to rotate, right-drag to pan, wheel to zoom), with water, cell borders and the seam, water, texture and floating marks drawn on it, and buttons to step to the next cell.
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
- Floating-object pass no longer needs Python, PyNifly or SciPy: collision is read in-process with the pure-C# Nifly
  library (no native DLLs, no Visual C++ runtime), checked against the old PyNifly output on real meshes and, end to end,
  against the previous reader (byte-identical plugin on a 453-plugin list).
- `--water-report`: read-only CSV of neighbouring cells whose water disagrees (candidate "random water squares"); no automatic fix yet.
- Road inputs explained in the window (hint, Browse button, live check of the road mask folder); a wrong folder skips the
  road pass with a clear message instead of failing. ACMOS Road Generator credited; its images are not bundled.
- The merged plugin is now `Groundskeeper.esp` (earlier test builds wrote `LandscapeTerrain.esp`; that name is still recognised as
  this tool's own output and replaced).
- MO2: the instance and profile are detected when started from MO2, the output defaults to a "Groundskeeper Output" mod folder in the MO2 mods folder (created
  on Generate), and settings are kept per instance.
- Passes: trust Northern Vanaheimr Roads (main plugin and patches) as a terrain source; new Preferred road mod (for road mods that edit the landscape).
- **Problem map** window (button, or *Open the problem map after Generate*) and `--problem-map` / `--with` / `--without` on the command line: a
  read-only scan of the load order, outputs included, drawn on a terrain map (height, water and texture seams, floating objects left
  alone), with filters, a ranked list, details and a copyable `cow` console command. Seams are credited to the mods that cause them (it can
  look through Landscape Fixer's output) and seams where both sides are Bethesda's own plugins are hidden by default.
- **Faster.** The floating-object pass is about three times faster (it was re-reading the same cells and base records, and scanning whole
  archive file lists for every mesh lookup), and a Scan's plan is reused by the Generate that follows. Output is byte-identical to before on
  the author's list. The log now prints how long each phase took.
- Window: in the conflict list the Make winner button now comes before the tick, rows have the name in bold with the numbers on a second line and
  alternating shading, and the arrow buttons of the winners and water lists are at the left.
- `GROUNDSKEEPER_CONFIG` selects a different settings file.
- New look, first part: the Groundskeeper mark as the app icon (a dark tile with the mountain in orange; strokes thickened for the small sizes) and as a
  glowing header logo, a gradient title, and a strip of passes (Seams, Textures, Roads, Floating, Merge) that lights up while a run goes.
- Seam guard: before saving, every written cell is checked against its neighbours; a gap the plugin itself introduced is repaired (edge pulled to
  the neighbour, blended inward, heights re-encoded, normals recomputed), or the cell keeps its previous terrain if the repair would be too steep.
- Terrain winners: a mod you add is placed in its load-order position and can then be moved; Sort by load order resets the list.
- Tests: `TerrainEditCheck`, `TerrainPriorityCheck`, `PlacedChildDeduperCheck`, `CollisionCheck`, `EdgeGuardCheck`.
