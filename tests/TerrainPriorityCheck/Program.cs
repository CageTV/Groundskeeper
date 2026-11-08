// User-ordered terrain winners: Skyrim.esm cell (0,0) flat at 0; ModA.esp edits it to 160; ModB.esp (loaded later, so the
// load-order winner) edits it to 240. With the user's order [A, B] the cell must be restored to A's 160; with [B, A] nothing changes;
// with a single-entry [A] A also wins over the untrusted B.
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using SeamFinder.Core;

var dir = Path.Combine(Path.GetTempPath(), "terrain-priority-check-" + Guid.NewGuid().ToString("N"));
var data = Path.Combine(dir, "data");
Directory.CreateDirectory(data);
var skyrimKey = ModKey.FromNameAndExtension("Skyrim.esm");
var aKey = ModKey.FromNameAndExtension("ModA.esp");
var bKey = ModKey.FromNameAndExtension("ModB.esp");

Landscape Land(float offset)
{
    var l = new Landscape(new FormKey(skyrimKey, 0x801), SkyrimRelease.SkyrimSE);
    l.VertexHeightMap = new LandscapeVertexHeightMap { Offset = offset, HeightMap = new Array2d<sbyte>(new sbyte[33, 33]) };
    return l;
}
Worldspace Ws(float offset)
{
    var cell = new Cell(new FormKey(skyrimKey, 0x800), SkyrimRelease.SkyrimSE) { Grid = new CellGrid { Point = new P2Int(0, 0) }, Landscape = Land(offset) };
    var ws = new Worldspace(new FormKey(skyrimKey, 0x900), SkyrimRelease.SkyrimSE) { EditorID = "Tamriel" };
    var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0 }; sub.Items.Add(cell);
    var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0 }; blk.Items.Add(sub); ws.SubCells.Add(blk);
    return ws;
}
void Write(SkyrimMod m, float offset, params ModKey[] masters)
{
    m.Worldspaces.Add(Ws(offset));
    foreach (var k in masters) m.ModHeader.MasterReferences.Add(new MasterReference { Master = k });
    m.BeginWrite.ToPath(Path.Combine(data, m.ModKey.FileName)).WithNoLoadOrder().Write();
}
Write(new SkyrimMod(skyrimKey, SkyrimRelease.SkyrimSE), 0f);
Write(new SkyrimMod(aKey, SkyrimRelease.SkyrimSE), 20f, skyrimKey);
Write(new SkyrimMod(bKey, SkyrimRelease.SkyrimSE), 30f, skyrimKey);
var plugins = new[] { "Skyrim.esm", "ModA.esp", "ModB.esp" }.Select(n => new Mo2Resolver.ResolvedPlugin(n, Path.Combine(data, n))).ToList();

var ok = true;
// returns the restored height at vertex (0,0) of cell 0x800, or null when nothing was patched
float? Run(string label, string[] order)
{
    var outDir = Path.Combine(dir, "out-" + label); Directory.CreateDirectory(outDir);
    var r = SeamFixer.GenerateFixPluginForResolvedPlugins(plugins, "Fix.esp", outDir, _ => { }, priorityOverNorthernRoadsPlugins: order);
    if (r.CellsPatched == 0) return null;
    var fix = SkyrimMod.CreateFromBinary(r.OutputPath, SkyrimRelease.SkyrimSE);
    var c = fix.EnumerateMajorRecords<Cell>().FirstOrDefault(x => x.FormKey == new FormKey(skyrimKey, 0x800));
    return c?.Landscape?.VertexHeightMap is { } v ? HeightmapDecoder.DecodeHeights(v)[0, 0] : null;
}
void Expect(string label, string[] order, float? want)
{
    var got = Run(label, order);
    var pass = want is null ? got is null : got is { } g && Math.Abs(g - want.Value) < 0.01f;
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")}: order [{string.Join(", ", order)}] -> {(got is null ? "no change" : got + " units")} (expected {(want is null ? "no change" : want + " units")})");
    ok &= pass;
}
Expect("ab", new[] { "ModA.esp", "ModB.esp" }, 160f);   // A outranks the load-order winner B
Expect("ba", new[] { "ModB.esp", "ModA.esp" }, null);     // B already is the top entry and the winner
Expect("a", new[] { "ModA.esp" }, 160f);                  // single entry behaves as before: A wins over the untrusted B
Console.WriteLine(ok ? "ALL PASS" : "FAILED");
return ok ? 0 : 1;
