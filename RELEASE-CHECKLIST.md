# Groundskeeper 0.1.0: release checklist

State as of 2026-11-08 (release candidate 1). Nothing has been published, pushed or deployed.

## Verified (by running it)

- Builds clean in Release; the published folder starts, opens its window and stays responsive.
- Tests pass: `TerrainEditCheck`, `TerrainPriorityCheck`, `PlacedChildDeduperCheck`, `EdgeGuardCheck`.
  `CollisionCheck` needs a reference JSON from the old PyNifly extractor and was last run when the in-process collision reader was written (269/269 meshes matched).
- Speed work is behaviour-neutral: old and new builds were run at the same moment on the same load order and the output plugin matched byte for byte,
  with an identical decision log (1920 lines). Full run on the 445-plugin Tabula list: about a minute.
- Problem map: scan runs headless (about 23 s on that list), the real window was opened against real data, scanned and rendered; before/after of
  a generated output showed 0 new marks and fewer floating objects.
- Comparison against the old Floating Object Fixer: 112 of its 113 corrections are identical, and the differences are where Groundskeeper reads collision the old tool could not.

## Not verified: needs the author

1. **In-game test.** The output has never been loaded in a game. Test on a copy of the profile, check the cells the map marks, the cells the
   floating pass moved, and the `Groundskeeper.esp` load position.
2. **Vortex / plain install** mode and **launching from MO2's executables list** are untested (direct mode only ran on a vanilla folder).
3. **Roads pass** has not run on real road data.
4. **Water seam** marks are candidates only; no automatic fix exists.
5. The problem map's mouse behaviour (wheel zoom, drag, click) was written and rendered but not driven by hand.

## Decisions for the author

- **Licence.** `LICENSE` is drafted as MIT for the source plus GPL-3.0 for the built program (Mutagen is GPL-3.0-only). Landscape Seam Fixer and
  Road Mask Merger are CC BY-NC-SA 4.0 in their repositories, so folding their code in relicenses it and drops the NonCommercial clause. Only the
  copyright holder can approve that wording. Do not publish before it is settled.
- **Name and Nexus page.** "Groundskeeper" is the working name; check it is free on Nexus.
- **Contact.** Cl3mus33 has not been contacted; the README says the app works alongside Landscape Fixer and never reads `LandscapeFixer.esp` as a source.
- **Defaults.** Nothing is reverted unless ticked, so a first Generate changes little on terrain by design. Decide whether the first-run guidance in the window is enough.

## To publish

1. Settle the licence; bump the version in `LandscapeHybrid.UI.csproj` if needed.
2. `dotnet publish src/LandscapeHybrid.UI -c Release -r win-x64 --self-contained false -o <folder>` and the same for `src/LandscapeHybrid.Cli` into the same folder
   (requires the .NET 10 Desktop Runtime on the user's machine).
3. Include `README.md`, `CHANGELOG.md`, `CREDITS.md`, `LICENSE`, `THIRD-PARTY-NOTICES.md`.
4. Review `CREDITS.md` and the Nexus description with the licence wording from step 1 (no licence claims in the Nexus description).
5. Use `bash tools/...` mod-release-review before uploading.

## Known limits (also in the README)

Skyrim SE / AE only, no LE, no VR testing. The floating pass only moves references that are clearly floating and leaves deeply sunk ones alone.
The problem map credits a seam to the mods that cause it only when asked to look through Landscape Fixer's output.
