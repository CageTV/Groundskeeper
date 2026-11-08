// Synthetic check that the seam pass really EDITS landscape (the real H list has nothing to repair, so it never ran that path).
// Scenario: Skyrim.esm cell (0,0) flat at 0; Update.esm repairs it to 80; Bad.esp carries the old Skyrim.esm terrain forward
// (the Creation Kit ITM case), hiding the repair. Expected: the fix plugin restores 80 for (0,0) and leaves (1,0) alone.
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using SeamFinder.Core;

var dir = Path.Combine(Path.GetTempPath(), "terrain-edit-check-" + Guid.NewGuid().ToString("N"));
var data = Path.Combine(dir, "data"); var outDir = Path.Combine(dir, "out");
Directory.CreateDirectory(data); Directory.CreateDirectory(outDir);

var skyrimKey = ModKey.FromNameAndExtension("Skyrim.esm");
var updateKey = ModKey.FromNameAndExtension("Update.esm");
var badKey = ModKey.FromNameAndExtension("Bad.esp");

Landscape Land(uint id, float offset)
{
    var l = new Landscape(new FormKey(skyrimKey, id), SkyrimRelease.SkyrimSE);
    var v = new LandscapeVertexHeightMap { Offset = offset, HeightMap = new Array2d<sbyte>(new sbyte[33, 33]) }; // all deltas 0: flat plane at Offset*8
    l.VertexHeightMap = v;
    return l;
}
Cell Cl(uint id, int x, Landscape? land) => new(new FormKey(skyrimKey, id), SkyrimRelease.SkyrimSE)
{ Grid = new CellGrid { Point = new P2Int(x, 0) }, Landscape = land };
Worldspace Ws(params Cell[] cells)
{
    var ws = new Worldspace(new FormKey(skyrimKey, 0x900), SkyrimRelease.SkyrimSE) { EditorID = "Tamriel" };
    var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0 };
    foreach (var c in cells) sub.Items.Add(c);
    var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0 }; blk.Items.Add(sub); ws.SubCells.Add(blk);
    return ws;
}
void Write(SkyrimMod m, params ModKey[] masters)
{
    foreach (var k in masters) m.ModHeader.MasterReferences.Add(new MasterReference { Master = k });
    m.BeginWrite.ToPath(Path.Combine(data, m.ModKey.FileName)).WithNoLoadOrder().Write();
}

var sky = new SkyrimMod(skyrimKey, SkyrimRelease.SkyrimSE);
sky.Worldspaces.Add(Ws(Cl(0x800, 0, Land(0x801, 0f)), Cl(0x810, 1, Land(0x811, 0f))));
Write(sky);
var upd = new SkyrimMod(updateKey, SkyrimRelease.SkyrimSE);
upd.Worldspaces.Add(Ws(Cl(0x800, 0, Land(0x801, 10f))));      // the repair: 10*8 = 80
Write(upd, skyrimKey);
var bad = new SkyrimMod(badKey, SkyrimRelease.SkyrimSE);
bad.Worldspaces.Add(Ws(Cl(0x800, 0, Land(0x801, 0f))));       // carries the OLD terrain forward over the repair
Write(bad, skyrimKey, updateKey);

var plugins = new[] { "Skyrim.esm", "Update.esm", "Bad.esp" }
    .Select(n => new Mo2Resolver.ResolvedPlugin(n, Path.Combine(data, n))).ToList();
var log = new List<string>();
var det = SeamDetector.RunForResolvedPlugins(plugins, m => Console.WriteLine("  [detect] " + m));
Console.WriteLine($"detector: cells={det.CellsProcessed} noLandscape={det.SkippedNoLandscape} noWorldspace={det.SkippedNoWorldspace} interior={det.SkippedInterior}");
var r = SeamFixer.GenerateFixPluginForResolvedPlugins(plugins, "LandscapeSeamFixes.esp", outDir, log.Add);
Console.WriteLine($"trust probe: IsBaseGamePlugin(Bad.esp)={TrustResolver.IsBaseGamePlugin("Bad.esp", false, new List<string>())}  IsPatchOfTrustedBase(Bad.esp, Skyrim.esm)={TrustResolver.IsPatchOfTrustedBase("Bad.esp", "Skyrim.esm")}");
foreach (var l in log.TakeLast(8)) Console.WriteLine("  " + l);
Console.WriteLine($"result: CellsPatched={r.CellsPatched} EdgesFixed={r.EdgesFixed} output={Path.GetFileName(r.OutputPath)} exists={File.Exists(r.OutputPath)}");

var ok = true;
if (r.CellsPatched != 1) { Console.WriteLine("FAIL: expected exactly 1 cell patched"); ok = false; }
if (File.Exists(r.OutputPath))
{
    var fix = SkyrimMod.CreateFromBinary(r.OutputPath, SkyrimRelease.SkyrimSE);
    var cells = fix.EnumerateMajorRecords<Cell>().ToList();
    Console.WriteLine($"fix plugin cells: {string.Join(", ", cells.Select(c => c.FormKey))}");
    var c0 = cells.FirstOrDefault(c => c.FormKey == new FormKey(skyrimKey, 0x800));
    if (c0?.Landscape?.VertexHeightMap is { } vh)
    {
        var h = HeightmapDecoder.DecodeHeights(vh)[0, 0];
        Console.WriteLine($"restored height at (0,0) vertex 0,0 = {h} (expected 80)");
        if (Math.Abs(h - 80f) > 0.01f) { Console.WriteLine("FAIL: wrong height"); ok = false; }
    }
    else { Console.WriteLine("FAIL: cell (0,0) landscape missing from the fix plugin"); ok = false; }
    if (cells.Any(c => c.FormKey == new FormKey(skyrimKey, 0x810))) { Console.WriteLine("FAIL: untouched cell (1,0) was patched"); ok = false; }
}
else ok = false;
Console.WriteLine(ok ? "PASS" : "FAILED");
return ok ? 0 : 1;
