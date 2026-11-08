using Mutagen.Bethesda.Plugins.Records; using Mutagen.Bethesda.Plugins.Binary.Parameters; using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda; using Mutagen.Bethesda.Plugins; using Mutagen.Bethesda.Plugins.Order; using Mutagen.Bethesda.Skyrim; using Noggog; using SeamFinder.Core;
var mk = ModKey.FromNameAndExtension("Master.esm"); var pk = ModKey.FromNameAndExtension("Patch.esp");
var master = new SkyrimMod(mk, SkyrimRelease.SkyrimSE); var stat = master.Statics.AddNew("S");
var patch = new SkyrimMod(pk, SkyrimRelease.SkyrimSE); patch.ModHeader.MasterReferences.Add(new MasterReference{ Master = mk });
FormKey Fk(uint id) => new(mk, id);
PlacedObject R(uint id) => new(Fk(id), SkyrimRelease.SkyrimSE) { Base = new FormLinkNullable<IPlaceableObjectGetter>(stat.FormKey), Placement = new Placement() };
Cell C(uint id, int x) => new(Fk(id), SkyrimRelease.SkyrimSE) { Grid = new CellGrid { Point = new P2Int(x, 0) } };
var ws = new Worldspace(Fk(0x900), SkyrimRelease.SkyrimSE); var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0 };
var a = C(0x800, 0); var b = C(0x801, 1);
a.Temporary.Add(R(0x884)); b.Temporary.Add(R(0x884));        // same REFR in two different cells (the "moved reference" shape)
a.Temporary.Add(R(0x885)); a.Temporary.Add(R(0x885));        // same REFR twice in one cell
a.Persistent.Add(R(0x886));                                    // unique - must survive
sub.Items.Add(a); sub.Items.Add(b); var blk = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0 }; blk.Items.Add(sub); ws.SubCells.Add(blk); patch.Worldspaces.Add(ws);
var lo = new LoadOrder<ISkyrimModGetter>(); lo.Add(master);
string Try(string name) { try { var dir = Path.Combine(Path.GetTempPath(), "dedupe-test", name); Directory.CreateDirectory(dir); patch.BeginWrite.ToPath(Path.Combine(dir, "Patch.esp")).WithLoadOrder(lo).Write(); return "WRITE OK"; } catch (Exception e) { return "WRITE FAILED: " + e.GetType().Name + ": " + e.Message; } }
Console.WriteLine("before guard -> " + Try("before.esp"));
var res = PlacedChildDeduper.Run(patch, key => key == Fk(0x884) ? b.FormKey : null);
Console.WriteLine($"guard: groups={res.DuplicateGroups} removed={res.CopiesRemoved}"); foreach (var m in res.Messages) Console.WriteLine(m);
Console.WriteLine("after guard  -> " + Try("after.esp"));
Console.WriteLine($"cell A temp={a.Temporary.Count} pers={a.Persistent.Count}; cell B temp={b.Temporary.Count} (expect A: 1 temp(0x885) + 1 pers; B: 1 temp(0x884))");
Console.WriteLine("0x884 in B: " + b.Temporary.Any(t => t.FormKey == Fk(0x884)) + "; in A: " + a.Temporary.Any(t => t.FormKey == Fk(0x884)));
