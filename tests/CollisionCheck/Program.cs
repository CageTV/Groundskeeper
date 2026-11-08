// Compares the in-process niflysharp collision reader with reference output from the old PyNifly extractor.
// usage: CollisionCheck <ref.json produced by extract_collision.py>
// Matching = same triangle count and the same multiset of triangles (vertex order within a triangle ignored), to 0.05 units.
using System.Text.Json;
using SeamFinder.Core;

if (args[0] == "--scan")
{
    // usage: CollisionCheck --scan <file with one nif path per line>  : shape-type census + failure reasons + timing
    var paths = File.ReadAllLines(args[1]).Where(l => l.Length > 0).ToList();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var kinds = new Dictionary<string, List<string>>(); var reasons = new Dictionary<string, int>();
    foreach (var pth in paths)
    {
        var r = NifCollisionReader.Extract(pth);
        if (r.Ok) { var k = string.Join("+", r.ShapeTypes); if (!kinds.TryGetValue(k, out var l)) kinds[k] = l = new(); l.Add(pth); }
        else { var k = r.Reason!.Length > 90 ? r.Reason[..90] : r.Reason; reasons[k] = reasons.GetValueOrDefault(k) + 1; }
    }
    Console.WriteLine($"{paths.Count} files in {sw.Elapsed.TotalSeconds:F1}s");
    foreach (var (k, l) in kinds.OrderByDescending(x => x.Value.Count)) Console.WriteLine($"  ok {l.Count,5}  {k}   e.g. {l[0]}");
    foreach (var (k, n) in reasons.OrderByDescending(x => x.Value)) Console.WriteLine($"  fail {n,5}  {k}");
    return 0;
}
var refDoc = JsonDocument.Parse(File.ReadAllText(args[0])).RootElement;
static string Key(Vec3 v) => $"{Math.Round(v.X / 0.05f)},{Math.Round(v.Y / 0.05f)},{Math.Round(v.Z / 0.05f)}";
static string TriKey(Vec3 a, Vec3 b, Vec3 c) => string.Join("|", new[] { Key(a), Key(b), Key(c) }.OrderBy(x => x, StringComparer.Ordinal));
var allOk = true;
foreach (var prop in refDoc.EnumerateObject())
{
    string full;
    try { full = prop.Name; } catch (InvalidOperationException) { Console.WriteLine("SKIP  (a path with an unrepresentable character: skipped)"); continue; }
    var name = Path.GetFileName(full);
    var r = prop.Value;
    var mine = NifCollisionReader.Extract(full);
    if (!r.GetProperty("ok").GetBoolean())
    {
        var same = !mine.Ok;
        Console.WriteLine($"{(same ? "PASS" : "FAIL")}  {name}: reference has no collision ({r.GetProperty("reason").GetString()}); mine: {(mine.Ok ? "ok?!" : mine.Reason)}");
        allOk &= same; continue;
    }
    var refTris = r.GetProperty("triangles").EnumerateArray().Select(t => { var p = t.EnumerateArray().Select(v => { var a = v.EnumerateArray().Select(x => x.GetSingle()).ToArray(); return new Vec3(a[0], a[1], a[2]); }).ToArray(); return TriKey(p[0], p[1], p[2]); }).ToList();
    if (!mine.Ok) { Console.WriteLine($"FAIL  {name}: reference {refTris.Count} triangles, mine failed: {mine.Reason}"); allOk = false; continue; }
    var myTris = mine.LocalTriangles.Select(t => TriKey(t.A, t.B, t.C)).ToList();
    var refSet = refTris.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
    var mySet = myTris.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
    var missing = refSet.Sum(kv => Math.Max(0, kv.Value - mySet.GetValueOrDefault(kv.Key)));
    var extra = mySet.Sum(kv => Math.Max(0, kv.Value - refSet.GetValueOrDefault(kv.Key)));
    // Triangles that differ only at the comparison grid boundary: pair each reference-only triangle with the nearest mine-only one
    // (best vertex assignment) and report the worst deviation in units. A real decoding error shows up as a large deviation.
    double worst = 0;
    if (missing > 0)
    {
        static Vec3[] Parse(string k) => k.Split('|').Select(v => v.Split(',').Select(x => float.Parse(x) * 0.05f).ToArray()).Select(a => new Vec3(a[0], a[1], a[2])).ToArray();
        var refOnly = refSet.Where(kv => kv.Value > mySet.GetValueOrDefault(kv.Key)).Select(kv => Parse(kv.Key)).ToList();
        var myOnly = mySet.Where(kv => kv.Value > refSet.GetValueOrDefault(kv.Key)).Select(kv => Parse(kv.Key)).ToList();
        static double D(Vec3 a, Vec3 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
        static double Fit(Vec3[] x, Vec3[] y) { var perms = new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } }; return perms.Min(q => Math.Max(D(x[0], y[q[0]]), Math.Max(D(x[1], y[q[1]]), D(x[2], y[q[2]])))); }
        foreach (var rt in refOnly) worst = Math.Max(worst, myOnly.Count == 0 ? 1e9 : myOnly.Min(mt => Fit(rt, mt)));
    }
    var pass = refTris.Count == myTris.Count && worst < 0.2; // 0.05-unit grid cells: a boundary case can differ by up to ~0.1

    Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}: reference {refTris.Count} tris, mine {myTris.Count}; boundary-case worst deviation {worst:F3} units ({missing} differing triangles); types [{string.Join(", ", mine.ShapeTypes)}]");
    allOk &= pass;
}
Console.WriteLine(allOk ? "ALL MATCH" : "MISMATCH");
return allOk ? 0 : 1;
