// EdgeGuard on synthetic terrain. Skyrim.esm: cells A (0,0) and B (0,1) (B is NORTH of A), both flat at 0 with upright normals.
// "Patch.esp" (the plugin being built) rewrites A only:
//   case 1: raises A's last three rows (y 30..32) by 48 -> a 48-unit step along the A|B edge that was not there before
//   case 2: raises A's middle (x,y in 10..20) by 48 -> nothing reaches an edge, so the guard must change nothing
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using SeamFinder.Core;

var dir = Path.Combine(Path.GetTempPath(), "edgeguard-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
var skyKey = ModKey.FromNameAndExtension("Skyrim.esm");

Landscape Land(uint id, float height)
{
    var l = new Landscape(new FormKey(skyKey, id), SkyrimRelease.SkyrimSE);
    var hm = new sbyte[33, 33];   // flat: every step 0, offset carries the height
    l.VertexHeightMap = new LandscapeVertexHeightMap { Offset = height / 8f, HeightMap = new Array2d<sbyte>(hm) };
    var nrm = new P3UInt8[33, 33];
    for (var x = 0; x <= 32; x++) for (var y = 0; y <= 32; y++) nrm[x, y] = new P3UInt8(0, 0, 127);
    l.VertexNormals = new Array2d<P3UInt8>(nrm);
    return l;
}
Cell MakeCell(uint cellId, uint landId, int x, int y) => new(new FormKey(skyKey, cellId), SkyrimRelease.SkyrimSE)
{ Grid = new CellGrid { Point = new P2Int(x, y) }, Landscape = Land(landId, 0f) };
Worldspace Ws(params Cell[] cells)
{
    var ws = new Worldspace(new FormKey(skyKey, 0x900), SkyrimRelease.SkyrimSE) { EditorID = "Tamriel" };
    var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0 };
    foreach (var c in cells) sub.Items.Add(c);
    var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0 }; blk.Items.Add(sub); ws.SubCells.Add(blk);
    return ws;
}
var sky = new SkyrimMod(skyKey, SkyrimRelease.SkyrimSE);
sky.Worldspaces.Add(Ws(MakeCell(0x800, 0x801, 0, 0), MakeCell(0x810, 0x811, 0, 1)));
sky.BeginWrite.ToPath(Path.Combine(dir, "Skyrim.esm")).WithNoLoadOrder().Write();

using var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>.Create(GameRelease.SkyrimSE)
    .WithLoadOrder(new[] { skyKey }).WithTargetDataFolder(dir).Build();
var prio = new Dictionary<ModKey, int> { [skyKey] = 0 };

// A patch plugin holding a modified copy of cell A (the same record, as the merge writes it)
SkyrimMod BuildPatch(Action<float[,]> edit)
{
    var patch = new SkyrimMod(ModKey.FromNameAndExtension("Patch.esp"), SkyrimRelease.SkyrimSE);
    patch.ModHeader.MasterReferences.Add(new MasterReference { Master = skyKey });
    var a = MakeCell(0x800, 0x801, 0, 0);
    var h = HeightmapDecoder.DecodeHeights(a.Landscape!.VertexHeightMap!);
    edit(h);
    // encode the edited heights (simple: each step = difference of neighbours /8, rounded)
    var d = new sbyte[33, 33]; var dec = new float[33, 33];
    var off = h[0, 0] / 8f; dec[0, 0] = off * 8f;
    for (var y = 0; y <= 32; y++) { if (y > 0) { var s0 = (sbyte)Math.Round((h[0, y] - dec[0, y - 1]) / 8f); d[0, y] = s0; dec[0, y] = dec[0, y - 1] + s0 * 8f; }
        for (var x = 1; x <= 32; x++) { var s = (sbyte)Math.Round((h[x, y] - dec[x - 1, y]) / 8f); d[x, y] = s; dec[x, y] = dec[x - 1, y] + s * 8f; } }
    a.Landscape!.VertexHeightMap = new LandscapeVertexHeightMap { Offset = off, HeightMap = new Array2d<sbyte>(d) };
    patch.Worldspaces.Add(Ws(a));
    return patch;
}

float[,] HeightsOf(SkyrimMod m) => HeightmapDecoder.DecodeHeights(m.EnumerateMajorRecords<Cell>().First().Landscape!.VertexHeightMap!);
var ok = true;
void Check(string what, bool pass, string detail = "") { Console.WriteLine($"{(pass ? "PASS" : "FAIL")}: {what}{(detail.Length > 0 ? " - " + detail : "")}"); ok &= pass; }

// ---- case 1: step along the north edge ----
var p1 = BuildPatch(h => { for (var x = 0; x <= 32; x++) for (var y = 30; y <= 32; y++) h[x, y] += 48f; });
var before1 = HeightsOf(p1);
float GapNorth(float[,] a) { float m = 0; for (var i = 0; i <= 32; i++) m = Math.Max(m, Math.Abs(a[i, 32] - 0f)); return m; } // B is flat at 0
Console.WriteLine($"case 1 before the guard: gap against the north neighbour = {GapNorth(before1):F0} units");
var r1 = EdgeGuard.Run(p1, env.LinkCache, prio, "Patch.esp", Console.WriteLine);
var after1 = HeightsOf(p1);
Check("case 1: the step along the shared edge is closed", GapNorth(after1) <= 8f, $"gap now {GapNorth(after1):F0} units");
Check("case 1: the cell was reported as adjusted", r1.CellsAdjusted == 1 && r1.EdgesRepaired >= 1, $"adjusted {r1.CellsAdjusted}, edges {r1.EdgesRepaired}");
Check("case 1: the interior far from the edge keeps the plugin's terrain (unchanged at y=10)", Math.Abs(after1[16, 10] - before1[16, 10]) < 0.01f);
var n = p1.EnumerateMajorRecords<Cell>().First().Landscape!.VertexNormals![16, 31];
Check("case 1: the normals near the repaired edge were recomputed (they lean now)", (sbyte)n.Y != 0 || (sbyte)n.X != 0, $"normal at (16,31) = ({(sbyte)n.X},{(sbyte)n.Y},{(sbyte)n.Z})");
Check("case 1: the encoded heights decode back to what the guard computed (no corruption)", HeightsOf(p1)[5, 5] == after1[5, 5]);

// ---- case 2: an edit that does not reach any edge ----
var p2 = BuildPatch(h => { for (var x = 10; x <= 20; x++) for (var y = 10; y <= 20; y++) h[x, y] += 48f; });
var before2 = HeightsOf(p2);
var r2 = EdgeGuard.Run(p2, env.LinkCache, prio, "Patch.esp", Console.WriteLine);
var after2 = HeightsOf(p2);
var same = true; for (var x = 0; x <= 32; x++) for (var y = 0; y <= 32; y++) same &= Math.Abs(after2[x, y] - before2[x, y]) < 0.01f;
Check("case 2: an edit away from the edges is left exactly as it was", same && r2.EdgesRepaired == 0 && r2.CellsAdjusted == 0, $"edges repaired {r2.EdgesRepaired}");

Console.WriteLine(ok ? "ALL PASS" : "FAILED");
try { Directory.Delete(dir, true); } catch { }
return ok ? 0 : 1;
